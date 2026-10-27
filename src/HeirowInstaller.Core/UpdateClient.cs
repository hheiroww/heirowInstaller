using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace HeirowInstaller.Core;

/// <summary>Downloads verified update data over HTTPS. Signed incremental packages are reconstructed and applied by the trusted engine.</summary>
public sealed class UpdateClient : IDisposable
{
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly bool allowLoopback;
    private readonly IUpdateTransport? transport;
    private readonly bool requireIncrementalUpdates;
    private readonly TimeSpan checkTimeout;
    public UpdateClient(bool allowLoopback = false, HttpClient? httpClient = null, IUpdateTransport? transport = null, bool requireIncrementalUpdates = false)
        : this(TimeSpan.FromSeconds(15), allowLoopback, httpClient, transport, requireIncrementalUpdates) { }

    public UpdateClient(TimeSpan checkTimeout, bool allowLoopback = false, HttpClient? httpClient = null, IUpdateTransport? transport = null, bool requireIncrementalUpdates = false)
    {
        this.allowLoopback = allowLoopback;
        this.transport = transport;
        this.requireIncrementalUpdates = requireIncrementalUpdates;
        this.checkTimeout = checkTimeout;
        if (this.checkTimeout <= TimeSpan.Zero || this.checkTimeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(checkTimeout));
        ownsClient = httpClient is null;
        client = httpClient ?? new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
    }

    public Task<UpdateCheckResult> CheckAsync(string feedUrl, string currentVersion, string expectedProductId, CancellationToken cancellationToken = default)
        => CheckAsync(feedUrl, currentVersion, expectedProductId, cancellationToken, null);

    public async Task<UpdateCheckResult> CheckAsync(string feedUrl, string currentVersion, string expectedProductId, CancellationToken cancellationToken, IProgress<InstallProgress>? progress)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(checkTimeout);
        try { return await ReadFeedAsync(feedUrl, currentVersion, expectedProductId, progress, deadline.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("The update server took too long to respond. Check your connection and try again."); }
    }

    private async Task<UpdateCheckResult> ReadFeedAsync(string feedUrl, string currentVersion, string expectedProductId, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        PackageSafety.ValidateId(expectedProductId);
        _ = PackageVersion.Parse(currentVersion);
        progress?.Report(new("check", "Connecting to the publisher's update server", 0, 0));
        using var response = await GetAsync(SelectTransition(ValidateUrl(feedUrl), currentVersion), 1024 * 1024, progress, cancellationToken);
        progress?.Report(new("check", "Reading published version information", 0, 0));
        if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidDataException("Update feed is too large.");
        using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[16384]; int read;
        while ((read = await input.ReadAsync(bytes, cancellationToken)) > 0)
        {
            if (buffer.Length + read > 1024 * 1024) throw new InvalidDataException("Update feed is too large.");
            await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new("check", "Validating published version information", 0, 0));
        var feed = JsonSerializer.Deserialize<UpdateFeed>(buffer.ToArray(), InstallerJson.Options) ?? throw new InvalidDataException("Update feed is empty.");
        ValidateFeed(feed);
        if (!feed.ProductId.Equals(expectedProductId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update feed belongs to a different product.");
        var available = PackageVersion.Parse(feed.Version).CompareTo(PackageVersion.Parse(currentVersion)) > 0;
        if (available && feed.PackageType.Equals("hupd", StringComparison.OrdinalIgnoreCase) && feed.BaseVersion != currentVersion)
            throw new InvalidDataException("No signed incremental transition was published for this installed base version.");
        if (available && requireIncrementalUpdates && !feed.PackageType.Equals("hupd", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This application requires a signed file update; its publisher has not provided a matching incremental package.");
        return new() { Available = available, CurrentVersion = currentVersion, Feed = feed };
    }

    public async Task<string> DownloadAsync(UpdateFeed feed, string destinationDirectory, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateFeed(feed);
        RequireIncremental(feed);
        var extension = feed.PackageType.ToLowerInvariant() switch { "hupd" => ".hupd", "hinst" => ".hinst", "exe" => ".exe", "msi" => ".msi", _ => throw new InvalidDataException("Unsupported update package type.") };
        destinationDirectory = Path.GetFullPath(destinationDirectory);
        PackageSafety.EnsureNoReparsePoints(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        using var incrementalDownload = feed.PackageType.Equals("hupd", StringComparison.OrdinalIgnoreCase) ? new IncrementalDownloadWorkspace(destinationDirectory, feed) : null;
        var destination = incrementalDownload?.PackagePath ?? PackageSafety.UnderRoot(destinationDirectory, feed.ProductId + "-" + feed.Version.Replace('+', '_') + extension);
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
            incrementalDownload?.Complete();
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<InstallResult> ApplyAsync(string downloadPath, UpdateFeed feed, InstallOptions? options = null,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        ValidateFeed(feed);
        RequireIncremental(feed);
        if (!PackageSafety.HashEquals(PackageSafety.HashFile(downloadPath), feed.Sha256)) throw new CryptographicException("Downloaded installer was changed before execution.");
        var installed = InstallerEngine.ListInstalled(options.StateRoot).FirstOrDefault(x => x.Id.Equals(feed.ProductId, StringComparison.OrdinalIgnoreCase) && (options.Scope is null || x.Scope == options.Scope));
        options.TrustedPublisherKeySha256 ??= installed?.PublisherKeySha256;
        if (options.TrustedPublisherKeySha256 is not null) options.RequireSignature = true;
        if (feed.PackageType.Equals("hupd", StringComparison.OrdinalIgnoreCase))
        {
            var result = await ApplyIncrementalAsync(downloadPath, feed, installed, options, progress, cancellationToken);
            IncrementalDownloadWorkspace.RemoveApplied(downloadPath, feed);
            return result;
        }
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

    private async Task<InstallResult> ApplyIncrementalAsync(string downloadPath, UpdateFeed feed, InstalledProduct? installed,
        InstallOptions options, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (installed is null) throw new InvalidOperationException("An incremental update requires an existing native installation; use its full signed installer for first installation.");
        if (installed.IsMsi || installed.IsExecutable) throw new NotSupportedException("Opaque MSI/executable installation backends cannot receive file updates. Use an authored native ownership migration first.");
        if (installed.PublisherKeySha256 is null) throw new CryptographicException("The installed application does not have a pinned incremental-update publisher.");
        if (feed.Size > 0 && new FileInfo(downloadPath).Length != feed.Size) throw new InvalidDataException("Incremental package size differs from its feed.");
        if (options.TargetDirectory is not null && !Path.GetFullPath(options.TargetDirectory).Equals(Path.GetFullPath(installed.InstallDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An incremental update cannot move the installed application.");
        var matches = InstallerEngine.ListInstalled(options.StateRoot).Count(product => product.Id.Equals(feed.ProductId, StringComparison.OrdinalIgnoreCase) && (options.Scope is null || product.Scope == options.Scope));
        if (matches != 1) throw new InvalidDataException("Select the exact user or machine installation before applying an incremental update.");
        options.Scope = installed.Scope;
        options.StateRoot ??= Path.GetDirectoryName(Path.GetDirectoryName(installed.ReceiptPath))!;
        options.TargetDirectory = installed.InstallDirectory;
        options.Isolated = installed.Isolated;
        options.RegisterProduct = installed.RegisteredProduct;
        options.RequireSignature = true;
        if (installed.Scope == "machine" && !installed.Isolated && !InstallerEngine.IsElevated) throw new UnauthorizedAccessException("Applying this machine-scoped file update requires administrator privileges.");
        using var update = IncrementalUpdateReader.Open(downloadPath);
        update.VerifySignature(options.TrustedPublisherKeySha256);
        var transition = update.Transition;
        if (transition.ProductId != feed.ProductId || transition.ToVersion != feed.Version || transition.FromVersion != feed.BaseVersion ||
            !PackageSafety.HashEquals(transition.BaseManifestSha256, feed.BaseManifestSha256!) || !PackageSafety.HashEquals(transition.TargetManifestSha256, feed.TargetManifestSha256!))
            throw new InvalidDataException("Signed incremental transition identity or manifest bindings differ from the update feed.");
        options.Features ??= installed.Features.Where(id => update.TargetManifest.Features.Any(feature => feature.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList();
        if (installed.Version == feed.Version)
        {
            using var cached = PackageReader.Open(installed.PackagePath);
            var plan = new InstallerEngine().Plan(cached, options);
            var complete = FileTransactionRecoveryContext.ForInstall(cached, plan, installed, options);
            if (!FileTransactionRecovery.TargetIsInstalled(complete) || !PackageSafety.HashEquals(complete.TargetManifestSha256, transition.TargetManifestSha256))
                throw new CryptographicException("The target receipt exists but its signed installed files need trusted repair.");
            await FileTransactionRecovery.RecoverAsync(complete, options.RunningApplicationsPrompt, cancellationToken: cancellationToken);
            IncrementalUpdateWorkspace.CleanupAbandoned(complete);
            return new() { ProductId = installed.Id, Version = installed.Version, InstallDirectory = installed.InstallDirectory, ReceiptPath = installed.ReceiptPath };
        }
        if (installed.Version != transition.FromVersion) throw new InvalidDataException("The incremental package is for a different installed base version.");
        var recovery = FileTransactionRecoveryContext.ForIncrementalUpdate(update, installed, options);
        await FileTransactionRecovery.RecoverAsync(recovery, options.RunningApplicationsPrompt, cancellationToken: cancellationToken);
        IncrementalUpdateWorkspace.CleanupAbandoned(recovery);
        installed = InstallerEngine.ListInstalled(options.StateRoot).Single(product => product.Id == installed.Id && product.Scope == installed.Scope);
        recovery.ValidateInstalledBase(installed);
        await update.VerifyAsync(progress, cancellationToken);
        using var workspace = IncrementalUpdateWorkspace.Create(recovery);
        try
        {
            await update.ReconstructAsync(installed, workspace.PackagePath, options.TrustedPublisherKeySha256, progress, cancellationToken);
            using var package = PackageReader.Open(workspace.PackagePath);
            options.FileRecoveryContext = recovery;
            workspace.MarkApplying();
            var result = await new InstallerEngine().InstallAsync(package, options, progress, cancellationToken);
            workspace.Complete();
            return result;
        }
        catch
        {
            workspace.AbortAfterRollback();
            throw;
        }
    }

    private void RequireIncremental(UpdateFeed feed)
    {
        if (requireIncrementalUpdates && !feed.PackageType.Equals("hupd", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This application requires signed incremental file updates; downloading an installer is not an update fallback.");
    }

    private void ValidateFeed(UpdateFeed feed)
    {
        PackageSafety.ValidateId(feed.ProductId);
        _ = PackageVersion.Parse(feed.Version);
        _ = ValidateUrl(feed.InstallerUrl);
        if (!PackageSafety.HashPattern().IsMatch(feed.Sha256)) throw new InvalidDataException("Update feed requires a SHA256 digest.");
        if (feed.Size < 0) throw new InvalidDataException("Update size cannot be negative.");
        if (feed.PackageType.ToLowerInvariant() is not ("hupd" or "hinst" or "msi" or "exe")) throw new InvalidDataException("Update package type must be hupd, hinst, msi, or exe.");
        if (feed.PackageType.Equals("hupd", StringComparison.OrdinalIgnoreCase))
        {
            if (feed.BaseVersion is null || feed.BaseManifestSha256 is null || feed.TargetManifestSha256 is null ||
                !PackageSafety.HashPattern().IsMatch(feed.BaseManifestSha256) || !PackageSafety.HashPattern().IsMatch(feed.TargetManifestSha256) ||
                PackageVersion.Parse(feed.Version).CompareTo(PackageVersion.Parse(feed.BaseVersion)) <= 0)
                throw new InvalidDataException("An incremental feed requires its exact base version and original base/target manifest SHA256 digests.");
        }
    }
    private static Uri SelectTransition(Uri uri, string currentVersion)
    {
        var fields = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var (name, expected) in new[] { ("fromVersion", currentVersion), ("updateProtocol", "hupd1") })
        {
            var matching = fields.Where(field => Uri.UnescapeDataString(field.Split('=', 2)[0].Replace('+', ' ')).Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matching.Length > 1) throw new InvalidDataException("The update feed contains duplicate transition selectors.");
            if (matching.Length == 1)
            {
                var pair = matching[0].Split('=', 2);
                if (pair.Length != 2 || Uri.UnescapeDataString(pair[1].Replace('+', ' ')) != expected)
                    throw new InvalidDataException("The update feed contains a conflicting transition selector.");
            }
            else fields.Add(name + "=" + Uri.EscapeDataString(expected));
        }
        return new UriBuilder(uri) { Query = string.Join('&', fields) }.Uri;
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
