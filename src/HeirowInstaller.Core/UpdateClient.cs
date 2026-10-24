using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace HeirowInstaller.Core;

/// <summary>Downloads complete installers over HTTPS and verifies the feed's expected SHA256 before executing them.</summary>
public sealed class UpdateClient : IDisposable
{
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly bool allowLoopback;
    private readonly IUpdateTransport? transport;
    public UpdateClient(bool allowLoopback = false, HttpClient? httpClient = null, IUpdateTransport? transport = null)
    {
        this.allowLoopback = allowLoopback;
        this.transport = transport;
        ownsClient = httpClient is null;
        client = httpClient ?? new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
    }

    public async Task<UpdateCheckResult> CheckAsync(string feedUrl, string currentVersion, string expectedProductId, CancellationToken cancellationToken = default)
    {
        PackageSafety.ValidateId(expectedProductId);
        _ = PackageVersion.Parse(currentVersion);
        using var response = await GetAsync(ValidateUrl(feedUrl), 1024 * 1024, null, cancellationToken);
        if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidDataException("Update feed is too large.");
        using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[16384]; int read;
        while ((read = await input.ReadAsync(bytes, cancellationToken)) > 0)
        {
            if (buffer.Length + read > 1024 * 1024) throw new InvalidDataException("Update feed is too large.");
            await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken);
        }
        var feed = JsonSerializer.Deserialize<UpdateFeed>(buffer.ToArray(), InstallerJson.Options) ?? throw new InvalidDataException("Update feed is empty.");
        ValidateFeed(feed);
        if (!feed.ProductId.Equals(expectedProductId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update feed belongs to a different product.");
        return new() { Available = PackageVersion.Parse(feed.Version).CompareTo(PackageVersion.Parse(currentVersion)) > 0, CurrentVersion = currentVersion, Feed = feed };
    }

    public async Task<string> DownloadAsync(UpdateFeed feed, string destinationDirectory, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateFeed(feed);
        var extension = feed.PackageType.ToLowerInvariant() switch { "hinst" => ".hinst", "exe" => ".exe", "msi" => ".msi", _ => throw new InvalidDataException("Unsupported update installer type.") };
        destinationDirectory = Path.GetFullPath(destinationDirectory);
        PackageSafety.EnsureNoReparsePoints(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var destination = PackageSafety.UnderRoot(destinationDirectory, feed.ProductId + "-" + feed.Version.Replace('+', '_') + extension);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var response = await GetAsync(ValidateUrl(feed.InstallerUrl), feed.Size > 0 ? feed.Size : 16L * 1024 * 1024 * 1024, progress, cancellationToken);
            var length = response.Content.Headers.ContentLength;
            if (length > 16L * 1024 * 1024 * 1024 || (feed.Size > 0 && length.HasValue && length != feed.Size)) throw new InvalidDataException("Installer download size does not match the update feed.");
            using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
            {
                var buffer = new byte[131072]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > 16L * 1024 * 1024 * 1024 || (feed.Size > 0 && total > feed.Size)) throw new InvalidDataException("Installer exceeds the declared download size.");
                    hash.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    var expected = feed.Size > 0 ? feed.Size : length ?? 0;
                    progress?.Report(new("download", $"Downloaded {total:N0} bytes", expected > 0 ? (int)Math.Min(1000, total * 1000 / expected) : 0, 1000));
                }
                if (feed.Size > 0 && total != feed.Size) throw new InvalidDataException("Installer download is incomplete.");
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!PackageSafety.HashEquals(actual, feed.Sha256)) throw new CryptographicException("Downloaded installer SHA256 does not match the publisher's update feed.");
            File.Move(temporary, destination, true);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<InstallResult> ApplyAsync(string downloadPath, UpdateFeed feed, InstallOptions? options = null,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        ValidateFeed(feed);
        if (!PackageSafety.HashEquals(PackageSafety.HashFile(downloadPath), feed.Sha256)) throw new CryptographicException("Downloaded installer was changed before execution.");
        var installed = InstallerEngine.ListInstalled(options.StateRoot).FirstOrDefault(x => x.Id.Equals(feed.ProductId, StringComparison.OrdinalIgnoreCase) && (options.Scope is null || x.Scope == options.Scope));
        options.TrustedPublisherKeySha256 ??= installed?.PublisherKeySha256;
        if (options.TrustedPublisherKeySha256 is not null) options.RequireSignature = true;
        if (feed.PackageType.Equals("hinst", StringComparison.OrdinalIgnoreCase) ||
            (feed.PackageType.Equals("exe", StringComparison.OrdinalIgnoreCase) && PackageReader.HasEmbeddedPackage(downloadPath)))
        {
            using var package = PackageReader.Open(downloadPath);
            package.VerifySignature(options.TrustedPublisherKeySha256, options.RequireSignature);
            await package.VerifyAsync(progress, cancellationToken);
            if (!package.Manifest.Id.Equals(feed.ProductId, StringComparison.OrdinalIgnoreCase) || package.Manifest.Version != feed.Version)
                throw new InvalidDataException("Update package identity does not match its feed.");
            // A catalog signature authorizes the package data, never an executable prefix attached to it.
            // Always use this already trusted engine to apply embedded update packages.
            return await new InstallerEngine().InstallAsync(package, options, progress, cancellationToken);
        }
        if (options.Isolated) throw new InvalidOperationException("Isolated validation cannot execute downloaded installers.");
        if (options.RequireSignature) throw new CryptographicException("This update is not an heirowInstaller package or bundle with the required pinned publisher signature.");
        List<string> arguments;
        string executable;
        if (feed.PackageType.Equals("msi", StringComparison.OrdinalIgnoreCase))
        {
            executable = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "msiexec.exe");
            arguments = ["/i", Path.GetFullPath(downloadPath), options.Silent ? "/qn" : "/passive", "/norestart", .. feed.Arguments];
        }
        else { executable = Path.GetFullPath(downloadPath); arguments = feed.Arguments.Count > 0 ? feed.Arguments : ["--silent"]; }
        cancellationToken.ThrowIfCancellationRequested();
        var exit = await WindowsIntegration.RunProcessAsync(executable, arguments, CancellationToken.None,
            msiArguments: feed.PackageType.Equals("msi", StringComparison.OrdinalIgnoreCase));
        if (exit is not (0 or 3010 or 1641)) throw new InvalidOperationException($"Update installer failed with exit code {exit}.");
        return new() { ProductId = feed.ProductId, Version = feed.Version, InstallDirectory = options.TargetDirectory ?? "", RebootRequired = exit is 3010 or 1641 };
    }

    private void ValidateFeed(UpdateFeed feed)
    {
        PackageSafety.ValidateId(feed.ProductId);
        _ = PackageVersion.Parse(feed.Version);
        _ = ValidateUrl(feed.InstallerUrl);
        if (!PackageSafety.HashPattern().IsMatch(feed.Sha256)) throw new InvalidDataException("Update feed requires a SHA256 digest.");
        if (feed.Size < 0) throw new InvalidDataException("Update size cannot be negative.");
        if (feed.PackageType.ToLowerInvariant() is not ("hinst" or "msi" or "exe")) throw new InvalidDataException("Update package type must be hinst, msi, or exe.");
    }
    private Uri ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 ||
            (uri.Scheme != Uri.UriSchemeHttps && !(allowLoopback && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            throw new InvalidDataException("Installer and update URLs must use HTTPS. HTTP is permitted only for explicitly enabled loopback validation.");
        return uri;
    }
    private async Task<HttpResponseMessage> GetAsync(Uri uri, long maximumResponseBytes, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        for (var redirects = 0; redirects < 6; redirects++)
        {
            var response = transport is null
                ? await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token)
                : await transport.GetAsync(uri, maximumResponseBytes, progress, token);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var next = response.Headers.Location;
                response.Dispose();
                if (next is null) throw new HttpRequestException("Redirect did not provide a destination.");
                uri = ValidateUrl(new Uri(uri, next).AbsoluteUri); continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; } catch { response.Dispose(); throw; }
        }
        throw new HttpRequestException("Too many download redirects.");
    }
    public void Dispose() { if (ownsClient) client.Dispose(); }
}
