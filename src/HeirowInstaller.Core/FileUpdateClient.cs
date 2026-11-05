using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace HeirowInstaller.Core;

/// <summary>Authenticated subset ZIP transport. Redirect destinations must be validated by the caller.</summary>
public interface IFileUpdateTransport : IUpdateTransport
{
    Task<HttpResponseMessage> PostAsync(Uri uri, long maximumResponseBytes, ReadOnlyMemory<byte> json,
        IProgress<InstallProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>Compares actual owned files with signed release metadata and stages only missing/different bytes.</summary>
public sealed class FileUpdateClient : IDisposable
{
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly bool allowLoopback;
    private readonly IUpdateTransport? transport;
    public FileUpdateClient(bool allowLoopback = false, HttpClient? httpClient = null, IUpdateTransport? transport = null)
    {
        this.allowLoopback = allowLoopback; this.transport = transport; ownsClient = httpClient is null;
        client = httpClient ?? new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
    }

    public async Task<PreparedFileUpdate> PrepareAsync(UpdateFeed feed, InstalledProduct installed, string stagingDirectory,
        InstallOptions? options = null, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        if (installed.IsMsi || installed.IsExecutable || installed.PublisherKeySha256 is null)
            throw new NotSupportedException("File selection updates require an existing signed native installation.");
        if (feed.PackageType != "hfiles" || feed.ProductId != installed.Id || feed.ManifestUrl is null || feed.FilesUrl is null ||
            feed.TargetManifestSha256 is null || !PackageSafety.HashPattern().IsMatch(feed.TargetManifestSha256))
            throw new InvalidDataException("The file update offer is missing its signed target binding.");
        if (options.TrustedPublisherKeySha256 is { } explicitPin && !PackageSafety.HashEquals(explicitPin, installed.PublisherKeySha256))
            throw new CryptographicException("The explicit publisher pin differs from the installed publisher.");
        BindOptions(installed, options);
        progress?.Report(new("manifest", "Downloading signed application file information", 0, 0));
        using var response = await GetAsync(Url(feed.ManifestUrl), 6 * 1024 * 1024, progress, cancellationToken);
        var metadata = FileUpdateMetadata.ReadEnvelope(await ReadBoundedAsync(response, 6 * 1024 * 1024, cancellationToken),
            installed.PublisherKeySha256, feed.TargetManifestSha256);
        if (metadata.Manifest.Id != installed.Id || metadata.Manifest.Version != feed.Version)
            throw new InvalidDataException("The signed target differs from the installed product or update offer.");
        if (PackageVersion.Parse(feed.Version).CompareTo(PackageVersion.Parse(installed.Version)) < 0 ||
            PackageVersion.Parse(feed.Version).CompareTo(PackageVersion.Parse(installed.Version)) == 0 && !options.AllowDowngrade)
            throw new InvalidDataException("A file update must advance the installed release; use explicit repair for the current release.");
        options.Features ??= installed.Features.Where(id => metadata.Manifest.Features.Any(feature => feature.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList();
        var stage = PreparedFileUpdate.Create(metadata, installed, stagingDirectory, options, feed);
        try
        {
            using var package = PackageReader.OpenMetadata(stage.MetadataPath);
            var plan = new InstallerEngine().Plan(package, options);
            await stage.InspectAsync(plan, progress, cancellationToken);
            if (stage.RequestedPaths.Count > 0)
            {
                var body = JsonSerializer.SerializeToUtf8Bytes(new { targetManifestSha256 = metadata.ManifestSha256, paths = stage.RequestedPaths }, InstallerJson.Options);
                if (body.Length > 4 * 1024 * 1024) throw new InvalidDataException("The requested file inventory exceeds its supported size.");
                progress?.Report(new("download", $"Downloading {stage.RequestedPaths.Count} changed application files", 0, 0));
                using var zipResponse = await PostAsync(Url(feed.FilesUrl), 2L * 1024 * 1024 * 1024, body, progress, cancellationToken);
                await stage.ReceiveAsync(zipResponse, progress, cancellationToken);
            }
            options.SharedPackageTransport ??= transport;
            await SharedPackages.PrefetchAsync(package, plan, stage.Root, options, progress, cancellationToken);
            await stage.MarkReadyAsync(cancellationToken);
            return stage;
        }
        catch { stage.Dispose(); throw; }
    }

    internal static void BindOptions(InstalledProduct installed, InstallOptions options)
    {
        if (options.Scope is not null && options.Scope != installed.Scope || options.StateRoot is not null &&
            !Path.GetFullPath(options.StateRoot).Equals(Path.GetDirectoryName(Path.GetDirectoryName(installed.ReceiptPath)), InstallerPlatform.PathComparison) ||
            options.TargetDirectory is not null && !Path.GetFullPath(options.TargetDirectory).Equals(installed.InstallDirectory, InstallerPlatform.PathComparison))
            throw new InvalidDataException("A file update cannot change installation identity, destination or scope.");
        options.Scope = installed.Scope; options.StateRoot = Path.GetDirectoryName(Path.GetDirectoryName(installed.ReceiptPath));
        options.TargetDirectory = installed.InstallDirectory; options.Isolated = installed.Isolated;
        options.TrustedPublisherKeySha256 = installed.PublisherKeySha256; options.RequireSignature = true;
        options.RegisterProduct = installed.RegisteredProduct;
    }

    private Uri Url(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
            uri.Scheme != "https" && !(allowLoopback && uri.Scheme == "http" && uri.IsLoopback))
            throw new InvalidDataException("File-update URLs require HTTPS.");
        return uri;
    }
    private async Task<HttpResponseMessage> GetAsync(Uri uri, long maximum, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        for (var count = 0; count < 6; count++)
        {
            var response = transport is null ? await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token) : await transport.GetAsync(uri, maximum, progress, token);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var next = response.Headers.Location; response.Dispose();
                uri = next is null ? throw new HttpRequestException("Missing file-update redirect destination.") : Url(new Uri(uri, next).AbsoluteUri); continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; } catch { response.Dispose(); throw; }
        }
        throw new HttpRequestException("Too many file-update redirects.");
    }
    private async Task<HttpResponseMessage> PostAsync(Uri uri, long maximum, byte[] body, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        // POST redirects are refused: never replay a credentialed selection body to another origin.
        HttpResponseMessage response;
        if (transport is IFileUpdateTransport selected) response = await selected.PostAsync(uri, maximum, body, progress, token);
        else if (transport is not null) throw new NotSupportedException("The configured authenticated transport does not support file-selection updates.");
        else
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        }
        try { response.EnsureSuccessStatusCode(); return response; } catch { response.Dispose(); throw; }
    }
    internal static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("File-update metadata is too large.");
        using var source = await response.Content.ReadAsStreamAsync(token); using var output = new MemoryStream();
        var buffer = new byte[32768]; int count;
        while ((count = await source.ReadAsync(buffer, token)) != 0)
        {
            if (count > maximum - output.Length) throw new InvalidDataException("File-update metadata exceeds its supported size.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return output.ToArray();
    }
    public void Dispose() { if (ownsClient) client.Dispose(); }
}
