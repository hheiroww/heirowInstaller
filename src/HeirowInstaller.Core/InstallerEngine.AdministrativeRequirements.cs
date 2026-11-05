using System.Security.Cryptography;
using System.Text.Json;

namespace HeirowInstaller.Core;

public sealed partial class InstallerEngine
{
    /// <summary>Runs only the signed privileged prerequisite phase before returning user files and HKCU to the original token.</summary>
    public async Task<bool> PrepareAdministrativeRequirementsAsync(PackageReader package, InstallPlan plan, InstallOptions options,
        IProgress<InstallProgress>? progress = null, CancellationToken token = default, PreparedFileUpdate? prepared = null)
    {
        package.VerifySignature(options.TrustedPublisherKeySha256, options.RequireSignature);
        if (plan.Manifest.Id != package.Manifest.Id || plan.Manifest.Version != package.Manifest.Version)
            throw new InvalidDataException("The prerequisite plan differs from its verified package.");
        var manifest = JsonSerializer.Deserialize<PackageManifest>(package.ReadManifestBytes(), InstallerJson.Options)!;
        manifest.Prerequisites = manifest.Prerequisites.Where(item => item.RequiresAdministrator && IsSelected(item.Feature, plan.Features)).ToList();
        if (manifest.Prerequisites.Count == 0) return false;
        if (!OperatingSystem.IsWindows() || !IsElevated || options.Isolated)
            throw new UnauthorizedAccessException("Privileged prerequisites require the verified administrator worker.");
        var state = DefaultStateRoot("machine");
        using var protectedState = MachineStateSecurity.Acquire(state, create: true);
        var stage = PackageSafety.UnderRoot(state, ".prerequisites/" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        foreach (var requirement in manifest.Prerequisites.Where(item => item.Executable is { Length: > 0 }))
        {
            var file = manifest.Payload.Single(item => item.Path.Equals(requirement.Executable!.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (prepared is null) await package.ExtractVerifiedAsync(stage, [file], progress, token);
            else
            {
                var target = PackageSafety.UnderRoot(stage, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                FileTransactionJournal.CopyDurable(prepared.Source(file, plan), target);
                if (new FileInfo(target).Length != file.Size || !PackageSafety.HashEquals(PackageSafety.HashFile(target), file.Sha256))
                    throw new CryptographicException("The privileged prerequisite differs from its signed payload.");
            }
        }
        return await WindowsIntegration.InstallPrerequisitesAsync(manifest, plan, stage, options, progress, token);
    }
}
