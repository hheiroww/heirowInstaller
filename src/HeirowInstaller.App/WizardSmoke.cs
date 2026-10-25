using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using HeirowInstaller.Core;

namespace HeirowInstaller.App;

/// <summary>Pointer-driven, package-bound acceptance; every process and file belongs to the marked fixture.</summary>
internal static class WizardSmoke
{
    internal static async Task RunAsync(InstallerGame game, AppArguments args)
    {
        if (args.MaintenanceProductId is not null) { await MaintenanceWizardSmoke.RunAsync(game, args); return; }
        var directory = args.WizardSmokeDirectory!;
        var checks = new List<string>();
        var processes = new List<(Process Process, string Run)>();
        var successfulFinish = false;
        try
        {
            PackageSafety.EnsureNoReparsePoints(directory);
            if (!args.Isolated || !args.NoRegister || !File.Exists(Path.Combine(directory, ".isolated-wizard-fixture")) ||
                File.ReadAllText(Path.Combine(directory, ".isolated-wizard-fixture")) != "heirowInstaller isolated wizard acceptance v1\n")
                throw new InvalidDataException("Wizard acceptance requires a fresh marked isolated root.");
            var installed = Path.Combine(directory, "installed");
            var state = Path.Combine(directory, "state");
            using (var package = PackageReader.Open(args.PackagePath!))
            {
                if (!package.Manifest.Id.StartsWith("heirowInstaller.wizard-fixture", StringComparison.Ordinal)) throw new InvalidDataException("Only the dedicated wizard fixture may be installed by acceptance.");
                await new InstallerEngine().InstallAsync(package, new() { TargetDirectory = installed, StateRoot = state, Isolated = true, RegisterProduct = false, Launch = false });
            }
            await game.MainScene.OpenPackageAsync(args.PackagePath!);
            await Wait(() => !game.MainScene.Controller.Busy && game.MainScene.Controller.Manifest is not null);
            var controller = game.MainScene.Controller;
            Require(GetForegroundWindow() != game.Window.Handle && game.Window.Position.X < -10000, "The isolated wizard rendered offscreen without taking the user's foreground window");
            Require(game.MainScene.Quality.AntiAliasingSamples == 16 && game.MainScene.Quality.Filtering == ProjectZ.Shared.Drawing.TextureFiltering.Anisotropic16x,
                "The app wizard used native 16x anti-aliasing and 16x anisotropic filtering");
            Require(await Ui(() => controller.IsWizard && game.Window.Title == controller.Manifest!.Name + " Setup"), "A compiled bound installer opened the application-specific wizard and window title");
            Require(await Ui(() => !game.MainScene.HasControl("nav.workspace") && !game.MainScene.HasControl("nav.installed") && !game.MainScene.HasControl("nav.updates") && !game.MainScene.HasControl("setup.open")), "The app wizard exposed no authoring, publisher, other-app management or package-switch controls");
            Require(await Ui(() => controller.WizardArtworkPath is not null && File.Exists(controller.WizardArtworkPath)), "The wizard loaded app artwork from the verified package payload");
            await game.CaptureAsync(Path.Combine(directory, "01-app-welcome.png"));
            if (controller.LicenseText is not null)
            {
                await Click("setup.next"); await Click("license.accept");
                Require(await Ui(() => controller.LicenseAccepted), "The application license retained its explicit native acceptance step");
            }
            var frames = new List<InstallerGame.NavigationFrame>();
            frames.AddRange(await game.CaptureNavigationAsync("setup.next", Path.Combine(directory, "frames-features")));
            await Click("feature.docs");
            frames.AddRange(await game.CaptureNavigationAsync("setup.next", Path.Combine(directory, "frames-location")));
            frames.AddRange(await game.CaptureNavigationAsync("setup.next", Path.Combine(directory, "frames-review")));
            Require(await Ui(() => controller.Stage == SetupStage.Review && controller.Options().Silent && !controller.Options().Launch), "Review retained the chosen app destination; backend UI and automatic elevated app launch are disabled");
            await game.CaptureAsync(Path.Combine(directory, "02-app-review.png"));
            var ownedFile = Path.Combine(installed, "notes.txt");
            await File.WriteAllTextAsync(ownedFile, "Existing application data before consent.");
            var receiptPath = Path.Combine(state, controller.Manifest!.Id, "receipt.json");
            var receipt = await File.ReadAllBytesAsync(receiptPath);
            foreach (var data in controller.Plan!.Files.Where(x => x.Path.StartsWith("data/", StringComparison.Ordinal)))
            {
                using var stream = File.OpenWrite(PackageSafety.UnderRoot(installed, data.Path)); stream.WriteByte(42);
            }
            var payloadBefore = controller.Plan.Files.Where(x => File.Exists(PackageSafety.UnderRoot(installed, x.Path)))
                .ToDictionary(x => x.Path, x => PackageSafety.HashFile(PackageSafety.UnderRoot(installed, x.Path)));
            var kept = await Start("save-work", true);
            checks.AddRange(await ElevatedInstallWorker.VerifyProtocolRejectionsAsync(directory, args.PackagePath!, controller.Plan!, controller.Options(), kept.Id));
            checks.AddRange(await SignedWizardBindingSmoke.RunAsync(directory));
            await Click("setup.next");
            await Wait(() => controller.RunningApplicationsDialog is { Waiting: false });
            RequireWorkerHasNoWindow();
            Require(await Ui(() => controller.RunningApplicationsDialog!.Applications.Any(x => x.ProcessId == kept.Id)), "The windowless same-image worker relayed the actual affected app to the original wizard");
            await game.CaptureAsync(Path.Combine(directory, "03-close-apps-original-wizard.png"));
            await Click("running.cancel");
            await Wait(() => !controller.Busy && controller.RunningApplicationsDialog is null);
            Require(!kept.HasExited && !File.Exists(Path.Combine(processes[0].Run, "close-requested.json")) &&
                (await File.ReadAllBytesAsync(receiptPath)).SequenceEqual(receipt) && PayloadUnchanged(),
                "Cancel left the existing app, receipt and files unchanged and sent no close request");
            await Click("setup.next");
            await Wait(() => controller.RunningApplicationsDialog is { Waiting: false });
            RequireWorkerHasNoWindow();
            await Click("running.close");
            await Wait(() => controller.RunningApplicationsDialog is { IsRetry: true, Waiting: false });
            Require(!kept.HasExited && File.Exists(Path.Combine(processes[0].Run, "close-requested.json")) &&
                (await File.ReadAllBytesAsync(receiptPath)).SequenceEqual(receipt) && PayloadUnchanged(),
                "A save guard kept its app open; the original wizard offered retry/cancel without mutating or force-closing it");
            await game.CaptureAsync(Path.Combine(directory, "04-save-work-retry.png"));
            await Click("running.cancel");
            await Wait(() => !controller.Busy);
            await File.WriteAllTextAsync(Path.Combine(processes[0].Run, "allow-close"), "The owned acceptance app may exit normally.");
            await kept.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var closable = await Start("close-normally", false);
            await Click("setup.next");
            await Wait(() => controller.RunningApplicationsDialog is { Waiting: false });
            RequireWorkerHasNoWindow();
            await Click("running.close");
            await Wait(() => controller.RunningApplicationsDialog is null && controller.Busy && controller.Progress is > 0 and < 1);
            Require(await Ui(() => game.MainScene.HasControl("setup.cancel") && game.MainScene.HasControl("setup.logs") && controller.Stage == SetupStage.Progress),
                "The original wizard displayed real intermediate installation progress after applications closed");
            await game.CaptureAsync(Path.Combine(directory, "05-install-progress.png"));
            await Wait(() => !controller.Busy && controller.Stage == SetupStage.Complete);
            await closable.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(ElevatedInstallWorker.LastObservation is { Completed: true, Isolated: true } && !controller.Failed,
                "The authenticated same-image worker returned a successful installation result and exited normally");
            Require(await File.ReadAllTextAsync(ownedFile) == "Wizard application payload.\n" && !File.Exists(Path.Combine(installed, "readme.txt")),
                "The worker installed the required bytes and respected the optional feature choice after graceful closure");
            Require(await Ui(() => controller.Progress == 1 && game.MainScene.HasControl("wizard.finish") && game.MainScene.HasControl("wizard.open") && !game.MainScene.HasControl("setup.installed") && !game.MainScene.HasControl("setup.another")),
                "Completion showed Finish plus Open this app, with no other-application or workspace actions");
            await game.OnUiAsync(() => game.ResizeForAcceptance(980, 760));
            await Task.Delay(120);
            Require(await Ui(() => game.MainScene.Control("wizard.finish").Rectangle.Bottom <= 760 - 103 && game.MainScene.Control("wizard.open").Rectangle.Bottom < game.MainScene.Control("wizard.finish").Rectangle.Top),
                "Finish and Open-app choices remained separated and usable at the minimum native window size");
            await game.CaptureAsync(Path.Combine(directory, "06-app-finish-compact.png"));
            var open = Environment.GetEnvironmentVariable("HEIROWINSTALLER_WIZARD_SMOKE_OPEN") == "1";
            if (!open) frames.AddRange(await game.CaptureNavigationAsync("wizard.open", Path.Combine(directory, "frames-finish-choice")));
            Require(await Ui(() => controller.OpenApplicationOnFinish == open), "The native Open-app choice retained the user's selection");
            Require(frames.All(x => x.RailPixels > 10_000 && x.HeaderTextPixels > 200), "Every captured consecutive wizard stage/choice frame retained its complete app rail and header");
            await File.WriteAllTextAsync(Path.Combine(directory, "wizard-frames.json"), JsonSerializer.Serialize(frames, InstallerJson.Options));
            await File.WriteAllTextAsync(Path.Combine(directory, "wizard-smoke.json"), JsonSerializer.Serialize(new { success = true, checks, isolated = true, realUacTested = false, openApplicationOnFinish = open,
                worker = ElevatedInstallWorker.LastObservation, nativeWindows = "One original wizard; zero top-level windows for each observed worker", notes = "No global applications, registry, MSI, services, authentication, network, or real elevation were used." }, InstallerJson.Options));
            successfulFinish = true;
            await Click("wizard.finish");

            bool PayloadUnchanged() => payloadBefore.All(x => PackageSafety.HashEquals(PackageSafety.HashFile(PackageSafety.UnderRoot(installed, x.Key)), x.Value));
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            await File.WriteAllTextAsync(Path.Combine(directory, "wizard-smoke.json"), JsonSerializer.Serialize(new { success = false, checks, error = error.ToString() }, InstallerJson.Options));
        }
        finally
        {
            foreach (var item in processes)
            {
                if (!item.Process.HasExited)
                {
                    await File.WriteAllTextAsync(Path.Combine(item.Run, "allow-close"), "The owned acceptance app may exit normally.");
                    await item.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                item.Process.Dispose();
            }
            if (!successfulFinish) game.Enqueue(game.Exit);
        }

        void Require(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); checks.Add(description); }
        void RequireWorkerHasNoWindow()
        {
            var worker = ElevatedInstallWorker.LastObservation ?? throw new InvalidOperationException("No authenticated worker was observed.");
            using var process = Process.GetProcessById(worker.ProcessId);
            Require(!process.HasExited && process.StartTime.ToUniversalTime().Ticks == worker.StartTicks, "The exact authenticated worker was alive while the original wizard requested consent");
            var windows = CountTopLevelWindows(worker.ProcessId);
            Require(windows == 0, "The live same-image worker created no top-level native window");
        }
        async Task Click(string id) { await game.OnUiAsync(() => game.Activate(id)); await Task.Delay(160); }
        async Task<T> Ui<T>(Func<T> read) { T result = default!; await game.OnUiAsync(() => result = read()); return result; }
        async Task Wait(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(40);
            while (!await Ui(condition)) { if (DateTime.UtcNow > deadline) throw new TimeoutException("The isolated wizard did not reach its next expected state."); await Task.Delay(50); }
        }
        async Task<Process> Start(string name, bool keepOpen)
        {
            var run = Path.Combine(directory, name); Directory.CreateDirectory(run);
            await File.WriteAllTextAsync(Path.Combine(run, ".wizard-application-fixture"), "heirowInstaller wizard application acceptance v1\n");
            var start = new ProcessStartInfo(Path.Combine(directory, "installed", "application", "WizardApplicationProbe.exe")) { UseShellExecute = false };
            start.ArgumentList.Add("--run-directory"); start.ArgumentList.Add(run);
            if (keepOpen) start.ArgumentList.Add("--keep-open");
            var process = Process.Start(start) ?? throw new InvalidOperationException("The owned wizard app did not start.");
            processes.Add((process, run));
            await Wait(() => File.Exists(Path.Combine(run, "ready.json")));
            return process;
        }
    }

    private delegate bool EnumWindow(IntPtr window, IntPtr data);
    internal static int CountTopLevelWindows(int processId)
    {
        var windows = 0;
        EnumWindows((window, data) => { GetWindowThreadProcessId(window, out var pid); if (pid == processId) windows++; return true; }, IntPtr.Zero);
        return windows;
    }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
