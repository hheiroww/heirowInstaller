using System.Diagnostics;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text.Json;

namespace HeirowInstaller.Core;

public sealed class InstallerEngine
{
    private readonly Dictionary<string, IInstallerModule> modules;
    public InstallerEngine(IEnumerable<IInstallerModule>? modules = null)
    {
        this.modules = new(StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules ?? [])
        {
            PackageSafety.ValidateId(module.Id);
            if (!this.modules.TryAdd(module.Id, module)) throw new ArgumentException("Duplicate installer module: " + module.Id, nameof(modules));
        }
    }
    public static string DefaultStateRoot(string scope = "user") => scope == "machine"
        ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), "heirowInstaller", "state")
        : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SocketJack", "heirowInstaller", "state");

    public static bool IsElevated => !OperatingSystem.IsWindows() || new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static IReadOnlyList<InstalledProduct> ListInstalled(string? stateRoot = null)
    {
        var roots = stateRoot is not null ? [Path.GetFullPath(stateRoot)] : new[] { DefaultStateRoot(), DefaultStateRoot("machine") }.Distinct(StringComparer.OrdinalIgnoreCase);
        var products = new List<InstalledProduct>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            PackageSafety.EnsureNoReparsePoints(root);
            var knownMachineRoot = Path.GetFullPath(root).Equals(Path.GetFullPath(DefaultStateRoot("machine")), StringComparison.OrdinalIgnoreCase);
            using var machineState = OperatingSystem.IsWindows() && knownMachineRoot ? MachineStateSecurity.Acquire(root, create: false) : null;
            if (OperatingSystem.IsWindows() && machineState is { RootExists: false }) continue;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (Path.GetFileName(directory).StartsWith('.')) continue;
                var receipt = Path.Combine(directory, "receipt.json");
                if (!File.Exists(receipt)) continue;
                try
                {
                    var product = ReadReceipt(receipt, knownMachineRoot ? "machine" : null);
                    if (product is not null) products.Add(product);
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException) { }
            }
        }
        return products.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public InstallPlan Plan(PackageReader package, InstallOptions? options = null)
    {
        options ??= new();
        if (options.Isolated && (options.StateRoot is null || options.TargetDirectory is null))
            throw new ArgumentException("Isolated validation requires explicit --state-root and --target directories.");
        var manifest = package.Manifest;
        PackageSafety.ValidateManifest(manifest, true);
        package.VerifySignature(options.TrustedPublisherKeySha256, options.RequireSignature);
        var scope = options.Scope ?? manifest.Scope;
        if (scope is not ("user" or "machine")) throw new InvalidDataException("Scope must be user or machine.");
        var stateRoot = GetStateRoot(options, scope);
        var previous = FindInstalled(manifest.Id, options, scope);
        if (previous?.PublisherKeySha256 is { } pinnedKey && !PackageSafety.HashEquals(pinnedKey, package.PublisherKeySha256 ?? ""))
            throw new CryptographicException("This update does not match the installed product's pinned publisher key.");
        if (previous is not null && !previous.Scope.Equals(scope, StringComparison.Ordinal)) throw new InvalidOperationException("Changing installation scope requires uninstalling the existing product first.");
        if (previous is not null && !options.AllowDowngrade && PackageVersion.Parse(manifest.Version).CompareTo(PackageVersion.Parse(previous.Version)) < 0)
            throw new InvalidOperationException($"Downgrade from {previous.Version} to {manifest.Version} is blocked. Pass --allow-downgrade to opt in.");
        var reportedMsiDirectory = manifest.LegacyMsi is not null ? WindowsIntegration.GetMsiInstallLocation(previous?.MsiProductCode ?? manifest.LegacyMsi.ProductCode) : null;
        var target = options.TargetDirectory ?? reportedMsiDirectory ?? previous?.InstallDirectory;
        if (string.IsNullOrWhiteSpace(target)) target = manifest.InstallDirectory;
        if (string.IsNullOrWhiteSpace(target)) target = scope == "machine"
            ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), manifest.Id)
            : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Programs", manifest.Id);
        target = Path.GetFullPath(Expand(target, ""));
        if (Path.GetDirectoryName(target) is null) throw new InvalidDataException("A volume root cannot be an installation directory.");
        PackageSafety.EnsureNoReparsePoints(target);
        if (IsWithin(target, stateRoot) || IsWithin(stateRoot, target)) throw new InvalidDataException("Installation and installer-state directories must be separate.");
        if (previous is not null && !previous.UseOriginalInstallDirectory && !Path.GetFullPath(previous.InstallDirectory).Equals(target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Changing the installation directory requires uninstalling the existing product first.");
        var selected = options.Features is null
            ? manifest.Features.Where(x => x.Default || x.Required).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : options.Features.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in manifest.Features.Where(x => x.Required)) selected.Add(feature.Id);
        if (selected.Any(x => !manifest.Features.Any(f => f.Id.Equals(x, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("An unknown feature was selected.");
        if (scope == "user" && (manifest.Services.Any(x => IsSelected(x.Feature, selected)) ||
            manifest.Registry.Any(x => x.Hive == "machine" && IsSelected(x.Feature, selected))))
            throw new InvalidDataException("Machine registry values and Windows services require machine installation scope. Select --scope machine.");
        var companionEnabled = manifest.UpdateCompanion is { } companion && IsSelected(companion.Feature, selected);
        if (companionEnabled && package.PublisherKeySha256 is null)
            throw new CryptographicException("The optional app-local updater requires a signed package and pinned publisher identity.");
        if (companionEnabled && manifest.LegacyExecutable is { } legacyExecutable)
        {
            BackendMaintenance.ValidateDefinition(legacyExecutable.MaintenanceRegistration ?? throw new InvalidDataException("Select application updates only after authoring the executable backend's exact maintenanceRegistration."), scope);
            if (legacyExecutable.InstallDirectoryArgument is null)
            {
                var fixedDirectory = !string.IsNullOrWhiteSpace(manifest.InstallDirectory) ? Path.GetFullPath(Expand(manifest.InstallDirectory, ""))
                    : previous is { UseOriginalInstallDirectory: false } ? Path.GetFullPath(previous.InstallDirectory) : null;
                if (fixedDirectory is null || !fixedDirectory.Equals(target, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("This executable backend has a fixed application directory. Author its exact installDirectory, or an actually supported installDirectoryArgument before selecting a different target.");
            }
        }
        if (companionEnabled && (manifest.LegacyMsi is not null || manifest.LegacyExecutable is not null) && !options.Isolated && !options.RegisterProduct)
            throw new InvalidDataException("A legacy backend with application updates requires its own registered heirowInstaller maintenance host.");
        var files = manifest.Payload.Where(x => x.Feature is null || selected.Contains(x.Feature)).ToList();
        if (manifest.LegacyMsi is { } msi && !files.Any(x => x.Path.Equals(msi.PayloadPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The MSI payload cannot be excluded by feature selection.");
        if (manifest.LegacyExecutable is { } executable && !files.Any(x => x.Path.Equals(executable.PayloadPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The executable installer cannot be excluded by feature selection.");
        foreach (var file in files) _ = PackageSafety.UnderRoot(target, file.Path);
        var plan = new InstallPlan
        {
            Manifest = manifest, InstallDirectory = target, Scope = scope, Features = selected.ToList(), Files = files,
            UpdateCompanionEnabled = companionEnabled,
            TotalBytes = files.Sum(x => x.Size), IsUpgrade = previous is not null,
            PreviousVersion = previous?.Version, RequiresElevation = scope == "machine" || manifest.Services.Any(x => IsSelected(x.Feature, selected)) || manifest.Registry.Any(x => x.Hive == "machine" && IsSelected(x.Feature, selected))
        };
        plan.UseOriginalInstallDirectory = (manifest.LegacyMsi is not null || manifest.LegacyExecutable is not null) &&
            options.TargetDirectory is null && string.IsNullOrWhiteSpace(manifest.InstallDirectory);
        if (companionEnabled && plan.UseOriginalInstallDirectory && reportedMsiDirectory is null && (previous is null || previous.UseOriginalInstallDirectory))
            throw new InvalidDataException("The original installer's application directory is unknown. Author its exact installDirectory/property or pass an explicit --target before selecting the app-local updater.");
        plan.Operations.Add(manifest.LegacyMsi is null ? $"Install {files.Count} verified files" : "Run Windows Installer with the original MSI database");
        if (plan.UseOriginalInstallDirectory) plan.Operations.Add("Preserve the original installer's authored destination");
        if (manifest.Registry.Count > 0) plan.Operations.Add($"Configure {manifest.Registry.Count} registry values");
        if (manifest.Shortcuts.Count > 0) plan.Operations.Add($"Create {manifest.Shortcuts.Count} shortcuts");
        if (manifest.Environment.Count > 0) plan.Operations.Add($"Configure {manifest.Environment.Count} environment values");
        if (manifest.Services.Count > 0) plan.Operations.Add($"Install {manifest.Services.Count} Windows services");
        if (manifest.Prerequisites.Count > 0) plan.Operations.Add("Check prerequisites; external installers manage their own rollback");
        if (companionEnabled) plan.Operations.Add("Install the optional app-local updater; check for updates after the application exits");
        foreach (var configuration in manifest.Modules)
        {
            if (!modules.TryGetValue(configuration.Key, out var module)) throw new NotSupportedException("This installer does not have required module: " + configuration.Key);
            plan.Operations.AddRange(module.Plan(configuration.Value, new(plan, null, options.Isolated)));
        }
        return plan;
    }

    public async Task<InstallResult> InstallAsync(PackageReader package, InstallOptions? options = null,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        var plan = Plan(package, options);
        if (plan.RequiresElevation && !IsElevated && !options.Isolated) throw new UnauthorizedAccessException("This installation requires an elevated process. Run the installer as administrator.");
        await EnsureApplicationsClosedAsync(plan, options, "install", progress, cancellationToken);
        var root = GetStateRoot(options, plan.Scope);
        using var machineState = OperatingSystem.IsWindows() && plan.Scope == "machine" && !options.Isolated ? MachineStateSecurity.Acquire(root, create: true) : null;
        if (machineState is null) Directory.CreateDirectory(root);
        using var operationLock = AcquireLock(root, package.Manifest.Id);
        cancellationToken.ThrowIfCancellationRequested();
        // Re-evaluate after acquiring the cross-process product lock.
        plan = Plan(package, options);
        var previous = FindInstalled(package.Manifest.Id, options, plan.Scope);
        if (options.CompanionStopAlreadyHandled && previous is null)
            throw new InvalidOperationException("The staged update's application is no longer installed.");
        if (options.CompanionStopAlreadyHandled) ValidateStagedApplication(previous!);
        var cache = PackageSafety.UnderRoot(root, package.Manifest.Id);
        var txRoot = Path.Combine(root, ".transactions", Guid.NewGuid().ToString("N"));
        using var transaction = new InstallTransaction(txRoot);
        using var externalMedia = package.Manifest.LegacyMsi is not null || package.Manifest.LegacyExecutable is not null
            ? new InstallTransaction(Path.Combine(txRoot, "external-media")) : null;
        var extracted = Path.Combine(txRoot, "payload");
        await package.VerifyAsync(progress, cancellationToken);
        await package.ExtractVerifiedAsync(extracted, null, progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureApplicationsClosedAsync(plan, options, "install", progress, cancellationToken);
        if (previous is not null && !options.CompanionStopAlreadyHandled) await StopCompanionAsync(previous, cancellationToken);
        Log(options, $"Installing {package.Manifest.Id} {package.Manifest.Version} to {plan.InstallDirectory}");
        var product = new InstalledProduct
        {
            Id = package.Manifest.Id, Name = package.Manifest.Name, Version = package.Manifest.Version,
            Publisher = package.Manifest.Publisher, InstallDirectory = plan.InstallDirectory, Scope = plan.Scope,
            InstalledAt = DateTimeOffset.UtcNow, UpdateUrl = package.Manifest.UpdateUrl,
            PublisherKeySha256 = package.PublisherKeySha256,
            ReceiptPath = Path.Combine(cache, "receipt.json"), Features = plan.Features,
            Isolated = options.Isolated, IsMsi = package.Manifest.LegacyMsi is not null,
            IsExecutable = package.Manifest.LegacyExecutable is not null,
            UseOriginalInstallDirectory = plan.UseOriginalInstallDirectory,
            MsiProductCode = package.Manifest.LegacyMsi?.ProductCode
        };
        if (plan.UpdateCompanionEnabled && (product.IsMsi || product.IsExecutable) && !options.Isolated)
        {
            product.UninstallerPath = CacheUninstaller(options, cache, transaction) ?? previous?.UninstallerPath;
            if (product.UninstallerPath is null || !File.Exists(product.UninstallerPath)) throw new InvalidOperationException("A selected backend updater requires an available trusted maintenance host. Supply InstallOptions.InstallerPath.");
        }
        var reboot = await WindowsIntegration.InstallPrerequisitesAsync(package.Manifest, plan, extracted, options, progress, cancellationToken);
        if (previous is not null && (previous.IsMsi != product.IsMsi || previous.IsExecutable != product.IsExecutable))
            throw new InvalidOperationException("Changing installation backend requires uninstalling the existing product first.");
        var digest = PackageSafety.HashFile(package.PackagePath);
        await EnsureApplicationsClosedAsync(plan, options, "install", progress, cancellationToken);
        if (product.IsMsi || product.IsExecutable)
            product.LegacySourceDirectory = PrepareLegacySource(package.Manifest, extracted, cache, digest, externalMedia!);
        if (OperatingSystem.IsWindows() && machineState is not null) MachineStateSecurity.ValidateExistingRoot(root);
        if (product.IsMsi)
        {
            var msi = package.Manifest.LegacyMsi!;
            var msiFile = PackageSafety.UnderRoot(product.LegacySourceDirectory!, msi.PayloadPath);
            ValidateMsiSourcePath(msiFile);
            var actualProductCode = WindowsIntegration.GetMsiProperty(msiFile, "ProductCode");
            if (!string.IsNullOrWhiteSpace(product.MsiProductCode) && Guid.Parse(product.MsiProductCode) != Guid.Parse(actualProductCode))
                throw new InvalidDataException("The authored MSI ProductCode does not match its verified original database.");
            product.MsiProductCode = actualProductCode;
            var exit = await WindowsIntegration.RunMsiAsync("install", msiFile, product.MsiProductCode, msi, plan, options, cancellationToken);
            reboot |= exit is 3010 or 1641;
            externalMedia!.Commit();
            cancellationToken = CancellationToken.None;
        }
        else if (product.IsExecutable)
        {
            var exit = await WindowsIntegration.RunExecutableAsync("install", package.Manifest.LegacyExecutable!, product.LegacySourceDirectory!, plan.InstallDirectory, options, cancellationToken);
            reboot |= exit is 3010 or 1641;
            externalMedia!.Commit();
            cancellationToken = CancellationToken.None;
        }
        else
        {
            if (previous is { IsMsi: true }) throw new InvalidOperationException("Migrating an MSI product to native payload installation requires uninstalling the MSI first.");
            if (previous is not null) await RestoreModulesAsync(previous, transaction, options, cancellationToken);
            var desired = plan.Files.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (plan.UpdateCompanionEnabled) desired.Add(package.Manifest.UpdateCompanion!.ConfigurationPath);
            if (previous is not null)
                foreach (var removed in previous.Files.Where(x => !desired.Contains(x.Path))) RestoreOwnedFile(previous, removed, cache, transaction);
            for (var i = 0; i < plan.Files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = plan.Files[i];
                var destination = PackageSafety.UnderRoot(plan.InstallDirectory, file.Path);
                var previousFile = previous?.Files.SingleOrDefault(x => x.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
                string? original = previousFile?.OriginalBackup;
                if (previousFile is null && File.Exists(destination))
                {
                    original = "originals/" + Guid.NewGuid().ToString("N");
                    transaction.ReplaceFile(PackageSafety.UnderRoot(cache, original), destination);
                }
                transaction.ReplaceFile(destination, PackageSafety.UnderRoot(extracted, file.Path));
                product.Files.Add(new() { Path = file.Path, Sha256 = file.Sha256, OriginalBackup = original });
                progress?.Report(new("install", file.Path, i + 1, plan.Files.Count));
            }
            if (plan.UpdateCompanionEnabled) InstallCompanionConfiguration(package.Manifest, plan, product, previous, root, cache, transaction);
            product.Modules = await WindowsIntegration.ApplyModulesAsync(package.Manifest, plan, transaction, options, cancellationToken);
            foreach (var configuration in package.Manifest.Modules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = await modules[configuration.Key].InstallAsync(configuration.Value, new(plan, transaction, options.Isolated), cancellationToken);
                snapshot.Kind = "extension:" + configuration.Key;
                product.Modules.Add(snapshot);
            }
        }
        if (product.IsMsi || product.IsExecutable)
        {
            if (product.IsMsi && WindowsIntegration.GetMsiInstallLocation(product.MsiProductCode) is { } installedDirectory)
            {
                plan.InstallDirectory = installedDirectory; product.InstallDirectory = installedDirectory;
            }
            InstallLegacyCompanion(package.Manifest, plan, product, previous, root, cache, extracted, transaction);
            if (OperatingSystem.IsWindows() && !options.Isolated)
                product.BackendRegistrations = BackendMaintenance.Configure(product, previous, plan, transaction);
        }
        cancellationToken.ThrowIfCancellationRequested();
        product.PackagePath = PackageSafety.UnderRoot(cache, "packages/" + digest + ".hinst");
        if (!File.Exists(product.PackagePath)) transaction.ReplaceFile(product.PackagePath, package.PackagePath);
        else if (!PackageSafety.HashEquals(PackageSafety.HashFile(product.PackagePath), digest)) throw new IOException("Cached package hash mismatch.");
        product.UninstallerPath ??= CacheUninstaller(options, cache, transaction);
        product.RegisteredProduct = options.RegisterProduct && !options.Isolated && (!(product.IsMsi || product.IsExecutable) || plan.UpdateCompanionEnabled) && product.UninstallerPath is not null;
        if (product.RegisteredProduct) WindowsIntegration.RegisterProduct(product, root, transaction, package.Manifest);
        else if (previous is { RegisteredProduct: true }) WindowsIntegration.UnregisterProduct(previous, transaction);
        var receiptTemporary = Path.Combine(txRoot, "new-receipt.json");
        await File.WriteAllTextAsync(receiptTemporary, JsonSerializer.Serialize(product, InstallerJson.Options), cancellationToken);
        transaction.ReplaceFile(product.ReceiptPath, receiptTemporary);
        if (OperatingSystem.IsWindows() && machineState is not null) MachineStateSecurity.ValidateExistingRoot(root);
        transaction.Commit();
        progress?.Report(new("complete", "Installation completed", 1, 1));
        Log(options, $"Installed {product.Id} {product.Version}; reboot required: {reboot}");
        if (options.Launch && package.Manifest.EntryPoint.Length > 0)
        {
            var entry = PackageSafety.UnderRoot(plan.InstallDirectory, plan.UpdateCompanionEnabled ? package.Manifest.UpdateCompanion!.ExecutablePath : package.Manifest.EntryPoint);
            if (File.Exists(entry))
            {
                var start = new ProcessStartInfo(entry) { UseShellExecute = true, WorkingDirectory = plan.InstallDirectory };
                if (plan.UpdateCompanionEnabled)
                {
                    start.ArgumentList.Add("launch"); start.ArgumentList.Add("--config");
                    start.ArgumentList.Add(PackageSafety.UnderRoot(plan.InstallDirectory, package.Manifest.UpdateCompanion!.ConfigurationPath));
                }
                Process.Start(start);
            }
        }
        return Result(product, reboot);
    }

    public async Task<InstallResult> RepairAsync(string productId, InstallOptions? options = null,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        var product = RequireInstalled(productId, options);
        using var machineState = GuardProductState(product, options);
        options.TargetDirectory = product.UseOriginalInstallDirectory ? null : product.InstallDirectory;
        options.Scope = product.Scope;
        options.Features ??= product.Features;
        options.Isolated = product.Isolated;
        options.RegisterProduct = product.RegisteredProduct || product.IsMsi || product.IsExecutable;
        if (product.IsMsi || product.IsExecutable)
        {
            if (product.Scope == "machine" && !IsElevated && !product.Isolated) throw new UnauthorizedAccessException("Repair requires administrator privileges.");
            using var package = PackageReader.Open(product.PackagePath);
            package.VerifySignature(product.PublisherKeySha256, product.PublisherKeySha256 is not null);
            var cache = Path.GetDirectoryName(product.ReceiptPath)!;
            var root = Path.GetDirectoryName(cache)!;
            using var operationLock = AcquireLock(root, product.Id);
            cancellationToken.ThrowIfCancellationRequested();
            var previous = ReadReceipt(product.ReceiptPath, product.Scope, options.Isolated) ?? throw new InvalidDataException("Installation receipt is missing.");
            var plan = Plan(package, options);
            await EnsureApplicationsClosedAsync(plan, options, "repair", progress, cancellationToken);
            using var transaction = new InstallTransaction(Path.Combine(root, ".transactions", Guid.NewGuid().ToString("N")));
            using var externalMedia = new InstallTransaction(Path.Combine(transaction.Root, "external-media"));
            await package.VerifyAsync(progress, cancellationToken);
            var extracted = Path.Combine(transaction.Root, "payload");
            await package.ExtractVerifiedAsync(extracted, null, progress, cancellationToken);
            await EnsureApplicationsClosedAsync(plan, options, "repair", progress, cancellationToken);
            await StopCompanionAsync(previous, cancellationToken);
            product.LegacySourceDirectory = PrepareLegacySource(package.Manifest, extracted, cache, PackageSafety.HashFile(product.PackagePath), externalMedia);
            await EnsureApplicationsClosedAsync(plan, options, "repair", progress, cancellationToken);
            var exit = product.IsMsi
                ? await WindowsIntegration.RunMsiAsync("repair", ValidateMsiSourcePath(PackageSafety.UnderRoot(product.LegacySourceDirectory, package.Manifest.LegacyMsi!.PayloadPath)), product.MsiProductCode, package.Manifest.LegacyMsi,
                    plan, options, cancellationToken)
                : await WindowsIntegration.RunExecutableAsync("repair", package.Manifest.LegacyExecutable!, product.LegacySourceDirectory, product.InstallDirectory, options, cancellationToken);
            externalMedia.Commit();
            product.Features = plan.Features; product.Files = [];
            if (product.IsMsi && WindowsIntegration.GetMsiInstallLocation(product.MsiProductCode) is { } installedDirectory)
                plan.InstallDirectory = installedDirectory;
            product.InstallDirectory = plan.InstallDirectory;
            InstallLegacyCompanion(package.Manifest, plan, product, previous, root, cache, extracted, transaction);
            if (OperatingSystem.IsWindows() && !options.Isolated)
            {
                product.BackendRegistrations = BackendMaintenance.Configure(product, previous, plan, transaction);
                if (plan.UpdateCompanionEnabled)
                {
                    product.UninstallerPath = CacheUninstaller(options, cache, transaction) ?? previous.UninstallerPath;
                    if (product.UninstallerPath is null || !File.Exists(product.UninstallerPath)) throw new InvalidOperationException("The backend companion's maintenance host is missing.");
                    product.RegisteredProduct = true;
                    WindowsIntegration.RegisterProduct(product, root, transaction, package.Manifest);
                }
                else if (previous.RegisteredProduct)
                {
                    WindowsIntegration.UnregisterProduct(previous, transaction); product.RegisteredProduct = false;
                }
            }
            var receipt = Path.Combine(transaction.Root, "receipt.json");
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(product, InstallerJson.Options), CancellationToken.None);
            transaction.ReplaceFile(product.ReceiptPath, receipt);
            transaction.Commit();
            return Result(product, exit is 3010 or 1641);
        }
        using (var package = PackageReader.Open(product.PackagePath)) return await InstallAsync(package, options, progress, cancellationToken);
    }

    public async Task<InstallResult> UninstallAsync(string productId, InstallOptions? options = null,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        var product = RequireInstalled(productId, options);
        if (product.Scope == "machine" && !IsElevated && !product.Isolated) throw new UnauthorizedAccessException("Uninstall requires administrator privileges.");
        var root = Path.GetDirectoryName(Path.GetDirectoryName(product.ReceiptPath))!;
        using var machineState = GuardProductState(product, options);
        using var operationLock = AcquireLock(root, product.Id);
        cancellationToken.ThrowIfCancellationRequested();
        product = ReadReceipt(product.ReceiptPath, product.Scope, options.Isolated) ?? throw new InvalidDataException("Installation receipt is missing.");
        using (var guardedPackage = PackageReader.Open(product.PackagePath))
        {
            guardedPackage.VerifySignature(product.PublisherKeySha256, product.PublisherKeySha256 is not null);
            await RunningApplications.EnsureClosedAsync(RunningApplicationTarget.FromManifest(guardedPackage.Manifest, product.InstallDirectory, "uninstall", product.Features),
                options.RunningApplicationsPrompt, cancellationToken);
            progress?.Report(new("applications", "Applications closed. Continuing uninstall.", 1, 1));
        }
        await StopCompanionAsync(product, cancellationToken);
        var cache = Path.GetDirectoryName(product.ReceiptPath)!;
        using var transaction = new InstallTransaction(Path.Combine(root, ".transactions", Guid.NewGuid().ToString("N")));
        options.Isolated = product.Isolated;
        var reboot = false;
        var backendAlreadyRemoved = OperatingSystem.IsWindows() && (product.IsMsi && !WindowsIntegration.IsMsiInstalled(product.MsiProductCode) ||
            product.IsExecutable && product.BackendRegistrations.Count > 0 && !BackendMaintenance.HasRegistration(product));
        if (product.IsExecutable && !backendAlreadyRemoved)
        {
            using var package = PackageReader.Open(product.PackagePath);
            package.VerifySignature(product.PublisherKeySha256, product.PublisherKeySha256 is not null);
            var extracted = Path.Combine(transaction.Root, "external");
            await package.ExtractVerifiedAsync(extracted, null, progress, cancellationToken);
            product.LegacySourceDirectory = PrepareLegacySource(package.Manifest, extracted, cache, PackageSafety.HashFile(product.PackagePath), transaction);
            await RunningApplications.EnsureClosedAsync(RunningApplicationTarget.FromManifest(package.Manifest, product.InstallDirectory, "uninstall", product.Features),
                options.RunningApplicationsPrompt, cancellationToken);
            var exit = await WindowsIntegration.RunExecutableAsync("uninstall", package.Manifest.LegacyExecutable!, product.LegacySourceDirectory, product.InstallDirectory, options, cancellationToken);
            reboot = exit is 3010 or 1641;
        }
        else if (product.IsMsi && !backendAlreadyRemoved)
        {
            using var package = PackageReader.Open(product.PackagePath);
            var exit = await WindowsIntegration.RunMsiAsync("uninstall", product.PackagePath, product.MsiProductCode, package.Manifest.LegacyMsi!,
                new() { InstallDirectory = product.InstallDirectory, Features = product.Features }, options, cancellationToken);
            reboot = exit is 3010 or 1641;
        }
        else if (!product.IsMsi && !product.IsExecutable)
        {
            await RestoreModulesAsync(product, transaction, options, cancellationToken);
            for (var i = 0; i < product.Files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RestoreOwnedFile(product, product.Files[i], cache, transaction);
                progress?.Report(new("uninstall", product.Files[i].Path, i + 1, product.Files.Count));
            }
        }
        if (product.IsMsi || product.IsExecutable)
        {
            foreach (var file in product.Files) RestoreOwnedFile(product, file, cache, transaction);
            if (OperatingSystem.IsWindows() && !options.Isolated) BackendMaintenance.Restore(product.BackendRegistrations, product.Scope, transaction);
        }
        if (product.RegisteredProduct) WindowsIntegration.UnregisterProduct(product, transaction);
        transaction.DeleteFile(product.ReceiptPath);
        transaction.Commit();
        foreach (var directory in product.Files.Select(x => Path.GetDirectoryName(PackageSafety.UnderRoot(product.InstallDirectory, x.Path))!).Distinct().OrderByDescending(x => x.Length))
            RemoveEmptyDirectories(directory, product.InstallDirectory);
        try { PackageSafety.EnsureNoReparsePoints(cache); Directory.Delete(cache, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (product.IsMsi || product.IsExecutable)
        {
            var legacy = LegacyProductRoot(root, product.Id);
            try { PackageSafety.EnsureNoReparsePoints(legacy); if (Directory.Exists(legacy)) Directory.Delete(legacy, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Log(options, $"Uninstalled {product.Id}");
        progress?.Report(new("complete", "Uninstall completed", 1, 1));
        return Result(product, reboot);
    }

    private static async Task EnsureApplicationsClosedAsync(InstallPlan plan, InstallOptions options, string operation,
        IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        await RunningApplications.EnsureClosedAsync(RunningApplicationTarget.FromPlan(plan, operation), options.RunningApplicationsPrompt, cancellationToken);
        progress?.Report(new("applications", "Applications closed. Continuing " + operation + ".", 1, 1));
    }

    private static async Task StopCompanionAsync(InstalledProduct product, CancellationToken cancellationToken)
    {
        // Read only signed authored metadata; never execute an installed updater as administrator.
        using var package = PackageReader.Open(product.PackagePath);
        package.VerifySignature(product.PublisherKeySha256, product.PublisherKeySha256 is not null);
        if (package.Manifest.UpdateCompanion is { } companion && product.Features.Contains(companion.Feature, StringComparer.OrdinalIgnoreCase))
            await UpdateCompanionControl.StopAsync(product.InstallDirectory, companion.ExecutablePath, cancellationToken);
    }

    private static void ValidateStagedApplication(InstalledProduct product)
    {
        using var installed = PackageReader.Open(product.PackagePath);
        installed.VerifySignature(product.PublisherKeySha256, requireSignature: true);
        if (installed.Manifest.EntryPoint.Length == 0 || !File.Exists(PackageSafety.UnderRoot(product.InstallDirectory, installed.Manifest.EntryPoint)) ||
            product.IsMsi && !WindowsIntegration.IsMsiInstalled(product.MsiProductCode) ||
            OperatingSystem.IsWindows() && product.IsExecutable && product.BackendRegistrations.Count > 0 && !BackendMaintenance.HasRegistration(product))
            throw new InvalidOperationException("The staged update's application was removed. Run its installer to install it again.");
    }

    private static string PrepareLegacySource(PackageManifest manifest, string extracted, string cache, string digest, InstallTransaction transaction)
    {
        var stable = PackageSafety.UnderRoot(LegacyProductRoot(Path.GetDirectoryName(cache)!, manifest.Id), digest[..24]);
        foreach (var file in manifest.Payload)
        {
            var target = PackageSafety.UnderRoot(stable, file.Path);
            if (!File.Exists(target) || !PackageSafety.HashEquals(PackageSafety.HashFile(target), file.Sha256))
                transaction.ReplaceFile(target, PackageSafety.UnderRoot(extracted, file.Path));
        }
        return stable;
    }
    private static string LegacyProductRoot(string stateRoot, string id)
    {
        PackageSafety.ValidateId(id);
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id.ToLowerInvariant()))).ToLowerInvariant();
        return PackageSafety.UnderRoot(stateRoot, ".legacy/" + hash[..16]);
    }
    private static string ValidateMsiSourcePath(string path)
    {
        if (path.Length >= 260) throw new PathTooLongException("Windows Installer needs a durable MSI source path shorter than 260 characters. Choose a shorter --state-root or MSI filename.");
        return path;
    }

    private async Task RestoreModulesAsync(InstalledProduct product, InstallTransaction transaction, InstallOptions options, CancellationToken token)
    {
        ValidateReceiptIntegrationScope(product);
        var plan = new InstallPlan { InstallDirectory = product.InstallDirectory, Scope = product.Scope, Features = product.Features };
        foreach (var snapshot in product.Modules.Where(x => x.Kind.StartsWith("extension:", StringComparison.Ordinal)).Reverse())
        {
            var id = snapshot.Kind["extension:".Length..];
            if (!modules.TryGetValue(id, out var module)) throw new NotSupportedException("Required uninstall module is unavailable: " + id);
            await module.UninstallAsync(snapshot, new(plan, transaction, options.Isolated), token);
        }
        await WindowsIntegration.RestoreModulesAsync(product, transaction, options, token);
    }

    private static void InstallCompanionConfiguration(PackageManifest manifest, InstallPlan plan, InstalledProduct product,
        InstalledProduct? previous, string stateRoot, string cache, InstallTransaction transaction)
    {
        var definition = manifest.UpdateCompanion!;
        var destination = PackageSafety.UnderRoot(plan.InstallDirectory, definition.ConfigurationPath);
        var previousFile = previous?.Files.SingleOrDefault(x => x.Path.Equals(definition.ConfigurationPath, StringComparison.OrdinalIgnoreCase));
        string? original = previousFile?.OriginalBackup;
        if (previousFile is null && File.Exists(destination))
        {
            original = "originals/" + Guid.NewGuid().ToString("N");
            transaction.ReplaceFile(PackageSafety.UnderRoot(cache, original), destination);
        }
        var configuration = new UpdateCompanionConfiguration
        {
            ProductId = product.Id, Name = product.Name, CurrentVersion = product.Version, FeedUrl = manifest.UpdateUrl!,
            InstallDirectory = product.InstallDirectory, StateRoot = stateRoot, Scope = product.Scope,
            PublisherKeySha256 = product.PublisherKeySha256!, Features = product.Features, EntryPoint = manifest.EntryPoint,
            Mode = definition.Mode, Isolated = product.Isolated, CredentialOrigin = definition.CredentialOrigin,
            AuthApplication = definition.AuthApplication,
            CookieEnvironmentVariable = definition.CookieEnvironmentVariable, TokenEnvironmentVariable = definition.TokenEnvironmentVariable,
            AffectedResourceFiles = RunningApplicationTarget.FromManifest(manifest, product.InstallDirectory, features: product.Features).AffectedResourceFiles.ToList()
        };
        var temporary = Path.Combine(transaction.Root, "app-updater-config.json");
        File.WriteAllText(temporary, JsonSerializer.Serialize(configuration, InstallerJson.Options));
        transaction.ReplaceFile(destination, temporary);
        product.Files.Add(new() { Path = definition.ConfigurationPath, Sha256 = PackageSafety.HashFile(temporary), OriginalBackup = original });
    }

    private static void InstallLegacyCompanion(PackageManifest manifest, InstallPlan plan, InstalledProduct product, InstalledProduct? previous,
        string root, string cache, string extracted, InstallTransaction transaction)
    {
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (plan.UpdateCompanionEnabled)
        {
            var companion = manifest.UpdateCompanion!;
            if (IsWithin(plan.InstallDirectory, root) || IsWithin(root, plan.InstallDirectory)) throw new InvalidDataException("The original installer reported a destination overlapping installer state.");
            if (!File.Exists(PackageSafety.UnderRoot(plan.InstallDirectory, manifest.EntryPoint)))
                throw new InvalidDataException("The authored application entry point was not found in the original installer's resolved directory; the updater was not copied.");
            desired.Add(companion.ExecutablePath); desired.Add(companion.ConfigurationPath);
        }
        if (previous is not null)
            foreach (var file in previous.Files.Where(x => !desired.Contains(x.Path))) RestoreOwnedFile(previous, file, cache, transaction);
        if (!plan.UpdateCompanionEnabled) return;
        var definition = manifest.UpdateCompanion!;
        var destination = PackageSafety.UnderRoot(plan.InstallDirectory, definition.ExecutablePath);
        var previousFile = previous?.Files.SingleOrDefault(x => x.Path.Equals(definition.ExecutablePath, StringComparison.OrdinalIgnoreCase));
        string? original = previousFile?.OriginalBackup;
        if (previousFile is null && File.Exists(destination))
        {
            original = "originals/" + Guid.NewGuid().ToString("N");
            transaction.ReplaceFile(PackageSafety.UnderRoot(cache, original), destination);
        }
        transaction.ReplaceFile(destination, PackageSafety.UnderRoot(extracted, definition.ExecutablePath));
        product.Files.Add(new() { Path = definition.ExecutablePath, Sha256 = PackageSafety.HashFile(destination), OriginalBackup = original });
        InstallCompanionConfiguration(manifest, plan, product, previous, root, cache, transaction);
    }

    private static void RestoreOwnedFile(InstalledProduct product, InstalledFile file, string cache, InstallTransaction transaction)
    {
        var destination = PackageSafety.UnderRoot(product.InstallDirectory, file.Path);
        if (file.OriginalBackup is { } original)
        {
            var backup = PackageSafety.UnderRoot(cache, original);
            if (!File.Exists(backup)) throw new IOException($"Original file backup is missing: {file.Path}");
            transaction.ReplaceFile(destination, backup);
        }
        else transaction.DeleteFile(destination);
    }

    private static string? CacheUninstaller(InstallOptions options, string cache, InstallTransaction transaction)
    {
        var source = options.InstallerPath;
        if (source is null)
        {
            var process = System.Environment.ProcessPath;
            if (process is not null && Path.GetFileNameWithoutExtension(process).StartsWith("heirowinstaller", StringComparison.OrdinalIgnoreCase)) source = process;
        }
        if (source is null || !File.Exists(source)) return null;
        source = Path.GetFullPath(source);
        var destination = PackageSafety.UnderRoot(cache, "uninstaller/" + Path.GetFileName(source));
        if (!source.Equals(destination, StringComparison.OrdinalIgnoreCase)) transaction.ReplaceFile(destination, source);
        // Framework-dependent builds need their managed entry point and the Core assembly beside the apphost.
        var stem = Path.GetFileNameWithoutExtension(source);
        var siblings = new[] { stem + ".dll", stem + ".deps.json", stem + ".runtimeconfig.json" }
            .Concat(Directory.EnumerateFiles(Path.GetDirectoryName(source)!, "*.dll", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var sibling in siblings)
        {
            var input = Path.Combine(Path.GetDirectoryName(source)!, sibling);
            var output = PackageSafety.UnderRoot(cache, "uninstaller/" + sibling);
            if (File.Exists(input) && !input.Equals(output, StringComparison.OrdinalIgnoreCase)) transaction.ReplaceFile(output, input);
        }
        return destination;
    }

    private static InstalledProduct? FindInstalled(string id, InstallOptions options, string scope)
    {
        var receipt = PackageSafety.UnderRoot(GetStateRoot(options, scope), id + "/receipt.json");
        return File.Exists(receipt) ? ReadReceipt(receipt, scope, options.Isolated) : null;
    }

    private static InstalledProduct RequireInstalled(string id, InstallOptions options)
    {
        PackageSafety.ValidateId(id);
        var scopes = options.Scope is null ? new[] { "user", "machine" } : [options.Scope];
        foreach (var scope in scopes)
            if (FindInstalled(id, options, scope) is { } product) return product;
        throw new InvalidOperationException($"Product is not installed: {id}");
    }

    private static InstalledProduct? ReadReceipt(string receipt, string? expectedScope = null, bool isolated = false)
    {
        PackageSafety.EnsureNoReparsePoints(receipt);
        var root = Path.GetDirectoryName(Path.GetDirectoryName(receipt))!;
        using var machineState = OperatingSystem.IsWindows() && expectedScope == "machine" && !isolated ? MachineStateSecurity.Acquire(root, create: false) : null;
        if (OperatingSystem.IsWindows() && machineState is { RootExists: false }) return null;
        var product = JsonSerializer.Deserialize<InstalledProduct>(File.ReadAllText(receipt), InstallerJson.Options);
        if (product is null) return null;
        PackageSafety.ValidateId(product.Id);
        if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(receipt)), product.Id, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Receipt product ID does not match its cache directory.");
        if (product.Scope is not ("user" or "machine")) throw new InvalidDataException("Invalid receipt scope.");
        if (expectedScope is not null && product.Scope != expectedScope) return null;
        ValidateReceiptIntegrationScope(product);
        // A custom-root listing initially reads only metadata. Re-open a non-isolated machine receipt under its validated namespace before returning trusted maintenance state.
        if (OperatingSystem.IsWindows() && expectedScope is null && product.Scope == "machine" && !product.Isolated && !isolated)
            return ReadReceipt(receipt, "machine");
        product.ReceiptPath = Path.GetFullPath(receipt);
        var cache = Path.GetDirectoryName(receipt)!;
        product.PackagePath = Path.GetFullPath(product.PackagePath);
        if (!IsWithin(product.PackagePath, cache)) throw new InvalidDataException("Cached package escapes its product directory.");
        if (product.LegacySourceDirectory is { } legacySource && !IsWithin(Path.GetFullPath(legacySource), cache) &&
            !IsWithin(Path.GetFullPath(legacySource), LegacyProductRoot(Path.GetDirectoryName(cache)!, product.Id))) throw new InvalidDataException("Legacy source escapes its product cache.");
        if (!Path.IsPathFullyQualified(product.InstallDirectory)) throw new InvalidDataException("Invalid installation directory in receipt.");
        foreach (var file in product.Files)
        {
            _ = PackageSafety.UnderRoot(product.InstallDirectory, file.Path);
            if (file.OriginalBackup is { } backup) _ = PackageSafety.UnderRoot(cache, backup);
        }
        return product;
    }

    private static void ValidateReceiptIntegrationScope(InstalledProduct product)
    {
        foreach (var snapshot in product.BackendRegistrations) BackendMaintenance.ValidateSnapshot(snapshot, product.Scope);
        if (product.Scope != "user") return;
        foreach (var snapshot in product.Modules)
            if (snapshot.Kind == "service" ||
                snapshot.Kind == "registry" && snapshot.Values.TryGetValue("hive", out var hive) && hive == "machine" ||
                snapshot.Kind == "environment" && snapshot.Values.TryGetValue("scope", out var scope) && scope == "machine")
                throw new InvalidDataException("User-scoped installation receipts cannot contain machine integration snapshots.");
    }

    internal static string Expand(string value, string installDirectory) => System.Environment.ExpandEnvironmentVariables(value
        .Replace("{InstallDir}", installDirectory, StringComparison.OrdinalIgnoreCase)
        .Replace("{LocalAppData}", System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase)
        .Replace("{AppData}", System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase)
        .Replace("{CommonAppData}", System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), StringComparison.OrdinalIgnoreCase)
        .Replace("{ProgramFiles}", System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase));

    internal static bool IsSelected(string? feature, IEnumerable<string> selected) => feature is null || selected.Contains(feature, StringComparer.OrdinalIgnoreCase);
    private static string GetStateRoot(InstallOptions options, string scope)
    {
        var root = Path.GetFullPath(options.StateRoot ?? DefaultStateRoot(scope));
        PackageSafety.EnsureNoReparsePoints(root);
        if (OperatingSystem.IsWindows() && scope == "machine" && !options.Isolated) MachineStateSecurity.ValidateExistingRoot(root);
        return root;
    }
    private static IDisposable? GuardProductState(InstalledProduct product, InstallOptions options)
    {
        if (!OperatingSystem.IsWindows() || product.Scope != "machine" || options.Isolated) return null;
        var root = Path.GetDirectoryName(Path.GetDirectoryName(product.ReceiptPath))!;
        var lease = MachineStateSecurity.Acquire(root, create: false);
        if (lease.RootExists) return lease;
        lease.Dispose();
        throw new InvalidDataException("The protected machine installation state is missing.");
    }
    private static FileStream AcquireLock(string root, string id)
    {
        var directory = Path.Combine(root, ".locks");
        PackageSafety.EnsureNoReparsePoints(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id + ".lock");
        PackageSafety.EnsureNoReparsePoints(path);
        try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException($"Another installer operation is running for {id}.", ex); }
    }
    private static bool IsWithin(string child, string parent) => child.Equals(parent, StringComparison.OrdinalIgnoreCase) || child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void RemoveEmptyDirectories(string current, string boundary)
    {
        while (IsWithin(current, boundary))
        {
            try
            {
                PackageSafety.EnsureNoReparsePoints(current);
                if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any()) break;
                Directory.Delete(current);
            }
            catch (IOException) { break; } catch (UnauthorizedAccessException) { break; }
            if (current.Equals(boundary, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current)!;
        }
    }
    private static InstallResult Result(InstalledProduct product, bool reboot) => new() { ProductId = product.Id, Version = product.Version, InstallDirectory = product.InstallDirectory, ReceiptPath = product.ReceiptPath, RebootRequired = reboot };
    private static void Log(InstallOptions options, string line)
    {
        if (options.LogPath is null) return;
        var path = Path.GetFullPath(options.LogPath);
        PackageSafety.EnsureNoReparsePoints(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {line}{System.Environment.NewLine}");
    }
}
