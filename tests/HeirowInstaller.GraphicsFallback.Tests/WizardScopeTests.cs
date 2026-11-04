using System.Reflection;
using System.Text.Json;
using HeirowInstaller.App;
using HeirowInstaller.Core;

internal static partial class Program
{
    static void WizardScope()
    {
        var fixture = BuildScopeFixture("scope-features", required: false);
        using (var form = NewForm(Arguments(fixture) with { Scope = null }))
        {
            Pump(form, form.InitializeForTestsAsync());
            var controller = form.Controller;
            Check(!controller.Failed && controller.Scope == "user" && !controller.RequiresMachineScope,
                "optional machine integration does not restrict an unselected user installation");
            Pump(form, Command(form, controller.NextStageAsync));
            Pump(form, Command(form, controller.NextStageAsync));
            fixture = fixture with { Target = Path.Combine(Path.GetDirectoryName(fixture.Target)!, "custom-choice") };
            ((TextBox)form.Control("setup.target")).Text = fixture.Target;
            controller.PreviousStage(); Drain(form);
            ((CheckBox)form.Control("feature.system")).Checked = true;
            Pump(form, Command(form, controller.NextStageAsync));
            Check(controller.Stage == SetupStage.Destination && controller.Scope == "machine" && !controller.Busy && !controller.Failed,
                "selecting signed machine integration advances to Destination with a valid machine-scope default");
            Check(form.Control("setup.scope.user") is RadioButton { Enabled: false } && form.Control("setup.scope.machine") is RadioButton { Checked: true, Enabled: true },
                "CPU destination prevents the unsupported Just for me choice while retaining the valid machine choice");
            Pump(form, controller.SelectScopeAsync("user"));
            Check(controller.Scope == "machine" && !controller.Failed && controller.TargetDirectory == fixture.Target,
                "the shared destination action cannot switch selected machine components into an unsupported user scope");

            ((TextBox)form.Control("setup.target")).Text = Path.GetPathRoot(root)!;
            Click(form, "setup.next");
            Until(form, () => !controller.Busy);
            Check(controller.Stage == SetupStage.Destination && controller.Failed && form.HasControl("setup.destination.error")
                && form.Control("setup.destination.error").Text == controller.StatusDetail
                && form.Control("setup.destination.error").ForeColor == System.Drawing.Color.Firebrick,
                "a real Continue event keeps an invalid location on Destination and shows the complete validation error inline");
            ((TextBox)form.Control("setup.target")).Text = fixture.Target;
            Click(form, "setup.next");
            Until(form, () => !controller.Busy);
            Check(controller.Stage == SetupStage.Review && !controller.Failed && controller.Plan is { Scope: "machine" },
                "correcting the destination and using Continue advances to the checked review instead of refreshing the same page");
            Click(form, "setup.next");
            Until(form, () => !controller.Busy);
            Check(controller.Stage == SetupStage.Complete && !controller.Failed && File.Exists(fixture.Receipt)
                && File.ReadAllText(Path.Combine(fixture.Target, "app.txt")) == "signed machine-scope fixture",
                "the real CPU Install button completes the signed isolated machine-scope Core transaction");
            Check(controller.Options() is { Isolated: true, RegisterProduct: false, Launch: false }
                && controller.Options().StateRoot == fixture.State && controller.Options().TargetDirectory == fixture.Target,
                "machine-scope wizard acceptance remains confined to its explicit fixture target and state without launch or product registration");
            Hidden(form);
        }

        var required = BuildScopeFixture("scope-required", required: true);
        using (var form = NewForm(Arguments(required)))
        {
            Pump(form, form.InitializeForTestsAsync());
            Check(form.Controller.Failed && form.Controller.Scope == "user" && form.Controller.Plan is null
                && form.Controller.StatusDetail.Contains("require machine installation scope", StringComparison.Ordinal)
                && !Directory.Exists(required.Target) && !Directory.Exists(required.State),
                "an explicitly requested command-line user scope still fails Core validation for signed required machine integration");
            Hidden(form);
        }
        using (var form = NewForm(Arguments(required) with { Scope = null, Features = ["core"] }))
        {
            Pump(form, form.InitializeForTestsAsync());
            Check(!form.Controller.Failed && form.Controller.Scope == "machine" && form.Controller.Plan is { Scope: "machine" },
                "a signed user-default manifest with required machine components opens successfully using its valid interactive scope");
            Pump(form, Command(form, form.Controller.NextStageAsync));
            Pump(form, Command(form, form.Controller.NextStageAsync));
            Click(form, "setup.next");
            Until(form, () => !form.Controller.Busy);
            Check(form.Controller.Stage == SetupStage.Review && !form.Controller.Failed,
                "required machine components with no explicit scope reach Review through the ordinary CPU wizard flow");
            Hidden(form);
        }

        var available = BuildScopeFixture("scope-user", required: false);
        using (var form = NewForm(Arguments(available) with { Scope = null }))
        {
            Pump(form, form.InitializeForTestsAsync());
            Pump(form, Command(form, form.Controller.NextStageAsync));
            Pump(form, Command(form, form.Controller.NextStageAsync));
            Check(form.Controller.Stage == SetupStage.Destination && form.Control("setup.scope.user").Enabled,
                "ordinary packages retain an available Just for me installation choice");
            ((RadioButton)form.Control("setup.scope.machine")).Checked = true;
            Until(form, () => !form.Controller.Busy);
            ((RadioButton)form.Control("setup.scope.user")).Checked = true;
            Until(form, () => !form.Controller.Busy);
            Click(form, "setup.next");
            Until(form, () => !form.Controller.Busy);
            Check(form.Controller.Stage == SetupStage.Review && !form.Controller.Failed && form.Controller.Plan is { Scope: "user" },
                "native scope-control events preserve valid user choices and Continue reaches Review");
            Hidden(form);
        }

        var edited = BuildScopeFixture("scope-edited-target", required: false, authoredTarget: true);
        using (var form = NewForm(Arguments(edited) with { Scope = null, TargetDirectory = null, Isolated = false }))
        {
            Pump(form, form.InitializeForTestsAsync());
            Pump(form, Command(form, form.Controller.NextStageAsync));
            Pump(form, Command(form, form.Controller.NextStageAsync));
            Check(!form.Controller.Failed && form.Controller.TargetDirectory == edited.Target,
                "an authored fixture destination opens without any command-line target override");
            string custom = Path.Combine(Path.GetDirectoryName(edited.Target)!, "edited-in-ui");
            ((TextBox)form.Control("setup.target")).Text = custom;
            form.Controller.PreviousStage(); Drain(form);
            ((CheckBox)form.Control("feature.system")).Checked = true;
            Pump(form, Command(form, form.Controller.NextStageAsync));
            Check(form.Controller.Stage == SetupStage.Destination && form.Controller.Scope == "machine"
                && form.Controller.TargetDirectory == custom,
                "editing the actual CPU destination marks a custom choice and preserves it when selected features promote machine scope");
            if (form.Controller.Failed)
                Check(form.Controller.StatusDetail.StartsWith("Unsafe machine installer state at ", StringComparison.Ordinal),
                    "normal machine-scope planning still rejects an ordinary user-owned fixture namespace instead of weakening protected-state validation");
            else
                Check(form.Controller.Plan?.InstallDirectory == custom,
                    "a trusted machine-state namespace plans the exact custom UI destination");
            // This normal-argument UI fixture deliberately starts without a
            // target override. Its temporary namespace is not production
            // machine state. Validate the same chosen target using Core's
            // explicit isolated planning mode; do not create or change ACLs.
            var isolated = form.Controller.Options(); isolated.Isolated = true;
            using (var package = PackageReader.Open(edited.Package))
            {
                var plan = new InstallerEngine().Plan(package, isolated);
                Check(plan.Scope == "machine" && plan.InstallDirectory == custom,
                    "the exact UI-selected options produce the intended machine-scope custom destination in isolated Core planning");
            }
            Check(!Directory.Exists(edited.Target) && !Directory.Exists(custom) && !Directory.Exists(edited.State),
                "the normal-argument target-edit regression plans only owned paths and invokes no installation");
            Hidden(form);
        }
    }

    static Fixture BuildScopeFixture(string name, bool required, bool authoredTarget = false)
    {
        string area = Path.Combine(root, name), source = Path.Combine(area, "source"); Directory.CreateDirectory(source);
        string id = "heirowInstaller.cpu-fixture." + name;
        var manifest = new PackageManifest
        {
            FormatVersion = 3, Id = id, Name = "Signed isolated scope fixture", Version = "1.0.0", Publisher = "Isolated test fixture", Scope = "user", LegalDocuments = LegalFixture.For(id),
            InstallDirectory = authoredTarget ? Path.Combine(area, "installed") : "",
            Features = [new() { Id = "core", Name = "Application", Required = true }, new() { Id = "system", Name = "Machine integration", Required = required, Default = required }],
            Payload = [new() { Source = "app.txt", Path = "app.txt", Feature = "core" }],
            Registry = [new() { Hive = "machine", Key = "Software\\SocketJack\\heirowInstaller\\ScopeFixture", Name = "fixture", Value = "isolated only", Feature = "system" }]
        };
        File.WriteAllText(Path.Combine(source, "app.txt"), "signed machine-scope fixture");
        string description = Path.Combine(area, "manifest.json"), package = Path.Combine(area, "fixture.hinst"), state = Path.Combine(area, "state");
        File.WriteAllText(description, JsonSerializer.Serialize(manifest, InstallerJson.Options));
        PackageBuilder.Build(description, source, package, new() { SigningKeyPath = signingKey });
        return new(id, package, Path.Combine(area, "installed"), state, Path.Combine(state, id, "receipt.json"), null);
    }

    static void Click(CpuInstallerForm form, string id)
    {
        var button = form.Control(id) as Button ?? throw new InvalidOperationException("Expected native wizard button.");
        Check(button.Enabled, "the requested CPU wizard action is enabled");
        // Invoke the actual control event without Show, a native handle, or input.
        typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(button, [EventArgs.Empty]);
        form.DrainForTests();
    }
}
