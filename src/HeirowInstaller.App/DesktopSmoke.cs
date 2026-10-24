using System.Text.Json;
using HeirowInstaller.Core;
using ProjectZ.Shared.Drawing.UI.Input;

namespace HeirowInstaller.App;

internal static class DesktopSmoke
{
    internal static async Task RunAsync(InstallerGame game, string directory)
    {
        var checks = new List<string>();
        Directory.CreateDirectory(directory);
        var fixture = Path.Combine(directory, "fixture");
        var install = Path.Combine(directory, "installed");
        try
        {
            Directory.CreateDirectory(fixture);
            await File.WriteAllTextAsync(Path.Combine(fixture, "hello.txt"), "heirowInstaller native desktop acceptance\n");
            await File.WriteAllTextAsync(Path.Combine(fixture, "readme.txt"), "An optional documentation feature.\n");
            await File.WriteAllTextAsync(Path.Combine(fixture, "LICENSE.txt"), "Desktop acceptance license\n\nPermission is granted to install this isolated acceptance fixture.\n");
            var manifest = new PackageManifest
            {
                Id = "heirowInstaller.desktop-fixture", Name = "Desktop acceptance app", Version = "1.0.0", Publisher = "heirowInstaller acceptance",
                Description = "An isolated package used to verify the native installer workflow.",
                LicenseFile = "LICENSE.txt",
                Features = [new() { Id = "core", Name = "Application files", Description = "Required application components.", Required = true }, new() { Id = "docs", Name = "Documentation", Description = "Optional reference files.", Default = true }],
                Payload = [new() { Source = "hello.txt", Path = "hello.txt", Feature = "core" }, new() { Source = "readme.txt", Path = "readme.txt", Feature = "docs" }, new() { Source = "LICENSE.txt", Path = "LICENSE.txt", Feature = "core" }]
            };
            var manifestPath = Path.Combine(fixture, "installer.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options));
            var package = Path.Combine(directory, "desktop-fixture.hinst");
            await Task.Delay(500);
            await game.CaptureAsync(Path.Combine(directory, "01-welcome.png"));
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
            Require(await Ui(() => game.MainScene.Quality.AntiAliasingSamples == 16 && game.MainScene.Quality.Filtering == ProjectZ.Shared.Drawing.TextureFiltering.Anisotropic16x), "Runtime used 16x anti-aliasing and anisotropic filtering");
            await File.WriteAllTextAsync(Path.Combine(directory, "ui-smoke.json"), JsonSerializer.Serialize(new { success = true, framework = "Project-Z", checks, isolated = true, notes = "Real native pointer/control events. Isolated state and filesystem only; no registry, shortcuts, services, publisher authentication or public update deployment." }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            await File.WriteAllTextAsync(Path.Combine(directory, "ui-smoke.json"), JsonSerializer.Serialize(new { success = false, checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { game.Enqueue(game.Exit); }

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
