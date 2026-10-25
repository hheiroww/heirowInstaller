using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HeirowInstaller.App;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;

var checks = 0;
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
async Task Reject<T>(Func<Task> action) where T : Exception
{ try { await action(); } catch (T) { checks++; return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
try
{
    foreach (var working in new string?[] { "App", null })
    {
        using (var fixture = new Fixture(working, shared: false))
        {
            await fixture.Install(launch: true);
            await CheckLaunch(fixture, null, shared: false);
        }
        using (var fixture = new Fixture(working, shared: false))
        {
            await fixture.Install();
            using var launch = Process.Start(InstalledApplicationLaunch.Create(fixture.Product, updaterEnabled: false))!;
            await CheckLaunch(fixture, launch, shared: false);
        }
        using (var fixture = new Fixture(working, shared: true))
        {
            await fixture.Install(); fixture.CopyIsolatedRuntimeSupport();
            var start = InstalledApplicationLaunch.Create(fixture.Product, updaterEnabled: true);
            Check(start.FileName == ApplicationUpdater.ResolveExecutable(fixture.Configuration!), "Finish must resolve the verified shared runtime.");
            Check(!File.Exists(Path.Combine(fixture.Target, "heirowUpdater.exe")), "The application must not receive a separate updater copy.");
            start.ArgumentList.Add("--status-file"); start.ArgumentList.Add(fixture.StatusPath);
            using var launch = Process.Start(start)!;
            await CheckLaunch(fixture, launch, shared: true);
        }
    }
    using (var fixture = new Fixture("App", shared: true))
    {
        await fixture.Install();
        var original = File.ReadAllText(fixture.ProfilePath);
        foreach (var tampered in new[] { "Other", "../outside" })
        {
            Directory.CreateDirectory(Path.Combine(fixture.Target, "Other"));
            var profile = JsonNode.Parse(original)!; profile["launchWorkingDirectory"] = tampered;
            File.WriteAllText(fixture.ProfilePath, profile.ToJsonString());
            await Reject<InvalidDataException>(() => Task.Run(() => ApplicationUpdater.ReadConfiguration(fixture.ProfilePath)));
            await Reject<InvalidDataException>(() => Task.Run(() => InstalledApplicationLaunch.Create(fixture.Product, updaterEnabled: true)));
            Check(!File.Exists(fixture.OpenedPath), "A tampered profile must not launch the owned application.");
        }
        File.WriteAllText(fixture.ProfilePath, original);
        Check(ApplicationUpdater.ReadConfiguration(fixture.ProfilePath).LaunchWorkingDirectory == "App", "The restored signed binding must remain usable.");
        using var package = PackageReader.Open(fixture.PackagePath);
        Check(package.Manifest.FormatVersion == 3, "An explicit launch directory requires the new package format.");
        await Reject<InvalidDataException>(() => Task.Run(() => PackageSafety.ResolveWorkingDirectory(fixture.Target, "../outside")));
        await Reject<InvalidDataException>(() => Task.Run(() => PackageSafety.ResolveWorkingDirectory(fixture.Target, fixture.Root)));
        await Reject<DirectoryNotFoundException>(() => Task.Run(() => PackageSafety.ResolveWorkingDirectory(fixture.Target, "missing")));
        var invalid = JsonSerializer.Deserialize<PackageManifest>(JsonSerializer.Serialize(package.Manifest, InstallerJson.Options), InstallerJson.Options)!;
        invalid.LaunchWorkingDirectory = "../outside";
        await Reject<InvalidDataException>(() => Task.Run(() => PackageSafety.ValidateManifest(invalid, true)));
        Check(!JsonSerializer.Serialize(new PackageManifest(), InstallerJson.Options).Contains("launchWorkingDirectory", StringComparison.Ordinal), "Legacy null fields must remain omitted.");
    }
    Console.WriteLine($"Passed {checks} working-directory checks: actual windowless WinExe, Core launch, Finish launch, shared updater launch, signed profile binding and null compatibility. No real applications, backend installers, registry entries or services were changed.");
    return 0;
}
catch (Exception failure) { Console.Error.WriteLine($"FAIL after {checks} checks: {failure}"); return 1; }

async Task CheckLaunch(Fixture fixture, Process? launcher, bool shared)
{
    Process? application = null;
    var watchers = new List<Process>();
    try
    {
        await Wait(() => TryRead(fixture.OpenedPath) is not null);
        var opened = TryRead(fixture.OpenedPath)!;
        var pid = opened["processId"]!.GetValue<int>(); var ticks = opened["startTicks"]!.GetValue<long>();
        application = Process.GetProcessById(pid);
        Check(application.StartTime.ToUniversalTime().Ticks == ticks && Path.GetFullPath(application.MainModule!.FileName).Equals(fixture.Executable, StringComparison.OrdinalIgnoreCase), "The report must identify the exact owned process.");
        Check(Path.GetFullPath(opened["workingDirectory"]!.GetValue<string>()).Equals(fixture.ExpectedDirectory, StringComparison.OrdinalIgnoreCase), "The real WinExe received the wrong current directory.");
        Check(Windows.Count(pid) == 0, "The owned WinExe must remain windowless and nonactivating.");
        if (shared)
        {
            await Wait(() => TryRead(fixture.StatusPath)?["status"]?.GetValue<string>() == "waiting-for-exit");
            var observers = Path.Combine(SharedUpdaterRuntime.ApplicationStateDirectory(fixture.Configuration!), "observers");
            foreach (var file in Directory.EnumerateFiles(observers, "*.lock"))
            {
                var identity = Path.GetFileNameWithoutExtension(file).Split('-');
                if (identity.Length != 3 || !int.TryParse(identity[0], out var ownerPid) || !long.TryParse(identity[1], out var ownerTicks)) continue;
                var watcher = Process.GetProcessById(ownerPid);
                Check(watcher.StartTime.ToUniversalTime().Ticks == ownerTicks && watcher.MainModule!.FileName.Equals(ApplicationUpdater.ResolveExecutable(fixture.Configuration!), StringComparison.OrdinalIgnoreCase), "The observer must identify the exact owned shared updater.");
                watchers.Add(watcher);
            }
            Check(watchers.Count > 0, "The actual shared updater must hold its own observer lease.");
            Check(!application.HasExited, "The app must remain alive while its optional watcher runs.");
            Check(await UpdateCompanionControl.StopAsync(fixture.Configuration!), "The exact owned watcher must stop before the application exits.");
        }
    }
    finally
    {
        if (shared && fixture.Configuration is not null) await UpdateCompanionControl.StopAsync(fixture.Configuration);
        foreach (var watcher in watchers) { await watcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); watcher.Dispose(); }
        File.WriteAllText(Path.Combine(fixture.Target, "App", "allow-exit"), "owned acceptance completed");
        var applicationId = application?.Id;
        if (application is not null)
        {
            await application.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Check(application.ExitCode == 0, "The owned WinExe must exit normally."); application.Dispose();
        }
        if (launcher is not null && launcher.Id != applicationId) await launcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
}
static JsonNode? TryRead(string path)
{ try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null; } catch (IOException) { return null; } catch (JsonException) { return null; } }
static async Task Wait(Func<bool> ready)
{ var deadline = DateTime.UtcNow.AddSeconds(25); while (!ready()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("The owned launch fixture did not become ready."); await Task.Delay(40); } }

sealed class Fixture : IDisposable
{
    internal readonly string Root = Path.Combine(Path.GetTempPath(), "heirowInstaller-working-directory-" + Guid.NewGuid().ToString("N"));
    internal string Target => Path.Combine(Root, "installed");
    internal string State => Path.Combine(Root, "state");
    internal string Executable => Path.Combine(Target, "App", "WorkingDirectoryProbe.exe");
    internal string OpenedPath => Path.Combine(Target, "App", "opened.json");
    internal string ProfilePath => Path.Combine(Target, "heirowUpdater.json");
    internal string StatusPath => Path.Combine(Root, "watcher-status.json");
    internal string ExpectedDirectory => working is null ? Target : Path.Combine(Target, working);
    internal readonly string PackagePath;
    internal InstalledProduct Product => InstallerEngine.ListInstalled(State).Single();
    internal UpdateCompanionConfiguration? Configuration;
    private readonly string? working;
    private readonly bool shared;
    private readonly string fixtures = Path.Combine(AppContext.BaseDirectory, "LaunchFixtures");
    internal Fixture(string? working, bool shared)
    {
        this.working = working; this.shared = shared;
        Directory.CreateDirectory(Root); File.WriteAllText(Path.Combine(Root, ".launch-fixture-root"), "heirowInstaller working directory acceptance\n");
        var source = Path.Combine(Root, "source"); var app = Path.Combine(source, "App"); Directory.CreateDirectory(app);
        foreach (var name in new[] { "WorkingDirectoryProbe.exe", "WorkingDirectoryProbe.dll", "WorkingDirectoryProbe.deps.json", "WorkingDirectoryProbe.runtimeconfig.json" })
            File.Copy(Path.Combine(fixtures, name), Path.Combine(app, name));
        File.WriteAllText(Path.Combine(app, ".working-directory-probe"), "heirowInstaller working directory acceptance\n");
        if (shared) File.Copy(Path.Combine(fixtures, "heirowUpdater.exe"), Path.Combine(source, "heirowUpdater.exe"));
        var manifest = new PackageManifest { Id = "test.working-directory", Name = "Owned working-directory probe", Publisher = "Fixture", Version = "1.0", Scope = "user",
            EntryPoint = "App/WorkingDirectoryProbe.exe", LaunchWorkingDirectory = working, UpdateUrl = "https://updates.example.invalid/feed.json" };
        if (shared)
        {
            manifest.Features = [new() { Id = "updates", Default = true }];
            manifest.UpdateCompanion = new() { RuntimeMode = "shared", UpdateMode = "files", CredentialOrigin = "https://updates.example.invalid" };
        }
        manifest.Payload = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Select(file => new PayloadFile
        { Path = Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/'), Feature = Path.GetFileName(file) == "heirowUpdater.exe" ? "updates" : null }).ToList();
        using var key = RSA.Create(2048); var keyPath = Path.Combine(Root, "fixture-private.pem"); File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        var json = Path.Combine(Root, "manifest.json"); File.WriteAllText(json, JsonSerializer.Serialize(manifest, InstallerJson.Options));
        PackagePath = Path.Combine(Root, "setup.hinst"); PackageBuilder.Build(json, source, PackagePath, new() { SigningKeyPath = keyPath });
    }
    internal async Task Install(bool launch = false)
    {
        using var package = PackageReader.Open(PackagePath);
        await new InstallerEngine().InstallAsync(package, new() { Scope = "user", TargetDirectory = Target, StateRoot = State, Isolated = true,
            RegisterProduct = false, Launch = launch, RequireSignature = true, TrustedPublisherKeySha256 = package.PublisherKeySha256 });
        if (shared) Configuration = ApplicationUpdater.ReadConfiguration(ProfilePath);
    }
    internal void CopyIsolatedRuntimeSupport()
    {
        if (!shared || Configuration is not { Isolated: true }) throw new InvalidOperationException("Runtime sidecars are isolated-fixture only.");
        var directory = Path.GetDirectoryName(ApplicationUpdater.ResolveExecutable(Configuration))!;
        foreach (var file in Directory.EnumerateFiles(fixtures, "*", SearchOption.TopDirectoryOnly).Where(file => Path.GetExtension(file) is ".dll" or ".json"))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: true);
    }
    public void Dispose()
    {
        if (Path.GetDirectoryName(Path.GetFullPath(Root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
            File.ReadAllText(Path.Combine(Root, ".launch-fixture-root")) != "heirowInstaller working directory acceptance\n") throw new InvalidOperationException("Invalid owned launch fixture root.");
        PackageSafety.EnsureNoReparsePoints(Root); Directory.Delete(Root, recursive: true);
    }
}
static class Windows
{
    internal static int Count(int pid) { var count = 0; EnumWindows((window, _) => { GetWindowThreadProcessId(window, out var owner); if (owner == pid) count++; return true; }, IntPtr.Zero); return count; }
    private delegate bool Callback(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
}
