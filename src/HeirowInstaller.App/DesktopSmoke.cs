using System.Text.Json;
using System.Diagnostics;
using HeirowInstaller.Core;
using ProjectZ.Shared.Drawing.UI.Input;
using Button = ProjectZ.Shared.Drawing.UI.Input.Button;

namespace HeirowInstaller.App;

internal static class DesktopSmoke
{
    internal static async Task RunAsync(InstallerGame game, string directory)
    {
        var checks = new List<string>();
        Directory.CreateDirectory(directory);
        var fixture = Path.Combine(directory, "fixture");
        var install = Path.Combine(directory, "installed");
        var probes = new List<(Process Process, string Directory)>();
        try
        {
            Directory.CreateDirectory(fixture);
            var probeSource = Environment.GetEnvironmentVariable("HEIROWINSTALLER_SMOKE_APPLICATION") ?? throw new InvalidOperationException("Build RunningApplicationProbe and set HEIROWINSTALLER_SMOKE_APPLICATION to its published executable before running native acceptance.");
            probeSource = Path.GetFullPath(probeSource);
            PackageSafety.EnsureNoReparsePoints(probeSource);
            if (!File.Exists(probeSource) || Path.GetFileName(probeSource) != "RunningApplicationProbe.exe") throw new InvalidDataException("Native acceptance requires the dedicated RunningApplicationProbe executable.");
            var probeFiles = new[] { "RunningApplicationProbe.exe", "RunningApplicationProbe.dll", "RunningApplicationProbe.deps.json", "RunningApplicationProbe.runtimeconfig.json" };
            var probePayload = Path.Combine(fixture, "application");
            Directory.CreateDirectory(probePayload);
            foreach (var file in probeFiles)
            {
                var source = Path.Combine(Path.GetDirectoryName(probeSource)!, file);
                PackageSafety.EnsureNoReparsePoints(source);
                File.Copy(source, Path.Combine(probePayload, file));
            }
            await File.WriteAllTextAsync(Path.Combine(fixture, "hello.txt"), "heirowInstaller native desktop acceptance\n");
            await File.WriteAllTextAsync(Path.Combine(fixture, "readme.txt"), "An optional documentation feature.\n");
            await File.WriteAllTextAsync(Path.Combine(fixture, "LICENSE.txt"), "Desktop acceptance license\n\nPermission is granted to install this isolated acceptance fixture.\n");
            var manifest = new PackageManifest
            {
                Id = "heirowInstaller.desktop-fixture", Name = "Desktop acceptance app", Version = "1.0.0", Publisher = "heirowInstaller acceptance",
                Description = "An isolated package used to verify the native installer workflow.",
                EntryPoint = "application/RunningApplicationProbe.exe",
                LicenseFile = "LICENSE.txt",
                Features = [new() { Id = "core", Name = "Application files", Description = "Required application components.", Required = true }, new() { Id = "docs", Name = "Documentation", Description = "Optional reference files.", Default = true }],
                Payload = [new() { Source = "hello.txt", Path = "hello.txt", Feature = "core" }, new() { Source = "readme.txt", Path = "readme.txt", Feature = "docs" }, new() { Source = "LICENSE.txt", Path = "LICENSE.txt", Feature = "core" }]
            };
            manifest.Payload.AddRange(probeFiles.Select(file => new PayloadFile { Source = "application/" + file, Path = "application/" + file, Feature = "core" }));
            var manifestPath = Path.Combine(fixture, "installer.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options));
            var package = Path.Combine(directory, "desktop-fixture.hinst");
            await Task.Delay(500);
            await game.CaptureAsync(Path.Combine(directory, "01-welcome.png"));
            if (Environment.GetEnvironmentVariable("HEIROWINSTALLER_SMOKE_MALFORMED_UPDATER_PROFILE") == "1")
                Require(string.IsNullOrEmpty(game.MainScene.Controller.UpdaterPath), "Malformed own-root updater metadata disabled shared updater actions without selecting a fallback executable");
            var navigationFrames = new List<InstallerGame.NavigationFrame>();
            foreach (var fixedTimeStep in new[] { true, false })
            {
                // Readback can cause fixed-step catch-up ticks. Also exercise a
                // single layout tick per presentation so those extra ticks cannot
                // conceal a page rebuilt after layout.
                await game.OnUiAsync(() => game.IsFixedTimeStep = fixedTimeStep);
                for (var pass = 1; pass <= 2; pass++)
                    foreach (var tab in new[] { "workspace", "installed", "updates", "activity", "settings", "install" })
                        navigationFrames.AddRange(await game.CaptureNavigationAsync("nav." + tab, Path.Combine(directory, "navigation", $"{(fixedTimeStep ? "fixed" : "single-update")}-{pass}-{tab}")));
            }
            await game.OnUiAsync(() => game.IsFixedTimeStep = true);
            await File.WriteAllTextAsync(Path.Combine(directory, "navigation-frames.json"), JsonSerializer.Serialize(navigationFrames, InstallerJson.Options));
            Require(navigationFrames.All(frame => frame.RailPixels > 10_000 && frame.HeaderTextPixels > 200), "All 96 consecutive fixed-step and single-update navigation frames retained the sidebar and header across cold and repeated visits to every tab");
            await Click("nav.workspace");
            await game.OnUiAsync(() =>
            {
                ((Textbox)game.MainScene.Control("workspace.manifest")).Text = manifestPath;
                ((Textbox)game.MainScene.Control("workspace.source")).Text = fixture;
                ((Textbox)game.MainScene.Control("workspace.output")).Text = package;
                ((Textbox)game.MainScene.Control("workspace.notes")).Text = "Verified through native Project-Z pointer and control events.";
            });
            await game.CaptureAsync(Path.Combine(directory, "02-workspace.png"));
            await Click("workspace.build");
            await Wait(() => !game.MainScene.Controller.Busy && game.MainScene.Controller.BuiltPackagePath is not null);
            Require(File.Exists(package), "Native Build button created a verified package");
            await Click("workspace.test");
            await Wait(() => !game.MainScene.Controller.Busy && game.MainScene.Controller.Manifest is not null);
            await game.CaptureAsync(Path.Combine(directory, "03-package.png"));
            await Click("setup.next");
            await game.CaptureAsync(Path.Combine(directory, "03-license.png"));
            await Click("license.accept");
            Require(await Ui(() => game.MainScene.Controller.LicenseAccepted), "Native license agreement required and recorded acceptance");
            await Click("setup.next");
            await Click("feature.docs");
            Require(!await Ui(() => game.MainScene.Controller.Features.Contains("docs")), "Feature button excluded an optional component");
            await game.CaptureAsync(Path.Combine(directory, "04-features.png"));
            await Click("setup.next");
            await game.OnUiAsync(() => ((Textbox)game.MainScene.Control("setup.target")).Text = install);
            await game.CaptureAsync(Path.Combine(directory, "05-destination.png"));
            await Click("setup.next");
            Require(await Ui(() => game.MainScene.Controller.Plan!.InstallDirectory == install), "Review plan used edited installation path");
            await game.CaptureAsync(Path.Combine(directory, "06-review.png"));
            await Click("setup.next");
            await Wait(() => !game.MainScene.Controller.Busy && game.MainScene.Controller.Stage == SetupStage.Complete);
            Require(File.Exists(Path.Combine(install, "hello.txt")), "Native Install button installed required payload");
            Require(!File.Exists(Path.Combine(install, "readme.txt")), "Installation respected optional-feature exclusion");
            await game.CaptureAsync(Path.Combine(directory, "07-complete.png"));
            await Click("setup.installed");
            await game.CaptureAsync(Path.Combine(directory, "08-installed.png"));
            await File.WriteAllTextAsync(Path.Combine(install, "hello.txt"), "intentional acceptance corruption");
            var installedReceipt = await Ui(() => game.MainScene.Controller.LastReceiptPath!);
            var receiptBeforePrompt = await File.ReadAllBytesAsync(installedReceipt);
            var cancelProbe = await StartProbe("cancel", keepOpen: false);
            await Click("installed.repair");
            await Wait(() => game.MainScene.Controller.RunningApplicationsDialog is { Waiting: false });
            Require(await Ui(() => game.MainScene.Controller.RunningApplicationsDialog!.Applications.Any(application => application.ProcessId == cancelProbe.Id)), "Running-app prompt listed the actual isolated application using affected files");
            await game.CaptureAsync(Path.Combine(directory, "08a-close-apps.png"));
            await game.OnUiAsync(() => game.ResizeForAcceptance(980, 760));
            await Task.Delay(160);
            await game.CaptureAsync(Path.Combine(directory, "08a-close-apps-compact.png"));
            Require(await Ui(() => game.MainScene.Control("running.close").Rectangle.Right <= 980 && game.MainScene.Control("running.cancel").Rectangle.Bottom <= 760), "The native close-apps prompt retained both choices at the minimum 980 by 760 viewport");
            await game.OnUiAsync(() => game.ResizeForAcceptance(1180, 800));
            await Task.Delay(160);
            await Click("running.cancel");
            await Wait(() => !game.MainScene.Controller.Busy && game.MainScene.Controller.RunningApplicationsDialog is null);
            Require(!cancelProbe.HasExited && !File.Exists(Path.Combine(directory, "running-cancel", "close-requests.json")), "Cancel made no close request and left the application running");
            var receiptAfterCancel = await File.ReadAllBytesAsync(installedReceipt);
            Require(await File.ReadAllTextAsync(Path.Combine(install, "hello.txt")) == "intentional acceptance corruption" && receiptBeforePrompt.SequenceEqual(receiptAfterCancel), "Cancel preserved application files and the installed receipt before any repair mutation");
            await Click("installed.repair");
            await Wait(() => game.MainScene.Controller.RunningApplicationsDialog is { Waiting: false });
            await Click("running.close");
            await Wait(() => !game.MainScene.Controller.Busy && game.MainScene.Controller.Status == "Repair complete");
            await cancelProbe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(cancelProbe.ExitCode == 0 && File.Exists(Path.Combine(directory, "running-cancel", "close-requests.json")), "Close apps and continue requested normal application closure and completed repair after exit");
            Require((await File.ReadAllTextAsync(Path.Combine(install, "hello.txt"))).Contains("native desktop acceptance"), "The close-apps repair restored the payload only after the application exited");

            var keepOpenProbe = await StartProbe("keep-open", keepOpen: true);
            await File.WriteAllTextAsync(Path.Combine(install, "hello.txt"), "save confirmation remains open");
            receiptBeforePrompt = await File.ReadAllBytesAsync(installedReceipt);
            await Click("installed.repair");
            await Wait(() => game.MainScene.Controller.RunningApplicationsDialog is { Waiting: false });
            await Click("running.close");
            await Wait(() => game.MainScene.Controller.RunningApplicationsDialog is { Waiting: true });
            await game.CaptureAsync(Path.Combine(directory, "08b-save-work.png"));
            await Wait(() => game.MainScene.Controller.RunningApplicationsDialog is { IsRetry: true, Waiting: false });
            Require(!keepOpenProbe.HasExited && File.Exists(Path.Combine(directory, "running-keep-open", "close-requests.json")), "An application save guard refused closure without being force-terminated");
            var receiptAfterRefusedClose = await File.ReadAllBytesAsync(installedReceipt);
            Require(await File.ReadAllTextAsync(Path.Combine(install, "hello.txt")) == "save confirmation remains open" && receiptBeforePrompt.SequenceEqual(receiptAfterRefusedClose), "A kept-open application blocked payload and receipt changes and returned to the retry prompt");
            await game.CaptureAsync(Path.Combine(directory, "08c-retry-close-apps.png"));
            await Click("running.cancel");
            await Wait(() => !game.MainScene.Controller.Busy && game.MainScene.Controller.RunningApplicationsDialog is null);
            Require(!keepOpenProbe.HasExited, "Cancel remained available after a save guard kept the application open");
            await File.WriteAllTextAsync(Path.Combine(directory, "running-keep-open", "allow-close"), "Acceptance fixture may close normally.");
            await keepOpenProbe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Click("installed.repair");
            await Wait(() => !game.MainScene.Controller.Busy && game.MainScene.Controller.Status == "Repair complete");
            Require((await File.ReadAllTextAsync(Path.Combine(install, "hello.txt"))).Contains("native desktop acceptance"), "Native Repair button restored a changed file");
            await Click("installed.uninstall");
            await game.CaptureAsync(Path.Combine(directory, "09-uninstall-confirmation.png"));
            await Click("confirm.cancel");
            Require(File.Exists(Path.Combine(install, "hello.txt")), "Cancel preserved the installed application");
            await Click("installed.uninstall");
            await Click("confirm.uninstall");
            await Wait(() => !game.MainScene.Controller.Busy && game.MainScene.Controller.Installed.Count == 0);
            Require(!File.Exists(Path.Combine(install, "hello.txt")), "Native confirmed Uninstall button removed recorded payload");
            await Click("nav.activity");
            await game.CaptureAsync(Path.Combine(directory, "10-activity.png"));
            await Click("nav.settings");
            await game.CaptureAsync(Path.Combine(directory, "11-settings.png"));
            await game.OnUiAsync(() => game.ResizeForAcceptance(980, 760));
            await Task.Delay(400);
            await game.CaptureAsync(Path.Combine(directory, "12-compact-settings.png"));
            Require(await Ui(() => game.MainScene.Control("settings.save").Rectangle.Right <= 980 && game.MainScene.Control("settings.save").Rectangle.Bottom <= 760 - 103), "Native resized layout retained usable controls at 980 by 760");
            await Click("nav.updates");
            await game.CaptureAsync(Path.Combine(directory, "13-updater.png"));
            if (await Ui(() => File.Exists(game.MainScene.Controller.UpdaterPath)))
            {
                await Click("updates.check");
                await Wait(() => !game.MainScene.Controller.Busy && game.MainScene.Controller.Status == "Updater operation complete");
                Require(await Ui(() => !string.IsNullOrWhiteSpace(game.MainScene.Controller.UpdaterOutput)), "Native Check all applications button ran the real companion updater");
                await Click("updates.status");
                await Wait(() => !game.MainScene.Controller.Busy && game.MainScene.Controller.Status == "Updater operation complete");
                Require(await Ui(() => game.MainScene.Controller.UpdaterOutput.Contains("enabled", StringComparison.OrdinalIgnoreCase)), "Native Schedule status button queried the real updater without changing scheduling");
                await game.CaptureAsync(Path.Combine(directory, "14-updater-result.png"));
            }
            await Click("nav.workspace");
            Require(await Ui(() => ((Button)game.MainScene.Control("workspace.publisher")).Text.Contains("Sign in to SocketJack")), "The native workspace exposes the in-memory publisher sign-in action");
            var standalone = Path.Combine(directory, "native-Setup.exe");
            await game.OnUiAsync(() => ((Textbox)game.MainScene.Control("workspace.output")).Text = standalone);
            await Click("workspace.build");
            await Wait(() => !game.MainScene.Controller.Busy && game.MainScene.Controller.BuiltPackagePath == standalone);
            Require(File.Exists(standalone) && PackageReader.HasEmbeddedPackage(standalone), "The native Build button created a standalone setup executable");
            using (var setup = PackageReader.Open(standalone))
            {
                await setup.VerifyAsync();
                Require(setup.Manifest.Id == manifest.Id && setup.Manifest.Version == manifest.Version, "The native standalone setup contains the verified application payload");
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HEIROWINSTALLER_SIGNING_KEY_PATH")))
                    Require(setup.PublisherKeySha256 is { Length: 64 }, "The native standalone Build action uses the ephemeral signing-key environment setting");
            }
            await game.CaptureAsync(Path.Combine(directory, "15-standalone-workspace.png"));
            Require(await Ui(() => game.MainScene.Quality.AntiAliasingSamples == 16 && game.MainScene.Quality.Filtering == ProjectZ.Shared.Drawing.TextureFiltering.Anisotropic16x), "Runtime used 16x anti-aliasing and anisotropic filtering");
            await File.WriteAllTextAsync(Path.Combine(directory, "ui-smoke.json"), JsonSerializer.Serialize(new { success = true, framework = "Project-Z", checks, isolated = true, notes = "Real native pointer/control events. Isolated state and filesystem only; no registry, shortcuts, services, publisher authentication or public update deployment." }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            await File.WriteAllTextAsync(Path.Combine(directory, "ui-smoke.json"), JsonSerializer.Serialize(new { success = false, checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            try
            {
                foreach (var probe in probes)
                {
                    try
                    {
                        if (!probe.Process.HasExited)
                        {
                            await File.WriteAllTextAsync(Path.Combine(probe.Directory, "allow-close"), "Acceptance fixture may close normally.");
                            await probe.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                        }
                    }
                    finally { probe.Process.Dispose(); }
                }
            }
            finally { game.Enqueue(game.Exit); }
        }

        void Require(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            checks.Add(description);
        }

        async Task Click(string id)
        {
            await game.OnUiAsync(() => game.Activate(id));
            await Task.Delay(160);
        }

        async Task<Process> StartProbe(string name, bool keepOpen)
        {
            var run = Path.Combine(directory, "running-" + name);
            Directory.CreateDirectory(run);
            await File.WriteAllTextAsync(Path.Combine(run, ".running-application-probe"), "heirowInstaller running-application acceptance\n");
            var start = new ProcessStartInfo(Path.Combine(install, "application", "RunningApplicationProbe.exe")) { UseShellExecute = false, WorkingDirectory = install };
            start.ArgumentList.Add("--run-directory"); start.ArgumentList.Add(run);
            start.ArgumentList.Add("--nonactivating");
            if (keepOpen) start.ArgumentList.Add("--keep-open");
            start.Environment.Remove("HEIROWUPDATER_TOKEN"); start.Environment.Remove("HEIROWUPDATER_COOKIE");
            start.Environment.Remove("SOCKETJACK_DOWNLOAD_TOKEN"); start.Environment.Remove("SOCKETJACK_DOWNLOAD_COOKIE");
            var process = Process.Start(start) ?? throw new InvalidOperationException("The owned running-application fixture did not start.");
            probes.Add((process, run));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(Path.Combine(run, "ready.json")))
            {
                if (process.HasExited) throw new InvalidOperationException("The running-application fixture exited with code " + process.ExitCode);
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("The running-application fixture did not become ready.");
                await Task.Delay(50);
            }
            return process;
        }

        async Task<T> Ui<T>(Func<T> get)
        {
            T value = default!;
            await game.OnUiAsync(() => value = get());
            return value;
        }

        async Task Wait(Func<bool> complete)
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                if (await Ui(() => game.MainScene.Controller.Failed)) throw new InvalidOperationException(await Ui(() => game.MainScene.Controller.StatusDetail));
                if (await Ui(complete)) break;
                if (DateTime.UtcNow >= until) throw new TimeoutException("Native UI operation did not complete within 30 seconds.");
                await Task.Delay(80);
            }
            await Task.Delay(160);
        }
    }
}
