using System.Diagnostics;
using System.Buffers.Binary;
using System.Drawing.Imaging;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;

namespace HeirowInstaller.App;

internal sealed partial class AppController
{
    private string? wizardArtworkDirectory;
    private readonly List<string> wizardArtworkFiles = [];
    internal string? WizardArtworkPath { get; private set; }
    internal InstallResult? LastInstallation { get; private set; }
    internal bool OpenApplicationOnFinish { get; set; } = true;
    internal bool CanOpenInstalledApplication => MaintenanceOperation != "uninstall" && Manifest is { EntryPoint.Length: > 0 } && LastInstallation is { Success: true } result &&
        File.Exists(PackageSafety.UnderRoot(result.InstallDirectory, Manifest.EntryPoint));

    internal async Task FinishAsync()
    {
        if (!IsWizard || Busy || Stage != SetupStage.Complete || LastInstallation is null) return;
        try
        {
            if (OpenApplicationOnFinish && CanOpenInstalledApplication) await LaunchInstalledApplicationAsync();
            if (arguments.WizardSmokeDirectory is { } acceptance)
                await File.WriteAllTextAsync(Path.Combine(acceptance, "finish-action.json"), System.Text.Json.JsonSerializer.Serialize(new { finish = true, openApplication = OpenApplicationOnFinish, productId = Manifest!.Id, version = LastInstallation.Version }));
            await game.OnUiAsync(game.Exit);
        }
        catch (Exception error) { await ErrorAsync("The application could not open", error); }
    }

    private Task LaunchInstalledApplicationAsync()
    {
        var manifest = Manifest ?? throw new InvalidOperationException("The installed application is unavailable.");
        var result = LastInstallation ?? throw new InvalidOperationException("Installation has not completed.");
        var product = InstallerEngine.ListInstalled(arguments.StateRoot).SingleOrDefault(x => x.Id == manifest.Id && x.Version == result.Version &&
            Path.GetFullPath(x.InstallDirectory).Equals(Path.GetFullPath(result.InstallDirectory), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The installed application receipt is unavailable. Repair the application before opening it.");
        var executable = PackageSafety.UnderRoot(product.InstallDirectory, manifest.EntryPoint);
        PackageSafety.EnsureNoReparsePoints(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("The installed application executable is missing.", executable);
        if (product.Files.FirstOrDefault(x => PackageSafety.UnderRoot(product.InstallDirectory, x.Path).Equals(executable, StringComparison.OrdinalIgnoreCase)) is { } owned &&
            !PackageSafety.HashEquals(PackageSafety.HashFile(executable), owned.Sha256))
            throw new InvalidDataException("The installed application changed after installation. Repair it before opening it.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = product.InstallDirectory };
        if (Plan!.UpdateCompanionEnabled && manifest.UpdateCompanion is { } companion)
        {
            var profilePath = PackageSafety.UnderRoot(product.InstallDirectory, companion.ConfigurationPath);
            _ = ApplicationUpdater.ReadConfiguration(profilePath);
            var updater = UpdaterHostResolver.FindExecutable(executable, product.InstallDirectory)
                ?? throw new FileNotFoundException("The optional updater is missing. Repair this application before opening it.");
            start.FileName = updater;
            start.ArgumentList.Add("launch"); start.ArgumentList.Add("--config"); start.ArgumentList.Add(profilePath);
        }
        _ = Process.Start(start) ?? throw new InvalidOperationException("Windows did not open the installed application.");
        return Task.CompletedTask;
    }

    // Artwork is selected only from verified payload metadata, never an external URL or arbitrary path.
    private async Task<string?> ReadWizardArtworkAsync(PackageReader package, CancellationToken token)
    {
        var authored = package.Manifest.Shortcuts.Select(x => x.Icon).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        var candidate = package.Manifest.Payload.FirstOrDefault(x => authored is not null && x.Path.Equals(authored, StringComparison.OrdinalIgnoreCase));
        candidate ??= package.Manifest.Payload.FirstOrDefault(x => new[] { "icon", "logo", "app" }.Contains(Path.GetFileNameWithoutExtension(x.Path), StringComparer.OrdinalIgnoreCase) &&
            new[] { ".png", ".ico" }.Contains(Path.GetExtension(x.Path), StringComparer.OrdinalIgnoreCase));
        if (candidate is null || candidate.Size > 2 * 1024 * 1024 || !new[] { ".png", ".ico" }.Contains(Path.GetExtension(candidate.Path), StringComparer.OrdinalIgnoreCase)) return null;
        DisposeWizardArtwork();
        wizardArtworkDirectory = Path.Combine(Path.GetTempPath(), "heirowInstaller-wizard-" + Guid.NewGuid().ToString("N"));
        await package.ExtractVerifiedAsync(wizardArtworkDirectory, [candidate], cancellationToken: token);
        var source = PackageSafety.UnderRoot(wizardArtworkDirectory, candidate.Path);
        wizardArtworkFiles.Add(source);
        if (Path.GetExtension(source).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            // Check IHDR before invoking an image decoder or allocating a texture.
            using (var input = File.OpenRead(source))
            {
                var header = new byte[24]; await input.ReadExactlyAsync(header, token);
                if (!header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                    !header.AsSpan(12, 4).SequenceEqual("IHDR"u8) || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4)) is < 1 or > 2048 ||
                    BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4)) is < 1 or > 2048) return null;
            }
            using var bitmap = System.Drawing.Image.FromFile(source);
            if (bitmap.Width is < 1 or > 2048 || bitmap.Height is < 1 or > 2048) return null;
            return source;
        }
        using var icon = new System.Drawing.Icon(source, 256, 256);
        using var converted = icon.ToBitmap();
        var png = Path.Combine(wizardArtworkDirectory, "verified-app-icon.png");
        converted.Save(png, ImageFormat.Png); wizardArtworkFiles.Add(png);
        return png;
    }

    private void DisposeWizardArtwork()
    {
        if (wizardArtworkDirectory is not { } directory) return;
        try
        {
            PackageSafety.EnsureNoReparsePoints(directory);
            foreach (var file in wizardArtworkFiles)
            {
                PackageSafety.EnsureNoReparsePoints(file);
                if (File.Exists(file)) File.Delete(file);
                var parent = Path.GetDirectoryName(file);
                while (parent is not null && parent.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                { Directory.Delete(parent); parent = Path.GetDirectoryName(parent); }
            }
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        wizardArtworkFiles.Clear(); wizardArtworkDirectory = null;
    }
}
