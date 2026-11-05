#if WINDOWS
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using HeirowInstaller.Core;

namespace HeirowInstaller.App;

internal static partial class ElevatedInstallWorker
{
    private sealed record WorkerDownloadRequest(string Url, long MaximumBytes);
    private sealed record WorkerDownloadReply(int Status, string? Redirect, string? Sha256, long Size);
    private sealed record DownloadAuthority(string Sha256, long Size);

    private static Dictionary<string, DownloadAuthority> DownloadsFor(PackageManifest manifest)
    {
        var result = new Dictionary<string, DownloadAuthority>(StringComparer.Ordinal);
        foreach (var item in manifest.SharedPackages ?? [])
        {
            var url = new Uri(item.DownloadUrl).AbsoluteUri;
            if (result.TryGetValue(url, out var existing) && (!PackageSafety.HashEquals(existing.Sha256, item.Sha256) || existing.Size != item.Size))
                throw new InvalidDataException("The signed package gives one dependency download conflicting content.");
            result[url] = new(item.Sha256, item.Size);
        }
        foreach (var prerequisite in manifest.Prerequisites.Where(item => item.DownloadUrl is not null && item.Sha256 is not null))
        {
            var uri = new Uri(prerequisite.DownloadUrl!).AbsoluteUri;
            if (result.TryGetValue(uri, out var existing) && !PackageSafety.HashEquals(existing.Sha256, prerequisite.Sha256!))
                throw new InvalidDataException("The signed package gives one download conflicting hashes.");
            if (!result.ContainsKey(uri)) result[uri] = new(prerequisite.Sha256!, 0);
        }
        return result;
    }
    private static string WorkerDownloadRoot(string userData, string? fixture, string nonce) => Path.Combine(
        fixture ?? Path.Combine(userData, "SocketJack", "heirowInstaller", "downloads"), "worker-" + nonce[..24]);
    private static Uri AddRedirect(Dictionary<string, DownloadAuthority> authorities, string source, string location)
    {
        var previous = new Uri(source); var next = new Uri(previous, location);
        if (next.Scheme != previous.Scheme || next.Host != previous.Host || next.Port != previous.Port || next.UserInfo.Length != 0 || next.Fragment.Length != 0)
            throw new InvalidDataException("The signed download redirected outside its approved origin.");
        authorities[next.AbsoluteUri] = authorities[source]; return next;
    }

    /// <summary>Only the original medium coordinator performs credentialed HTTP. The worker receives pinned, read-leased files.</summary>
    private sealed class ParentDownloadBroker : IDisposable
    {
        private readonly Dictionary<string, DownloadAuthority> authorities;
        private readonly InstallOptions options;
        private readonly IProgress<InstallProgress>? progress;
        private readonly string root;
        private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromMinutes(30) };
        private readonly Dictionary<string, FileStream> leases = new(StringComparer.OrdinalIgnoreCase);
        internal ParentDownloadBroker(PackageManifest manifest, InstallOptions options, string? fixture, string nonce, IProgress<InstallProgress>? progress)
        {
            authorities = DownloadsFor(manifest); this.options = options; this.progress = progress;
            root = WorkerDownloadRoot(InstallerPlatform.UserData, fixture, nonce);
        }
        internal async Task<WorkerDownloadReply> GetAsync(WorkerDownloadRequest request, CancellationToken token)
        {
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || !authorities.TryGetValue(uri.AbsoluteUri, out var expected) ||
                request.MaximumBytes <= 0 || request.MaximumBytes > (expected.Size > 0 ? expected.Size : 16L * 1024 * 1024 * 1024))
                throw new InvalidDataException("The worker requested a download absent from the reviewed signed package.");
            var path = PackageSafety.UnderRoot(root, expected.Sha256.ToLowerInvariant() + ".download");
            if (File.Exists(path))
            {
                await VerifyAsync(path, expected, request.MaximumBytes, token);
                if (!leases.ContainsKey(path)) leases.Add(path, File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                return new(200, null, expected.Sha256, new FileInfo(path).Length);
            }
            using var response = options.SharedPackageTransport is { } transport
                ? await transport.GetAsync(uri, request.MaximumBytes, progress, token)
                : await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var next = AddRedirect(authorities, uri.AbsoluteUri, response.Headers.Location?.OriginalString ?? throw new InvalidDataException("Missing signed download redirect."));
                return new((int)response.StatusCode, next.AbsoluteUri, null, 0);
            }
            if (!response.IsSuccessStatusCode) return new((int)response.StatusCode, null, null, 0);
            if (response.Content.Headers.ContentLength > request.MaximumBytes || expected.Size > 0 && response.Content.Headers.ContentLength is { } length && length != expected.Size)
                throw new InvalidDataException("A signed dependency download has a different length.");
            PackageSafety.EnsureNoReparsePoints(root); Directory.CreateDirectory(root);
            var drive = new DriveInfo(Path.GetPathRoot(root)!);
            if (expected.Size > 0 && drive.AvailableFreeSpace < expected.Size + 16 * 1024 * 1024)
                throw new IOException("There is insufficient space to stage the signed dependency.");
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[131072]; long total = 0; int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    if (count > request.MaximumBytes - total) throw new InvalidDataException("The signed dependency exceeds its download bound.");
                    total += count; hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token);
                    progress?.Report(new("dependencies", $"Downloaded {total:N0} dependency bytes", expected.Size > 0 ? (int)Math.Min(1000, total * 1000 / expected.Size) : 0, expected.Size > 0 ? 1000 : 0));
                }
                if (expected.Size > 0 && total != expected.Size || !PackageSafety.HashEquals(Convert.ToHexString(hash.GetHashAndReset()), expected.Sha256))
                    throw new CryptographicException("The signed dependency download is incomplete or changed.");
            }
            leases.Add(path, File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read));
            return new(200, null, expected.Sha256, new FileInfo(path).Length);
        }
        private static async Task VerifyAsync(string path, DownloadAuthority expected, long maximum, CancellationToken token)
        {
            PackageSafety.EnsureNoReparsePoints(path);
            await using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > maximum || expected.Size > 0 && input.Length != expected.Size ||
                !PackageSafety.HashEquals(Convert.ToHexString(await SHA256.HashDataAsync(input, token)), expected.Sha256))
                throw new CryptographicException("An existing staged dependency changed and was retained for review.");
        }
        public void Dispose() { foreach (var lease in leases.Values) lease.Dispose(); http.Dispose(); }
    }

    private sealed class WorkerDownloadTransport(WorkerChannel channel, ConcurrentDictionary<string, TaskCompletionSource<WorkerDownloadReply>> pending,
        PackageManifest manifest, InstallerUserContext original, WorkerRequest request) : IUpdateTransport
    {
        private readonly Dictionary<string, DownloadAuthority> authorities = DownloadsFor(manifest);
        public async Task<HttpResponseMessage> GetAsync(Uri uri, long maximumResponseBytes, IProgress<InstallProgress>? progress, CancellationToken token)
        {
            if (!authorities.TryGetValue(uri.AbsoluteUri, out var expected)) throw new InvalidDataException("The worker requested an undeclared dependency.");
            var id = Guid.NewGuid().ToString("N"); var completion = new TaskCompletionSource<WorkerDownloadReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pending.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate worker download.");
            WorkerDownloadReply reply;
            try
            {
                await channel.SendAsync(new("download") { RequestId = id, Download = new(uri.AbsoluteUri, maximumResponseBytes) }, token);
                reply = await completion.Task.WaitAsync(token);
            }
            finally { pending.TryRemove(id, out _); }
            if (reply.Status is < 100 or > 599) throw new InvalidDataException("The coordinator returned an invalid download status.");
            var response = new HttpResponseMessage((HttpStatusCode)reply.Status) { RequestMessage = new(HttpMethod.Get, uri) };
            if (reply.Status is 301 or 302 or 303 or 307 or 308)
            {
                response.Headers.Location = AddRedirect(authorities, uri.AbsoluteUri, reply.Redirect ?? throw new InvalidDataException("Missing coordinated download redirect."));
                response.Content = new ByteArrayContent([]); return response;
            }
            if (reply.Status is >= 200 and < 300)
            {
                if (reply.Sha256 is null || !PackageSafety.HashEquals(reply.Sha256, expected.Sha256) || reply.Size < 0 || reply.Size > maximumResponseBytes || expected.Size > 0 && reply.Size != expected.Size)
                    throw new CryptographicException("The coordinator returned an unbound dependency file.");
                var root = WorkerDownloadRoot(original.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), request.IsolatedFixtureRoot, request.Nonce);
                var path = PackageSafety.UnderRoot(root, expected.Sha256.ToLowerInvariant() + ".download");
                var file = original.RunAsOriginalUser(() =>
                {
                    PackageSafety.EnsureNoReparsePoints(path);
                    return File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                });
                if (file.Length != reply.Size) { file.Dispose(); throw new CryptographicException("The coordinated dependency changed before it was opened."); }
                response.Content = new StreamContent(file); response.Content.Headers.ContentLength = reply.Size;
            }
            else response.Content = new ByteArrayContent([]);
            return response;
        }
    }
}
#endif
