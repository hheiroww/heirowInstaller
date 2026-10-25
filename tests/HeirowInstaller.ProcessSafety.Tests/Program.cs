using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("These tests exercise Windows Restart Manager and normal window closure.");
if (args.Length != 1) throw new ArgumentException("Pass the freshly published RunningApplicationProbe directory.");
var source = Path.GetFullPath(args[0]);
if (!File.Exists(Path.Combine(source, "RunningApplicationProbe.exe"))) throw new FileNotFoundException("Publish the owned fixture first.");
var root = Path.Combine(Path.GetTempPath(), "heirowInstaller-process-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++; Console.WriteLine("PASS " + description);
}
async Task Throws<T>(Func<Task> action, string description) where T : Exception
{
    try { await action(); } catch (T) { Check(true, description); return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + description);
}
PackageManifest Manifest() => new() { Id = "process-safety-fixture", Name = "Desktop acceptance app", Publisher = "Isolated test", Version = "1.0.0", EntryPoint = "RunningApplicationProbe.exe" };
RunningApplicationTarget Target(Probe app) => RunningApplicationTarget.FromManifest(Manifest(), app.Directory);
try
{
    // Resource declarations are file-scoped, selected by feature, and cannot expand arbitrary environment variables.
    var authoringRoot = Path.Combine(root, "authoring"); Directory.CreateDirectory(authoringRoot);
    foreach (var invalid in new[] { "*.dll", "relative.dll", "{app}\\..\\outside.dll", "{app}\\module.dll:stream", "%PATH%\\module.dll", "{app}\\folder" })
        await Throws<InvalidDataException>(() => Task.Run(() => RunningApplicationTarget.ResolveResourceFile(invalid, authoringRoot)), "unsafe affected-resource path rejected");
    await Throws<InvalidDataException>(() => Task.Run(() => RunningApplicationTarget.ResolveResourceFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "kernel32.dll"), authoringRoot)), "Windows system resources rejected");
    var authored = Manifest(); authored.Features = [new() { Id = "plugin", Name = "Plugin", Default = false }];
    authored.AffectedResources = [new() { Path = "{app}\\owned-plugin.vst3", Feature = "plugin" }];
    Check(RunningApplicationTarget.FromManifest(authored, authoringRoot).AffectedResourceFiles.Count == 0, "unselected plugin resource excluded");
    Check(RunningApplicationTarget.FromManifest(authored, authoringRoot, features: ["plugin"]).AffectedResourceFiles.Single() == Path.Combine(authoringRoot, "owned-plugin.vst3"), "selected plugin resource resolves to one exact file");
    var oldProfile = JsonSerializer.Deserialize<UpdateCompanionConfiguration>("{}", InstallerJson.Options)!;
    Check(oldProfile.AffectedResourceFiles.Count == 0, "existing updater profiles preserve empty resource default");
    Check(!JsonSerializer.Serialize(new InstallOptions { RunningApplicationsPrompt = (_, _) => Task.FromResult(RunningApplicationsChoice.Cancel) }, InstallerJson.Options).Contains("runningApplicationsPrompt"), "runtime consent callback is never serialized");

    await using (var app = await Probe.Start(source, root, "precise", true))
    await using (var unrelated = await Probe.Start(source, root, "same-name-other-root", true))
    {
        var observed = await RunningApplications.InspectAsync(Target(app));
        Check(observed.Count == 1 && observed[0].ProcessId == app.Process.Id && observed[0].StartTimeUtcTicks == app.StartTicks && observed[0].ExecutablePath == app.Executable, "only exact executable/root and UTC creation identity matches");
        Check(observed[0].CanRequestClose && observed[0].Kind == "application", "verified GUI application can receive normal close");
        var watch = Stopwatch.StartNew();
        await Throws<RunningApplicationsException>(() => RunningApplications.EnsureClosedAsync(Target(app)), "noninteractive guard fails without prompting or closing");
        Check(watch.Elapsed < TimeSpan.FromSeconds(4) && app.Requests == 0 && unrelated.Requests == 0, "noninteractive operation stays bounded and leaves both apps untouched");

        var manifestFile = Path.Combine(root, "manifest.json"); File.WriteAllText(manifestFile, JsonSerializer.Serialize(Manifest(), InstallerJson.Options));
        var packageFile = Path.Combine(root, "fixture.hinst"); PackageBuilder.Build(manifestFile, source, packageFile);
        using var package = PackageReader.Open(packageFile);
        var state = Path.Combine(root, "never-created-state");
        var options = new InstallOptions { TargetDirectory = app.Directory, StateRoot = state, Isolated = true, RegisterProduct = false,
            RunningApplicationsPrompt = (_, _) => Task.FromResult(RunningApplicationsChoice.Cancel) };
        var before = PackageSafety.HashFile(app.Executable);
        await Throws<OperationCanceledException>(() => new InstallerEngine().InstallAsync(package, options), "installer Cancel exits before transaction creation");
        Check(!Directory.Exists(state) && before == PackageSafety.HashFile(app.Executable) && app.Requests == 0, "cancel preserves installed bytes and does not create installer state");
    }

    await using (var app = await Probe.Start(source, root, "graceful", false))
    {
        var prompts = 0;
        await RunningApplications.EnsureClosedAsync(Target(app), (request, _) =>
        {
            Check(!request.IsRetry && request.Applications.Single().ProcessId == app.Process.Id, "initial consent lists actual affected identity");
            prompts++; return Task.FromResult(RunningApplicationsChoice.CloseAppsAndContinue);
        });
        Check(prompts == 1 && app.Process.HasExited && app.Requests == 1, "consented normal window close succeeds without termination");
    }

    var raceTarget = Path.Combine(root, "starts-during-verification"); Directory.CreateDirectory(raceTarget);
    foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(raceTarget, Path.GetFileName(file)));
    Probe? lateApp = null;
    try
    {
        using var package = PackageReader.Open(Path.Combine(root, "fixture.hinst"));
        var before = PackageSafety.HashFile(Path.Combine(raceTarget, "RunningApplicationProbe.exe"));
        var prompts = 0;
        var options = new InstallOptions { TargetDirectory = raceTarget, StateRoot = Path.Combine(root, "race-state"), Isolated = true, RegisterProduct = false,
            RunningApplicationsPrompt = (request, _) => { prompts++; Check(request.Applications.Single().ProcessId == lateApp!.Process.Id, "final pre-mutation gate detects an app opened during extraction"); return Task.FromResult(RunningApplicationsChoice.Cancel); } };
        var progress = new InlineProgress(value =>
        {
            if (value.Phase == "extract" && value.Completed == value.Total && lateApp is null)
                lateApp = Probe.StartExisting(raceTarget, root, "late-app", true).GetAwaiter().GetResult();
        });
        await Throws<OperationCanceledException>(() => new InstallerEngine().InstallAsync(package, options, progress), "late app appearance cancels installation before app file replacement");
        Check(prompts == 1 && before == PackageSafety.HashFile(Path.Combine(raceTarget, "RunningApplicationProbe.exe")) &&
            !InstallerEngine.ListInstalled(options.StateRoot).Any() && lateApp!.Requests == 0, "late cancellation preserves application and commits no receipt");
    }
    finally { if (lateApp is not null) await lateApp.DisposeAsync(); }

    await using (var app = await Probe.Start(source, root, "save-dialog", true))
    {
        var prompts = 0;
        await Throws<OperationCanceledException>(() => RunningApplications.EnsureClosedAsync(Target(app), (request, _) =>
        {
            prompts++;
            if (prompts == 1) return Task.FromResult(RunningApplicationsChoice.CloseAppsAndContinue);
            Check(request.IsRetry && request.Applications.Single().ProcessId == app.Process.Id, "refused close returns a responsive Retry/Cancel request");
            return Task.FromResult(RunningApplicationsChoice.Cancel);
        }), "user can cancel while save confirmation remains open");
        Check(prompts == 2 && !app.Process.HasExited && app.Requests == 1, "save-dialog refusal never escalates to forced termination or repeated close");
    }

    await using (var app = await Probe.Start(source, root, "cancellable", true))
    {
        using var cancellation = new CancellationTokenSource();
        var wait = RunningApplications.EnsureClosedAsync(Target(app), (_, _) => Task.FromResult(RunningApplicationsChoice.CloseAppsAndContinue), cancellation.Token);
        await Probe.Until(() => app.Requests == 1, TimeSpan.FromSeconds(10));
        var watch = Stopwatch.StartNew(); cancellation.Cancel();
        await Throws<OperationCanceledException>(() => wait, "close wait accepts cancellation");
        Check(watch.Elapsed < TimeSpan.FromSeconds(2) && !app.Process.HasExited && app.Requests == 1, "cancel during close wait preserves the refusing app");
    }

    Probe? replacement = null;
    await using (var original = await Probe.Start(source, root, "restart", true))
    {
        var prompts = 0;
        try
        {
            await Throws<OperationCanceledException>(() => RunningApplications.EnsureClosedAsync(Target(original), async (request, _) =>
            {
                prompts++;
                if (prompts == 1)
                {
                    await original.Release();
                    replacement = await Probe.StartExisting(original.Directory, root, "replacement", true);
                    return RunningApplicationsChoice.CloseAppsAndContinue;
                }
                Check(request.IsRetry && request.Applications.Single().ProcessId == replacement!.Process.Id && request.Applications[0].StartTimeUtcTicks == replacement.StartTicks,
                    "restarted application requires new consent for its new PID/start time");
                return RunningApplicationsChoice.Cancel;
            }), "restart cannot inherit the prior close consent");
            Check(prompts == 2 && replacement!.Requests == 0 && !replacement.Process.HasExited, "replacement app receives no inherited close message");
        }
        finally { if (replacement is not null) await replacement.DisposeAsync(); }
    }

    await using (var host = await Probe.Start(source, root, "wait-only", true))
    {
        var target = RunningApplicationTarget.FromManifest(Manifest(), Path.Combine(root, "different-application"))
            .WithWaitOnlyExecutables([host.Executable]);
        var snapshot = await RunningApplications.InspectAsync(target);
        Check(snapshot.Count == 1 && snapshot[0].ProcessId == host.Process.Id && !snapshot[0].CanRequestClose, "registered host path alone permits only manual closure");
        var prompts = 0;
        await Throws<OperationCanceledException>(() => RunningApplications.EnsureClosedAsync(target, (_, _) => Task.FromResult(++prompts == 1 ? RunningApplicationsChoice.CloseAppsAndContinue : RunningApplicationsChoice.Cancel)), "manual-only host returns bounded retry and cancel");
        Check(host.Requests == 0 && !host.Process.HasExited, "unverified external host never receives a close request");
        var ownerManifest = Manifest(); ownerManifest.EntryPoint = "ActualApplication.exe";
        ownerManifest.UpdateCompanion = new() { ExecutablePath = "RunningApplicationProbe.exe" };
        var ownerTarget = RunningApplicationTarget.FromManifest(ownerManifest, host.Directory);
        Check((await RunningApplications.InspectAsync(ownerTarget)).Count == 0, "ordinary companion resource ownership is excluded from app closure discovery");
        var ownerSnapshot = await RunningApplications.InspectAsync(ownerTarget.WithWaitOnlyExecutables([host.Executable]));
        Check(ownerSnapshot.Count == 1 && ownerSnapshot[0].ProcessId == host.Process.Id && !ownerSnapshot[0].CanRequestClose,
            "explicit stalled companion dependency remains visible for manual-only closure");
    }

    var pluginDirectory = Path.Combine(root, "external-plugin"); Directory.CreateDirectory(pluginDirectory);
    var plugin = Path.Combine(pluginDirectory, "OwnedTestPlugin.vst3");
    File.Copy(Path.Combine(source, "RunningApplicationProbe.dll"), plugin);
    await using (var host = await Probe.Start(source, root, "plugin-host", false, plugin))
    {
        var manifest = Manifest();
        manifest.AffectedResources = [new() { Path = plugin }];
        var target = RunningApplicationTarget.FromManifest(manifest, Path.Combine(root, "plugin-owner"));
        var snapshot = await RunningApplications.InspectAsync(target);
        Check(snapshot.Count == 1 && snapshot[0].ProcessId == host.Process.Id && snapshot[0].Kind == "plugin-host" && snapshot[0].CanRequestClose && snapshot[0].AffectedPath == plugin,
            "exact authored external plugin file and actual loaded module identify its GUI host");
        await RunningApplications.EnsureClosedAsync(target, (_, _) => Task.FromResult(RunningApplicationsChoice.CloseAppsAndContinue));
        Check(host.Process.HasExited && host.Requests == 1, "verified external plugin host closes only after explicit consent");
    }
    Console.WriteLine($"Passed {checks} process-safety checks. Only freshly compiled, marker-bound fixture apps were started or closed.");
    return 0;
}
finally
{
    // This test owns one freshly generated direct child of TEMP; a running refusing fixture is never force-terminated for cleanup.
    if (Path.GetDirectoryName(root)?.Equals(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) == true &&
        Path.GetFileName(root).StartsWith("heirowInstaller-process-tests-", StringComparison.Ordinal))
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}

sealed class Probe : IAsyncDisposable
{
    public string Directory { get; }
    public string RunDirectory { get; }
    public string Executable => Path.Combine(Directory, "RunningApplicationProbe.exe");
    public Process Process { get; }
    public long StartTicks { get; }
    public int Requests
    {
        get
        {
            try { using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(RunDirectory, "close-requests.json"))); return json.RootElement.GetProperty("requests").GetInt32(); }
            catch (Exception ex) when (ex is IOException or JsonException) { return 0; }
        }
    }
    private Probe(string directory, string run, Process process) { Directory = directory; RunDirectory = run; Process = process; StartTicks = process.StartTime.ToUniversalTime().Ticks; }
    public static async Task<Probe> Start(string source, string root, string name, bool keepOpen, string? module = null)
    {
        var directory = Path.Combine(root, name); System.IO.Directory.CreateDirectory(directory);
        foreach (var file in System.IO.Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        return await StartExisting(directory, root, name, keepOpen, module);
    }
    public static async Task<Probe> StartExisting(string directory, string root, string name, bool keepOpen, string? module = null)
    {
        var run = Path.Combine(root, name + "-run-" + Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, ".running-application-probe"), "heirowInstaller running-application acceptance\n", new UTF8Encoding(false));
        var start = new ProcessStartInfo(Path.Combine(directory, "RunningApplicationProbe.exe")) { UseShellExecute = false, WorkingDirectory = directory, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Normal };
        start.ArgumentList.Add("--run-directory"); start.ArgumentList.Add(run);
        if (keepOpen) start.ArgumentList.Add("--keep-open");
        if (module is not null) { start.ArgumentList.Add("--module"); start.ArgumentList.Add(module); }
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the owned fixture.");
        var probe = new Probe(directory, run, process);
        try
        {
            await Until(() =>
            {
                if (process.HasExited) throw new InvalidOperationException("Owned fixture exited before readiness: " + process.ExitCode);
                JsonDocument identity;
                try { identity = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "ready.json"))); }
                catch (Exception ex) when (ex is IOException or JsonException) { return false; }
                using (identity)
                {
                    // Identity validation stays outside the transient read/parse retry catch.
                    if (identity.RootElement.GetProperty("processId").GetInt32() != process.Id ||
                        identity.RootElement.GetProperty("startTimeUtcTicks").GetInt64() != probe.StartTicks ||
                        !string.Equals(identity.RootElement.GetProperty("executablePath").GetString(), probe.Executable, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Fixture identity receipt mismatch.");
                }
                return true;
            }, TimeSpan.FromSeconds(15));
            return probe;
        }
        catch { await probe.DisposeAsync(); throw; }
    }
    public async Task Release()
    {
        if (!Process.HasExited) { File.WriteAllText(Path.Combine(RunDirectory, "allow-close"), "fixture-owned graceful release"); await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
    }
    public async ValueTask DisposeAsync() { try { await Release(); } finally { Process.Dispose(); } }
    public static async Task Until(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed >= timeout) throw new TimeoutException("Fixture readiness/close observation timed out."); await Task.Delay(50); }
    }
}

sealed class InlineProgress(Action<InstallProgress> report) : IProgress<InstallProgress>
{
    public void Report(InstallProgress value) => report(value);
}
