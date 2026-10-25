using System.Security.Cryptography;
using System.Text.Json;

namespace HeirowInstaller.Core;

// This capability is constructed by the engine from signed bytes, never deserialized from a module or journal.
internal sealed class VerifiedModuleAuthority
{
    internal string ProductId { get; }
    internal string PublisherPin { get; }
    internal string ManifestSha256 { get; }
    internal string StateRoot { get; }
    internal string InstallRoot { get; }
    internal string Scope { get; }
    internal JsonElement Configuration { get; }

    private VerifiedModuleAuthority(PackageManifest manifest, string pin, string digest, string stateRoot, string installRoot, string scope,
        JsonElement configuration)
    {
        ProductId = manifest.Id; PublisherPin = pin; ManifestSha256 = digest; StateRoot = stateRoot;
        InstallRoot = installRoot; Scope = scope; Configuration = configuration.Clone();
    }

    internal static VerifiedModuleAuthority? Create(PackageReader package, InstallPlan plan, InstallOptions options, InstalledProduct? previous)
    {
        var bytes = FileTransactionRecoveryContext.ReadManifest(package.PackagePath);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(bytes, InstallerJson.Options)!;
        if (!manifest.Modules.TryGetValue(LegacyStagingCleanupModule.ModuleId, out var configuration)) return null;
        var expectedPin = options.TrustedPublisherKeySha256 ?? previous?.PublisherKeySha256
            ?? throw new CryptographicException("Legacy cleanup requires an explicitly trusted or previously installed publisher pin.");
        package.VerifySignature(expectedPin, requireSignature: true);
        var followsMigrationScope = previous is not null && previous.Scope == plan.Scope &&
            manifest.NativeMigration?.Baselines.Any(baseline => baseline.Scope == plan.Scope) == true;
        if (package.PublisherKeySha256 is null || manifest.Id != plan.Manifest.Id || manifest.Version != plan.Manifest.Version ||
            plan.Scope is not ("user" or "machine") || manifest.Scope != plan.Scope && !followsMigrationScope)
            throw new CryptographicException("Legacy cleanup requires the exact verified product and scope.");
        var root = Path.GetFullPath(plan.InstallDirectory);
        if (previous is not null && (previous.Id != manifest.Id || previous.Scope != plan.Scope ||
            !Path.GetFullPath(previous.InstallDirectory).Equals(root, StringComparison.OrdinalIgnoreCase) ||
            previous.PublisherKeySha256 is null || !PackageSafety.HashEquals(previous.PublisherKeySha256, package.PublisherKeySha256)))
            throw new CryptographicException("Legacy cleanup cannot change the installed product's publisher or root.");
        var state = Path.GetFullPath(options.StateRoot ?? InstallerEngine.DefaultStateRoot(plan.Scope));
        PackageSafety.EnsureNoReparsePoints(root); PackageSafety.EnsureNoReparsePoints(state);
        var persistent = LegacyStagingDefinition.PersistentRoot(state, manifest.Id);
        if (persistent.Equals(root, StringComparison.OrdinalIgnoreCase) || RunningApplicationTarget.IsWithin(persistent, root))
            throw new InvalidDataException("Legacy quarantine must remain outside the application root.");
        return new(manifest, package.PublisherKeySha256.ToLowerInvariant(), IncrementalPackageIO.Hash(bytes), state, root, plan.Scope, configuration);
    }

    internal void Validate(InstallerModuleContext context, JsonElement configuration)
    {
        if (context.Plan.Manifest.Id != ProductId || context.Scope != Scope ||
            !Path.GetFullPath(context.InstallDirectory).Equals(InstallRoot, StringComparison.OrdinalIgnoreCase) ||
            !Configuration.GetRawText().Equals(configuration.GetRawText(), StringComparison.Ordinal))
            throw new CryptographicException("Legacy cleanup configuration differs from its signed authority.");
    }
}
