using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HeirowInstaller.Core;

/// <summary>A content binding, never an arbitrary executable path.</summary>
public sealed class SharedUpdaterRuntimeReference
{
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

public sealed record SharedUpdaterReferenceBinding(string Path, byte[]? Content);

/// <summary>Immutable, scope-wide updater files with a separate reference for each installed application.</summary>
public static class SharedUpdaterRuntime
{
    public static bool IsRuntimePayload(PackageManifest manifest, string path) => manifest.UpdateCompanion is { RuntimeMode: "shared" } definition &&
        path.Equals(definition.ExecutablePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Expected reference bytes derived from signed package metadata, never from mutable reference-file contents.</summary>
    public static SharedUpdaterReferenceBinding GetReferenceBinding(PackageManifest manifest, InstalledProduct identity, string stateRoot, bool enabled)
    {
        var configuration = new UpdateCompanionConfiguration { ProductId = identity.Id, InstallDirectory = identity.InstallDirectory, Scope = identity.Scope,
            StateRoot = stateRoot, Isolated = identity.Isolated, PublisherKeySha256 = identity.PublisherKeySha256 ?? "" };
        var path = ReferencePath(configuration);
        if (!enabled || manifest.UpdateCompanion is not { RuntimeMode: "shared" } definition) return new(path, null);
        if (!manifest.Id.Equals(identity.Id, StringComparison.OrdinalIgnoreCase) || configuration.PublisherKeySha256.Length != 64) throw new InvalidDataException("Reference recovery requires a trusted installed identity.");
        var payload = manifest.Payload.Single(x => x.Path.Equals(definition.ExecutablePath, StringComparison.OrdinalIgnoreCase));
        configuration.SharedRuntime = new() { Sha256 = payload.Sha256, Size = payload.Size };
        return new(path, ReferenceBytes(configuration));
    }
    public static string ApplicationStateDirectory(UpdateCompanionConfiguration configuration, bool create = false)
    {
        var identity = configuration.ProductId.ToLowerInvariant() + "\n" + configuration.InstallDirectory.ToLowerInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24];
        var parent = configuration.Isolated ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configuration.StateRoot))!, "updater-lifecycle")
            : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SocketJack", "heirowUpdater", "applications");
        var directory = PackageSafety.UnderRoot(parent, key);
        if (create) Directory.CreateDirectory(directory);
        return directory;
    }
    public static string Root(UpdateCompanionConfiguration configuration)
    {
        if (configuration.Scope is not ("user" or "machine")) throw new InvalidDataException("Unsupported shared updater scope.");
        var parent = configuration.Isolated
            ? Path.GetDirectoryName(Path.GetFullPath(configuration.StateRoot))!
            : InstallerEngine.DefaultStateRoot(configuration.Scope);
        return PackageSafety.UnderRoot(parent, ".updater-runtime");
    }

    public static SharedUpdaterRuntimeReference Provision(PackageManifest manifest, InstalledProduct product, string extractedRoot, string stateRoot)
    {
        var definition = manifest.UpdateCompanion ?? throw new InvalidDataException("The package has no updater definition.");
        if (definition.RuntimeMode != "shared") throw new InvalidDataException("The package does not request a shared updater runtime.");
        if (string.IsNullOrEmpty(product.PublisherKeySha256)) throw new CryptographicException("A shared updater requires a signed installed package.");
        var payload = manifest.Payload.Single(x => x.Path.Equals(definition.ExecutablePath, StringComparison.OrdinalIgnoreCase));
        var source = PackageSafety.UnderRoot(extractedRoot, payload.Path);
        VerifyFile(source, payload.Sha256, payload.Size);
        var version = FileVersionInfo.GetVersionInfo(source).FileVersion ?? throw new InvalidDataException("The updater runtime has no file version.");
        ValidateVersion(version);
        var reference = new SharedUpdaterRuntimeReference { Version = version, Sha256 = payload.Sha256, Size = payload.Size };
        var configuration = new UpdateCompanionConfiguration
        {
            ProductId = product.Id, InstallDirectory = product.InstallDirectory, Scope = product.Scope,
            Isolated = product.Isolated, StateRoot = stateRoot, PublisherKeySha256 = product.PublisherKeySha256, SharedRuntime = reference
        };
        using var protection = Guard(configuration, create: true);
        var destination = ExecutablePath(configuration);
        if (File.Exists(destination)) { VerifyFile(destination, reference.Sha256, reference.Size); return reference; }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        PackageSafety.EnsureNoReparsePoints(destination);
        var temporary = destination + ".incoming-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary);
            VerifyFile(temporary, reference.Sha256, reference.Size);
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination)) { VerifyFile(destination, reference.Sha256, reference.Size); }
            VerifyFile(destination, reference.Sha256, reference.Size);
            return reference;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Checks profile data against the actual signed package, including its authentication destination.</summary>
    public static void ValidateBinding(UpdateCompanionConfiguration configuration, PackageManifest manifest)
    {
        var definition = manifest.UpdateCompanion ?? throw new InvalidDataException("The signed package has no updater definition.");
        if (!SameOrigin(configuration.CredentialOrigin, definition.CredentialOrigin) ||
            configuration.AuthApplication != definition.AuthApplication ||
            configuration.UpdateMode != definition.UpdateMode ||
            configuration.CookieEnvironmentVariable != definition.CookieEnvironmentVariable ||
            configuration.TokenEnvironmentVariable != definition.TokenEnvironmentVariable ||
            !configuration.FeedUrl.Equals(manifest.UpdateUrl, StringComparison.Ordinal))
            throw new InvalidDataException("The updater authentication settings do not match its signed installed package.");
        if (configuration.SharedRuntime is not { } reference)
        {
            if (definition.RuntimeMode == "shared") throw new InvalidDataException("The shared updater binding is missing. Repair this application's installation.");
            return; // Existing app-local profiles remain supported.
        }
        ValidateReference(reference);
        var payload = manifest.Payload.SingleOrDefault(x => x.Path.Equals(definition.ExecutablePath, StringComparison.OrdinalIgnoreCase));
        if (payload is null || !PackageSafety.HashEquals(payload.Sha256, reference.Sha256) || payload.Size != reference.Size)
            throw new CryptographicException("The shared updater binding does not match its signed installed payload.");
    }

    /// <summary>Resolves only the scope-derived, hash-verified runtime. Old profiles resolve their existing app-local companion.</summary>
    public static string Resolve(UpdateCompanionConfiguration configuration)
    {
        if (configuration.SharedRuntime is null)
        {
            var installed = InstallerEngine.ListInstalled(configuration.StateRoot).Single(x => x.Id.Equals(configuration.ProductId, StringComparison.OrdinalIgnoreCase) && x.Scope == configuration.Scope);
            using var package = PackageReader.Open(installed.PackagePath);
            package.VerifySignature(configuration.PublisherKeySha256, requireSignature: true);
            ValidateBinding(configuration, package.Manifest);
            var definition = package.Manifest.UpdateCompanion!;
            var local = PackageSafety.UnderRoot(configuration.InstallDirectory, definition.ExecutablePath);
            var payload = package.Manifest.Payload.Single(x => x.Path.Equals(definition.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            VerifyFile(local, payload.Sha256, payload.Size);
            return local;
        }
        ValidateReference(configuration.SharedRuntime);
        using var protection = Guard(configuration, create: false);
        var executable = ExecutablePath(configuration);
        VerifyFile(executable, configuration.SharedRuntime.Sha256, configuration.SharedRuntime.Size);
        if (FileVersionInfo.GetVersionInfo(executable).FileVersion != configuration.SharedRuntime.Version)
            throw new InvalidDataException("The shared updater runtime version does not match its binding.");
        return executable;
    }

    /// <summary>Recovers only this application's existing owned reference when its generated profile needs repair.</summary>
    public static SharedUpdaterRuntimeReference? RecoverReference(UpdateCompanionConfiguration trustedConfiguration, PackageManifest trustedManifest)
    {
        var path = ReferencePath(trustedConfiguration);
        if (!File.Exists(path)) return null;
        using var protection = Guard(trustedConfiguration, create: false);
        var owner = ValidateReferenceOwner(path, trustedConfiguration);
        var content = owner.Runtime ?? throw new InvalidDataException("The owned updater reference is empty.");
        var definition = trustedManifest.UpdateCompanion ?? throw new InvalidDataException("The signed package has no updater definition.");
        var payload = trustedManifest.Payload.Single(x => x.Path.Equals(definition.ExecutablePath, StringComparison.OrdinalIgnoreCase));
        if (!PackageSafety.HashEquals(content.Sha256, payload.Sha256) || content.Size != payload.Size)
            throw new CryptographicException("The owned updater reference does not match this application's signed runtime.");
        var matches = new List<SharedUpdaterRuntimeReference>();
        foreach (var directory in Directory.EnumerateDirectories(Root(trustedConfiguration), "*-" + content.Sha256.ToLowerInvariant(), SearchOption.TopDirectoryOnly))
        {
            PackageSafety.EnsureNoReparsePoints(directory);
            var name = Path.GetFileName(directory); var version = name[..^(content.Sha256.Length + 1)];
            ValidateVersion(version);
            var executable = Path.Combine(directory, "heirowUpdater.exe");
            if (!File.Exists(executable)) continue;
            VerifyFile(executable, content.Sha256, content.Size);
            if (FileVersionInfo.GetVersionInfo(executable).FileVersion != version) throw new InvalidDataException("The recovered updater runtime has a different file version.");
            matches.Add(new() { Version = version, Sha256 = content.Sha256, Size = content.Size });
        }
        if (matches.Count > 1) throw new InvalidDataException("Multiple updater runtime directories claim this application's signed content.");
        return matches.SingleOrDefault();
    }

    internal static void Bind(UpdateCompanionConfiguration configuration, InstallTransaction transaction)
    {
        if (configuration.SharedRuntime is null) return;
        _ = Resolve(configuration);
        var path = ReferencePath(configuration);
        if (File.Exists(path)) ValidateReferenceOwner(path, configuration);
        var bytes = ReferenceBytes(configuration);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
        var temporary = Path.Combine(transaction.Root, "updater-reference-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(temporary, bytes);
        transaction.ReplaceFile(path, temporary);
    }

    internal static void Release(UpdateCompanionConfiguration configuration, InstallTransaction transaction)
    {
        if (configuration.SharedRuntime is null) return;
        var path = ReferencePath(configuration);
        if (!File.Exists(path)) return;
        ValidateReferenceOwner(path, configuration);
        transaction.DeleteFile(path);
        // Immutable binaries are deliberately retained. Releasing one app must never remove another app's runtime or a running image.
    }

    public static string ReferencePath(UpdateCompanionConfiguration configuration)
    {
        PackageSafety.ValidateId(configuration.ProductId);
        var identity = configuration.ProductId.ToLowerInvariant() + "\n" + Path.GetFullPath(configuration.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return PackageSafety.UnderRoot(Root(configuration), "references/" + key + ".json");
    }

    private static string ExecutablePath(UpdateCompanionConfiguration configuration)
    {
        var reference = configuration.SharedRuntime ?? throw new InvalidDataException("The shared runtime reference is missing.");
        ValidateReference(reference);
        return PackageSafety.UnderRoot(Root(configuration), reference.Version + "-" + reference.Sha256.ToLowerInvariant() + "/heirowUpdater.exe");
    }
    private static byte[] ReferenceBytes(UpdateCompanionConfiguration configuration) => JsonSerializer.SerializeToUtf8Bytes(new ApplicationReference
    {
        ProductId = configuration.ProductId, InstallDirectory = Path.GetFullPath(configuration.InstallDirectory),
        PublisherKeySha256 = configuration.PublisherKeySha256, Runtime = new()
        { Sha256 = configuration.SharedRuntime!.Sha256, Size = configuration.SharedRuntime.Size }
    }, InstallerJson.Options);

    private static IDisposable? Guard(UpdateCompanionConfiguration configuration, bool create)
    {
        PackageSafety.EnsureNoReparsePoints(Root(configuration));
        if (!OperatingSystem.IsWindows() || configuration.Isolated || configuration.Scope != "machine")
        {
            if (create) Directory.CreateDirectory(Root(configuration));
            return null;
        }
        var lease = MachineStateSecurity.Acquire(InstallerEngine.DefaultStateRoot("machine"), create);
        try
        {
            if (!lease.RootExists) throw new InvalidDataException("The protected shared updater namespace is missing.");
            if (create) Directory.CreateDirectory(Root(configuration));
            if (Directory.Exists(Root(configuration)) && !MachineStateSecurity.IsStateObjectProtected(Root(configuration)))
                throw new UnauthorizedAccessException("The shared updater namespace has an unsafe owner or ACL.");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static ApplicationReference ValidateReferenceOwner(string path, UpdateCompanionConfiguration configuration)
    {
        PackageSafety.EnsureNoReparsePoints(path);
        if (new FileInfo(path).Length > 16 * 1024) throw new InvalidDataException("The updater application reference is too large.");
        var owner = JsonSerializer.Deserialize<ApplicationReference>(File.ReadAllText(path), InstallerJson.Options);
        if (owner is null || owner.FormatVersion != 1 || !owner.ProductId.Equals(configuration.ProductId, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(owner.InstallDirectory).Equals(Path.GetFullPath(configuration.InstallDirectory), StringComparison.OrdinalIgnoreCase) ||
            !PackageSafety.HashEquals(owner.PublisherKeySha256, configuration.PublisherKeySha256))
            throw new InvalidDataException("The updater application reference belongs to a different installation.");
        return owner;
    }

    private static void ValidateReference(SharedUpdaterRuntimeReference reference)
    {
        ValidateVersion(reference.Version);
        if (reference.Size is <= 0 or > 512L * 1024 * 1024 || reference.Sha256.Length != 64 || !reference.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid shared updater content binding.");
    }
    private static void ValidateVersion(string version)
    {
        if (version.Length is < 1 or > 64 || !Version.TryParse(version, out _) || version.Any(c => !char.IsAsciiDigit(c) && c != '.'))
            throw new InvalidDataException("The shared updater requires a numeric file version.");
    }
    private static bool SameOrigin(string a, string b) => Uri.TryCreate(a, UriKind.Absolute, out var left) && Uri.TryCreate(b, UriKind.Absolute, out var right) &&
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) && left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port && left.AbsolutePath == "/" && right.AbsolutePath == "/" && left.Query.Length == 0 && right.Query.Length == 0;
    private static void VerifyFile(string path, string hash, long size)
    {
        PackageSafety.EnsureNoReparsePoints(path);
        if (!File.Exists(path) || new FileInfo(path).Length != size || !PackageSafety.HashEquals(PackageSafety.HashFile(path), hash))
            throw new CryptographicException("The shared updater runtime failed its signed content binding.");
    }
    private sealed class ApplicationReference
    {
        public int FormatVersion { get; set; } = 1;
        public string ProductId { get; set; } = "";
        public string InstallDirectory { get; set; } = "";
        public string PublisherKeySha256 { get; set; } = "";
        public RuntimeContent Runtime { get; set; } = new();
    }
    private sealed class RuntimeContent { public string Sha256 { get; set; } = ""; public long Size { get; set; } }
}
