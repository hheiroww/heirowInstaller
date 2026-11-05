using System.Security.Cryptography;

namespace HeirowInstaller.Core;

public static partial class SharedPackages
{
    /// <summary>Acquires signed archives with the initiating account; activation and extraction remain in the apply phase.</summary>
    public static async Task PrefetchAsync(PackageReader package, InstallPlan plan, string preparedRoot, InstallOptions options,
        IProgress<InstallProgress>? progress = null, CancellationToken token = default)
    {
        ValidateAuthority(package);
        package.VerifySignature(options.TrustedPublisherKeySha256, requireSignature: true);
        var signed = System.Text.Json.JsonSerializer.Deserialize<PackageManifest>(package.ReadManifestBytes(), InstallerJson.Options)!;
        if (signed.Id != plan.Manifest.Id || signed.Version != plan.Manifest.Version) throw new CryptographicException("Dependency prefetch differs from its signed target.");
        var state = options.StateRoot ?? InstallerEngine.DefaultStateRoot(plan.Scope);
        var store = Root(plan.Scope, state, options.Isolated);
        var staging = PackageSafety.UnderRoot(preparedRoot, "dependencies");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var definition in (signed.SharedPackages ?? []).Where(item => item.Feature is null || plan.Features.Contains(item.Feature, StringComparer.OrdinalIgnoreCase)))
        {
            if (!PackageSafety.HashEquals(definition.PublisherKeySha256, package.PublisherKeySha256!)) throw new CryptographicException("A dependency belongs to another publisher.");
            var existing = PackageSafety.UnderRoot(PackageRoot(store, definition), "package.hinst");
            var path = File.Exists(existing) ? existing : PackageSafety.UnderRoot(staging, definition.Sha256.ToLowerInvariant() + ".hinst");
            if (!File.Exists(path))
            {
                PackageSafety.EnsureNoReparsePoints(staging); Directory.CreateDirectory(staging);
                RequireSpace(staging, definition.Size);
                await DownloadAsync(definition, Origin(signed), path, options, client, progress, token);
            }
            await VerifyFileAsync(path, definition.Size, definition.Sha256, token);
            using var archive = PackageReader.Open(path); ValidateArchive(archive, definition, plan.Scope);
            await archive.VerifyAsync(progress, token);
            progress?.Report(new("dependencies", "Staged verified dependency " + definition.Id, 1, 1));
        }
    }

    private static async Task CopyPreparedArchiveAsync(SharedPackageDefinition definition, string destination, InstallOptions options, CancellationToken token)
    {
        var directory = options.PreparedSharedPackageDirectory ?? throw new InvalidOperationException("No prepared dependency directory was bound.");
        var source = PackageSafety.UnderRoot(directory, definition.Sha256.ToLowerInvariant() + ".hinst");
        FileStream Open()
        {
            PackageSafety.EnsureNoReparsePoints(source);
            if (!File.Exists(source)) throw new InvalidDataException("A selected dependency is absent from the prepared update. Retry preparation while signed in before closing the application.");
            return new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        // Open as the kernel-bound original account: a raced junction cannot gain privileged file-read authority.
        await using var input = OperatingSystem.IsWindows() && InstallerUserContext.Current is { } user ? user.RunAsOriginalUser(Open) : Open();
        if (input.Length != definition.Size) throw new CryptographicException("A prepared dependency has a different signed length.");
        if (!PackageSafety.HashEquals(Convert.ToHexString(await SHA256.HashDataAsync(input, token)), definition.Sha256))
            throw new CryptographicException("A prepared dependency has changed since download.");
        input.Position = 0;
        var temporary = destination + ".prepared";
        PackageSafety.EnsureNoReparsePoints(temporary);
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.WriteThrough))
        { await input.CopyToAsync(output, token); await output.FlushAsync(token); output.Flush(true); }
        await VerifyFileAsync(temporary, definition.Size, definition.Sha256, token);
        File.Move(temporary, destination, false);
    }
}
