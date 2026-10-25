using System.Net;
using HeirowInstaller.Core;

namespace HeirowUpdater.SocketJack;

/// <summary>Uses the public SocketJack networking client for each HTTP request. Redirect decisions stay in UpdateClient.</summary>
public sealed class SocketJackUpdateTransport : IUpdateTransport
{
    private readonly Uri? credentialOrigin;
    private readonly Func<ApplicationSessionSnapshot> session;

    public SocketJackUpdateTransport(Uri? credentialOrigin = null, string? cookie = null, string? bearerToken = null)
    {
        if ((cookie is not null || bearerToken is not null) && credentialOrigin is null)
            throw new ArgumentException("Credentials require an explicit origin.");
        if (credentialOrigin is not null && (!credentialOrigin.IsAbsoluteUri || credentialOrigin.UserInfo.Length > 0 ||
            (credentialOrigin.Scheme != "https" && !(credentialOrigin.Scheme == "http" && credentialOrigin.IsLoopback))))
            throw new ArgumentException("Credential origin must use HTTPS.", nameof(credentialOrigin));
        this.credentialOrigin = credentialOrigin;
        var snapshot = new ApplicationSessionSnapshot(cookie, bearerToken);
        session = () => snapshot;
    }

    internal SocketJackUpdateTransport(Uri credentialOrigin, Func<ApplicationSessionSnapshot> session)
        : this(credentialOrigin) => this.session = session;

    public async Task<HttpResponseMessage> GetAsync(Uri uri, long maximumResponseBytes, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0) throw new ArgumentException("Unsupported update URL.");
        var temporary = Path.Combine(Path.GetTempPath(), "heirowUpdater-network-" + Guid.NewGuid().ToString("N") + ".tmp");
        var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 131072,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        try
        {
            using var client = new global::SocketJack.Net.HttpClient { Timeout = TimeSpan.FromSeconds(30), MaxRedirects = 0 };
            client.DefaultHeaders["User-Agent"] = $"heirowUpdater/{typeof(SocketJackUpdateTransport).Assembly.GetName().Version} SocketJack/2026.10.24";
            client.DefaultHeaders["Accept-Encoding"] = "identity";
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (credentialOrigin is not null && SameOrigin(uri, credentialOrigin))
            {
                var current = session();
                if (!string.IsNullOrWhiteSpace(current.Cookie)) headers["Cookie"] = current.Cookie;
                if (!string.IsNullOrWhiteSpace(current.BearerToken)) headers["Authorization"] = "Bearer " + current.BearerToken;
            }
            using var limited = new LimitedDestination(file, Math.Max(maximumResponseBytes, 64 * 1024), progress, leaveOpen: true);
            var response = await client.SendOnceAsync("GET", uri.AbsoluteUri, headers, null, limited, null, cancellationToken)
                .WaitAsync(TimeSpan.FromMinutes(30), cancellationToken);
            if (response is null) throw new HttpRequestException("SocketJack received no HTTP response.");
            var status = response.Context?.StatusCode?.Split(' ', 2)[0];
            if (!int.TryParse(status, out var code)) code = response.StatusCodeNumber;
            if (code is < 100 or > 599) throw new HttpRequestException("SocketJack received an invalid HTTP status.");
            await file.FlushAsync(cancellationToken); file.Position = 0;
            var result = new HttpResponseMessage((HttpStatusCode)code)
            {
                Content = new StreamContent(file), RequestMessage = new(HttpMethod.Get, uri)
            };
            foreach (var header in response.Headers)
            {
                if (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                if (!result.Headers.TryAddWithoutValidation(header.Key, header.Value)) result.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return result;
        }
        catch { await file.DisposeAsync(); throw; }
    }

    private static bool SameOrigin(Uri a, Uri b) => a.Scheme.Equals(b.Scheme, StringComparison.OrdinalIgnoreCase) &&
        a.IdnHost.Equals(b.IdnHost, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    private sealed class LimitedDestination(Stream destination, long maximumBytes, IProgress<InstallProgress>? progress, bool leaveOpen) : Stream
    {
        private long written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() => destination.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) { Reserve(count); destination.Write(buffer, offset, count); Report(); }
        public override void Write(ReadOnlySpan<byte> buffer) { Reserve(buffer.Length); destination.Write(buffer); Report(); }
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { Reserve(count); await destination.WriteAsync(buffer.AsMemory(offset, count), cancellationToken); Report(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Reserve(buffer.Length); await destination.WriteAsync(buffer, cancellationToken); Report(); }
        private void Reserve(int count) { if (count > maximumBytes - written) throw new InvalidDataException("Update response exceeds its allowed size."); written += count; }
        private void Report() => progress?.Report(new("network", $"SocketJack received {written:N0} bytes", 0, 0));
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing && !leaveOpen) destination.Dispose(); base.Dispose(disposing); }
    }
}
