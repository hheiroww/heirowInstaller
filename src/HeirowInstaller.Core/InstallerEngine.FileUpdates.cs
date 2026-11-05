using System.Security.Cryptography;
using System.Text.Json;

namespace HeirowInstaller.Core;

public sealed partial class InstallerEngine
{
    /// <summary>Applies a verified sparse stage directly, retaining signed metadata instead of rebuilding a full installer.</summary>
    public async Task<InstallResult> ApplyPreparedFileUpdateAsync(PreparedFileUpdate stage, InstallOptions? options = null,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default,
        RunningApplicationTarget? runningApplications = null)
    {
        ArgumentNullException.ThrowIfNull(stage); options ??= new();
        if (!stage.IsReady) throw new InvalidOperationException("The selected update files have not completed staging.");
        FileUpdateClient.BindOptions(stage.Installed, options);
        options.Features = stage.SelectedFeatures.ToList();
        using var package = PackageReader.OpenMetadata(stage.MetadataPath); package.VerifySignature(stage.Installed.PublisherKeySha256, true);
        var plan = Plan(package, options);
        if (plan.RequiresElevation && !IsElevated && !options.Isolated && !(plan.Scope == "user" && options.AdministratorRequirementsHandled))
            throw new UnauthorizedAccessException("Applying this machine-scoped file update requires administrator privileges.");
        // Network work and every source hash finish before application shutdown.
        await stage.VerifySourcesAsync(plan, progress, cancellationToken);
        options.PreparedSharedPackageDirectory = PackageSafety.UnderRoot(stage.Root, "dependencies");
        var shared = await SharedPackages.PrepareAsync(package, plan, options, progress, cancellationToken);
        var prerequisitesRoot = Path.Combine(stage.Root, "prerequisites");
        foreach (var prerequisite in package.Manifest.Prerequisites.Where(item => IsSelected(item.Feature, plan.Features) && item.Executable is { Length: > 0 }))
        {
            var file = plan.Files.SingleOrDefault(item => item.Path.Equals(prerequisite.Executable!.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("A selected prerequisite executable is absent from the selected signed payload.");
            var destination = PackageSafety.UnderRoot(prerequisitesRoot, file.Path);
            if (!File.Exists(destination))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                FileTransactionJournal.CopyDurable(stage.Source(file, plan), destination);
            }
            IncrementalPackageIO.VerifyFile(destination, file.Size, file.Sha256);
        }
        var reboot = await WindowsIntegration.InstallPrerequisitesAsync(package.Manifest, plan, prerequisitesRoot, options, progress, cancellationToken);
        var root = GetStateRoot(options, plan.Scope);
        using var machineState = OperatingSystem.IsWindows() && plan.Scope == "machine" && !options.Isolated ? MachineStateSecurity.Acquire(root, create: true) : null;
        if (machineState is null) Directory.CreateDirectory(root);
        using var operationLock = new ProductOperationLease(root, package.Manifest.Id, plan.Scope, options.Isolated, AcquireLock(root, package.Manifest.Id));
        var recovery = FileTransactionRecoveryContext.ForFileUpdate(stage, plan, options);
        await FileTransactionRecovery.RecoverAsync(recovery, options.RunningApplicationsPrompt, operationLockHeld: true, cancellationToken);
        var previous = FindInstalled(package.Manifest.Id, options, plan.Scope) ?? throw new InvalidDataException("The application was removed before update installation.");
        if (previous.Version == package.Manifest.Version && previous.Features.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(plan.Features) && FileTransactionRecovery.TargetIsInstalled(recovery))
        {
            await stage.MarkCompleteAsync(); return Result(previous, false);
        }
        stage.ValidateReceipt(previous); recovery.ValidateInstalledBase(previous);
        if (runningApplications is not null) await RunningApplications.EnsureClosedAsync(runningApplications, options.RunningApplicationsPrompt, cancellationToken);
        await EnsureApplicationsClosedAsync(plan, options, "update", progress, cancellationToken);
        if (!options.CompanionStopAlreadyHandled) await StopCompanionAsync(previous, cancellationToken);
        // A reopened application or modified file may invalidate a source after the first validation.
        await stage.VerifySourcesAsync(plan, progress, cancellationToken);
        foreach (var file in previous.Files)
        {
            var actual = await PreparedFileUpdate.ObserveAsync(PayloadDestinations.ResolveInstalled(previous, file), cancellationToken);
            if (!stage.Before.TryGetValue(file.Path, out var expected) || actual.Size != expected.Size ||
                !IncrementalPackageIO.NullableHashEquals(actual.Sha256, expected.Sha256))
                throw new IOException("An owned application file changed while its update was staged: " + file.Path);
        }
        var cache = PackageSafety.UnderRoot(root, previous.Id);
        using var transaction = new InstallTransaction(Path.Combine(root, ".transactions", Guid.NewGuid().ToString("N")), recovery);
        await stage.MarkApplyingAsync(cancellationToken);
        try
        {
            var product = new InstalledProduct
            {
                Id = package.Manifest.Id, Name = package.Manifest.Name, Version = package.Manifest.Version, Publisher = package.Manifest.Publisher,
                InstallDirectory = plan.InstallDirectory, Scope = plan.Scope, InstalledAt = DateTimeOffset.UtcNow, UpdateUrl = package.Manifest.UpdateUrl,
                PublisherKeySha256 = package.PublisherKeySha256, ReceiptPath = previous.ReceiptPath, Features = plan.Features,
                Isolated = previous.Isolated, RegisteredProduct = previous.RegisteredProduct, UninstallerPath = previous.UninstallerPath
            };
            // Keep the existing OS/module rollback semantics for native file updates.
            await RestoreModulesAsync(previous, transaction, options, cancellationToken);
            var desired = plan.Files.Where(file => !IsSharedRuntimePayload(package.Manifest, file.Path)).Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (plan.UpdateCompanionEnabled) desired.Add(package.Manifest.UpdateCompanion!.ConfigurationPath);
            if (shared is not null) desired.Add(SharedPackages.BindingFileName);
            if (SharedPackages.ProbingConfigurationPath(package.Manifest) is { } probes) desired.Add(probes);
            foreach (var removed in previous.Files.Where(file => !desired.Contains(file.Path))) RestoreOwnedFile(previous, removed, cache, transaction);
            for (var index = 0; index < plan.Files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested(); var file = plan.Files[index];
                if (IsSharedRuntimePayload(package.Manifest, file.Path)) continue;
                var destination = PayloadDestinations.Resolve(package.Manifest, plan.InstallDirectory, plan.Scope, options.Isolated, file);
                var old = previous.Files.SingleOrDefault(owned => owned.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
                if (old is null && File.Exists(destination)) throw new IOException("An unowned destination appeared during the update: " + file.Path);
                var source = stage.Source(file, plan);
                if (!destination.Equals(source, InstallerPlatform.PathComparison)) transaction.ReplaceFile(destination, source);
                product.Files.Add(new() { Path = file.Path, Sha256 = file.Sha256, TargetRoot = file.TargetRoot, OriginalBackup = old?.OriginalBackup });
                progress?.Report(new("install", file.Path, index + 1, plan.Files.Count));
            }
            if (plan.UpdateCompanionEnabled)
            {
                var companion = package.Manifest.UpdateCompanion!;
                if (companion.SharedPackageId is null)
                {
                    var runtimeFile = plan.Files.Single(file => file.Path.Equals(companion.ExecutablePath, StringComparison.OrdinalIgnoreCase));
                    var runtimeSource = stage.Source(runtimeFile, plan);
                    var localRuntime = PackageSafety.UnderRoot(Path.Combine(transaction.Root, "companion"), runtimeFile.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(localRuntime)!); File.Copy(runtimeSource, localRuntime);
                }
                InstallCompanionConfiguration(package.Manifest, plan, product, previous, root, cache, transaction, Path.Combine(transaction.Root, "companion"));
            }
            else ReleaseSharedRuntimeReference(previous, transaction);
            SharedPackages.Activate(shared, product, previous, root, transaction);
            product.Modules = await WindowsIntegration.ApplyModulesAsync(package.Manifest, plan, transaction, options, cancellationToken, product);
            foreach (var configuration in package.Manifest.Modules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = await modules[configuration.Key].InstallAsync(configuration.Value,
                    InstallerModuleContext.ForVerifiedPackage(plan, transaction, package, options, previous, productLockHeld: true), cancellationToken);
                snapshot.Kind = "extension:" + configuration.Key; product.Modules.Add(snapshot);
            }
            product.PackagePath = PackageSafety.UnderRoot(cache, "metadata/" + stage.Metadata.ManifestSha256 + ".hmeta");
            if (!File.Exists(product.PackagePath)) transaction.ReplaceFile(product.PackagePath, stage.MetadataPath);
            else
            {
                using var existing = PackageReader.OpenMetadata(product.PackagePath); existing.VerifySignature(product.PublisherKeySha256, true);
                if (!PackageSafety.HashEquals(IncrementalPackageIO.Hash(existing.ReadManifestBytes()), stage.Metadata.ManifestSha256))
                    throw new CryptographicException("The installed target metadata cache changed.");
            }
            progress?.Report(new("metadata", "Committing signed application metadata", 1, 1));
            if (product.RegisteredProduct) WindowsIntegration.RegisterProduct(product, root, transaction, package.Manifest);
            var receipt = Path.Combine(transaction.Root, "new-receipt.json");
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(product, InstallerJson.Options), cancellationToken);
            transaction.ReplaceFile(product.ReceiptPath, receipt);
            transaction.Commit();
            await stage.MarkCompleteAsync();
            progress?.Report(new("complete", "Application files updated", 1, 1));
            return Result(product, reboot);
        }
        catch { transaction.Rollback(); throw; }
    }
}
