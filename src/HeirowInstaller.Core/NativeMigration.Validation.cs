using System.Security.Cryptography;
using System.Text.Json;

namespace HeirowInstaller.Core;

internal static class NativeMigrationValidation
{
    internal static void ValidateDefinition(PackageManifest manifest)
    {
        if (manifest.NativeMigration is not { } migration) return;
        if (manifest.FormatVersion < 3 || manifest.LegacyMsi is not null || manifest.LegacyExecutable is not null)
            throw new InvalidDataException("Native migration requires a format 3 managed package.");
        if (migration.Baselines is null || migration.Baselines.Count is < 1 or > 32)
            throw new InvalidDataException("Native migration requires between one and 32 exact published baselines.");
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in migration.Baselines)
        {
            if (definition is null || !identities.Add(definition.Scope + "\n" + definition.FromVersion + "\n" + definition.FromManifestSha256))
                throw new InvalidDataException("Native migration baselines must be distinct and unambiguous.");
            _ = PackageVersion.Parse(definition.FromVersion);
            if (!PackageSafety.HashPattern().IsMatch(definition.FromManifestSha256) || definition.Scope is not ("user" or "machine"))
                throw new InvalidDataException("Native migration requires an exact signed baseline digest and scope.");
            if (definition.Backend == "msi")
            {
                if (!Guid.TryParse(definition.MsiProductCode, out _) || definition.BurnRegistration is not null || definition.BurnMsiProductCodes is not null)
                    throw new InvalidDataException("MSI migration requires its exact ProductCode only.");
            }
            else if (definition.Backend == "burn")
            {
                if (definition.MsiProductCode is not null || definition.BurnRegistration is not { } registration)
                    throw new InvalidDataException("Burn migration requires its exact signed registration.");
                BackendMaintenance.ValidateDefinition(registration, definition.Scope);
                if (registration.BundleUpgradeCode is null)
                    throw new InvalidDataException("Burn migration requires an exact BundleUpgradeCode.");
                if (definition.BurnMsiProductCodes is null || definition.BurnMsiProductCodes.Count > 64 ||
                    definition.BurnMsiProductCodes.Any(code => !Guid.TryParse(code, out _)) ||
                    definition.BurnMsiProductCodes.Select(Guid.Parse).Distinct().Count() != definition.BurnMsiProductCodes.Count)
                    throw new InvalidDataException("Burn migration must explicitly list its distinct application MSI child ProductCodes, or an explicit empty no-MSI chain.");
            }
            else throw new InvalidDataException("Native migration supports recorded MSI and Burn installations only.");
        }
        if (manifest.UpdateCompanion is { UpdateMode: not "files" })
            throw new InvalidDataException("A migrated application's optional updater must use file updates.");
    }

    internal static NativeMigrationBaseline? ForPlan(PackageReader target, InstallPlan plan, InstalledProduct? previous,
        InstallOptions options, INativeMigrationBackend backend)
    {
        var signed = JsonSerializer.Deserialize<PackageManifest>(FileTransactionRecoveryContext.ReadManifest(target.PackagePath), InstallerJson.Options)
            ?? throw new InvalidDataException("The signed target manifest is empty.");
        var modelChangesBackend = previous is not null &&
            (previous.IsMsi != (target.Manifest.LegacyMsi is not null) || previous.IsExecutable != (target.Manifest.LegacyExecutable is not null));
        if ((signed.NativeMigration is not null || target.Manifest.NativeMigration is not null || modelChangesBackend) &&
            JsonSerializer.Serialize(signed, InstallerJson.Options) != JsonSerializer.Serialize(target.Manifest, InstallerJson.Options))
            throw new CryptographicException("Migration authority must come from the original signed bytes, not a modified package model.");
        ValidateDefinition(signed);
        var targetLegacy = target.Manifest.LegacyMsi is not null || target.Manifest.LegacyExecutable is not null;
        if (previous is null)
        {
            if (target.Manifest.NativeMigration is { } fresh)
            {
                if (Directory.Exists(plan.InstallDirectory) && Directory.EnumerateFileSystemEntries(plan.InstallDirectory).Any() ||
                    !options.Isolated && (!fresh.Baselines.Any(baseline => baseline.Scope == plan.Scope) ||
                    fresh.Baselines.Any(baseline => backend.ObserveDeclared(baseline, isolated: false) != NativeBackendState.Absent)))
                    throw new InvalidOperationException("An existing legacy application cannot be adopted without its recorded, signed heirowInstaller receipt. Repair the original installation first.");
            }
            return null;
        }
        var changed = previous.IsMsi != (target.Manifest.LegacyMsi is not null) || previous.IsExecutable != (target.Manifest.LegacyExecutable is not null);
        if (!changed) return null;
        if (targetLegacy || !(previous.IsMsi || previous.IsExecutable) || target.Manifest.NativeMigration is not { } migration)
            throw new InvalidOperationException("Changing installation backend requires an exact signed native migration or uninstalling the existing product first.");
        if (options.Isolated && backend is WindowsNativeMigrationBackend)
            throw new InvalidOperationException("Isolated validation may install a fresh native payload but cannot retire a real legacy backend.");
        if (plan.Scope != previous.Scope || previous.Isolated != options.Isolated ||
            !Path.GetFullPath(previous.InstallDirectory).Equals(Path.GetFullPath(plan.InstallDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native migration cannot change the recorded scope or installation directory.");
        var candidates = migration.Baselines.Where(definition => definition.Scope == previous.Scope && definition.FromVersion == previous.Version).ToArray();
        if (candidates.Length == 0 || previous.PublisherKeySha256 is null)
            throw new CryptographicException("Native migration does not authorize this recorded baseline.");
        target.VerifySignature(previous.PublisherKeySha256, requireSignature: true);
        using var original = PackageReader.Open(previous.PackagePath);
        original.VerifySignature(previous.PublisherKeySha256, requireSignature: true);
        var digest = ManifestDigest(original);
        candidates = candidates.Where(definition => PackageSafety.HashEquals(digest, definition.FromManifestSha256)).ToArray();
        if (original.Manifest.Id != previous.Id || target.Manifest.Id != previous.Id || original.Manifest.Version != previous.Version || candidates.Length != 1)
            throw new CryptographicException("The original signed repair package differs from the migration baseline.");
        var authorization = candidates[0];
        if (authorization.Backend == "msi")
        {
            if (!previous.IsMsi || previous.IsExecutable || original.Manifest.LegacyMsi is not { } msi ||
                !SameGuid(previous.MsiProductCode, authorization.MsiProductCode) || !SameGuid(msi.ProductCode, authorization.MsiProductCode))
                throw new CryptographicException("The recorded MSI ProductCode differs from the signed migration.");
        }
        else
        {
            if (!previous.IsExecutable || previous.IsMsi || original.Manifest.LegacyExecutable is not { } executable ||
                executable.UninstallArguments.Count == 0 || !SameRegistration(executable.MaintenanceRegistration, authorization.BurnRegistration))
                throw new CryptographicException("The recorded Burn backend differs from the signed migration.");
            if (previous.BackendRegistrations.Count != 1)
                throw new InvalidDataException("Burn migration requires its exact recorded maintenance registration.");
            BackendMaintenance.ValidateSnapshot(previous.BackendRegistrations[0], previous.Scope);
            var snapshot = previous.BackendRegistrations[0];
            if (snapshot.Values.GetValueOrDefault("view") != authorization.BurnRegistration!.View)
                throw new InvalidDataException("The recorded Burn registry view differs from its signed definition.");
        }
        return new(Clone(previous), Clone(original.Manifest), Clone(authorization), ManifestDigest(target), "");
    }

    internal static string ManifestDigest(PackageReader reader) => IncrementalPackageIO.Hash(FileTransactionRecoveryContext.ReadManifest(reader.PackagePath));
    internal static InstalledProduct? SelectRecorded(IEnumerable<InstalledProduct> verifiedReceipts, string productId)
    {
        var matches = verifiedReceipts.Where(product => product.Id.Equals(productId, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 0 ? null : matches.Length == 1 ? matches[0]
            : throw new InvalidOperationException("This product has receipts in both installer namespaces. Select an explicit installation scope before migrating.");
    }
    internal static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, InstallerJson.Options), InstallerJson.Options)!;
    internal static bool SameGuid(string? left, string? right) => Guid.TryParse(left, out var a) && Guid.TryParse(right, out var b) && a == b;
    private static bool SameRegistration(BackendRegistrationDefinition? left, BackendRegistrationDefinition? right) => left is not null && right is not null &&
        left.Hive == right.Hive && left.View == right.View && left.UninstallKey == right.UninstallKey && SameGuid(left.BundleUpgradeCode, right.BundleUpgradeCode);
}

internal sealed record NativeMigrationBaseline(InstalledProduct Product, PackageManifest Manifest, NativeMigrationBaselineDefinition Authorization,
    string TargetManifestSha256, string OriginalPackageSha256);

/// <summary>An internal identity binding around the one existing cross-process product lock.</summary>
internal sealed class ProductOperationLease : IDisposable
{
    private readonly FileStream handle;
    private readonly string stateRoot, productId, scope;
    private readonly bool isolated;
    internal ProductOperationLease(string stateRoot, string productId, string scope, bool isolated, FileStream handle)
    { this.stateRoot = Path.GetFullPath(stateRoot); this.productId = productId; this.scope = scope; this.isolated = isolated; this.handle = handle; }
    internal void Validate(InstalledProduct product, string expectedRoot)
    {
        if (handle.SafeFileHandle.IsClosed || product.Id != productId || product.Scope != scope || product.Isolated != isolated ||
            !stateRoot.Equals(Path.GetFullPath(expectedRoot), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(product.ReceiptPath).Equals(PackageSafety.UnderRoot(stateRoot, productId + "/receipt.json"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The migration does not own this recorded product operation lease.");
    }
    public void Dispose() => handle.Dispose();
}
