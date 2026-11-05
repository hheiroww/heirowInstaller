using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using HeirowInstaller.Core;
using HeirowInstaller.BootstrapContract;

namespace HeirowInstaller.Publishing;

public sealed partial class PublisherClient
{
    /// <summary>Attaches a small signed native web installer to an existing immutable application release.</summary>
    public async Task<WebBootstrapAttachmentReceipt> AttachWebBootstrapAsync(string bootstrapPath, string productId, string version,
        CancellationToken cancellationToken = default, IProgress<PublishProgress>? progress = null, string? trustedPublisherKeySha256 = null)
    {
        RequireNativeAttachmentSession(); RequireAttachmentIdentity(productId, version);
        string path = Path.GetFullPath(bootstrapPath);
        if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Attach a signed native web installer .exe.", nameof(bootstrapPath));
        await using var file = OpenAttachment(path, 20_000_000);
        await Task.Run(() =>
        {
            var binding = WebBootstrapBindings.Read(path, trustedPublisherKeySha256);
            using var document = JsonDocument.Parse(binding.DescriptorBytes); var descriptor = document.RootElement;
            if (!descriptor.TryGetProperty("productId", out var id) || id.GetString() != productId || !descriptor.TryGetProperty("version", out var target) || target.GetString() != version ||
                !descriptor.TryGetProperty("hostKind", out var kind) || kind.GetString() != "installer" ||
                !descriptor.TryGetProperty("publisherKeySha256", out var publisher) || !binding.PublisherKeySha256.Equals(publisher.GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The signed web installer has a different product, version, role or publisher binding.");
        }, cancellationToken).ConfigureAwait(false);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant(); file.Position = 0;
        var bytes = await UploadAttachmentAsync($"api/installer/publisher/bootstrap/{Uri.EscapeDataString(productId)}/{Uri.EscapeDataString(version)}", file, hash, progress, cancellationToken);
        var receipt = JsonSerializer.Deserialize<WebBootstrapAttachmentReceipt>(bytes, Json) ?? throw new InvalidDataException("Publisher returned no web installer receipt.");
        string artifact = productId == "FruityLoopsCollab" ? $"/api/installer/beta/FruityLoopsCollab/{version}/bootstrap.exe" : $"/installers/{productId}/{version}/{productId}-{version}-WebSetup.exe";
        ValidateAttachmentReceipt(receipt.Version, receipt.Size, receipt.Sha256, receipt.InstallerUrl, receipt.Immutable, version, file.Length, hash, artifact);
        if (receipt.ProductId != productId || !receipt.SourcePackagePreserved) throw new InvalidDataException("Publisher did not preserve the requested immutable application release.");
        return receipt;
    }

    /// <summary>Adds a raw signed application representation to an already registered FruityLoopsCollab native beta target.</summary>
    public async Task<BetaApplicationAttachmentReceipt> AttachBetaApplicationAsync(string packagePath, string version,
        CancellationToken cancellationToken = default, IProgress<PublishProgress>? progress = null, string? trustedPublisherKeySha256 = null)
    {
        RequireNativeAttachmentSession(); RequireAttachmentIdentity("FruityLoopsCollab", version);
        string path = Path.GetFullPath(packagePath);
        if (!Path.GetExtension(path).Equals(".hinst", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Attach a signed raw application .hinst.", nameof(packagePath));
        await using var file = OpenAttachment(path, 100_000_000);
        using (var package = PackageReader.Open(path))
        {
            package.VerifySignature(trustedPublisherKeySha256, requireSignature: true);
            if (package.Manifest.Id != "FruityLoopsCollab" || package.Manifest.Version != version || package.Manifest.FormatVersion is < 2 or > 5 || package.Manifest.UpdateCompanion?.UpdateMode is not (null or "files") || package.Manifest.LegacyExecutable is not null || package.Manifest.LegacyMsi is not null)
                throw new InvalidDataException("The raw beta application must match the exact signed native file-update target.");
            await package.VerifyAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant(); file.Position = 0;
        var bytes = await UploadAttachmentAsync("api/installer/publisher/beta-application/" + Uri.EscapeDataString(version), file, hash, progress, cancellationToken);
        var receipt = JsonSerializer.Deserialize<BetaApplicationAttachmentReceipt>(bytes, Json) ?? throw new InvalidDataException("Publisher returned no beta application receipt.");
        ValidateAttachmentReceipt(receipt.Version, receipt.Size, receipt.Sha256, receipt.InstallerUrl, receipt.Immutable, version, file.Length, hash, $"/api/installer/beta/FruityLoopsCollab/{version}/application.hinst");
        return receipt;
    }

    private void RequireNativeAttachmentSession()
    {
        if (client.DefaultRequestHeaders.Authorization is not { Scheme: "Bearer", Parameter.Length: > 0 } || client.DefaultRequestHeaders.Contains("Cookie"))
            throw new InvalidOperationException("Immutable installer attachment requires the native publisher's in-memory administrator bearer session.");
    }
    private static void RequireAttachmentIdentity(string product, string version)
    {
        if (product.Length is < 1 or > 100 || !char.IsAsciiLetterOrDigit(product[0]) || product.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-') ||
            version.Length is < 1 or > 64 || version.Any(c => !char.IsAsciiDigit(c) && c != '.') || !Version.TryParse(version, out _))
            throw new ArgumentException("Supply one exact product ID and numeric release version.");
    }
    private static FileStream OpenAttachment(string path, long maximum)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length is > 0 && file.Length < maximum) return file;
        file.Dispose(); throw new InvalidDataException("The immutable installer attachment exceeds its package size limit.");
    }
    private async Task<byte[]> UploadAttachmentAsync(string route, FileStream file, string hash, IProgress<PublishProgress>? progress, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, route)); request.Headers.Add("X-HeirowInstaller-SHA256", hash);
        request.Content = new AttachmentContent(file, progress);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, token).ConfigureAwait(false);
        return await ReadBoundedAsync(response, token).ConfigureAwait(false);
    }
    private void ValidateAttachmentReceipt(string version, long size, string hash, string url, bool immutable,
        string expectedVersion, long expectedSize, string expectedHash, string expectedPath)
    {
        if (!immutable || version != expectedVersion || size != expectedSize || !string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var artifact) || artifact.UserInfo.Length != 0 || artifact.Query.Length != 0 || artifact.Fragment.Length != 0 ||
            artifact.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority) || artifact.AbsolutePath != expectedPath)
            throw new InvalidDataException("Publisher attachment receipt differs from the exact uploaded artifact.");
    }
    private sealed class AttachmentContent(FileStream file, IProgress<PublishProgress>? progress) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = file.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        {
            var buffer = new byte[131072]; long sent = 0; int read; progress?.Report(new(0, file.Length));
            while ((read = await file.ReadAsync(buffer, token)) != 0)
            { await stream.WriteAsync(buffer.AsMemory(0, read), token); sent += read; progress?.Report(new(sent, file.Length)); }
        }
    }
}

public sealed class WebBootstrapAttachmentReceipt
{
    public string ProductId { get; set; } = "";
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string InstallerUrl { get; set; } = "";
    public bool Immutable { get; set; }
    public bool SourcePackagePreserved { get; set; }
}
public sealed class BetaApplicationAttachmentReceipt
{
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string InstallerUrl { get; set; } = "";
    public bool Immutable { get; set; }
}
