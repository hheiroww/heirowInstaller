using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("These tests exercise Windows Restart Manager and normal window closure.");
if (args.Length != 1) throw new ArgumentException("Pass the freshly published RunningApplicationProbe directory.");
var source = Path.GetFullPath(args[0]);
if (!File.Exists(Path.Combine(source, "RunningApplicationProbe.exe"))) throw new FileNotFoundException("Publish the owned fixture first.");
var configuredRoot = Environment.GetEnvironmentVariable("HEIROWINSTALLER_TEST_OUTPUT_ROOT");
var root = configuredRoot is null
    ? Path.Combine(Path.GetTempPath(), "heirowInstaller-process-tests-" + Guid.NewGuid().ToString("N"))
    : Path.GetFullPath(configuredRoot);
if (Directory.Exists(root)) throw new IOException("Process acceptance requires a fresh owned output directory.");
var keepOutputs = Environment.GetEnvironmentVariable("HEIROWINSTALLER_KEEP_TEST_OUTPUTS") == "1";
Directory.CreateDirectory(root);
Console.WriteLine("Process acceptance output: " + root);
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
PackageManifest Manifest() => new() { Id = "process-safety-fixture", Name = "Desktop acceptance app", Publisher = "Isolated test", Version = "1.0.0", EntryPoint = "RunningApplicationProbe.exe", LegalDocuments = LegalFixture.For("process-safety-fixture") };
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
    var component = Manifest(); component.Scope = "user";
    component.Features = [new() { Id = "classroom", Name = "Classroom", Default = false }];
    component.InstallationRoots = [new() { Id = "plugin", KnownFolder = "commonFiles64", RelativeDirectory = "VST3/Fixture" }];
    component.Payload = [new() { Path = "plugin/RunningApplicationProbe.exe", TargetRoot = "plugin", Feature = "classroom" },
        new() { Path = "plugin/UnrelatedWorker.exe", TargetRoot = "plugin", Feature = "classroom" }];
    var componentPlan = new InstallPlan { Manifest = component, InstallDirectory = authoringRoot, Scope = "machine", Features = ["classroom"] };
    var isolatedComponent = RunningApplicationTarget.FromPlan(componentPlan, "install", isolated: true);
    var isolatedPath = Path.Combine(authoringRoot, ".component-roots", "plugin", "VST3", "Fixture", "RunningApplicationProbe.exe");
    Check(isolatedComponent.AffectedResourceFiles.SequenceEqual([isolatedPath]) && isolatedComponent.OwnedApplicationImages.Contains(isolatedPath), "isolated selected external entrypoint resolves only beneath fixture component root");
    var actualPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3", "Fixture", "RunningApplicationProbe.exe");
    Check(RunningApplicationTarget.FromPlan(componentPlan).AffectedResourceFiles.SequenceEqual([actualPath]), "plan actual machine scope overrides manifest default for selected signed component");
    componentPlan.Features = [];
    Check(RunningApplicationTarget.FromPlan(componentPlan, "install", isolated: true).AffectedResourceFiles.Count == 0, "unselected external copies and differently named workers cannot gain entrypoint force authority");
    var resourceOnly = Manifest(); resourceOnly.AffectedResources = [new() { Path = actualPath }];
    Check(!RunningApplicationTarget.FromManifest(resourceOnly, authoringRoot).OwnedApplicationImages.Contains(actualPath), "same-name external affected resource without signed payload ownership cannot gain force authority");
    var runtimeProfile = new UpdateCompanionConfiguration { VerifiedOwnedApplicationImages = [isolatedPath] };
    var profileJson = JsonSerializer.Serialize(runtimeProfile, InstallerJson.Options);
    Check(!profileJson.Contains("verifiedOwnedApplicationImages", StringComparison.OrdinalIgnoreCase) &&
        JsonSerializer.Deserialize<UpdateCompanionConfiguration>("{\"verifiedOwnedApplicationImages\":[\"forged.exe\"]}", InstallerJson.Options)!.VerifiedOwnedApplicationImages.Count == 0,
        "writable updater profile cannot serialize or supply verified external force authority");
    var oldProfile = JsonSerializer.Deserialize<UpdateCompanionConfiguration>("{}", InstallerJson.Options)!;
    Check(oldProfile.AffectedResourceFiles.Count == 0, "existing updater profiles preserve empty resource default");
    Check(!JsonSerializer.Serialize(new InstallOptions { RunningApplicationsPrompt = (_, _) => Task.FromResult(RunningApplicationsChoice.Cancel) }, InstallerJson.Options).Contains("runningApplicationsPrompt"), "runtime consent callback is never serialized");

    var boundedManifest = Manifest();
    boundedManifest.Features = [new() { Id = "optional", Name = "Optional plugin", Default = false }];
    boundedManifest.Payload = [new() { Path = "RunningApplicationProbe.exe" }, new() { Path = "Required.dll" },
        new() { Path = "Optional.dll", Feature = "optional" }, new() { Path = "settings.json" }];
    boundedManifest.AffectedResources = [new() { Path = "{app}\\External.vst3", Feature = "optional" }];
    var boundedTarget = RunningApplicationTarget.FromManifest(boundedManifest, authoringRoot);
    Check(boundedTarget.InspectionResourceFiles is { } boundedFiles && boundedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase)
        .SetEquals([Path.Combine(authoringRoot, "RunningApplicationProbe.exe"), Path.Combine(authoringRoot, "Required.dll")]),
        "verified payload limits inspection to selected executable resources");
    var optionalTarget = RunningApplicationTarget.FromManifest(boundedManifest, authoringRoot, features: ["optional"]);
    Check(optionalTarget.InspectionResourceFiles is { } optionalFiles && optionalFiles.Contains(Path.Combine(authoringRoot, "Optional.dll")) &&
        optionalFiles.Contains(Path.Combine(authoringRoot, "External.vst3")) && !optionalFiles.Contains(Path.Combine(authoringRoot, "settings.json")),
        "selected executable payload and exact declared plugin resources are inspected together");
    Check(RunningApplicationTarget.FromManifest(Manifest(), authoringRoot).InspectionResourceFiles is null,
        "metadata-free legacy targets retain conservative root discovery");
    var opaqueMsi = Manifest(); opaqueMsi.Payload = [new() { Path = "backend.msi" }]; opaqueMsi.LegacyMsi = new();
    Check(RunningApplicationTarget.FromManifest(opaqueMsi, authoringRoot).InspectionResourceFiles is null,
        "opaque MSI backend targets retain discovery of files absent from the wrapper payload");
    var opaqueExecutable = Manifest(); opaqueExecutable.Payload = [new() { Path = "backend.exe" }]; opaqueExecutable.LegacyExecutable = new();
    Check(RunningApplicationTarget.FromManifest(opaqueExecutable, authoringRoot).InspectionResourceFiles is null,
        "opaque executable backend targets retain discovery of files absent from the wrapper payload");
    var boundedProfile = new UpdateCompanionConfiguration { ProductId = "process-safety-fixture", Name = "Fixture",
        InstallDirectory = authoringRoot, EntryPoint = "RunningApplicationProbe.exe", VerifiedInspectionResourceFiles = boundedTarget.InspectionResourceFiles };
    Check(RunningApplicationTarget.FromConfiguration(boundedProfile).InspectionResourceFiles!.SequenceEqual(boundedTarget.InspectionResourceFiles!),
        "verified runtime payload resources survive configuration target construction");
    Check(!JsonSerializer.Serialize(boundedProfile, InstallerJson.Options).Contains("verifiedInspectionResourceFiles", StringComparison.OrdinalIgnoreCase) &&
        JsonSerializer.Deserialize<UpdateCompanionConfiguration>("{\"verifiedInspectionResourceFiles\":[\"forged.dll\"]}", InstallerJson.Options)!.VerifiedInspectionResourceFiles is null,
        "writable updater profiles cannot supply inspection scope authority");

    var scopedRoot = Path.Combine(root, "signed-payload-scope"); Directory.CreateDirectory(scopedRoot);
    await using (var current = await Probe.Start(source, scopedRoot, "App", true))
    await using (var backup = await Probe.Start(source, scopedRoot, "App.backup-20000101-000000-0123456789abcdef0123456789abcdef", true))
    {
        var scopedManifest = Manifest(); scopedManifest.EntryPoint = "App/RunningApplicationProbe.exe";
        scopedManifest.Payload = [new() { Path = "App/RunningApplicationProbe.exe" }, new() { Path = "App/RunningApplicationProbe.dll" }];
        var target = RunningApplicationTarget.FromManifest(scopedManifest, scopedRoot);
        var snapshot = await RunningApplications.InspectAsync(target);
        Check(snapshot.Count == 1 && snapshot[0].ProcessId == current.Process.Id && snapshot[0].CanForceClose,
            "signed current payload detects the active app without inspecting preserved backup images");
        Check(current.Requests == 0 && backup.Requests == 0 && !backup.Process.HasExited,
            "bounded payload inspection preserves both current and backup processes");
        var helperManifest = Manifest(); helperManifest.EntryPoint = "App/AnotherApplication.exe";
        helperManifest.Payload = [new() { Path = "App/AnotherApplication.exe" }, new() { Path = "App/RunningApplicationProbe.exe" },
            new() { Path = "App/RunningApplicationProbe.dll" }];
        var helperSnapshot = await RunningApplications.InspectAsync(RunningApplicationTarget.FromManifest(helperManifest, scopedRoot));
        Check(helperSnapshot.Count == 1 && helperSnapshot[0].ProcessId == current.Process.Id &&
            !helperSnapshot[0].CanRequestClose && !helperSnapshot[0].CanForceClose,
            "inspected payload helpers never gain entrypoint closure authority");
        var fallback = Manifest(); fallback.EntryPoint = scopedManifest.EntryPoint;
        var legacySnapshot = await RunningApplications.InspectAsync(RunningApplicationTarget.FromManifest(fallback, scopedRoot));
        Check(legacySnapshot.Any(x => x.ProcessId == current.Process.Id) && legacySnapshot.Any(x => x.ProcessId == backup.Process.Id),
            "legacy discovery still inspects executable resources throughout its unscoped installation root");
    }

    await using (var app = await Probe.Start(source, root, "precise", true))
    await using (var unrelated = await Probe.Start(source, root, "same-name-other-root", true))
    {
        var observed = await RunningApplications.InspectAsync(Target(app));
        Check(observed.Count == 1 && observed[0].ProcessId == app.Process.Id && observed[0].StartTimeUtcTicks == app.StartTicks && observed[0].ExecutablePath == app.Executable, "only exact executable/root and UTC creation identity matches");
        Check(observed[0].CanRequestClose && observed[0].CanForceClose && observed[0].Kind == "application", "verified GUI application can receive normal close and explicit force consent");
        var identity = observed[0];
        Check(!RunningApplications.ForceCloseForTests(Target(app), identity with { StartTimeUtcTicks = identity.StartTimeUtcTicks + 1 }), "force rejects stale creation identity");
        Check(!RunningApplications.ForceCloseForTests(Target(app), identity with { ExecutablePath = unrelated.Executable }), "force rejects changed executable path");
        Check(!RunningApplications.ForceCloseForTests(Target(app), identity with { Kind = "plugin-host" }), "force rejects plugin host identities");
        Check(!RunningApplications.ForceCloseForTests(Target(app), identity with { CanForceClose = false }), "manual identity cannot gain force authority");
        Check(!RunningApplications.ForceCloseForTests(Target(app), identity with { ProcessId = unrelated.Process.Id, StartTimeUtcTicks = unrelated.StartTicks, ExecutablePath = unrelated.Executable, AffectedPath = unrelated.Executable }), "unlisted same-name external application cannot inherit force consent");
        Check(!RunningApplications.ForceCloseForTests(Target(app).WithWaitOnlyProcesses([identity]), identity), "wait-only process cannot be force closed even with an old eligible snapshot");
        var serviceManifest = Manifest(); serviceManifest.Services = [new() { Name = "FixtureService", Executable = "RunningApplicationProbe.exe" }];
        Check(!RunningApplications.ForceCloseForTests(RunningApplicationTarget.FromManifest(serviceManifest, app.Directory), identity), "authored service image cannot be force closed");
        var otherManifest = Manifest(); otherManifest.EntryPoint = "OtherApplication.exe";
        Check(!RunningApplications.ForceCloseForTests(RunningApplicationTarget.FromManifest(otherManifest, app.Directory), identity), "non-entry helper or AI image cannot gain authority from installation root");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Throws<OperationCanceledException>(() => Task.Run(() => RunningApplications.ForceCloseForTests(Target(app), identity, cancelled.Token)), "canceled force consent performs no action");
        }
        Check(!app.Process.HasExited && !unrelated.Process.HasExited && app.Requests == 0, "all rejected force identities preserve both owned fixtures");
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

    await using (var app = await Probe.Start(source, root, "guard-completion", false))
    await using (var unrelated = await Probe.Start(source, root, "guard-unrelated", true))
    {
        string? guardId = null; var prompts = 0; var completions = 0;
        await RunningApplications.EnsureClosedAsync(Target(app), (request, _) =>
        {
            if (request.Completed)
            {
                completions++;
                Check(request.GuardId == guardId && request.Applications.Count == 0 && app.Process.HasExited,
                    "successful guard sends its correlated terminal event only after affected applications exit");
                return Task.FromResult(RunningApplicationsChoice.ForceCloseListedApps);
            }
            request.NotifyCompletion = true; guardId = request.GuardId; prompts++;
            return Task.FromResult(RunningApplicationsChoice.CloseAppsAndContinue);
        });
        Check(prompts == 1 && completions == 1 && !unrelated.Process.HasExited && unrelated.Requests == 0,
            "terminal callback return never grants process closure authority");
    }

    await using (var app = await Probe.Start(source, root, "guard-completion-failures", true))
    {
        foreach (var failure in new[] { "cancel", "error", "disposed-ui", "unresponsive-ui" })
        {
            var completed = false; var watch = Stopwatch.StartNew();
            Task<RunningApplicationsChoice> Prompt(RunningApplicationsRequest request, CancellationToken token)
            {
                if (request.Completed)
                {
                    completed = true;
                    if (failure == "unresponsive-ui") return new TaskCompletionSource<RunningApplicationsChoice>().Task;
                    throw new ObjectDisposedException("Fixture UI");
                }
                request.NotifyCompletion = true;
                if (failure == "error") throw new InvalidDataException("Original fixture failure");
                return Task.FromResult(RunningApplicationsChoice.Cancel);
            }
            if (failure == "error")
                await Throws<InvalidDataException>(() => RunningApplications.EnsureClosedAsync(Target(app), Prompt), "terminal presentation failure preserves original guard error");
            else
                await Throws<OperationCanceledException>(() => RunningApplications.EnsureClosedAsync(Target(app), Prompt), "terminal presentation failure preserves guard cancellation");
            Check(completed && watch.Elapsed < TimeSpan.FromSeconds(5) && !app.Process.HasExited && app.Requests == 0,
                "guard cleanup remains bounded and cannot bypass cancellation or touch an application");
        }
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

    await using (var app = await Probe.Start(source, root, "windowless", true, mode: "--windowless"))
    {
        var snapshot = (await RunningApplications.InspectAsync(Target(app))).Single();
        Check(!snapshot.CanRequestClose && snapshot.CanForceClose, "windowless exact owned application offers force separately from graceful close");
        var prompts = 0;
        await RunningApplications.EnsureClosedAsync(Target(app), (request, _) =>
        {
            Check(request.Applications.Single().ProcessId == app.Process.Id, "windowless force consent contains only the displayed exact fixture");
            return Task.FromResult(++prompts == 1 ? RunningApplicationsChoice.CheckAgain : RunningApplicationsChoice.ForceCloseListedApps);
        });
        Check(prompts == 2 && app.Process.HasExited && app.Requests == 0, "explicit force terminates only the approved windowless process");
    }

    await using (var app = await Probe.Start(source, root, "unresponsive", true, mode: "--unresponsive"))
    await using (var unrelated = await Probe.Start(source, root, "unresponsive-unrelated", true))
    {
        var prompts = 0; var watch = Stopwatch.StartNew();
        await RunningApplications.EnsureClosedAsync(Target(app), (request, _) =>
        {
            prompts++;
            if (prompts == 1) return Task.FromResult(RunningApplicationsChoice.CloseAppsAndContinue);
            Check(request.IsRetry && watch.Elapsed < TimeSpan.FromSeconds(13), "non-pumping process returns bounded retry after graceful attempt");
            return Task.FromResult(RunningApplicationsChoice.ForceCloseListedApps);
        });
        Check(prompts == 2 && app.Process.HasExited && !unrelated.Process.HasExited && unrelated.Requests == 0, "confirmed force handles non-pumping fixture without touching unrelated same-name process");
    }

    await using (var app = await Probe.Start(source, root, "stuck-inspection", true))
    {
        var snapshot = await RunningApplications.InspectAsync(Target(app));
        var calls = 0; using var release = new ManualResetEventSlim(); using var exited = new ManualResetEventSlim();
        using var test = RunningApplications.OverrideInspectionForTests(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) return snapshot;
            try { release.Wait(); return snapshot; } finally { exited.Set(); }
        });
        try
        {
            var prompts = 0; var watch = Stopwatch.StartNew();
            await Throws<OperationCanceledException>(() => RunningApplications.EnsureClosedAsync(Target(app), (request, _) =>
            {
                prompts++;
                if (prompts == 1) return Task.FromResult(RunningApplicationsChoice.CloseAppsAndContinue);
                Check(request.InspectionTimedOut && request.Applications.Single().ProcessId == app.Process.Id && watch.Elapsed < TimeSpan.FromSeconds(14), "stuck native discovery returns bounded prompt with previous exact snapshot");
                return Task.FromResult(RunningApplicationsChoice.Cancel);
            }), "cancel remains available after a stuck native inspection");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            watch.Restart();
            await Throws<OperationCanceledException>(() => RunningApplications.InspectAsync(Target(app), cancellation.Token), "coalesced stuck scan accepts caller cancellation");
            Check(calls == 2 && watch.Elapsed < TimeSpan.FromSeconds(2) && app.Requests == 0, "retries share one stuck read-only worker and never send late close");
        }
        finally { release.Set(); exited.Wait(TimeSpan.FromSeconds(3)); }
        await Task.Delay(150);
        Check(!app.Process.HasExited && app.Requests == 0, "released timed-out inspection cannot execute an abandoned close action");
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
                    Check(!RunningApplications.ForceCloseForTests(Target(original), request.Applications.Single()) && !replacement.Process.HasExited,
                        "stale force consent never terminates a restarted replacement");
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
        Check(snapshot.Count == 1 && snapshot[0].ProcessId == host.Process.Id && !snapshot[0].CanRequestClose && !snapshot[0].CanForceClose, "registered host path alone permits only manual closure");
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
        Check(snapshot.Count == 1 && snapshot[0].ProcessId == host.Process.Id && snapshot[0].Kind == "plugin-host" && snapshot[0].CanRequestClose && !snapshot[0].CanForceClose && snapshot[0].AffectedPath == plugin,
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
    if (!keepOutputs && configuredRoot is null &&
        Path.GetDirectoryName(root)?.Equals(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) == true &&
        Path.GetFileName(root).StartsWith("heirowInstaller-process-tests-", StringComparison.Ordinal))
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    else Console.WriteLine("Retained process acceptance output: " + root);
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
    public static async Task<Probe> Start(string source, string root, string name, bool keepOpen, string? module = null, string? mode = null)
    {
        var directory = Path.Combine(root, name); System.IO.Directory.CreateDirectory(directory);
        foreach (var file in System.IO.Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        return await StartExisting(directory, root, name, keepOpen, module, mode);
    }
    public static async Task<Probe> StartExisting(string directory, string root, string name, bool keepOpen, string? module = null, string? mode = null)
    {
        var run = Path.Combine(root, name + "-run-" + Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, ".running-application-probe"), "heirowInstaller running-application acceptance\n", new UTF8Encoding(false));
        var start = new ProcessStartInfo(Path.Combine(directory, "RunningApplicationProbe.exe")) { UseShellExecute = false, WorkingDirectory = directory, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Normal };
        start.ArgumentList.Add("--run-directory"); start.ArgumentList.Add(run);
        if (keepOpen) start.ArgumentList.Add("--keep-open");
        start.ArgumentList.Add("--nonactivating");
        if (mode is not null) start.ArgumentList.Add(mode);
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
