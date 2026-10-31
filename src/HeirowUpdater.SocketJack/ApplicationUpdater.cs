using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;

namespace HeirowUpdater.SocketJack;

public enum ApplicationUpdateChoice { UpdateNow, Postpone, ApplicationReopened }
public sealed record ApplicationUpdatePrompt(UpdateCompanionConfiguration Application, UpdateCheckResult Update, bool ApplicationRunning, string? HostExecutable = null);
public sealed class ApplicationLifecycleOptions
{
    public bool AllowLoopback { get; set; }
    public bool NoPrompt { get; set; }
    public string? StatusPath { get; set; }
    public string? DownloadDirectory { get; set; }
    public string? ApplicationHost { get; set; }
    public Func<UpdateCompanionConfiguration, CancellationToken, Task<string?>>? AuthenticateAsync { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public ApplicationSessionSnapshot? SessionSnapshot { get; set; }
    public Action<ApplicationLifecycleResult>? StatusChanged { get; set; }
    /// <summary>Visible feedback for an explicit check, including a check relayed to an existing watcher. Checking feedback must return promptly.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<ApplicationLifecycleResult, CancellationToken, Task>? ManualStatusChangedAsync { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<RunningApplicationsRequest, CancellationToken, Task<RunningApplicationsChoice>>? RunningApplicationsPrompt { get; set; }
}
public sealed class ApplicationLifecycleResult : IDisposable
{
    public string Status { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string Name { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string? LatestVersion { get; set; }
    public bool Available { get; set; }
    public bool DeferredUntilExit { get; set; }
    public bool ManualCheck { get; set; }
    public string? ManualRequestId { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset? PostponeUntil { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public ProcessStartInfo? Handoff { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string? LifecycleStatePath { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string? HandoffReadyPath { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public ApplicationStopMonitor? StopMonitor { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public IDisposable? Ownership { get; set; }
    public void Dispose() { StopMonitor?.Dispose(); StopMonitor = null; Ownership?.Dispose(); Ownership = null; }
}

/// <summary>Optional app-local updates. Background checks begin only after every instance of the app exits.</summary>
public static class ApplicationUpdater
{
    public static bool StartAfterExit(string configurationPath, string? bearerToken = null, string? sessionCookie = null, bool replaceCredentials = false) => StartCompanion(configurationPath, "after-exit", bearerToken, sessionCookie, replaceCredentials);
    public static bool CheckForUpdates(string configurationPath, string? bearerToken = null, string? sessionCookie = null, bool replaceCredentials = false) => StartCompanion(configurationPath, "check-for-updates", bearerToken, sessionCookie, replaceCredentials);
    public static bool StartAfterExit(string configurationPath, ApplicationSessionSnapshot session) => StartCompanion(configurationPath, "after-exit", null, null, true, session);
    public static bool CheckForUpdates(string configurationPath, ApplicationSessionSnapshot session) => StartCompanion(configurationPath, "check-for-updates", null, null, true, session);

    private static bool StartCompanion(string configurationPath, string command, string? bearerToken, string? sessionCookie, bool replaceCredentials, ApplicationSessionSnapshot? session = null)
    {
        try
        {
            session ??= replaceCredentials || bearerToken is not null || sessionCookie is not null ? new ApplicationSessionSnapshot(sessionCookie, bearerToken) : null;
            if (!File.Exists(configurationPath)) return false;
            var configuration = ReadConfiguration(configurationPath);
            var executable = ResolveExecutable(configuration);
            if (!File.Exists(executable)) return false;
            using var application = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = configuration.InstallDirectory };
            start.ArgumentList.Add(command); start.ArgumentList.Add("--config"); start.ArgumentList.Add(Path.GetFullPath(configurationPath));
            start.ArgumentList.Add("--application-pid"); start.ArgumentList.Add(application.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--application-start"); start.ArgumentList.Add(application.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (System.Environment.ProcessPath is { } host && !Path.GetFullPath(host).StartsWith(Path.GetDirectoryName(Path.GetFullPath(configurationPath))! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { start.ArgumentList.Add("--application-host"); start.ArgumentList.Add(host); }
            if (session is not null) SetSessionEnvironment(start, session);
            else SetSessionEnvironment(start, bearerToken, sessionCookie, replaceCredentials);
            using var companion = Process.Start(start);
            return companion is not null;
        }
        catch { return false; }
    }

    public static UpdateCompanionConfiguration ReadConfiguration(string path, bool allowLoopback = false, bool requireEntryPoint = true)
    {
        path = Path.GetFullPath(path);
        PackageSafety.EnsureNoReparsePoints(path);
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Application updater configuration is too large.");
        var configuration = JsonSerializer.Deserialize<UpdateCompanionConfiguration>(File.ReadAllText(path), InstallerJson.Options) ?? throw new InvalidDataException("Application updater configuration is empty.");
        PackageSafety.ValidateId(configuration.ProductId);
        _ = PackageVersion.Parse(configuration.CurrentVersion);
        if (configuration.FormatVersion != 1 || configuration.Mode != "after-exit" || configuration.Scope is not ("user" or "machine")) throw new InvalidDataException("Unsupported application updater configuration.");
        if (configuration.PublisherKeySha256.Length != 64 || !configuration.PublisherKeySha256.All(Uri.IsHexDigit)) throw new CryptographicException("Application updates require an installed publisher-key pin.");
        configuration.InstallDirectory = Path.GetFullPath(configuration.InstallDirectory);
        if (!Path.GetDirectoryName(path)!.Equals(configuration.InstallDirectory, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The updater configuration must live in its application's root directory.");
        _ = PackageSafety.UnderRoot(configuration.InstallDirectory, configuration.EntryPoint);
        if (requireEntryPoint && !File.Exists(PackageSafety.UnderRoot(configuration.InstallDirectory, configuration.EntryPoint))) throw new FileNotFoundException("The configured application executable is missing.");
        if (!Uri.TryCreate(configuration.FeedUrl, UriKind.Absolute, out var feed) || feed.UserInfo.Length != 0 ||
            (feed.Scheme != "https" && !(configuration.Isolated && feed.IsLoopback && feed.Scheme == "http")))
            throw new InvalidDataException("Application update feeds require HTTPS.");
        if (!Uri.TryCreate(configuration.CredentialOrigin, UriKind.Absolute, out var origin) || (origin.Scheme != "https" && !(configuration.Isolated && origin.IsLoopback && origin.Scheme == "http")) || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new InvalidDataException("Updater credential origin must be an HTTPS origin.");
        foreach (var name in new[] { configuration.CookieEnvironmentVariable, configuration.TokenEnvironmentVariable })
            if (name is not null && (name.Length is < 1 or > 128 || !(char.IsAsciiLetter(name[0]) || name[0] == '_') || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')))
                throw new InvalidDataException("Updater credential settings contain environment-variable names only.");
        var installed = InstallerEngine.ListInstalled(configuration.StateRoot).FirstOrDefault(x => x.Id.Equals(configuration.ProductId, StringComparison.OrdinalIgnoreCase) && x.Scope == configuration.Scope)
            ?? throw new InvalidDataException("The application installation receipt is missing. Repair its installation before checking for updates.");
        if (!Path.GetFullPath(installed.InstallDirectory).Equals(configuration.InstallDirectory, StringComparison.OrdinalIgnoreCase) ||
            !PackageSafety.HashEquals(installed.PublisherKeySha256 ?? "", configuration.PublisherKeySha256) || installed.Isolated != configuration.Isolated)
            throw new InvalidDataException("The application updater configuration does not match its installed receipt.");
        // The writable profile is not authority to close another application. Rebind closure resources to signed package metadata.
        using (var package = PackageReader.Open(installed.PackagePath))
        {
            package.VerifySignature(installed.PublisherKeySha256, requireSignature: true);
            SharedUpdaterRuntime.ValidateBinding(configuration, package.Manifest);
            if (!package.Manifest.EntryPoint.Equals(configuration.EntryPoint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The updater entry point does not match its signed installed package.");
            var runningTarget = RunningApplicationTarget.FromManifest(package.Manifest, installed.InstallDirectory,
                "update", installed.Features, installed.Scope, installed.Isolated);
            configuration.AffectedResourceFiles = runningTarget.AffectedResourceFiles.ToList();
            configuration.VerifiedOwnedApplicationImages = runningTarget.OwnedApplicationImages.ToArray();
        }
        configuration.CurrentVersion = installed.Version;
        configuration.Features = installed.Features;
        return configuration;
    }

    public static string ResolveExecutable(UpdateCompanionConfiguration configuration)
    {
        var installed = InstallerEngine.ListInstalled(configuration.StateRoot).Single(x => x.Id.Equals(configuration.ProductId, StringComparison.OrdinalIgnoreCase) && x.Scope == configuration.Scope);
        using var package = PackageReader.Open(installed.PackagePath);
        package.VerifySignature(configuration.PublisherKeySha256, requireSignature: true);
        SharedUpdaterRuntime.ValidateBinding(configuration, package.Manifest);
        var executable = configuration.SharedRuntime is null
            ? PackageSafety.UnderRoot(configuration.InstallDirectory, package.Manifest.UpdateCompanion!.ExecutablePath)
            : SharedUpdaterRuntime.Resolve(configuration);
        var payload = package.Manifest.Payload.Single(x => x.Path.Equals(package.Manifest.UpdateCompanion!.ExecutablePath, StringComparison.OrdinalIgnoreCase));
        if (!File.Exists(executable) || new FileInfo(executable).Length != payload.Size || !PackageSafety.HashEquals(PackageSafety.HashFile(executable), payload.Sha256))
            throw new CryptographicException("The installed updater does not match its signed package.");
        return executable;
    }

    internal static void SetSessionEnvironment(ProcessStartInfo start, string? bearer, string? cookie, bool replace)
    {
        ApplicationSessionSnapshot.ValidateValue(bearer); ApplicationSessionSnapshot.ValidateValue(cookie);
        foreach (var name in SessionEnvironmentNames) start.Environment.Remove(name);
        if (!replace && bearer is null && cookie is null) return;
        SetSessionEnvironment(start, new ApplicationSessionSnapshot(cookie, bearer));
    }
    internal static void SetSessionEnvironment(ProcessStartInfo start, ApplicationSessionSnapshot session)
    {
        foreach (var name in SessionEnvironmentNames) start.Environment.Remove(name);
        start.Environment["HEIROWUPDATER_SESSION_SUPPLIED"] = "1";
        start.Environment["HEIROWUPDATER_SESSION_CAPTURED_AT"] = session.CapturedAtUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["HEIROWUPDATER_SESSION_ID"] = session.Id;
        start.Environment["HEIROWUPDATER_SESSION_SOURCE_PID"] = session.SourceProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["HEIROWUPDATER_SESSION_SOURCE_START"] = session.SourceStartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["HEIROWUPDATER_SESSION_SEQUENCE"] = session.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (session.BearerToken is not null) start.Environment["HEIROWUPDATER_TOKEN"] = session.BearerToken;
        if (session.Cookie is not null) start.Environment["HEIROWUPDATER_COOKIE"] = session.Cookie;
    }
    private static readonly string[] SessionEnvironmentNames = ["HEIROWUPDATER_TOKEN", "HEIROWUPDATER_COOKIE", "HEIROWUPDATER_SESSION_SUPPLIED", "HEIROWUPDATER_SESSION_CAPTURED_AT", "HEIROWUPDATER_SESSION_ID", "HEIROWUPDATER_SESSION_SOURCE_PID", "HEIROWUPDATER_SESSION_SOURCE_START", "HEIROWUPDATER_SESSION_SEQUENCE"];

    public static string StateDirectory(UpdateCompanionConfiguration configuration)
        => SharedUpdaterRuntime.ApplicationStateDirectory(configuration, create: true);

    public static async Task<ApplicationLifecycleResult> RunAsync(string configurationPath, int? applicationPid, long? applicationStart,
        bool manual, ApplicationLifecycleOptions options, Func<ApplicationUpdatePrompt, CancellationToken, Task<ApplicationUpdateChoice>> prompt,
        CancellationToken cancellationToken = default)
    {
        configurationPath = Path.GetFullPath(configurationPath);
        var configuration = ReadConfiguration(configurationPath, options.AllowLoopback, requireEntryPoint: false);
        var executable = PackageSafety.UnderRoot(configuration.InstallDirectory, configuration.EntryPoint);
        if ((applicationPid is null) != (applicationStart is null) || (!manual && applicationPid is null)) throw new ArgumentException("Application process ID and exact creation time must be supplied together.");
        if (options.ApplicationHost is not null) { options.ApplicationHost = Path.GetFullPath(options.ApplicationHost); PackageSafety.EnsureNoReparsePoints(options.ApplicationHost); }
        if (applicationPid is { } pid && applicationStart is { } ticks) ApplicationProcesses.ValidateIdentity(options.ApplicationHost ?? executable, pid, ticks);
        var directory = StateDirectory(configuration);
        if (options.ApplicationHost is not null && applicationPid is { } hostPid && applicationStart is { } hostStart)
            await ApplicationHostRegistry.RegisterAsync(configuration, options.ApplicationHost, hostPid, hostStart, cancellationToken);
        bool AppRunning() => ApplicationProcesses.AnyRunning(executable, options.ApplicationHost, ApplicationHostRegistry.Paths(configuration));
        Task WaitForApps() => ApplicationProcesses.WaitForExitAsync(executable, cancellationToken, options.ApplicationHost, () => ApplicationHostRegistry.Paths(configuration));
        var statePath = Path.Combine(directory, "state.json");
        var state = ReadState(statePath);
        var requestPath = Path.Combine(directory, "manual-request.json");
        var activePath = Path.Combine(directory, "manual-active.json");
        var manualRequestId = manual ? Guid.NewGuid().ToString("N") : null;
        var manualErrorReported = false;
        ApplicationLifecycleResult Result(string status, UpdateCheckResult? update = null) => new()
        {
            Status = status, ProductId = configuration.ProductId, Name = configuration.Name, CurrentVersion = configuration.CurrentVersion,
            LatestVersion = update?.Feed.Version, Available = update?.Available ?? false, DeferredUntilExit = AppRunning(),
            PostponeUntil = state.PostponeUntil, LifecycleStatePath = statePath,
            ManualCheck = manual, ManualRequestId = manual ? manualRequestId : null,
            Message = status == "error" ? "The update check could not finish. Check your connection and try again." : null
        };
        async Task<ApplicationLifecycleResult> Status(string status, UpdateCheckResult? update = null, bool showManualFeedback = true)
        {
            var result = Result(status, update); options.StatusChanged?.Invoke(result);
            await WriteJsonAsync(options.StatusPath ?? Path.Combine(directory, "status.json"), result, cancellationToken);
            if (showManualFeedback && manual && !options.NoPrompt && options.ManualStatusChangedAsync is { } feedback)
                await feedback(result, cancellationToken);
            return result;
        }
        bool StillInstalled() => File.Exists(executable) && InstallerEngine.ListInstalled(configuration.StateRoot).Any(x =>
            x.Id.Equals(configuration.ProductId, StringComparison.OrdinalIgnoreCase) && x.Scope == configuration.Scope &&
            Path.GetFullPath(x.InstallDirectory).Equals(configuration.InstallDirectory, StringComparison.OrdinalIgnoreCase) &&
            PackageSafety.HashEquals(x.PublisherKeySha256 ?? "", configuration.PublisherKeySha256));
        string? Secret(string? name) => name is null ? null : System.Environment.GetEnvironmentVariable(name);
        var cookie = Secret(configuration.CookieEnvironmentVariable); var bearer = Secret(configuration.TokenEnvironmentVariable);
        var supplied = options.SessionSnapshot is not null || Secret("HEIROWUPDATER_SESSION_SUPPLIED") == "1" || cookie is not null || bearer is not null;
        var capturedAt = long.TryParse(Secret("HEIROWUPDATER_SESSION_CAPTURED_AT"), out var captured) && captured > 0 ? captured : DateTimeOffset.UtcNow.UtcTicks;
        var snapshotId = Secret("HEIROWUPDATER_SESSION_ID");
        if (!Guid.TryParseExact(snapshotId, "N", out _)) snapshotId = Guid.NewGuid().ToString("N");
        _ = int.TryParse(Secret("HEIROWUPDATER_SESSION_SOURCE_PID"), out var sourcePid);
        _ = long.TryParse(Secret("HEIROWUPDATER_SESSION_SOURCE_START"), out var sourceStart);
        _ = long.TryParse(Secret("HEIROWUPDATER_SESSION_SEQUENCE"), out var sourceSequence);
        if (sourcePid <= 0 || sourceStart <= 0 || sourceSequence <= 0)
        {
            using var sourceProcess = Process.GetCurrentProcess();
            sourcePid = applicationPid ?? sourceProcess.Id; sourceStart = applicationStart ?? sourceProcess.StartTime.ToUniversalTime().Ticks; sourceSequence = 1;
        }
        // Unknown startup state must sort before the application's first real
        // capture; only supplied snapshots own a positive source sequence.
        var snapshot = options.SessionSnapshot ?? new ApplicationSessionSnapshot(cookie, bearer, supplied ? capturedAt : 0, snapshotId!, sourcePid, sourceStart, supplied ? sourceSequence : 0);
        var sessions = new ApplicationSessionStore(snapshot);
        var trustedImage = ResolveExecutable(configuration);
        var installedHost = System.Environment.ProcessPath is { } currentImage && Path.GetFullPath(currentImage).Equals(trustedImage, StringComparison.OrdinalIgnoreCase);
        if (installedHost)
            foreach (var name in SessionEnvironmentNames.Concat(new[] { configuration.CookieEnvironmentVariable, configuration.TokenEnvironmentVariable }).OfType<string>().Distinct())
                System.Environment.SetEnvironmentVariable(name, null); // Native UI children never inherit the application's credentials.
        Task<T> Authorized<T>(Func<SocketJackUpdater, Task<T>> operation) => ApplicationAuthorization.RunAsync(sessions, async () =>
        {
            using var client = new SocketJackUpdater(configuration.Isolated, new Uri(configuration.CredentialOrigin), () => sessions.Read().Snapshot, requireIncrementalUpdates: configuration.UpdateMode == "files");
            return await operation(client);
        }, options.NoPrompt || options.AuthenticateAsync is null || configuration.AuthApplication is null ? null : token => options.AuthenticateAsync(configuration, token),
            async status => { await Status(status); }, async () =>
            {
                state.PostponeUntil = DateTimeOffset.UtcNow.AddHours(24); await WriteJsonAsync(statePath, state, cancellationToken);
                await Status("postponed");
            }, cancellationToken);
        var target = new UpdaterTarget { ProductId = configuration.ProductId, Name = configuration.Name, CurrentVersion = configuration.CurrentVersion,
            FeedUrl = configuration.FeedUrl, InstallDirectory = configuration.InstallDirectory, StateRoot = configuration.StateRoot, Scope = configuration.Scope,
            PublisherKeySha256 = configuration.PublisherKeySha256, Features = configuration.Features, UpdateMode = configuration.UpdateMode };
        if (options.NoPrompt)
        {
            if (!StillInstalled()) return await Status("application-removed");
            var check = await Authorized(client => client.CheckAsync(target, cancellationToken));
            return await Status(check.Available ? "available" : "current", check);
        }
        FileStream? ownership;
        try { ownership = new FileStream(Path.Combine(directory, "lifecycle.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            if (supplied)
            {
                if (PendingIsAlive(ReadState(statePath))) return Result("update-staged");
                if (!installedHost) throw new InvalidOperationException("Session delivery requires the verified installed updater process.");
                ApplicationSessionRelay.Reply acknowledgement;
                try
                {
                    acknowledgement = await ApplicationSessionRelay.SendAsync(ApplicationSessionRelay.Binding(configuration), trustedImage, snapshot, manual, cancellationToken);
                }
                catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception)
                {
                    throw new InvalidOperationException("The running updater could not accept the current application login. Close its update window and try again.", error);
                }
                if (!manual) return Result("already-watching");
                manualRequestId = acknowledgement.ManualRequestId ?? throw new InvalidDataException("The updater did not acknowledge the manual check.");
                var delivered = Result("manual-requested"); options.StatusChanged?.Invoke(delivered); return delivered;
            }
            if (!manual) return Result("already-watching");
            var active = ReadControl<ManualActivity>(activePath);
            if (active is not null && Guid.TryParseExact(active.RequestId, "N", out _) && ProcessIsAlive(active.ProcessId, active.StartTicks))
                manualRequestId = active.RequestId;
            else if (ReadManualRequest(requestPath) is { } pending && pending.Nonce != ReadState(statePath).LastManualRequestId)
                manualRequestId = pending.Nonce;
            else
                await WriteJsonAsync(requestPath, new ManualRequest { FormatVersion = 1, RequestedAt = DateTimeOffset.UtcNow, Nonce = manualRequestId! }, cancellationToken);
            // Only the owner publishes shared status and opens feedback. A relay must not overwrite its current dialog/progress.
            var relayed = Result("manual-requested"); options.StatusChanged?.Invoke(relayed); return relayed;
        }
        var transferOwnership = false;
        var originalCancellation = cancellationToken;
        ApplicationStopMonitor? stopMonitor = null;
        ApplicationSessionRelay? sessionRelay = null;
        using var ownerProcess = Process.GetCurrentProcess();
        async Task BeginManualFeedback()
        {
            manualRequestId ??= Guid.NewGuid().ToString("N");
            manualErrorReported = false;
            await WriteJsonAsync(activePath, new ManualActivity { RequestId = manualRequestId, ProcessId = ownerProcess.Id,
                StartTicks = ownerProcess.StartTime.ToUniversalTime().Ticks }, cancellationToken);
        }
        void EndManualFeedback()
        {
            if (File.Exists(activePath)) { PackageSafety.EnsureNoReparsePoints(activePath); File.Delete(activePath); }
            manual = false; manualRequestId = null;
        }
        bool TakeManualRequest()
        {
            var request = ReadManualRequest(requestPath);
            if (request is null || request.Nonce == state.LastManualRequestId) return false;
            manualRequestId = request.Nonce; manual = true; return true;
        }
        async Task AcknowledgeQueuedManualRequest()
        {
            // Direct checks have independent feedback IDs; they must not make a previously consumed queue entry look new.
            if (ReadManualRequest(requestPath)?.Nonce != manualRequestId) return;
            state.LastManualRequestId = manualRequestId;
            await WriteJsonAsync(statePath, state, cancellationToken);
        }
        try
        {
            state = ReadState(statePath);
            if (ReadManualRequest(requestPath) is { } queued && queued.Nonce != state.LastManualRequestId)
            { manualRequestId = queued.Nonce; manual = true; }
            if (PendingIsAlive(state))
            {
                if (manual) await BeginManualFeedback();
                var pendingResult = await Status("update-staged");
                if (manual) await AcknowledgeQueuedManualRequest();
                return pendingResult;
            }
            stopMonitor = new ApplicationStopMonitor(configuration, resetForOwner: true, cancellationToken);
            cancellationToken = stopMonitor.Token;
            if (installedHost)
            {
                sessionRelay = new(ApplicationSessionRelay.Binding(configuration), trustedImage, async (replacement, requestId, token) =>
                {
                    var accepted = sessions.Replace(replacement);
                    string? requested = null;
                    if (requestId is not null)
                    {
                        if (manual && manualRequestId is not null) requested = manualRequestId;
                        else if (ReadManualRequest(requestPath) is { } queuedRequest && queuedRequest.Nonce != ReadState(statePath).LastManualRequestId) requested = queuedRequest.Nonce;
                        else
                        {
                            requested = requestId;
                            await WriteJsonAsync(requestPath, new ManualRequest { FormatVersion = 1, RequestedAt = DateTimeOffset.UtcNow, Nonce = requested }, token);
                        }
                    }
                    return new(requestId ?? "", requested, accepted);
                }, configuration.Isolated ? static code => Console.Error.WriteLine("Isolated session relay rejection: " + code) : null);
            }
            if (manual && ReadManualRequest(requestPath) is { } pending && pending.Nonce != state.LastManualRequestId)
                manualRequestId = pending.Nonce;
            while (true)
            {
                if (!manual) TakeManualRequest();
                if (!StillInstalled()) return await Status("application-removed");
                if (!manual)
                {
                    await Status("waiting-for-exit");
                    while (AppRunning())
                    {
                        if (TakeManualRequest()) break;
                        await Task.Delay(200, cancellationToken);
                    }
                    if (!manual) await WaitForApps();
                }
                if (!manual && state.PostponeUntil > DateTimeOffset.UtcNow) return await Status("postponed");
                if (!StillInstalled()) return await Status("application-removed");
                UpdateCheckResult check;
                try
                {
                    if (manual) await BeginManualFeedback();
                    await Status("checking");
                    if (manual)
                    {
                        // Acknowledge only after the host has opened feedback. Active metadata coalesces clicks during initialization.
                        await AcknowledgeQueuedManualRequest();
                    }
                    check = await Authorized(client => client.CheckAsync(target, cancellationToken));
                }
                catch (Exception error) when (error is not OperationCanceledException && manual)
                {
                    try { await Status("error"); manualErrorReported = true; } catch { } // Feedback failure must not replace the original check error.
                    if (manualErrorReported && AppRunning()) { EndManualFeedback(); continue; }
                    throw;
                }
                state.LastCheckedAt = DateTimeOffset.UtcNow; state.LatestVersion = check.Feed.Version;
                await WriteJsonAsync(statePath, state, cancellationToken);
                if (!manual && AppRunning()) { manual = false; continue; }
                if (!check.Available)
                {
                    var currentResult = await Status("current", check);
                    if (manual && AppRunning()) { EndManualFeedback(); continue; }
                    if (!manual && TakeManualRequest()) continue;
                    return currentResult;
                }
                await Status("available", check);
                var choice = await prompt(new(configuration, check, AppRunning(), options.ApplicationHost), cancellationToken);
                if (choice == ApplicationUpdateChoice.ApplicationReopened) { EndManualFeedback(); continue; }
                if (choice == ApplicationUpdateChoice.Postpone)
                {
                    state.PostponeUntil = DateTimeOffset.UtcNow.AddHours(24);
                    await WriteJsonAsync(statePath, state, cancellationToken);
                    var postponed = await Status("postponed", check);
                    if (manual && AppRunning()) { EndManualFeedback(); continue; }
                    if (!manual && TakeManualRequest()) continue;
                    return postponed;
                }
                if (!StillInstalled()) return await Status("application-removed");
                await Status("downloading", check);
                var policy = new UpdaterPolicy { Isolated = configuration.Isolated, TrustedPublisherKeySha256 = configuration.PublisherKeySha256,
                    DownloadDirectory = options.DownloadDirectory ?? Path.Combine(directory, "downloads"), StageForElevation = true };
                using var current = Process.GetCurrentProcess();
                var handoff = await Authorized(client => client.PrepareInstallerHandoffAsync(target, check, current.Id, current.StartTime.ToUniversalTime().Ticks, policy, cancellationToken: cancellationToken));
                if (AppRunning()) await Status("waiting-for-applications", check);
                var runningTarget = RunningApplicationTarget.FromConfiguration(configuration).WithWaitOnlyExecutables(
                    ApplicationHostRegistry.Paths(configuration).Concat(options.ApplicationHost is { } host ? [host] : []));
                await RunningApplications.EnsureClosedAsync(runningTarget,
                    options.NoPrompt ? null : options.RunningApplicationsPrompt, cancellationToken);
                void HandoffValue(string name, string value) { if (!handoff.ArgumentList.Contains(name)) { handoff.ArgumentList.Add(name); handoff.ArgumentList.Add(value); } }
                HandoffValue("--lifecycle-config", configurationPath); HandoffValue("--lifecycle-state", statePath);
                var readyIndex = handoff.ArgumentList.IndexOf("--lifecycle-ready");
                var readyPath = readyIndex >= 0 ? handoff.ArgumentList[readyIndex + 1] : null;
                if (readyPath is null)
                {
                    var readyDirectory = PackageSafety.UnderRoot(directory, "handoffs/" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(readyDirectory);
                    readyPath = Path.Combine(readyDirectory, "ready.json"); HandoffValue("--lifecycle-ready", readyPath);
                }
                if (options.ApplicationHost is not null) { handoff.ArgumentList.Add("--application-host"); handoff.ArgumentList.Add(options.ApplicationHost); }
                if (options.AllowLoopback) handoff.ArgumentList.Add("--allow-loopback");
                state.PostponeUntil = null;
                await WriteJsonAsync(statePath, state, cancellationToken);
                var staged = await Status("update-staged", check, showManualFeedback: false);
                staged.Handoff = handoff;
                staged.HandoffReadyPath = readyPath; staged.StopMonitor = stopMonitor;
                staged.Ownership = ownership; transferOwnership = true;
                return staged;
            }
        }
        catch (OperationCanceledException) when (stopMonitor?.StopRequested == true && !originalCancellation.IsCancellationRequested)
        {
            cancellationToken = originalCancellation;
            return await Status("stopped");
        }
        catch (Exception error) when (error is not OperationCanceledException && manual && !manualErrorReported)
        {
            try { await Status("error"); } catch { } // Preserve the underlying failure if the feedback host also fails.
            throw;
        }
        finally
        {
            if (sessionRelay is not null) await sessionRelay.DisposeAsync();
            try { if (File.Exists(activePath)) { PackageSafety.EnsureNoReparsePoints(activePath); File.Delete(activePath); } } catch (IOException) { }
            if (!transferOwnership) { stopMonitor?.Dispose(); ownership.Dispose(); }
        }
    }

    public static async Task MarkHandoffAsync(string statePath, Process helper, CancellationToken cancellationToken = default)
    {
        var state = ReadState(statePath); state.PendingProcessId = helper.Id; state.PendingStartTicks = helper.StartTime.ToUniversalTime().Ticks;
        await WriteJsonAsync(statePath, state, cancellationToken);
    }
    public static async Task CompleteHandoffAsync(string statePath, CancellationToken cancellationToken = default)
    {
        var state = ReadState(statePath); state.PendingProcessId = null; state.PendingStartTicks = null; state.PostponeUntil = null;
        await WriteJsonAsync(statePath, state, cancellationToken);
    }
    private static bool PendingIsAlive(LifecycleState state)
    {
        if (state.PendingProcessId is not { } pid || state.PendingStartTicks is not { } ticks) return false;
        try { using var process = Process.GetProcessById(pid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks; }
        catch (ArgumentException) { return false; } catch (InvalidOperationException) { return false; }
    }
    private static LifecycleState ReadState(string path)
    {
        PackageSafety.EnsureNoReparsePoints(path);
        if (!File.Exists(path)) return new();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<LifecycleState>(stream, InstallerJson.Options) ?? new();
    }
    internal static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path); PackageSafety.EnsureNoReparsePoints(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, InstallerJson.Options), cancellationToken); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private sealed class LifecycleState
    {
        public DateTimeOffset? PostponeUntil { get; set; }
        public DateTimeOffset? LastCheckedAt { get; set; }
        public string? LatestVersion { get; set; }
        public int? PendingProcessId { get; set; }
        public long? PendingStartTicks { get; set; }
        public string? LastManualRequestId { get; set; }
    }

    private static T? ReadControl<T>(string path) where T : class
    {
        PackageSafety.EnsureNoReparsePoints(path);
        try
        {
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 8192) throw new InvalidDataException("Application update control metadata is too large.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream, InstallerJson.Options);
        }
        catch (Exception error) when (error is JsonException or IOException) { return null; }
    }
    private static ManualRequest? ReadManualRequest(string path)
    {
        var request = ReadControl<ManualRequest>(path);
        return request is { FormatVersion: 1 } && Guid.TryParseExact(request.Nonce, "N", out _) &&
            request.RequestedAt >= DateTimeOffset.UtcNow.AddMinutes(-10) && request.RequestedAt <= DateTimeOffset.UtcNow.AddMinutes(1) ? request : null;
    }
    private static bool ProcessIsAlive(int processId, long startTicks)
    {
        if (processId <= 0 || startTicks <= 0) return false;
        try { using var process = Process.GetProcessById(processId); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTicks; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    private sealed class ManualRequest
    {
        public int FormatVersion { get; set; }
        public DateTimeOffset RequestedAt { get; set; }
        public string Nonce { get; set; } = "";
    }
    private sealed class ManualActivity
    {
        public string RequestId { get; set; } = "";
        public int ProcessId { get; set; }
        public long StartTicks { get; set; }
    }
}
