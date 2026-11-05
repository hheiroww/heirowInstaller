using System.Security;
using System.Text.Json;
using HeirowInstaller.Core;
using RegistryView = HeirowInstaller.Core.PrerequisiteRegistryView;
using RegistryValueKind = HeirowInstaller.Core.PrerequisiteRegistryValueKind;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { Check(true, message); return; } throw new Exception(message); }
PrerequisiteDetectionDefinition Definition(string kind = "dotnet-desktop", string minimum = "10.0.0", string architecture = "x64") => new() { Kind = kind, Architecture = architecture, MinimumVersion = minimum };
var dotnet = Definition(); var vc = Definition("vc-runtime", "14.0.0.0");
Dictionary<string, PrerequisiteRegistryValue> Values(params (string Name, RegistryValueKind Kind, object? Value)[] values) => values.ToDictionary(value => value.Name, value => new PrerequisiteRegistryValue(value.Kind, value.Value), StringComparer.OrdinalIgnoreCase);
Dictionary<string, PrerequisiteRegistryValue> Dotnet(string version, object? installed = null, RegistryValueKind kind = RegistryValueKind.DWord) => Values((version, kind, installed ?? 1));
Dictionary<string, PrerequisiteRegistryValue> Vc(string version = "v14.52.37025.00", object? installed = null, RegistryValueKind kind = RegistryValueKind.DWord) => Values(("Installed", kind, installed ?? 1), ("Version", RegistryValueKind.String, version));

var registry = new FakeRegistry();
Check(!PrerequisiteDetection.Detect(dotnet, registry).Satisfied && !PrerequisiteDetection.Detect(vc, registry).Satisfied, "Missing fixed runtime keys are not treated as installed");
registry.Set(PrerequisiteRegistryLocation.DotNetDesktopX64, RegistryView.Registry32, Dotnet("10.0.0"));
Check(PrerequisiteDetection.Detect(dotnet, registry).Satisfied, ".NET Desktop10 minimum is detected in the documented 32-bit registry view");
registry.Set(PrerequisiteRegistryLocation.DotNetDesktopX64, RegistryView.Registry64, Dotnet("10.0.13"));
Check(PrerequisiteDetection.Detect(dotnet, registry).InstalledVersion == "10.0.13.0", "Newer .NET10 patches satisfy the minimum and highest version is reported");
Check(PrerequisiteDetection.Detect(Definition(minimum: "10.0.0.0"), registry).Satisfied, "Three- and four-component minimum versions compare consistently");
Check(!PrerequisiteDetection.Detect(Definition(minimum: "10.0.14"), registry).Satisfied, "Installed .NET10 below a specified minimum is rejected");
var numericVersions = new FakeRegistry(); numericVersions.Set(PrerequisiteRegistryLocation.DotNetDesktopX64, RegistryView.Registry32, Values(("10.0.9", RegistryValueKind.DWord, 1), ("10.0.13", RegistryValueKind.DWord, 1)));
Check(PrerequisiteDetection.Detect(dotnet, numericVersions).InstalledVersion == "10.0.13.0", "Runtime versions compare numerically rather than lexicographically");
numericVersions.Set(PrerequisiteRegistryLocation.DotNetDesktopX64, RegistryView.Registry32, Dotnet("10.1.0"));
Check(PrerequisiteDetection.Detect(dotnet, numericVersions).Satisfied, "A newer minor runtime in the required .NET major satisfies the minimum");
foreach (var version in new[] { "9.0.99", "11.0.0", "10.0.0-preview.4.25258.110", "10.0", " 10.0.0", "10.0.0 ", "10.0.0+metadata", "999999999999.0.0" })
{
    var source = new FakeRegistry(); source.Set(PrerequisiteRegistryLocation.DotNetDesktopX64, RegistryView.Registry32, Dotnet(version));
    Check(!PrerequisiteDetection.Detect(dotnet, source).Satisfied, "Older/different major, preview or malformed .NET registration cannot satisfy Desktop10");
}
foreach (var value in new (RegistryValueKind Kind, object Value)[] { (RegistryValueKind.DWord, 0), (RegistryValueKind.DWord, -1), (RegistryValueKind.String, "1"), (RegistryValueKind.QWord, 1L), (RegistryValueKind.Binary, new byte[] { 1 }), (RegistryValueKind.DWord, true) })
{
    var source = new FakeRegistry(); source.Set(PrerequisiteRegistryLocation.DotNetDesktopX64, RegistryView.Registry32, Dotnet("10.0.0", value.Value, value.Kind));
    Check(!PrerequisiteDetection.Detect(dotnet, source).Satisfied, ".NET detection requires an exact DWORD installed marker");
}
var fallback = new FakeRegistry { ErrorView = RegistryView.Registry32, Error = new UnauthorizedAccessException() };
fallback.Set(PrerequisiteRegistryLocation.DotNetDesktopX64, RegistryView.Registry64, Dotnet("10.0.12"));
Check(PrerequisiteDetection.Detect(dotnet, fallback).Satisfied, "A denied registry view does not hide a valid supported view");
foreach (var error in new Exception[] { new IOException(), new SecurityException(), new PlatformNotSupportedException() })
{
    var source = new FakeRegistry { ErrorView = RegistryView.Registry32, Error = error };
    Check(!PrerequisiteDetection.Detect(dotnet, source).Satisfied, "Unavailable registry evidence remains unsatisfied without crashing detection");
}
registry.Set(PrerequisiteRegistryLocation.VisualCppX64, RegistryView.Registry64, Vc());
Check(PrerequisiteDetection.Detect(vc, registry) is { Satisfied: true, InstalledVersion: "14.52.37025.0" }, "VC14 Installed1 and a newer numeric version are recognized");
Check(!PrerequisiteDetection.Detect(Definition("vc-runtime", "14.53.0.0"), registry).Satisfied, "VC14 below the minimum version is rejected");
foreach (var value in new (RegistryValueKind Kind, object Value)[] { (RegistryValueKind.DWord, 0), (RegistryValueKind.String, "1"), (RegistryValueKind.QWord, 1L) })
{
    var source = new FakeRegistry(); source.Set(PrerequisiteRegistryLocation.VisualCppX64, RegistryView.Registry32, Vc(installed: value.Value, kind: value.Kind));
    Check(!PrerequisiteDetection.Detect(vc, source).Satisfied, "VC detection requires the exact DWORD Installed1 value");
}
foreach (var version in new[] { "13.99.0.0", "15.0.0.0", "14.52.0-preview", "14.52", "not a version" })
{
    var source = new FakeRegistry(); source.Set(PrerequisiteRegistryLocation.VisualCppX64, RegistryView.Registry32, Vc(version));
    Check(!PrerequisiteDetection.Detect(vc, source).Satisfied, "A malformed or incompatible VC runtime version is rejected");
}
var components = new FakeRegistry();
components.Set(PrerequisiteRegistryLocation.VisualCppX64, RegistryView.Registry32, Values(("Installed", RegistryValueKind.DWord, 1), ("Major", RegistryValueKind.DWord, 14), ("Minor", RegistryValueKind.DWord, 40), ("Bld", RegistryValueKind.DWord, 33810), ("Rbld", RegistryValueKind.DWord, 0)));
Check(PrerequisiteDetection.Detect(vc, components).InstalledVersion == "14.40.33810.0", "Documented VC DWORD version components work when the version string is absent");
var malformedVersion = Values(("Installed", RegistryValueKind.DWord, 1), ("Version", RegistryValueKind.String, "malformed"), ("Major", RegistryValueKind.DWord, 14), ("Minor", RegistryValueKind.DWord, 40), ("Bld", RegistryValueKind.DWord, 33810), ("Rbld", RegistryValueKind.DWord, 0));
components.Set(PrerequisiteRegistryLocation.VisualCppX64, RegistryView.Registry32, malformedVersion);
Check(PrerequisiteDetection.Detect(vc, components).Satisfied, "A malformed VC string can use independently valid documented DWORD version components");
components.Set(PrerequisiteRegistryLocation.VisualCppX64, RegistryView.Registry32, Values(("Installed", RegistryValueKind.DWord, 1), ("Major", RegistryValueKind.DWord, 14), ("Minor", RegistryValueKind.DWord, 40), ("Bld", RegistryValueKind.String, "33810"), ("Rbld", RegistryValueKind.DWord, 0)));
Check(!PrerequisiteDetection.Detect(vc, components).Satisfied, "Malformed VC DWORD version components cannot create a false positive");
foreach (var invalid in new[] { Definition("shell"), Definition(architecture: "x86"), Definition(minimum: "10.0.0-preview.1"), Definition(minimum: "10.0"), Definition(minimum: "10.0.0 & command"), Definition("vc-runtime", "15.0.0.0") })
    Reject(() => PrerequisiteDetection.Validate(invalid), "Unknown detector, architecture or invalid numeric minimum fails closed");
Check(registry.Calls.All(value => (value.Location is PrerequisiteRegistryLocation.DotNetDesktopX64 or PrerequisiteRegistryLocation.VisualCppX64) && (value.View is RegistryView.Registry32 or RegistryView.Registry64)), "Injected detection can query only fixed runtime locations and explicit supported views");

var webview = Definition("webview2", "1.0.0.0"); var web = new FakeRegistry();
Check(!PrerequisiteDetection.Detect(webview, web).Satisfied, "Missing fixed machine and original-user WebView2 registrations require setup provisioning");
web.Set(PrerequisiteRegistryLocation.WebView2Machine, RegistryView.Registry32, Values(("pv", RegistryValueKind.String, "134.0.3124.44")));
Check(PrerequisiteDetection.Detect(webview, web) is { Satisfied: true, InstalledVersion: "134.0.3124.44" }, "Documented machine WebView2 pv string satisfies the prerequisite");
web.Set(PrerequisiteRegistryLocation.WebView2CurrentUser, RegistryView.Registry32, Values(("pv", RegistryValueKind.String, "150.0.1.2")));
Check(PrerequisiteDetection.Detect(Definition("webview2", "140.0.0.0"), web).InstalledVersion == "150.0.1.2", "Original-user WebView2 can satisfy setup and newer major versions remain compatible");
Check(!PrerequisiteDetection.Detect(Definition("webview2", "151.0.0.0"), web).Satisfied, "Installed WebView2 below explicit stable minimum remains missing");
foreach (var version in new[] { "0.0.0.0", "", "134.0", "v134.0.1.2", "134.0.1.2-beta", " 134.0.1.2", "134.0.1.2 ", "999999999999.0.1.2" })
{
    var invalid = new FakeRegistry(); invalid.Set(PrerequisiteRegistryLocation.WebView2Machine, RegistryView.Registry32, Values(("pv", RegistryValueKind.String, version)));
    Check(!PrerequisiteDetection.Detect(webview, invalid).Satisfied, "Zero, preview and malformed WebView2 registration cannot skip setup");
}
foreach (var kind in new[] { RegistryValueKind.DWord, RegistryValueKind.QWord, RegistryValueKind.Unknown, RegistryValueKind.Binary })
{
    var invalid = new FakeRegistry(); invalid.Set(PrerequisiteRegistryLocation.WebView2CurrentUser, RegistryView.Registry32, Values(("pv", kind, "134.0.1.2")));
    Check(!PrerequisiteDetection.Detect(webview, invalid).Satisfied, "WebView2 pv must be a plain version string rather than another registry type");
}
var wrongWebView = new FakeRegistry(); wrongWebView.Set(PrerequisiteRegistryLocation.WebView2Machine, RegistryView.Registry64, Values(("pv", RegistryValueKind.String, "999.0.1.2")));
Check(!PrerequisiteDetection.Detect(webview, wrongWebView).Satisfied, "An unrelated registry view cannot invent installed WebView2 authority");
var userFallback = new FakeRegistry { ErrorView = RegistryView.Registry32, ErrorLocation = PrerequisiteRegistryLocation.WebView2Machine, Error = new UnauthorizedAccessException() };
userFallback.Set(PrerequisiteRegistryLocation.WebView2CurrentUser, RegistryView.Registry32, Values(("pv", RegistryValueKind.String, "134.0.1.2")));
Check(PrerequisiteDetection.Detect(webview, userFallback).Satisfied, "A denied machine registration does not hide the original user's valid runtime");
Check(web.Calls.All(call => call.View == RegistryView.Registry32 && call.Location is PrerequisiteRegistryLocation.WebView2Machine or PrerequisiteRegistryLocation.WebView2CurrentUser), "WebView2 detector queries only the two fixed documented locations in the explicit 32-bit view");

// Safe integration controls never allow external prerequisite execution.
var ownedOutput = Environment.GetEnvironmentVariable("HEIROWINSTALLER_TEST_OUTPUT_ROOT");
var temporary = ownedOutput is null ? Path.Combine(Path.GetTempPath(), "heirow-prerequisite-fixture-" + Guid.NewGuid().ToString("N")) : Path.GetFullPath(ownedOutput);
if (Directory.Exists(temporary)) throw new IOException("Use a fresh prerequisite test fixture root."); Directory.CreateDirectory(temporary);
try
{
    var existing = Path.Combine(temporary, "old-file-marker"); File.WriteAllText(existing, "inert fixture marker");
    var plan = new InstallPlan { Manifest = new() { Id = "prerequisite.fixture" }, InstallDirectory = temporary, Features = [] };
    var options = new InstallOptions { Isolated = true };
    var legacy = new PackageManifest { Prerequisites = [new() { Id = "legacy.fixture", Name = "Existing file marker", DetectFile = existing }] };
    Check(!await WindowsIntegration.InstallPrerequisitesAsync(legacy, plan, temporary, options, null, default), "Existing file-only prerequisite detection remains compatible and starts no process");
    var missing = new PackageManifest { Prerequisites = [new() { Id = "new.fixture", Name = "Missing runtime", DetectFile = existing, Detection = Definition(minimum: "10.999.999") }] };
    try { await WindowsIntegration.InstallPrerequisitesAsync(missing, plan, temporary, options, null, default); throw new Exception("An existing file bypassed structured runtime detection."); }
    catch (InvalidOperationException error) when (error.Message.Contains("Isolated validation", StringComparison.Ordinal)) { Check(true, "An existing file cannot bypass a missing structured runtime; isolated validation never launches its installer"); }
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    try { await WindowsIntegration.InstallPrerequisitesAsync(legacy, plan, temporary, options, null, cancelled.Token); throw new Exception("Cancellation ignored."); }
    catch (OperationCanceledException) { Check(true, "Cancellation is honored before prerequisite detection or installer activity"); }
    var found = PrerequisiteDetection.Detect(dotnet);
    if (found.Satisfied)
    {
        var installed = new PackageManifest { Prerequisites = [new() { Id = "installed.fixture", Name = "Detected Desktop10", Detection = dotnet, Executable = "never-executed.exe" }] };
        Check(!await WindowsIntegration.InstallPrerequisitesAsync(installed, plan, temporary, options, null, default), "Read-only local runtime detection skips installer execution before the isolated guard");
    }
    var source = Path.Combine(temporary, "package-source"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "prerequisite.exe"), "inert signed-payload fixture, never executed");
    var authoring = new PackageManifest { Id = "prerequisite.format.fixture", Name = "Prerequisite format fixture", Publisher = "Fixture", Version = "1.0.0", LegalDocuments = LegalFixture.For("prerequisite.format.fixture"),
        Prerequisites = [new() { Id = "desktop.fixture", Name = "Desktop10", Detection = dotnet, Executable = "prerequisite.exe" }] };
    var json = Path.Combine(temporary, "format.json"); File.WriteAllText(json, JsonSerializer.Serialize(authoring, InstallerJson.Options));
    var packagePath = Path.Combine(temporary, "format.hinst"); PackageBuilder.Build(json, source, packagePath);
    using (var package = PackageReader.Open(packagePath))
    {
        Check(package.Manifest.FormatVersion == 3 && package.Manifest.Prerequisites.Single().Detection?.MinimumVersion == "10.0.0", "Structured runtime detection promotes packages to format3 and survives package reading");
        foreach (var format in new[] { 1, 2 })
        {
            package.Manifest.FormatVersion = format;
            Reject(() => PackageSafety.ValidateManifest(package.Manifest, true), "Older package formats cannot claim structured prerequisite semantics");
        }
    }
    authoring.Prerequisites.Single().Detection = webview;
    File.WriteAllText(json, JsonSerializer.Serialize(authoring, InstallerJson.Options));
    var webviewPackage = Path.Combine(temporary, "webview.hinst"); PackageBuilder.Build(json, source, webviewPackage);
    using (var package = PackageReader.Open(webviewPackage))
        Check(package.Manifest.FormatVersion == 3 && package.Manifest.Prerequisites.Single().Detection?.Kind == "webview2", "Fixed WebView2 detection survives actual package validation and roundtrip");
    var old = new PrerequisiteDefinition { Id = "old.fixture", Name = "Old file prerequisite", DetectFile = "marker" };
    Check(!JsonSerializer.Serialize(old).Contains("Detection", StringComparison.Ordinal), "Omitted nullable detection preserves legacy prerequisite JSON");
    authoring.Prerequisites = [old]; File.WriteAllText(json, JsonSerializer.Serialize(authoring, InstallerJson.Options));
    PackageBuilder.Build(json, source, Path.Combine(temporary, "legacy.hinst"));
    using (var package = PackageReader.Open(Path.Combine(temporary, "legacy.hinst")))
        Check(package.Manifest.FormatVersion == 1 && package.Manifest.Prerequisites.Single().Detection is null, "Old prerequisite manifests retain format1 instead of being unnecessarily promoted");
}
finally
{
    if (ownedOutput is null)
    {
        if (!Path.GetFullPath(temporary).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(temporary).StartsWith("heirow-prerequisite-fixture-", StringComparison.Ordinal)) throw new Exception("Unsafe fixture cleanup root.");
        Directory.Delete(temporary, true);
    }
}

var localDotnet = PrerequisiteDetection.Detect(dotnet); var localVc = PrerequisiteDetection.Detect(vc); var localWebview = PrerequisiteDetection.Detect(webview);
if (args.Length == 1)
{
    var receipt = Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
    File.WriteAllText(receipt, JsonSerializer.Serialize(new { passed = true, assertions = checks, localReadOnlyProbe = new { dotnetDesktopX64 = localDotnet, vcRuntimeX64 = localVc, webview2 = localWebview }, registryWrites = 0, externalInstallerStarts = 0, productionOperations = 0, retained = ownedOutput is not null }, new JsonSerializerOptions { WriteIndented = true }));
}
Console.WriteLine($"Passed {checks} prerequisite checks. Read-only local probe: Desktop10={localDotnet.Satisfied}, VC14={localVc.Satisfied}; no installer ran.");

sealed class FakeRegistry : IPrerequisiteRegistry
{
    readonly Dictionary<(PrerequisiteRegistryLocation, RegistryView), IReadOnlyDictionary<string, PrerequisiteRegistryValue>> values = [];
    public List<(PrerequisiteRegistryLocation Location, RegistryView View)> Calls { get; } = [];
    public RegistryView? ErrorView { get; init; }
    public PrerequisiteRegistryLocation? ErrorLocation { get; init; }
    public Exception? Error { get; init; }
    public void Set(PrerequisiteRegistryLocation location, RegistryView view, IReadOnlyDictionary<string, PrerequisiteRegistryValue> content) => values[(location, view)] = content;
    public IReadOnlyDictionary<string, PrerequisiteRegistryValue>? Read(PrerequisiteRegistryLocation location, RegistryView view)
    {
        Calls.Add((location, view)); if (view == ErrorView && (ErrorLocation is null || location == ErrorLocation) && Error is not null) throw Error; return values.GetValueOrDefault((location, view));
    }
}
