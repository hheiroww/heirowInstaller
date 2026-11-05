namespace HeirowInstaller.Core;

public sealed partial class InstallerEngine
{
    private async Task<InstallResult> RepairSelectedFilesAsync(InstalledProduct installed, InstallOptions options,
        IProgress<InstallProgress>? progress, CancellationToken token)
    {
        if (installed.PublisherKeySha256 is null || installed.IsMsi || installed.IsExecutable)
            throw new InvalidDataException("Online file repair requires a signed native installed application.");
        var source = FileUpdateSource.Read(installed.PackagePath, installed.PublisherKeySha256);
        // Repair is tied to the installed immutable release, even if a newer feed is active.
        options.AllowDowngrade = true;
        using var client = new FileUpdateClient(allowLoopback: installed.Isolated, transport: options.SharedPackageTransport);
        var stageRoot = Path.Combine(options.StateRoot ?? Path.GetDirectoryName(Path.GetDirectoryName(installed.ReceiptPath))!, ".file-repairs");
        using var stage = await client.PrepareAsync(source.ToFeed(), installed, stageRoot, options, progress, token);
        return await ApplyPreparedFileUpdateAsync(stage, options, progress, token);
    }
}
