using System.Diagnostics;
using System.Text.Json;
using HeirowInstaller.Core;

namespace HeirowInstaller.App;

internal static class MaintenanceWizardSmoke
{
    internal static async Task RunAsync(InstallerGame game, AppArguments args)
    {
        var root = args.WizardSmokeDirectory!;
        var checks = new List<string>();
        Process? probe = null;
        var run = Path.Combine(root, "running-app");
        var finished = false;
        try
        {
            if (!args.Isolated || !args.NoRegister || args.MaintenanceProductId != "heirowInstaller.wizard-fixture" ||
                File.ReadAllText(Path.Combine(root, ".isolated-wizard-fixture")) != "heirowInstaller isolated wizard acceptance v1\n")
                throw new InvalidDataException("Maintenance acceptance requires its dedicated isolated fixture.");
            await game.MainScene.Controller.InitializeMaintenanceWizardAsync();
            var controller = game.MainScene.Controller;
            await Wait(() => !controller.Busy && game.MainScene.HasControl("maintenance.start"));
            var product = controller.SelectedProduct ?? throw new InvalidOperationException("The isolated app is missing.");
            if (!product.Isolated || product.RegisteredProduct || !Path.GetFullPath(product.PackagePath).StartsWith(Path.Combine(root, "state") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFullPath(product.InstallDirectory).Equals(Path.Combine(root, "installed"), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFullPath(product.ReceiptPath).Equals(Path.Combine(root, "state", "heirowInstaller.wizard-fixture", "receipt.json"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Maintenance acceptance may only mutate its own isolated cached package.");
            Require(controller.IsMaintenanceWizard && controller.Installed.Count == 1 && controller.Stage == SetupStage.Review && controller.Manifest!.Id == product.Id,
                "Explicit maintenance loaded only the exact installed app and verified cached package into Review");
            Require(!game.MainScene.HasControl("nav.workspace") && !game.MainScene.HasControl("nav.installed") && game.MainScene.HasControl("maintenance.start"),
                "The maintenance wizard exposed no other apps, authoring workspace or unrelated actions");
            Require(((ProjectZ.Shared.Drawing.UI.Input.Textbox)game.MainScene.Control("maintenance.location")).IsReadOnly && game.MainScene.Control("maintenance.location").Rectangle.Right < game.GraphicsDevice.Viewport.Width,
                "The complete reviewed location remained available in a bounded read-only field");
            await game.CaptureAsync(Path.Combine(root, "01-maintenance-review.png"));
            var notes = Path.Combine(product.InstallDirectory, "notes.txt");
            if (args.MaintenanceOperation == "repair") await File.WriteAllTextAsync(notes, "This owned fixture needs repair.\n");
            var beforeNotes = PackageSafety.HashFile(notes);
            var beforeReceipt = PackageSafety.HashFile(product.ReceiptPath);
            Directory.CreateDirectory(run);
            await File.WriteAllTextAsync(Path.Combine(run, ".wizard-application-fixture"), "heirowInstaller wizard application acceptance v1\n");
            var start = new ProcessStartInfo(Path.Combine(product.InstallDirectory, "application", "WizardApplicationProbe.exe")) { UseShellExecute = false };
            start.ArgumentList.Add("--run-directory"); start.ArgumentList.Add(run);
            probe = Process.Start(start) ?? throw new InvalidOperationException("The owned app did not start.");
            await Wait(() => File.Exists(Path.Combine(run, "ready.json")));
            await Click("maintenance.start");
            await Wait(() => controller.RunningApplicationsDialog is { Waiting: false });
            var firstWorker = ElevatedInstallWorker.LastObservation!;
            Require(WizardSmoke.CountTopLevelWindows(firstWorker.ProcessId) == 0 && controller.RunningApplicationsDialog!.Operation == args.MaintenanceOperation,
                "The live maintenance worker had no native window and relayed consent to the original app wizard");
            await Click("running.cancel");
            await Wait(() => !controller.Busy && controller.Stage == SetupStage.Review);
            Require(!probe.HasExited && !File.Exists(Path.Combine(run, "close-requested.json")) &&
                PackageSafety.HashEquals(PackageSafety.HashFile(notes), beforeNotes) && PackageSafety.HashEquals(PackageSafety.HashFile(product.ReceiptPath), beforeReceipt),
                "Cancel maintenance left the app alive and all observed app/receipt bytes unchanged");

            // The only damaged cache is this fresh, marked fixture; restore only our exact one-byte test write.
            var cacheHash = PackageSafety.HashFile(product.PackagePath);
            byte firstByte;
            using (var file = File.Open(product.PackagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            { firstByte = (byte)file.ReadByte(); file.Position = 0; file.WriteByte((byte)(firstByte ^ 1)); }
            var changedHash = PackageSafety.HashFile(product.PackagePath);
            try
            {
                await Click("maintenance.start"); await Wait(() => !controller.Busy && controller.Failed);
                Require(ReferenceEquals(firstWorker, ElevatedInstallWorker.LastObservation) && !probe.HasExited && !File.Exists(Path.Combine(run, "close-requested.json")) &&
                    PackageSafety.HashEquals(PackageSafety.HashFile(product.ReceiptPath), beforeReceipt),
                    "A cached package changed after Review was rejected before launching a worker or requesting app closure");
            }
            finally
            {
                if (!PackageSafety.HashEquals(PackageSafety.HashFile(product.PackagePath), changedHash)) throw new InvalidDataException("The owned cache changed externally; it was preserved without overwriting it.");
                using var file = File.Open(product.PackagePath, FileMode.Open, FileAccess.Write, FileShare.Read); file.WriteByte(firstByte);
            }
            if (!PackageSafety.HashEquals(PackageSafety.HashFile(product.PackagePath), cacheHash)) throw new InvalidDataException("The owned fixture cache did not restore exactly.");
            await Click("maintenance.start");
            await Wait(() => controller.RunningApplicationsDialog is { Waiting: false });
            Require(WizardSmoke.CountTopLevelWindows(ElevatedInstallWorker.LastObservation!.ProcessId) == 0, "Retry used the same windowless worker route");
            await Click("running.close");
            await Wait(() => !controller.Busy && controller.Stage == SetupStage.Complete);
            await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(ElevatedInstallWorker.LastObservation is { Completed: true } && !controller.Failed, "Actual maintenance completed through the authenticated worker and original wizard");
            if (args.MaintenanceOperation == "repair")
            {
                Require(await File.ReadAllTextAsync(notes) == "Wizard application payload.\n" && game.MainScene.HasControl("wizard.open"),
                    "Repair restored the owned payload and offered the repaired app's optional Open choice");
                await Click("wizard.open");
            }
            else Require(!File.Exists(notes) && !File.Exists(product.ReceiptPath) && !game.MainScene.HasControl("wizard.open"),
                "Uninstall removed its recorded payload/receipt and hid the Open-app choice");
            await game.CaptureAsync(Path.Combine(root, "02-maintenance-finish.png"));
            await File.WriteAllTextAsync(Path.Combine(root, "wizard-smoke.json"), JsonSerializer.Serialize(new { success = true, checks, isolated = true, realUacTested = false,
                maintenance = args.MaintenanceOperation, worker = ElevatedInstallWorker.LastObservation }, InstallerJson.Options));
            finished = true;
            await Click("wizard.finish");
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            var controller = game.MainScene.Controller;
            await File.WriteAllTextAsync(Path.Combine(root, "wizard-smoke.json"), JsonSerializer.Serialize(new { success = false, checks, error = error.ToString(),
                controller.Busy, controller.Failed, controller.Status, controller.StatusDetail, controller.Stage }, InstallerJson.Options));
        }
        finally
        {
            if (probe is not null)
            {
                if (!probe.HasExited) { await File.WriteAllTextAsync(Path.Combine(run, "allow-close"), "The owned fixture may exit normally."); await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                probe.Dispose();
            }
            if (!finished) game.Enqueue(game.Exit);
        }
        void Require(bool condition, string text) { if (!condition) throw new InvalidOperationException(text); checks.Add(text); }
        async Task Click(string id) { await game.OnUiAsync(() => game.Activate(id)); await Task.Delay(160); }
        async Task Wait(Func<bool> ready)
        {
            var deadline = DateTime.UtcNow.AddSeconds(40);
            while (true)
            {
                var done = false; await game.OnUiAsync(() => done = ready()); if (done) return;
                if (DateTime.UtcNow > deadline) throw new TimeoutException("The isolated maintenance wizard did not reach its expected state.");
                await Task.Delay(50);
            }
        }
    }
}
