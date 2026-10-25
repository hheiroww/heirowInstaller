using System.Diagnostics;
using HeirowInstaller.Core;

namespace HeirowInstaller.App;

internal static class DesktopUpdateHandoff
{
    internal static bool IsRunningProduct(InstalledProduct product)
    {
        var executable = Environment.ProcessPath;
        if (executable is null) return false;
        var root = Path.GetFullPath(product.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(executable).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    internal static void Start(string download, UpdateFeed feed, InstalledProduct product, InstallOptions options)
    {
        if (product.Scope == "machine" && string.IsNullOrWhiteSpace(product.PublisherKeySha256))
            throw new System.Security.Cryptography.CryptographicException("Administrator self-updates require the installed product's pinned publisher key. Reinstall from the publisher's signed installer before updating it.");
        if (!feed.PackageType.Equals("hinst", StringComparison.OrdinalIgnoreCase) &&
            !(feed.PackageType.Equals("exe", StringComparison.OrdinalIgnoreCase) && PackageReader.HasEmbeddedPackage(download)))
            throw new InvalidOperationException("Updating heirowInstaller itself requires a .hinst package or a bundled heirowInstaller executable.");
        using (var package = PackageReader.Open(download))
        {
            if (!PackageSafety.HashEquals(PackageSafety.HashFile(download), feed.Sha256))
                throw new System.Security.Cryptography.CryptographicException("The downloaded self-update changed before handoff.");
            package.VerifySignature(product.PublisherKeySha256, product.PublisherKeySha256 is not null);
            if (!package.Manifest.Id.Equals(product.Id, StringComparison.OrdinalIgnoreCase) || package.Manifest.Version != feed.Version)
                throw new InvalidDataException("The update package does not match the selected product and release.");
            package.VerifyAsync().GetAwaiter().GetResult();
        }
        // The signed package does not authenticate an arbitrary downloaded EXE prefix.
        // Run our already installed engine and treat the entire download as package data.
        var executable = CopyHelper(options);
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        if (product.Scope == "machine" && !InstallerEngine.IsElevated) start.Verb = "runas";
        start.ArgumentList.Add("--close-on-complete");
        start.ArgumentList.Add("--install"); start.ArgumentList.Add(download);
        start.ArgumentList.Add("--expected-product-id"); start.ArgumentList.Add(product.Id);
        start.ArgumentList.Add("--expected-version"); start.ArgumentList.Add(feed.Version);
        start.ArgumentList.Add("--expected-sha256"); start.ArgumentList.Add(feed.Sha256);
        start.ArgumentList.Add("--wait-pid"); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--wait-start"); start.ArgumentList.Add(Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!product.UseOriginalInstallDirectory) { start.ArgumentList.Add("--target"); start.ArgumentList.Add(product.InstallDirectory); }
        start.ArgumentList.Add("--scope"); start.ArgumentList.Add(product.Scope);
        start.ArgumentList.Add("--features"); start.ArgumentList.Add(string.Join(',', product.Features));
        if (options.StateRoot is not null) { start.ArgumentList.Add("--state-root"); start.ArgumentList.Add(options.StateRoot); }
        if (options.Isolated) start.ArgumentList.Add("--isolated");
        if (product.PublisherKeySha256 is { } trustedKey) { start.ArgumentList.Add("--trust-key"); start.ArgumentList.Add(trustedKey); start.ArgumentList.Add("--require-signature"); }
        if (!options.RegisterProduct) start.ArgumentList.Add("--no-register");
        if (options.LogPath is not null) { start.ArgumentList.Add("--log"); start.ArgumentList.Add(options.LogPath); }
        foreach (var value in options.LegacyArguments) { start.ArgumentList.Add("--backend-arg"); start.ArgumentList.Add(value); }
        start.ArgumentList.Add("--launch");
        _ = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the update installer.");
    }

    private static string CopyHelper(InstallOptions options)
    {
        var helperRoot = options.Isolated
            ? Path.Combine(options.StateRoot ?? throw new InvalidOperationException("Isolated self-updates require a separate state root."), ".update-host")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "heirowInstaller", "UpdateHost");
        PackageSafety.EnsureNoReparsePoints(helperRoot);
        var directory = TrustedHelperStaging.NewDirectoryPath(helperRoot, "heirowInstaller.exe");
        Directory.CreateDirectory(directory);
        var source = Path.GetFullPath(AppContext.BaseDirectory);
        PackageSafety.EnsureNoReparsePoints(source);
        // A published single file includes these dependencies. Copying them also
        // supports an unpackaged development launch without locking its install.
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            PackageSafety.EnsureNoReparsePoints(file);
            var relative = Path.GetRelativePath(source, file);
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".dll" or ".exe" or ".json" or ".config" or ".dat" or ".png" or ".ico" or ".xnb")) continue;
            var destination = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
        var executable = Path.Combine(directory, "heirowInstaller.exe");
        File.Copy(Environment.ProcessPath!, executable, true);
        return executable;
    }
}
