using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Channels;
using HeirowInstaller.Core;
using HeirowUpdater;
using HeirowUpdater.SocketJack;

internal static partial class UpdaterProgram
{
    private static async Task<int> ApplyIncrementalAsync(Arguments args, CancellationToken token, Action<string, string> boundPaths)
    {
        var path = Path.GetFullPath(args.Positionals.ElementAtOrDefault(1) ?? throw new ArgumentException("apply-incremental requires signed update data."));
        PackageSafety.EnsureNoReparsePoints(path);
        var profilePath = Path.GetFullPath(args.Require("lifecycle-config"));
        var configuration = ApplicationUpdater.ReadConfiguration(profilePath, requireEntryPoint: false);
        var id = args.Require("product-id"); var key = args.Require("trust-key"); var expectedVersion = args.Require("expected-version");
        if (configuration.SharedRuntime is null || configuration.ProductId != id || !PackageSafety.HashEquals(configuration.PublisherKeySha256, key) ||
            configuration.Scope != args.Require("scope") || configuration.Isolated != args.Has("isolated") ||
            !Path.GetFullPath(args.Require("target")).Equals(configuration.InstallDirectory, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(args.Require("state-root")).Equals(Path.GetFullPath(configuration.StateRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The file-update handoff does not match its signed installed application.");
        var trustedRuntime = ApplicationUpdater.ResolveExecutable(configuration);
        var installed = InstallerEngine.ListInstalled(configuration.StateRoot).Single(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && x.Scope == configuration.Scope);
        if (installed.IsMsi || installed.IsExecutable) throw new NotSupportedException("This application needs a native ownership migration before file updates.");
        if (System.Environment.ProcessPath is not { } processImage || !Path.GetFullPath(processImage).Equals(trustedRuntime, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("File updates must run through the application's verified shared updater.");
        var stateDirectory = ApplicationUpdater.StateDirectory(configuration);
        var statusPath = BoundHandoffPath(args.Require("status-file"), stateDirectory, "status.json");
        var readyPath = BoundHandoffPath(args.Require("lifecycle-ready"), stateDirectory, "ready.json");
        var statePath = Path.GetFullPath(args.Require("lifecycle-state"));
        if (!statePath.Equals(Path.Combine(stateDirectory, "state.json"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update lifecycle state belongs to a different application.");
        boundPaths(statusPath, statePath);
        var feed = new UpdateFeed
        {
            ProductId = id, Name = configuration.Name, Version = expectedVersion, PackageType = "hupd",
            InstallerUrl = "https://updates.invalid/verified-local-data.hupd", // No request is sent; a signed URL is never copied into command-line arguments.
            Sha256 = args.Require("sha256"), Size = long.Parse(args.Require("file-size"), CultureInfo.InvariantCulture),
            BaseVersion = args.Require("base-version"), BaseManifestSha256 = args.Require("base-manifest-sha256"), TargetManifestSha256 = args.Require("target-manifest-sha256")
        };
        if (new FileInfo(path).Length != feed.Size || !PackageSafety.HashEquals(PackageSafety.HashFile(path), feed.Sha256)) throw new CryptographicException("Staged file-update hash or size mismatch.");
        using (var delta = IncrementalUpdateReader.Open(path))
        {
            delta.VerifySignature(key, requireSignature: true); await delta.VerifyAsync(null, token);
            var transition = delta.Transition;
            if (transition.ProductId != id || transition.FromVersion != feed.BaseVersion || transition.ToVersion != expectedVersion ||
                !PackageSafety.HashEquals(transition.BaseManifestSha256, feed.BaseManifestSha256) || !PackageSafety.HashEquals(transition.TargetManifestSha256, feed.TargetManifestSha256) ||
                (configuration.CurrentVersion != feed.BaseVersion && configuration.CurrentVersion != expectedVersion))
                throw new InvalidDataException("The staged file update has a different signed base or target.");
        }
        using var maintenance = new ApplicationStopMonitor(configuration, resetForOwner: false, token);
        token = maintenance.Token;
        await UpdaterSchedule.WriteJsonAsync(readyPath, new { status = "ready", helperProcessId = System.Environment.ProcessId }, token);
        var waitingDependency = await WaitForIncrementalOwnerAsync(args, trustedRuntime, token);
        var runningTarget = RunningApplicationTarget.FromConfiguration(configuration).WithWaitOnlyExecutables(ApplicationHostRegistry.Paths(configuration)
            .Concat(args.Get("application-host") is { } host ? new[] { host } : []))
            .WithWaitOnlyProcesses(waitingDependency is null ? [] : new[] { waitingDependency });
        Func<RunningApplicationsRequest, CancellationToken, Task<RunningApplicationsChoice>>? prompt = args.Has("no-prompt") ? null :
            (request, cancellation) => NativeUpdatePrompt.ShowRunningApplicationsAsync(request, configuration, profilePath, cancellation);
        await RunningApplications.EnsureClosedAsync(runningTarget, prompt, token);
        if (!PackageSafety.HashEquals(PackageSafety.HashFile(path), feed.Sha256)) throw new CryptographicException("The staged file update changed while applications were closing.");
        using var client = new UpdateClient(configuration.Isolated, transport: new SocketJackUpdateTransport(new Uri(configuration.CredentialOrigin)), requireIncrementalUpdates: true);
        await using var progress = new IncrementalProgress(statusPath);
        var result = await client.ApplyAsync(path, feed, new()
        {
            TargetDirectory = configuration.InstallDirectory, StateRoot = configuration.StateRoot, Scope = configuration.Scope,
            TrustedPublisherKeySha256 = key, RequireSignature = true, Features = configuration.Features,
            Silent = true, Launch = false, RegisterProduct = installed.RegisteredProduct, Isolated = configuration.Isolated,
            CompanionStopAlreadyHandled = true, RunningApplicationsPrompt = prompt, LogPath = args.Get("log")
        }, progress, token);
        await progress.CompleteAsync();
        await ApplicationUpdater.CompleteHandoffAsync(statePath, CancellationToken.None);
        await UpdaterSchedule.WriteJsonAsync(statusPath, new { status = "updated", result }, CancellationToken.None);
        if (!args.Has("background")) Print(result);
        return result.RebootRequired ? 3010 : 0;
    }

    private static string BoundHandoffPath(string path, string stateDirectory, string filename)
    {
        path = Path.GetFullPath(path); PackageSafety.EnsureNoReparsePoints(path);
        var root = Path.Combine(stateDirectory, "handoffs") + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path) != filename ||
            !Path.GetDirectoryName(Path.GetDirectoryName(path))!.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(Path.GetDirectoryName(path)), "N", out _))
            throw new InvalidDataException("File-update progress must remain in this application's owned handoff directory.");
        return path;
    }

    private static async Task<RunningApplication?> WaitForIncrementalOwnerAsync(Arguments args, string trustedRuntime, CancellationToken token)
    {
        var pid = int.Parse(args.Require("wait-pid"), CultureInfo.InvariantCulture); var ticks = long.Parse(args.Require("wait-start"), CultureInfo.InvariantCulture);
        if (pid <= 0 || pid == System.Environment.ProcessId || ticks <= 0) throw new InvalidDataException("Invalid updater handoff process identity.");
        try
        {
            using var previous = Process.GetProcessById(pid);
            if (previous.StartTime.ToUniversalTime().Ticks != ticks) return null;
            var image = previous.MainModule?.FileName ?? throw new InvalidDataException("The previous update process could not be verified.");
            var dependency = new RunningApplication(pid, ticks, Path.GetFullPath(image), "Previous application updater", Path.GetFullPath(image), "manual", false);
            if (!TrustedUpdaterImagesMatch(image, trustedRuntime)) return dependency; // Manual-only dependency, never closure authority.
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(token); grace.CancelAfter(TimeSpan.FromSeconds(3));
            try { await previous.WaitForExitAsync(grace.Token); return null; }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return dependency; }
        }
        catch (ArgumentException) { return null; }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("The previous updater cannot be inspected. Close it and retry."); }
    }

    private sealed class IncrementalProgress : IProgress<InstallProgress>, IAsyncDisposable
    {
        private readonly Channel<InstallProgress> channel = Channel.CreateBounded<InstallProgress>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        private readonly Task writer;
        internal IncrementalProgress(string path) => writer = WriteAsync(path);
        public void Report(InstallProgress value) => channel.Writer.TryWrite(value);
        private async Task WriteAsync(string path)
        {
            await foreach (var progress in channel.Reader.ReadAllAsync())
                await UpdaterSchedule.WriteJsonAsync(path, new { status = "installing", progress }, CancellationToken.None);
        }
        internal async Task CompleteAsync() { channel.Writer.TryComplete(); await writer; }
        public async ValueTask DisposeAsync() => await CompleteAsync();
    }
}
