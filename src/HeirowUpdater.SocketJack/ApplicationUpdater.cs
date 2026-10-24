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
    public Action<ApplicationLifecycleResult>? StatusChanged { get; set; }
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
    public static bool StartAfterExit(string configurationPath, string? bearerToken = null, string? sessionCookie = null) => StartCompanion(configurationPath, "after-exit", bearerToken, sessionCookie);
    public static bool CheckForUpdates(string configurationPath, string? bearerToken = null, string? sessionCookie = null) => StartCompanion(configurationPath, "check-for-updates", bearerToken, sessionCookie);

    private static bool StartCompanion(string configurationPath, string command, string? bearerToken, string? sessionCookie)
    {
        try
        {
            var executable = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configurationPath))!, "heirowUpdater.exe");
            if (!File.Exists(executable) || !File.Exists(configurationPath)) return false;
            using var application = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
            start.ArgumentList.Add(command); start.ArgumentList.Add("--config"); start.ArgumentList.Add(Path.GetFullPath(configurationPath));
            start.ArgumentList.Add("--application-pid"); start.ArgumentList.Add(application.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--application-start"); start.ArgumentList.Add(application.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (System.Environment.ProcessPath is { } host && !Path.GetFullPath(host).StartsWith(Path.GetDirectoryName(Path.GetFullPath(configurationPath))! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { start.ArgumentList.Add("--application-host"); start.ArgumentList.Add(host); }
            if (bearerToken is not null) start.Environment["HEIROWUPDATER_TOKEN"] = bearerToken;
            if (sessionCookie is not null) start.Environment["HEIROWUPDATER_COOKIE"] = sessionCookie;
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
        configuration.CurrentVersion = installed.Version;
        configuration.Features = installed.Features;
        return configuration;
    }

    public static string StateDirectory(UpdateCompanionConfiguration configuration)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuration.ProductId.ToLowerInvariant() + "\n" + configuration.InstallDirectory.ToLowerInvariant()))).ToLowerInvariant()[..24];
        var root = configuration.Isolated ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configuration.StateRoot))!, "updater-lifecycle")
            : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SocketJack", "heirowUpdater", "applications");
        var directory = Path.Combine(root, key);
        PackageSafety.EnsureNoReparsePoints(directory); Directory.CreateDirectory(directory);
        return directory;
    }

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
        ApplicationLifecycleResult Result(string status, UpdateCheckResult? update = null) => new()
        {
            Status = status, ProductId = configuration.ProductId, Name = configuration.Name, CurrentVersion = configuration.CurrentVersion,
            LatestVersion = update?.Feed.Version, Available = update?.Available ?? false, DeferredUntilExit = AppRunning(),
            PostponeUntil = state.PostponeUntil, LifecycleStatePath = statePath
        };
        async Task<ApplicationLifecycleResult> Status(string status, UpdateCheckResult? update = null)
        {
            var result = Result(status, update); options.StatusChanged?.Invoke(result);
            await WriteJsonAsync(options.StatusPath ?? Path.Combine(directory, "status.json"), result, cancellationToken);
            return result;
        }
        bool StillInstalled() => File.Exists(executable) && InstallerEngine.ListInstalled(configuration.StateRoot).Any(x =>
            x.Id.Equals(configuration.ProductId, StringComparison.OrdinalIgnoreCase) && x.Scope == configuration.Scope &&
            Path.GetFullPath(x.InstallDirectory).Equals(configuration.InstallDirectory, StringComparison.OrdinalIgnoreCase) &&
            PackageSafety.HashEquals(x.PublisherKeySha256 ?? "", configuration.PublisherKeySha256));
        string? Secret(string? name) => name is null ? null : System.Environment.GetEnvironmentVariable(name);
        var cookie = Secret(configuration.CookieEnvironmentVariable); var bearer = Secret(configuration.TokenEnvironmentVariable);
        async Task<T> Authorized<T>(Func<SocketJackUpdater, Task<T>> operation)
        {
            using var client = new SocketJackUpdater(configuration.Isolated, new Uri(configuration.CredentialOrigin), cookie, bearer);
            try { return await operation(client); }
            catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.Unauthorized && !options.NoPrompt && options.AuthenticateAsync is not null && configuration.AuthApplication is not null)
            {
                await Status("sign-in-required");
                bearer = await options.AuthenticateAsync(configuration, cancellationToken);
                if (string.IsNullOrEmpty(bearer))
                {
                    state.PostponeUntil = DateTimeOffset.UtcNow.AddHours(24); await WriteJsonAsync(statePath, state, cancellationToken);
                    await Status("postponed");
                    throw new OperationCanceledException("Application update sign-in was postponed.", cancellationToken);
                }
                cookie = null;
                using var authenticated = new SocketJackUpdater(configuration.Isolated, new Uri(configuration.CredentialOrigin), null, bearer);
                return await operation(authenticated);
            }
        }
        var target = new UpdaterTarget { ProductId = configuration.ProductId, Name = configuration.Name, CurrentVersion = configuration.CurrentVersion,
            FeedUrl = configuration.FeedUrl, InstallDirectory = configuration.InstallDirectory, StateRoot = configuration.StateRoot, Scope = configuration.Scope,
            PublisherKeySha256 = configuration.PublisherKeySha256, Features = configuration.Features };
        if (options.NoPrompt)
        {
            if (!StillInstalled()) return await Status("application-removed");
            var check = await Authorized(client => client.CheckAsync(target, cancellationToken));
            return await Status(check.Available ? "available" : "current", check);
        }
        if (PendingIsAlive(state)) return await Status("update-staged");
        FileStream? ownership;
        try { ownership = new FileStream(Path.Combine(directory, "lifecycle.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            if (manual) await WriteJsonAsync(Path.Combine(directory, "manual-request.json"), new { requestedAt = DateTimeOffset.UtcNow, nonce = Guid.NewGuid().ToString("N") }, cancellationToken);
            return await Status(manual ? "manual-requested" : "already-watching");
        }
        var transferOwnership = false;
        var originalCancellation = cancellationToken;
        ApplicationStopMonitor? stopMonitor = null;
        try
        {
            state = ReadState(statePath);
            if (PendingIsAlive(state)) return await Status("update-staged");
            stopMonitor = new ApplicationStopMonitor(configuration.InstallDirectory, resetForOwner: true, cancellationToken);
            cancellationToken = stopMonitor.Token;
            var requestPath = Path.Combine(directory, "manual-request.json");
            var lastRequest = File.Exists(requestPath) ? File.GetLastWriteTimeUtc(requestPath) : DateTime.MinValue;
            while (true)
            {
                if (!StillInstalled()) return await Status("application-removed");
                if (!manual)
                {
                    await Status("waiting-for-exit");
                    while (AppRunning())
                    {
                        if (File.Exists(requestPath) && File.GetLastWriteTimeUtc(requestPath) > lastRequest) { lastRequest = File.GetLastWriteTimeUtc(requestPath); manual = true; break; }
                        await Task.Delay(200, cancellationToken);
                    }
                    if (!manual) await WaitForApps();
                }
                if (!manual && state.PostponeUntil > DateTimeOffset.UtcNow) return await Status("postponed");
                if (!StillInstalled()) return await Status("application-removed");
                await Status("checking");
                var check = await Authorized(client => client.CheckAsync(target, cancellationToken));
                state.LastCheckedAt = DateTimeOffset.UtcNow; state.LatestVersion = check.Feed.Version;
                await WriteJsonAsync(statePath, state, cancellationToken);
                if (!manual && AppRunning()) { manual = false; continue; }
                if (!check.Available)
                {
                    var currentResult = await Status("current", check);
                    if (manual && AppRunning()) { manual = false; continue; }
                    return currentResult;
                }
                await Status("available", check);
                var choice = await prompt(new(configuration, check, AppRunning(), options.ApplicationHost), cancellationToken);
                if (choice == ApplicationUpdateChoice.ApplicationReopened) { manual = false; continue; }
                if (choice == ApplicationUpdateChoice.Postpone)
                {
                    state.PostponeUntil = DateTimeOffset.UtcNow.AddHours(24);
                    await WriteJsonAsync(statePath, state, cancellationToken);
                    var postponed = await Status("postponed", check);
                    if (manual && AppRunning()) { manual = false; continue; }
                    return postponed;
                }
                if (!StillInstalled()) return await Status("application-removed");
                await Status("downloading", check);
                var policy = new UpdaterPolicy { Isolated = configuration.Isolated, TrustedPublisherKeySha256 = configuration.PublisherKeySha256,
                    DownloadDirectory = options.DownloadDirectory ?? Path.Combine(directory, "downloads"), StageForElevation = true };
                using var current = Process.GetCurrentProcess();
                var handoff = await Authorized(client => client.PrepareInstallerHandoffAsync(target, check, current.Id, current.StartTime.ToUniversalTime().Ticks, policy, cancellationToken: cancellationToken));
                if (AppRunning()) await Status("waiting-for-exit", check);
                await WaitForApps();
                handoff.ArgumentList.Add("--lifecycle-config"); handoff.ArgumentList.Add(configurationPath);
                handoff.ArgumentList.Add("--lifecycle-state"); handoff.ArgumentList.Add(statePath);
                var readyPath = Path.Combine(Path.GetDirectoryName(handoff.FileName)!, "lifecycle-ready-" + Guid.NewGuid().ToString("N") + ".json");
                handoff.ArgumentList.Add("--lifecycle-ready"); handoff.ArgumentList.Add(readyPath);
                if (options.ApplicationHost is not null) { handoff.ArgumentList.Add("--application-host"); handoff.ArgumentList.Add(options.ApplicationHost); }
                if (options.AllowLoopback) handoff.ArgumentList.Add("--allow-loopback");
                state.PostponeUntil = null;
                await WriteJsonAsync(statePath, state, cancellationToken);
                var staged = await Status("update-staged", check);
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
        finally { if (!transferOwnership) { stopMonitor?.Dispose(); ownership.Dispose(); } }
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
        return File.Exists(path) ? JsonSerializer.Deserialize<LifecycleState>(File.ReadAllText(path), InstallerJson.Options) ?? new() : new();
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
    }
}
