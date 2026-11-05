using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HeirowInstaller.Core;

/// <summary>Verified immutable dependencies shared only within one installation scope. No package garbage collection is performed.</summary>
public static class SharedPackages
{
    public const string BindingFileName = ".socketjack-shared.json";

    internal static void ValidatePublisher(PackageManifest manifest, string? publisherPin)
    {
        if (manifest.SharedPackages is not { Count: > 0 }) return;
        if (publisherPin is null || manifest.SharedPackages.Any(definition => !PackageSafety.HashEquals(definition.PublisherKeySha256, publisherPin)))
            throw new CryptographicException("Every shared dependency must be pinned to the application's signing publisher.");
    }

    internal static void ValidateAuthority(PackageReader package)
    {
        var signed = JsonSerializer.Deserialize<PackageManifest>(package.ReadManifestBytes(), InstallerJson.Options)!;
        if (signed.SharedPackages?.Count is not > 0 && package.Manifest.SharedPackages?.Count is not > 0) return;
        package.VerifySignature(requireSignature: true);
        if (JsonSerializer.Serialize(signed.SharedPackages, InstallerJson.Options) != JsonSerializer.Serialize(package.Manifest.SharedPackages, InstallerJson.Options) ||
            JsonSerializer.Serialize(signed.ApplicationLaunch, InstallerJson.Options) != JsonSerializer.Serialize(package.Manifest.ApplicationLaunch, InstallerJson.Options) ||
            signed.UpdateCompanion?.SharedPackageId is not null &&
            JsonSerializer.Serialize(signed.UpdateCompanion, InstallerJson.Options) != JsonSerializer.Serialize(package.Manifest.UpdateCompanion, InstallerJson.Options))
            throw new CryptographicException("Shared package authority must come from the unchanged signed application manifest.");
        ValidatePublisher(signed, package.PublisherKeySha256);
    }

    internal static void ValidateDefinition(PackageManifest manifest, IReadOnlySet<string> features)
    {
        if (manifest.SharedPackages is not { Count: > 0 } definitions) return;
        if (manifest.FormatVersion < 5 || manifest.TargetRuntime is null || manifest.LegacyMsi is not null || manifest.LegacyExecutable is not null)
            throw new InvalidDataException("Shared dependency packages require a format-5 native package and an exact targetRuntime.");
        if (definitions.Count > 128) throw new InvalidDataException("A package may declare at most 128 shared dependencies.");
        var origin = Origin(manifest);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            PackageSafety.ValidateId(definition.Id);
            _ = PackageVersion.Parse(definition.Version);
            if (!ids.Add(definition.Id) || definition.TargetRuntime != manifest.TargetRuntime || definition.Size <= 0 ||
                !PackageSafety.HashPattern().IsMatch(definition.Sha256) || !PackageSafety.HashPattern().IsMatch(definition.PublisherKeySha256) ||
                definition.Feature is not null && !features.Contains(definition.Feature))
                throw new InvalidDataException("Shared dependencies require distinct IDs, exact target runtime, size, hashes, publisher and known feature.");
            if (!Uri.TryCreate(definition.DownloadUrl, UriKind.Absolute, out var download))
                throw new InvalidDataException("A shared dependency requires an absolute download URL.");
            RequireOrigin(download, origin);
        }
        foreach (var file in manifest.Payload)
            if (OverlapsBinding(file.Path)) throw new InvalidDataException("The generated shared-package binding cannot overlap application payload.");
        if (manifest.UpdateCompanion is { } companion && OverlapsBinding(companion.ConfigurationPath))
            throw new InvalidDataException("The updater profile cannot overlap the shared-package binding.");
    }

    internal static void ValidateLaunch(PackageManifest manifest)
    {
        if (manifest.ApplicationLaunch is not { } launch) return;
        if (manifest.FormatVersion < 5 || manifest.SharedPackages is not { Count: > 0 } || manifest.LegacyMsi is not null || manifest.LegacyExecutable is not null)
            throw new InvalidDataException("A shared application launcher requires format 5 native file ownership and pinned dependencies.");
        launch.ExecutablePath = PackageSafety.NormalizeRelativePath(launch.ExecutablePath);
        launch.RuntimeConfigPath = PackageSafety.NormalizeRelativePath(launch.RuntimeConfigPath);
        if (launch.ExecutablePath.Equals(manifest.EntryPoint, StringComparison.OrdinalIgnoreCase) ||
            !launch.RuntimeConfigPath.EndsWith(".runtimeconfig.json", StringComparison.Ordinal) ||
            !manifest.Payload.Any(file => file.Path == launch.ExecutablePath && file.TargetRoot is null && file.Feature is null) ||
            !manifest.Payload.Any(file => file.Path == launch.RuntimeConfigPath && file.TargetRoot is null && file.Feature is null))
            throw new InvalidDataException("The actual application host and runtime configuration must be required local payload files distinct from the bootstrap.");
        var ids = launch.ProbingPackageIds.Append(launch.RuntimePackageId).ToArray();
        if (launch.ProbingPackageIds.Count > 128 || launch.ProbingPackageIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != launch.ProbingPackageIds.Count ||
            ids.Any(id => !manifest.SharedPackages.Any(package => package.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && package.Feature is null)))
            throw new InvalidDataException("Application runtime and probing IDs must select exact required shared packages.");
        var generated = ProbingConfigurationPath(manifest)!;
        if (manifest.Payload.Any(file => Overlaps(file.Path, generated)) || manifest.UpdateCompanion is { } updater && Overlaps(updater.ConfigurationPath, generated))
            throw new InvalidDataException("The generated runtime probing configuration cannot overlap application payload or updater metadata.");
    }

    public static string? ProbingConfigurationPath(PackageManifest manifest) => manifest.ApplicationLaunch is { } launch
        ? launch.RuntimeConfigPath[..^5] + ".dev.json" : null;

    /// <summary>The scope-owned cache; machine content is never loaded from a per-user writable location.</summary>
    public static string Root(string scope, string stateRoot, bool isolated = false)
    {
        if (scope is not ("user" or "machine")) throw new InvalidDataException("Invalid shared package scope.");
        if (isolated) return PackageSafety.UnderRoot(Path.GetFullPath(stateRoot), ".shared-packages");
        var parent = scope == "machine"
            ? OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) : "/opt"
            : InstallerPlatform.UserData;
        if (string.IsNullOrWhiteSpace(parent)) throw new InvalidOperationException("The shared-package scope directory is unavailable.");
        return PackageSafety.UnderRoot(parent, "SocketJack/Shared");
    }

    internal static SharedPackageBinding? Describe(PackageManifest manifest, string manifestHash, string publisherPin,
        string installRoot, string scope, string stateRoot, bool isolated, IEnumerable<string> features)
    {
        var selected = features.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var definitions = manifest.SharedPackages?.Where(x => x.Feature is null || selected.Contains(x.Feature)).ToArray() ?? [];
        if (definitions.Length == 0) return null;
        var root = Root(scope, stateRoot, isolated);
        return new()
        {
            ProductId = manifest.Id, Version = manifest.Version, InstallDirectory = Path.GetFullPath(installRoot), Scope = scope,
            PublisherKeySha256 = publisherPin.ToLowerInvariant(), ManifestSha256 = manifestHash,
            ApplicationLaunch = manifest.ApplicationLaunch,
            Packages = definitions.Select(definition => new SharedPackageReference
            {
                Id = definition.Id, Version = definition.Version, TargetRuntime = definition.TargetRuntime,
                Sha256 = definition.Sha256.ToLowerInvariant(), Size = definition.Size,
                Directory = PackageSafety.UnderRoot(PackageRoot(root, definition), "files")
            }).ToList()
        };
    }

    /// <summary>Downloads and verifies every selected dependency before the target application needs to exit.</summary>
    public static async Task<SharedPackageBinding?> PrepareAsync(PackageReader package, InstallPlan plan, InstallOptions options,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateAuthority(package);
        if (package.Manifest.SharedPackages is not { Count: > 0 }) return null;
        package.VerifySignature(options.TrustedPublisherKeySha256, requireSignature: true);
        var signed = JsonSerializer.Deserialize<PackageManifest>(package.ReadManifestBytes(), InstallerJson.Options)!;
        if (JsonSerializer.Serialize(signed.SharedPackages, InstallerJson.Options) != JsonSerializer.Serialize(package.Manifest.SharedPackages, InstallerJson.Options) ||
            plan.Manifest.Id != signed.Id || plan.Manifest.Version != signed.Version || signed.TargetRuntime != plan.Manifest.TargetRuntime)
            throw new CryptographicException("Shared dependencies must come from the unchanged signed application manifest.");
        var stateRoot = options.StateRoot ?? InstallerEngine.DefaultStateRoot(plan.Scope);
        var binding = Describe(signed, Hash(package.ReadManifestBytes()), package.PublisherKeySha256!, plan.InstallDirectory,
            plan.Scope, stateRoot, options.Isolated, plan.Features);
        if (binding is null) return null;
        var root = Root(plan.Scope, stateRoot, options.Isolated);
        using var machineParent = OperatingSystem.IsWindows() && plan.Scope == "machine" && !options.Isolated
            ? MachineStateSecurity.Acquire(Path.GetDirectoryName(root)!, create: true) : null;
        using var machineRoot = OperatingSystem.IsWindows() && plan.Scope == "machine" && !options.Isolated
            ? MachineStateSecurity.Acquire(root, create: true) : null;
        if (machineRoot is null) Directory.CreateDirectory(root);
        PackageSafety.EnsureNoReparsePoints(root);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        var selected = plan.Features.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in signed.SharedPackages!.Where(x => x.Feature is null || selected.Contains(x.Feature)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PackageSafety.HashEquals(definition.PublisherKeySha256, package.PublisherKeySha256!))
                throw new CryptographicException("A shared dependency must be signed by the application's pinned publisher.");
            var directory = PackageRoot(root, definition);
            Directory.CreateDirectory(directory);
            using var lease = await AcquireAsync(PackageSafety.UnderRoot(directory, "provision.lock"), cancellationToken);
            var archive = PackageSafety.UnderRoot(directory, "package.hinst");
            if (File.Exists(archive)) await VerifyFileAsync(archive, definition.Size, definition.Sha256, cancellationToken);
            else
            {
                RequireSpace(directory, definition.Size);
                await DownloadAsync(definition, Origin(signed), archive, options, client, progress, cancellationToken);
            }
            using var dependency = PackageReader.Open(archive);
            ValidateArchive(dependency, definition, plan.Scope);
            var filesRoot = PackageSafety.UnderRoot(directory, "files");
            Directory.CreateDirectory(filesRoot);
            var complete = PackageSafety.UnderRoot(directory, "complete.json");
            var immutable = File.Exists(complete);
            RequireSpace(directory, dependency.Manifest.Payload.Where(file => !File.Exists(PackageSafety.UnderRoot(filesRoot, file.Path)))
                .Aggregate(0L, (total, file) => checked(total + file.Size)));
            foreach (var file in dependency.Manifest.Payload)
            {
                var path = PackageSafety.UnderRoot(filesRoot, file.Path);
                if (File.Exists(path))
                {
                    await VerifyFileAsync(path, file.Size, file.Sha256, cancellationToken);
                    continue;
                }
                if (immutable) throw new CryptographicException("An activated immutable dependency changed; its files were preserved for repair.");
                await ExtractFileAsync(archive, file, path, PackageSafety.UnderRoot(directory, "staging"), progress, cancellationToken);
            }
            ValidateInventory(filesRoot, dependency.Manifest);
            if (!immutable)
                await File.WriteAllTextAsync(complete, JsonSerializer.Serialize(new { id = definition.Id, version = definition.Version,
                    targetRuntime = definition.TargetRuntime, sha256 = definition.Sha256, publisherKeySha256 = definition.PublisherKeySha256 }, InstallerJson.Options), cancellationToken);
            progress?.Report(new("dependencies", "Verified shared dependency " + definition.Id, binding.Packages.FindIndex(x => x.Id == definition.Id) + 1, binding.Packages.Count));
        }
        return binding;
    }

    internal static void Activate(SharedPackageBinding? binding, InstalledProduct product, InstalledProduct? previous,
        string stateRoot, InstallTransaction transaction)
    {
        if (binding is null && previous?.Files.Any(x => x.Path == BindingFileName) != true) return;
        var reference = ReferencePath(product, stateRoot);
        var owned = previous?.Files.SingleOrDefault(x => x.Path == BindingFileName);
        if (File.Exists(reference) && (owned is null || !PackageSafety.HashEquals(PackageSafety.HashFile(reference), owned.Sha256)))
            throw new CryptographicException("The shared-package reference is unowned or changed; it was preserved.");
        if (binding is null)
        {
            // Removing a product reference never removes an immutable dependency or another application's reference.
            if (previous is not null && File.Exists(reference)) transaction.DeleteFile(reference);
            return;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(binding, InstallerJson.Options);
        var temporary = Path.Combine(transaction.Root, "shared-binding.json");
        File.WriteAllBytes(temporary, bytes);
        var destination = PackageSafety.UnderRoot(product.InstallDirectory, BindingFileName);
        if (File.Exists(destination) && (owned is null || !PackageSafety.HashEquals(PackageSafety.HashFile(destination), owned.Sha256)))
            throw new CryptographicException("The application shared-package binding is unowned or changed; it was preserved.");
        transaction.ReplaceFile(destination, temporary);
        transaction.ReplaceFile(reference, temporary);
        product.Files.Add(new() { Path = BindingFileName, Sha256 = Hash(bytes) });
        if (binding.ApplicationLaunch is { } launch)
        {
            var probingPath = launch.RuntimeConfigPath[..^5] + ".dev.json";
            var probingBytes = ProbingBytes(binding);
            var installedProbing = PackageSafety.UnderRoot(product.InstallDirectory, probingPath);
            var old = previous?.Files.SingleOrDefault(file => file.Path == probingPath);
            if (File.Exists(installedProbing) && (old is null || !PackageSafety.HashEquals(PackageSafety.HashFile(installedProbing), old.Sha256)))
                throw new CryptographicException("The runtime probing configuration is unowned or changed; it was preserved.");
            var probingTemporary = Path.Combine(transaction.Root, "shared-probing.json"); File.WriteAllBytes(probingTemporary, probingBytes);
            transaction.ReplaceFile(installedProbing, probingTemporary);
            product.Files.Add(new() { Path = probingPath, Sha256 = Hash(probingBytes) });
        }
    }

    internal static void Release(InstalledProduct product, string stateRoot, InstallTransaction transaction)
    {
        var owned = product.Files.SingleOrDefault(x => x.Path == BindingFileName);
        if (owned is null) return;
        var path = ReferencePath(product, stateRoot);
        if (File.Exists(path))
        {
            if (!PackageSafety.HashEquals(PackageSafety.HashFile(path), owned.Sha256))
                throw new CryptographicException("The shared-package reference changed; it was preserved.");
            transaction.DeleteFile(path);
        }
    }

    internal static IEnumerable<FileRecoveryTarget> RecoveryTargets(PackageManifest manifest, PackageManifest? previousManifest,
        InstalledProduct? previous, InstallPlan plan, string targetDigest, string? baseDigest, string? publisherPin, string stateRoot, bool isolated)
    {
        if (manifest.SharedPackages?.Count is not > 0 && previousManifest?.SharedPackages?.Count is not > 0) yield break;
        var next = Describe(manifest, targetDigest, publisherPin!, plan.InstallDirectory, plan.Scope, stateRoot, isolated, plan.Features);
        var old = previous is not null && previousManifest is not null
            ? Describe(previousManifest, baseDigest!, previous.PublisherKeySha256!, previous.InstallDirectory, previous.Scope, stateRoot, isolated, previous.Features) : null;
        var nextBytes = next is null ? null : JsonSerializer.SerializeToUtf8Bytes(next, InstallerJson.Options);
        var oldBytes = old is null ? null : JsonSerializer.SerializeToUtf8Bytes(old, InstallerJson.Options);
        yield return new() { Path = BindingFileName, BeforeSha256 = oldBytes is null ? null : Hash(oldBytes), BeforeSize = oldBytes?.LongLength,
            AfterSha256 = nextBytes is null ? null : Hash(nextBytes), AfterSize = nextBytes?.LongLength };
        var identity = new InstalledProduct { Id = manifest.Id, InstallDirectory = plan.InstallDirectory, Scope = plan.Scope, Isolated = isolated };
        var reference = ReferencePath(identity, stateRoot);
        yield return new() { Location = "external", Path = "shared-packages/reference.json", TargetRootDirectory = Path.GetDirectoryName(reference),
            TargetRelativePath = Path.GetFileName(reference), BeforeSha256 = oldBytes is null ? null : Hash(oldBytes), BeforeSize = oldBytes?.LongLength,
            AfterSha256 = nextBytes is null ? null : Hash(nextBytes), AfterSize = nextBytes?.LongLength };
        var nextProbing = manifest.ApplicationLaunch is not null && next is not null ? ProbingBytes(next) : null;
        var oldProbing = previousManifest?.ApplicationLaunch is not null && old is not null ? ProbingBytes(old) : null;
        foreach (var path in new[] { ProbingConfigurationPath(manifest), previousManifest is null ? null : ProbingConfigurationPath(previousManifest) }
                     .Where(path => path is not null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var before = previousManifest is not null && path == ProbingConfigurationPath(previousManifest) ? oldProbing : null;
            var after = path == ProbingConfigurationPath(manifest) ? nextProbing : null;
            yield return new() { Path = path!, BeforeSha256 = before is null ? null : Hash(before), BeforeSize = before?.LongLength,
                AfterSha256 = after is null ? null : Hash(after), AfterSize = after?.LongLength };
        }
    }

    internal static string ReferencePath(InstalledProduct product, string stateRoot)
    {
        var identity = product.Id.ToLowerInvariant() + "\n" + product.Scope + "\n" +
            (OperatingSystem.IsWindows() ? Path.GetFullPath(product.InstallDirectory).ToLowerInvariant() : Path.GetFullPath(product.InstallDirectory));
        return PackageSafety.UnderRoot(Root(product.Scope, stateRoot, product.Isolated), "references/" + Hash(Encoding.UTF8.GetBytes(identity)) + ".json");
    }

    internal static (PayloadFile Payload, string Source) UpdaterSource(PackageManifest manifest, InstalledProduct product, string stateRoot)
    {
        var updater = manifest.UpdateCompanion ?? throw new InvalidDataException("The signed package has no updater definition.");
        var definition = manifest.SharedPackages?.SingleOrDefault(x => x.Id.Equals(updater.SharedPackageId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The updater dependency is not declared by the signed application package.");
        if (!PackageSafety.HashEquals(definition.PublisherKeySha256, product.PublisherKeySha256 ?? ""))
            throw new CryptographicException("The updater dependency has a different publisher identity.");
        var directory = PackageRoot(Root(product.Scope, stateRoot, product.Isolated), definition);
        var archive = PackageSafety.UnderRoot(directory, "package.hinst");
        using var package = PackageReader.Open(archive);
        if (new FileInfo(archive).Length != definition.Size || !PackageSafety.HashEquals(package.ComputePackageSha256(), definition.Sha256))
            throw new CryptographicException("The cached updater dependency differs from its signed archive binding.");
        ValidateArchive(package, definition, product.Scope);
        var payload = package.Manifest.Payload.SingleOrDefault(x => x.Path.Equals(updater.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The updater executable is missing from its signed dependency package.");
        return (payload, PackageSafety.UnderRoot(PackageSafety.UnderRoot(directory, "files"), payload.Path));
    }

    private static string PackageRoot(string root, SharedPackageDefinition definition) =>
        PackageSafety.UnderRoot(root, "packages/" + definition.PublisherKeySha256[..16].ToLowerInvariant() + "/" + definition.Sha256.ToLowerInvariant());

    private static bool OverlapsBinding(string path)
    {
        path = PackageSafety.NormalizeRelativePath(path);
        return path.Equals(BindingFileName, StringComparison.OrdinalIgnoreCase) || path.StartsWith(BindingFileName + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Overlaps(string first, string second) => first.Equals(second, StringComparison.OrdinalIgnoreCase) ||
        first.StartsWith(second + "/", StringComparison.OrdinalIgnoreCase) || second.StartsWith(first + "/", StringComparison.OrdinalIgnoreCase);

    private static byte[] ProbingBytes(SharedPackageBinding binding) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        runtimeOptions = new { additionalProbingPaths = binding.ApplicationLaunch!.ProbingPackageIds.Select(id =>
            binding.Packages.Single(package => package.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Directory).ToArray() }
    }, InstallerJson.Options);

    private static Uri Origin(PackageManifest manifest)
    {
        if (!Uri.TryCreate(manifest.UpdateUrl ?? manifest.Homepage, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
            uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new InvalidDataException("Shared dependencies require a signed HTTPS application origin.");
        return new Uri(uri.GetLeftPart(UriPartial.Authority));
    }

    private static void RequireOrigin(Uri uri, Uri origin)
    {
        if (uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Scheme != origin.Scheme || uri.Host != origin.Host || uri.Port != origin.Port)
            throw new InvalidDataException("Shared dependency downloads and redirects must stay on the signed application's origin.");
    }

    private static async Task DownloadAsync(SharedPackageDefinition definition, Uri origin, string destination, InstallOptions options,
        HttpClient client, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        var uri = new Uri(definition.DownloadUrl, UriKind.Absolute);
        if (uri.Scheme != "https" && !(options.Isolated && uri.IsLoopback))
            throw new InvalidDataException("HTTP shared dependency downloads are permitted only in isolated loopback checks.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        var partial = destination + ".download";
        for (var redirect = 0; redirect < 6; redirect++)
        {
            RequireOrigin(uri, origin);
            progress?.Report(new("dependencies", "Downloading " + definition.Id, 0, 1000));
            using var response = options.SharedPackageTransport is { } transport
                ? await transport.GetAsync(uri, definition.Size, progress, deadline.Token)
                : await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.RequestMessage?.RequestUri is { } effective) RequireOrigin(effective, origin);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (response.Headers.Location is not { } location) throw new HttpRequestException("Dependency redirect has no destination.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != definition.Size)
                throw new InvalidDataException("Shared dependency download size differs from its signed definition.");
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
            {
                var buffer = new byte[131072];
                long total = 0;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, deadline.Token);
                    if (count == 0) break;
                    total = checked(total + count);
                    if (total > definition.Size) throw new InvalidDataException("Shared dependency response exceeds its signed size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                    progress?.Report(new("dependencies", "Downloading " + definition.Id, (int)Math.Min(1000, total * 1000d / definition.Size), 1000));
                }
            }
            await VerifyFileAsync(partial, definition.Size, definition.Sha256, deadline.Token);
            File.Move(partial, destination, overwrite: false);
            return;
        }
        throw new HttpRequestException("Too many shared dependency redirects.");
    }

    private static void ValidateArchive(PackageReader package, SharedPackageDefinition definition, string scope)
    {
        package.VerifySignature(definition.PublisherKeySha256, requireSignature: true);
        var manifest = package.Manifest;
        if (manifest.Id != definition.Id || manifest.Version != definition.Version || manifest.TargetRuntime != definition.TargetRuntime ||
            manifest.LegacyMsi is not null || manifest.LegacyExecutable is not null || manifest.UpdateCompanion is not null ||
            manifest.NativeMigration is not null || manifest.SharedPackages?.Count > 0 || manifest.Modules.Count > 0 || manifest.InstallationRoots?.Count > 0 ||
            manifest.Registry.Count + manifest.Shortcuts.Count + manifest.Environment.Count + manifest.Services.Count + manifest.Prerequisites.Count + manifest.Features.Count > 0 ||
            manifest.Payload.Any(x => x.TargetRoot is not null || x.Feature is not null))
            throw new InvalidDataException("A shared dependency must be an inert signed payload package with its exact declared identity, scope and runtime.");
        PackagePlatform.RequireCurrentHost(manifest);
        _ = manifest.Payload.Aggregate(0L, (total, file) => checked(total + file.Size));
    }

    private static async Task ExtractFileAsync(string archive, PayloadFile file, string destination, string staging, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.CreateDirectory(staging);
        var temporary = PackageSafety.UnderRoot(staging, Hash(Encoding.UTF8.GetBytes(file.Path)) + ".part");
        PackageSafety.EnsureNoReparsePoints(temporary);
        using var zip = System.IO.Compression.ZipFile.OpenRead(archive);
        var entry = zip.GetEntry("payload/" + file.Path) ?? throw new InvalidDataException("Missing dependency payload.");
        await using (var input = entry.Open())
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
        {
            var buffer = new byte[131072]; long total = 0;
            while (true)
            {
                var count = await input.ReadAsync(buffer, token); if (count == 0) break;
                total = checked(total + count);
                if (total > file.Size) throw new InvalidDataException("Expanded dependency exceeds its signed size.");
                await output.WriteAsync(buffer.AsMemory(0, count), token);
            }
        }
        await VerifyFileAsync(temporary, file.Size, file.Sha256, token);
        File.Move(temporary, destination, overwrite: false);
        if (!OperatingSystem.IsWindows() && file.UnixMode is { } mode) File.SetUnixFileMode(destination, (UnixFileMode)mode);
        progress?.Report(new("dependencies", "Extracted " + file.Path, 1, 1));
    }

    private static async Task VerifyFileAsync(string path, long size, string hash, CancellationToken token)
    {
        PackageSafety.EnsureNoReparsePoints(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length != size || !PackageSafety.HashEquals(Convert.ToHexString(await SHA256.HashDataAsync(file, token)), hash))
            throw new CryptographicException("A shared dependency's size or SHA-256 differs from its signed definition.");
    }

    private static void ValidateInventory(string directory, PackageManifest manifest)
    {
        var expected = manifest.Payload.Select(x => Path.GetFullPath(PackageSafety.UnderRoot(directory, x.Path))).ToHashSet(InstallerPlatform.PathComparer);
        foreach (var path in EnumerateFiles(directory))
            if (!expected.Contains(path)) throw new InvalidDataException("The shared dependency contains an undeclared file.");
    }

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            PackageSafety.EnsureNoReparsePoints(path);
            if (Directory.Exists(path)) { foreach (var child in EnumerateFiles(path)) yield return child; }
            else yield return Path.GetFullPath(path);
        }
    }

    private static async Task<FileStream> AcquireAsync(string path, CancellationToken token)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            PackageSafety.EnsureNoReparsePoints(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (started.Elapsed < TimeSpan.FromMinutes(2)) { await Task.Delay(100, token); }
        }
    }

    private static void RequireSpace(string directory, long needed)
    {
        if (needed <= 0) return;
        var volume = Path.GetPathRoot(Path.GetFullPath(directory));
        if (volume is not null && new DriveInfo(volume).AvailableFreeSpace < needed)
            throw new IOException($"The shared dependency store needs {needed:N0} additional bytes of free space.");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
