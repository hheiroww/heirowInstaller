using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HeirowInstaller.App;
using HeirowInstaller.Core;
using Microsoft.Xna.Framework.Graphics;

internal static partial class Program
{
    static readonly List<string> checks = [];
    static string root = "";
    static string signingKey = "";
    static string signingPin = "";

    [STAThread]
    static int Main(string[] args)
    {
        string? oldTemp = Environment.GetEnvironmentVariable("TEMP"), oldTmp = Environment.GetEnvironmentVariable("TMP");
        bool oldAutoInstall = WindowsFormsSynchronizationContext.AutoInstall;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("These hidden form checks require Windows.");
            if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Supply one absolute owned output root.");
            string taskRoot = Path.GetFullPath(args[0]);
            PackageSafety.EnsureNoReparsePoints(taskRoot);
            Check(taskRoot.TrimEnd(Path.DirectorySeparatorChar).Length > 3, "test outputs use an explicit owned output directory");
            root = Path.Combine(taskRoot, "test-data", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(root, "temp"));
            Environment.SetEnvironmentVariable("TEMP", Path.Combine(root, "temp")); Environment.SetEnvironmentVariable("TMP", Path.Combine(root, "temp"));
            // No Application.Run exists in this harness. Native controls must not
            // capture an unpumped WindowsFormsSynchronizationContext while rebuilt.
            WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(null);
            Policy();
            RemoteDownloadProgress();
            using (var key = RSA.Create(2048))
            {
                signingKey = Path.Combine(root, "synthetic-fixture-key.pem"); File.WriteAllText(signingKey, key.ExportPkcs8PrivateKeyPem());
                signingPin = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
            }
            Wizard();
            WizardScope();
            GraphicsBackendChecks.Run(checks);
            RejectedIdentity();
            CancelAndClose();
            Check(!Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Equals("mgruntime.dll", StringComparison.OrdinalIgnoreCase)), "CPU form and controller operations never loaded the MonoGame native graphics runtime");
            File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { result = "PASS", assertions = checks.Count, checks, shownForms = 0, desktopInput = false, captures = 0, syntheticFixtureKeyOnly = true, outputsRetained = true }, InstallerJson.Options));
            Console.WriteLine($"PASS {checks.Count} graphics fallback and hidden CPU wizard checks; output {root}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (root.Length > 0) File.WriteAllText(Path.Combine(root, "failure.json"), JsonSerializer.Serialize(new { result = "FAIL", assertions = checks.Count, checks, error = error.ToString(), shownForms = 0, desktopInput = false }, InstallerJson.Options));
            return 1;
        }
        finally { WindowsFormsSynchronizationContext.AutoInstall = oldAutoInstall; Environment.SetEnvironmentVariable("TEMP", oldTemp); Environment.SetEnvironmentVariable("TMP", oldTmp); }
    }

    static void RemoteDownloadProgress()
    {
        var host = new QueuedProgressHost();
        using var controller = new AppController(host, AppArguments.Parse([]) with { RemotePackageUrl = "https://example.invalid/app.hinst" });
        var reporter = controller.Reporter();
        reporter.Report(new("download", "Downloading verified package", 1, 10));
        host.Flush();
        Check(controller.Manifest is null && controller.Status == "Downloading application files", "remote download progress renders before the manifest exists");
        reporter.Report(new("prepare", "Preparing verified package", 2, 10));
        host.Flush();
        Check(controller.Status == "Preparing application setup", "unknown pre-manifest progress does not dereference an absent manifest");
    }

    sealed class QueuedProgressHost : IInstallerDesktopHost
    {
        readonly Queue<Action> actions = new();
        public string Title { set { } }
        public void Exit() { }
        public void Enqueue(Action action) => actions.Enqueue(action);
        public Task OnUiAsync(Action action) { action(); return Task.CompletedTask; }
        public void Flush() { while (actions.TryDequeue(out var action)) action(); }
    }

    static void Policy()
    {
        string[] known = ["Failed to create graphics device!", "Failed to initialize SDL platform!", "Failed to initialize SDL window!", "Failed to initialize graphics system!", "No graphics adapters were found!"];
        foreach (string message in known)
        {
            var graphics = new NoSuitableGraphicsDeviceException(message);
            Check(GraphicsStartup.ShouldUseSoftwareFallback(graphics, false, true), "known native graphics initialization failure selects CPU: " + message);
            Check(!GraphicsStartup.ShouldUseSoftwareFallback(graphics, true, true), "graphics-looking failures after content loading never restart the wizard");
            Check(!GraphicsStartup.ShouldUseSoftwareFallback(graphics, false, false), "native CPU presentation is Windows only");
        }
        var cause = new NoSuitableGraphicsDeviceException(known[0]);
        Check(GraphicsStartup.ShouldUseSoftwareFallback(new TypeInitializationException("graphics", new TargetInvocationException(cause)), false, true), "standard CLR wrappers retain the specific graphics initialization cause");
        Exception excessive = cause; for (int i = 0; i < 8; i++) excessive = new TargetInvocationException(excessive);
        Check(!GraphicsStartup.ShouldUseSoftwareFallback(excessive, false, true), "wrapper traversal is bounded");
        foreach (var error in new Exception[] { new NoSuitableGraphicsDeviceException("Unknown graphics defect"), new InvalidOperationException(known[0]), new IOException(known[0]), new CryptographicException(known[0]), new UnauthorizedAccessException(known[0]), new AggregateException(cause), new Exception("outer", cause) })
            Check(!GraphicsStartup.ShouldUseSoftwareFallback(error, false, true), "unrelated, unknown, or application-wrapped failures keep the normal error path");
        int hardware = 0, software = 0;
        GraphicsStartup.Run(() => hardware++, () => software++, () => false, true);
        Check(hardware == 1 && software == 0, "successful hardware startup runs once without CPU retry");
        GraphicsStartup.Run(() => { hardware++; throw cause; }, () => software++, () => false, true);
        Check(hardware == 2 && software == 1, "a genuine pre-content graphics failure invokes one CPU host");
        GraphicsStartup.Run(() => hardware++, () => software++, () => false, true, true);
        Check(hardware == 2 && software == 2, "explicit CPU mode bypasses hardware creation entirely");
        Throws<PlatformNotSupportedException>(() => GraphicsStartup.Run(() => hardware++, () => software++, () => false, false, true));
        var contentError = new InvalidDataException("fixture content defect");
        try { GraphicsStartup.Run(() => throw contentError, () => software++, () => false, true); throw new Exception("content error was swallowed"); }
        catch (InvalidDataException error) { Check(ReferenceEquals(error, contentError) && software == 2, "content exceptions preserve their original instance and never trigger CPU fallback"); }
        var softwareError = new InvalidOperationException("CPU fixture failure");
        try { GraphicsStartup.Run(() => throw cause, () => throw softwareError, () => false, true); throw new Exception("CPU error was swallowed"); }
        catch (InvalidOperationException error) { Check(ReferenceEquals(error, softwareError), "a failed CPU host is propagated without a retry loop"); }
        Throws<NoSuitableGraphicsDeviceException>(() => GraphicsStartup.Run(() => throw cause, () => software++, () => true, true));
    }

    static void Wizard()
    {
        var fixture = Build("wizard", includeLicense: true);
        using var form = NewForm(Arguments(fixture));
        Pump(form, form.InitializeForTestsAsync());
        var controller = form.Controller;
        Check(controller.Manifest?.Id == fixture.Id && controller.Plan is not null && !controller.Failed, "hidden CPU host opens and plans the actual signed Core fixture");
        Check(form.HasControl("setup.next") && !form.HasControl("nav.workspace") && !form.HasControl("nav.installed"), "bound CPU wizard retains its app flow without authoring or other-app navigation");
        Check(controller.LicenseText == fixture.License && !controller.LicenseAccepted, "verified package license remains explicit in CPU mode");
        using (var document = form.CreateLicenseDialogForTests())
        {
            var text = (TextBox)document.Controls.Find("document.content", true).Single();
            Check(!document.Visible && !document.IsHandleCreated && text.ReadOnly && text.Text == fixture.License, "CPU license document is read-only and can be prepared without showing a window");
        }
        foreach (bool privacy in new[] { true, false })
        {
            using var document = form.CreateLegalDialogForTests(privacy);
            var text = (TextBox)document.Controls.Find("document.content", true).Single();
            var expected = privacy ? controller.Manifest!.LegalDocuments!.PrivacyPolicy! : controller.Manifest!.LegalDocuments!.TermsAndConditions!;
            Check(!document.Visible && !document.IsHandleCreated && text.ReadOnly && text.Text == expected.Content, "CPU legal document retains the exact signed text in a never-shown read-only form");
        }
        Pump(form, Command(form, controller.InstallAsync));
        Check(controller.Stage == SetupStage.Package && !File.Exists(fixture.Receipt) && !File.Exists(Path.Combine(fixture.Target, "app.txt")), "CPU install cannot bypass unaccepted package license");
        var license = form.Control("setup.license.accept") as CheckBox ?? throw new Exception("CPU license control is not a native checkbox");
        license.Checked = true;
        Check(controller.LicenseAccepted, "native CPU license control changes the shared consent state");
        Pump(form, controller.NextStageAsync()); Drain(form);
        Check(controller.Stage == SetupStage.Features, "CPU wizard advances from package to feature choices");
        var feature = form.Control("feature.docs") as CheckBox ?? throw new Exception("CPU feature control is not a native checkbox");
        Check(feature.Checked && controller.Features.Contains("docs"), "signed optional documentation is initially selected before testing its deselection");
        feature.Checked = false;
        Check(!controller.Features.Contains("docs") && controller.Features.Contains("core"), "optional feature checkbox preserves required features");
        Pump(form, controller.NextStageAsync()); Drain(form); Check(controller.Stage == SetupStage.Destination, "CPU wizard advances to the destination");
        Pump(form, controller.NextStageAsync());
        Check(controller.Stage == SetupStage.Review, "CPU wizard enters reviewed installation choices");
        var options = controller.Options();
        Check(options.Isolated && !options.RegisterProduct && !options.Launch && options.Silent && options.TargetDirectory == fixture.Target, "CPU review retains isolated destination, no registration and no app launch");
        Pump(form, Command(form, controller.InstallAsync));
        Check(controller.Stage == SetupStage.Complete && !controller.Failed && File.Exists(fixture.Receipt), "actual Core installation completes through the never-shown CPU host");
        Check(File.ReadAllText(Path.Combine(fixture.Target, "app.txt")) == "signed synthetic application" && !File.Exists(Path.Combine(fixture.Target, "docs.txt")), "Core installs the selected signed payload and omits unchecked optional content");
        Check(InstallerEngine.ListInstalled(fixture.State).Single().Id == fixture.Id, "CPU installation writes only its isolated product receipt");
        Hidden(form);
        form.Dispose();
        Maintenance(fixture);
    }

    static void Maintenance(Fixture fixture)
    {
        File.WriteAllText(Path.Combine(fixture.Target, "app.txt"), "Owned synthetic mutation before repair");
        AppArguments Options(string operation) => AppArguments.Parse(["--software-rendering", "--isolated", "--no-register", "--state-root", fixture.State,
            "--scope", "user", "--trust-key", signingPin, "--require-signature", "--expected-product-id", fixture.Id, "--expected-version", "1.0.0"], maintenanceInvocation: true)
            with { MaintenanceProductId = fixture.Id, MaintenanceOperation = operation };
        using (var repair = NewForm(Options("repair")))
        {
            Pump(repair, repair.InitializeForTestsAsync());
            Check(!repair.Controller.Failed && repair.Controller.IsMaintenanceWizard && repair.HasControl("maintenance.start")
                && repair.Control("maintenance.location") is TextBox { ReadOnly: true } location && location.Text == fixture.Target,
                "CPU repair context verifies the isolated signed product and presents its fixed read-only location");
            Pump(repair, Command(repair, repair.Controller.RepairAsync));
            Check(!repair.Controller.Failed && File.ReadAllText(Path.Combine(fixture.Target, "app.txt")) == "signed synthetic application" && File.Exists(fixture.Receipt),
                "actual Core repair restores the signed payload through the never-shown CPU maintenance host");
            Hidden(repair);
        }
        using var uninstall = NewForm(Options("uninstall"));
        Pump(uninstall, uninstall.InitializeForTestsAsync());
        Check(!uninstall.Controller.Failed && uninstall.HasControl("maintenance.start") && uninstall.Control("maintenance.location") is TextBox { ReadOnly: true },
            "CPU uninstall reviews only the verified fixture and keeps its install location read-only");
        Pump(uninstall, Command(uninstall, uninstall.Controller.UninstallAsync));
        Check(!uninstall.Controller.Failed && !File.Exists(fixture.Receipt) && !File.Exists(Path.Combine(fixture.Target, "app.txt"))
            && InstallerEngine.ListInstalled(fixture.State).Count == 0, "actual Core uninstall removes only the isolated fixture receipt and payload");
        Hidden(uninstall);
    }

    static void RejectedIdentity()
    {
        var fixture = Build("identity", includeLicense: false);
        using var form = NewForm(Arguments(fixture) with { ExpectedVersion = "2.0.0" });
        Pump(form, form.InitializeForTestsAsync());
        Check(form.Controller.Failed && form.Controller.Manifest is null && !File.Exists(fixture.Receipt) && !Directory.Exists(fixture.Target), "CPU host rejects a mismatched expected signed identity before planning or writes");
        Hidden(form);
    }

    static void CancelAndClose()
    {
        var fixture = Build("cancel", includeLicense: false, waitingModule: true);
        using var form = NewForm(Arguments(fixture));
        var module = new WaitModule();
        var engine = (InstallerEngine)typeof(AppController).GetField("engine", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form.Controller)!;
        var modules = (Dictionary<string, IInstallerModule>)typeof(InstallerEngine).GetField("modules", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
        modules.Add(module.Id, module);
        Pump(form, form.InitializeForTestsAsync());
        Task? coreOperation = null;
        bool disposedAfterCore = false;
        form.Disposed += (_, _) => disposedAfterCore = coreOperation?.IsCompleted == true;
        var operation = Command(form, () => coreOperation = form.Controller.InstallAsync());
        Until(form, () => module.Started.Task.IsCompleted);
        Check(form.Controller.Busy && !operation.IsCompleted, "real Core transaction is pending inside the owned cancellation fixture");
        form.RequestCloseForTests();
        Check(form.ClosePending && !form.ControllerDisposed, "closing a busy CPU form requests cancellation and retains its controller while Core unwinds");
        Pump(form, operation);
        Until(form, () => form.ControllerDisposed);
        Check(module.Canceled && !File.Exists(fixture.Receipt) && !File.Exists(Path.Combine(fixture.Target, "app.txt")), "CPU close cancels Core and rolls back the owned installation before disposing its controller");
        Check(disposedAfterCore, "tracked CPU command completes its underlying Core task before disposing the form and controller");
        Hidden(form);
    }

    sealed record Fixture(string Id, string Package, string Target, string State, string Receipt, string? License);
    static Fixture Build(string name, bool includeLicense, bool waitingModule = false)
    {
        string area = Path.Combine(root, name), source = Path.Combine(area, "source"); Directory.CreateDirectory(source);
        string id = "heirowInstaller.cpu-fixture." + name, license = "Synthetic isolated CPU fallback fixture license. Explicit acceptance is required.";
        var manifest = new PackageManifest { FormatVersion = 3, Id = id, Name = "Synthetic CPU wizard fixture", Version = "1.0.0", Publisher = "Isolated test fixture", Scope = "user", LegalDocuments = LegalFixture.For(id),
            Features = [new() { Id = "core", Name = "Application", Required = true }, new() { Id = "docs", Name = "Optional documentation", Default = true }],
            Payload = [new() { Source = "app.txt", Path = "app.txt", Feature = "core" }, new() { Source = "docs.txt", Path = "docs.txt", Feature = "docs" }] };
        File.WriteAllText(Path.Combine(source, "app.txt"), "signed synthetic application"); File.WriteAllText(Path.Combine(source, "docs.txt"), "optional synthetic documentation");
        if (includeLicense) { File.WriteAllText(Path.Combine(source, "LICENSE.txt"), license); manifest.LicenseFile = "LICENSE.txt"; manifest.Payload.Add(new() { Source = "LICENSE.txt", Path = "LICENSE.txt" }); }
        if (waitingModule) manifest.Modules.Add("fixture.wait", JsonSerializer.SerializeToElement(new { }));
        string description = Path.Combine(area, "manifest.json"), package = Path.Combine(area, "fixture.hinst"), state = Path.Combine(area, "state");
        File.WriteAllText(description, JsonSerializer.Serialize(manifest, InstallerJson.Options)); PackageBuilder.Build(description, source, package, new() { SigningKeyPath = signingKey });
        return new(id, package, Path.Combine(area, "installed"), state, Path.Combine(state, id, "receipt.json"), includeLicense ? license : null);
    }

    static AppArguments Arguments(Fixture fixture) => AppArguments.Parse([fixture.Package, "--software-rendering", "--isolated", "--no-register", "--state-root", fixture.State,
        "--target", fixture.Target, "--scope", "user", "--trust-key", signingPin, "--require-signature", "--expected-product-id", fixture.Id, "--expected-version", "1.0.0", "--expected-sha256", PackageSafety.HashFile(fixture.Package)]);
    static CpuInstallerForm NewForm(AppArguments args) { var form = new CpuInstallerForm(args, startOnShown: false); SynchronizationContext.SetSynchronizationContext(null); Hidden(form); return form; }
    static void Hidden(CpuInstallerForm form) => Check(!form.Visible && !form.IsHandleCreated, "CPU fixture remains never shown and creates no native window handle");
    static void Drain(CpuInstallerForm form) { form.DrainForTests(); Check(!form.Visible, "UI completion draining never shows the CPU form"); }
    static void Pump(CpuInstallerForm form, Task operation) { Until(form, () => operation.IsCompleted); operation.GetAwaiter().GetResult(); form.DrainForTests(); }
    static Task Command(CpuInstallerForm form, Func<Task> operation) => (Task)typeof(CpuInstallerForm)
        .GetMethod("RunCommandAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [operation])!;
    static void Until(CpuInstallerForm form, Func<bool> done) { var started = Stopwatch.StartNew(); while (!done()) { form.DrainForTests(); if (started.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Hidden CPU fixture did not complete."); Thread.Sleep(1); } form.DrainForTests(); }
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
    static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { checks.Add("expected " + typeof(T).Name + " remains on its original error path"); return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    sealed class WaitModule : IInstallerModule
    {
        public string Id => "fixture.wait";
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Canceled { get; private set; }
        public IReadOnlyList<string> Plan(JsonElement configuration, InstallerModuleContext context) => ["Wait for owned cancellation acceptance"];
        public async Task<ModuleSnapshot> InstallAsync(JsonElement configuration, InstallerModuleContext context, CancellationToken cancellationToken) { Started.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return new(); } catch (OperationCanceledException) { Canceled = true; throw; } }
        public Task UninstallAsync(ModuleSnapshot originalState, InstallerModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
