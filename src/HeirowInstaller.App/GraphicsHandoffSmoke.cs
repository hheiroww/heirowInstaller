#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;

namespace HeirowInstaller.App;

// This windowless route accepts only its own signed, data-only bundled fixture.
// It exercises the real DX11-companion -> original primary worker boundary.
internal static class GraphicsHandoffSmoke
{
    internal const string ProductId = "heirowInstaller.wizard-fixture.graphics-handoff";
    internal const string Marker = "heirowInstaller isolated wizard acceptance v1\n";
    internal const string Notes = "heirowInstaller graphics handoff fixture v1\n";
    internal const string Privacy = "Synthetic isolated graphics-handoff fixture. No personal data is collected.\n";
    internal const string Terms = "Synthetic isolated graphics-handoff acceptance only. No production product is installed.\n";

    internal static void ValidateArguments(AppArguments options)
    {
        string root = options.GraphicsHandoffSmokeRoot ?? throw new InvalidDataException("The marked graphics fixture root is missing.");
        Require(Path.IsPathFullyQualified(root), "The graphics fixture root must be absolute.");
        root = Path.GetFullPath(root);
        PackageSafety.EnsureNoReparsePoints(root);
        Require(options.Isolated && options.NoRegister && options.Scope == "machine" && options.RequireSignature
            && options.StateRoot is not null && SamePath(options.StateRoot, Path.Combine(root, "graphics-context"))
            && options.TargetDirectory is not null && SamePath(options.TargetDirectory, Path.Combine(root, "installed"))
            && options.PackagePath is not null && SamePath(options.PackagePath, Path.Combine(root, "fixture-setup.exe"))
            && options.ExpectedProductId == ProductId && options.ExpectedVersion == "1.0.0" && IsHash(options.TrustedPublisherKeySha256)
            && IsHash(options.ExpectedSha256) && !options.Install && !options.Launch && !options.Silent && !options.SoftwareRendering
            && !options.AcceptLicense && !options.CloseOnComplete && !options.MaintenanceConfirmed && options.MaintenanceProductId is null
            && options.MaintenanceOperation is null && options.CapturePath is null && options.SmokeDirectory is null && !options.RenderSmoke
            && options.WizardSmokeDirectory is null && options.PerformanceSmokeDirectory is null && options.WaitForPid is null
            && options.WaitForStartTicks is null && options.Features is null && options.LogPath is null && options.LegacyArguments.Count == 0
            && options.SelfUpdateSmokeFeed is null && options.SelfUpdateSmokeReport is null,
            "Only the exact isolated, noninteractive data-fixture handoff arguments are allowed.");
        string marker = Path.Combine(root, ".isolated-wizard-fixture"), setup = Path.Combine(root, "fixture-setup.exe");
        PackageSafety.EnsureNoReparsePoints(marker); PackageSafety.EnsureNoReparsePoints(setup);
        Require(new FileInfo(marker).Length == Encoding.UTF8.GetByteCount(Marker) && File.ReadAllText(marker) == Marker,
            "The exact isolated fixture marker is required before starting a companion.");
        using var own = PackageReader.Open(setup);
        own.VerifySignature(options.TrustedPublisherKeySha256, requireSignature: true);
        ValidateManifest(own.Manifest);
        Require(PackageSafety.HashEquals(PackageSafety.HashFile(setup), options.ExpectedSha256!),
            "The exact bundled fixture setup must be bound before starting a companion.");
    }

    internal static async Task<int> RunAsync(string fixtureRoot)
    {
        Require(OperatingSystem.IsWindows() && DirectX11Fallback.HasChildContext, "A validated DirectX 11 companion context is required.");
        Require(Path.IsPathFullyQualified(fixtureRoot), "The handoff fixture requires an absolute owned directory.");
        string root = Path.GetFullPath(fixtureRoot);
        Require(Path.GetDirectoryName(root) is not null && Directory.Exists(root), "The marked handoff fixture directory is required.");
        PackageSafety.EnsureNoReparsePoints(root);
        string markerPath = Path.Combine(root, ".isolated-wizard-fixture"), bindingPath = Path.Combine(root, "graphics-handoff-fixture.json");
        string packagePath = Path.Combine(root, "fixture.hinst"), primaryPath = Path.Combine(root, "fixture-setup.exe");
        string target = Path.Combine(root, "installed"), state = Path.Combine(root, "state"), report = Path.Combine(root, "graphics-handoff-result.json");
        foreach (string path in new[] { markerPath, bindingPath, packagePath, primaryPath, target, state, report }) PackageSafety.EnsureNoReparsePoints(path);
        using var markerLease = File.Open(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(markerLease.Length == Encoding.UTF8.GetByteCount(Marker) && File.ReadAllText(markerPath) == Marker, "The exact isolated fixture marker is required.");
        using var bindingLease = File.Open(bindingPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(bindingLease.Length is > 0 and <= 16384, "The fixture binding exceeds its bounded schema.");
        var binding = JsonSerializer.Deserialize<FixtureBinding>(File.ReadAllText(bindingPath), InstallerJson.Options)
            ?? throw new InvalidDataException("The handoff fixture binding is missing.");
        Require(binding.Schema == 1 && SamePath(binding.Root, root) && binding.ProductId == ProductId && binding.Version == "1.0.0"
            && IsHash(binding.PackageSha256) && IsHash(binding.PublisherKeySha256) && IsHash(binding.PrimarySha256)
            && binding.PackageBytes is > 0 and <= 1048576 && binding.PrimaryBytes is > 0 and <= 1073741824,
            "The fixture binding does not describe the closed data-only fixture.");
        Require(!Directory.Exists(target) && !File.Exists(target) && !Directory.Exists(state) && !File.Exists(state) && !File.Exists(report),
            "Preserve existing fixture state; use a fresh marked fixture.");
        Require(DirectX11Fallback.CurrentPrimaryProcessId is > 0 && DirectX11Fallback.CurrentPrimaryProcessId != Environment.ProcessId
            && SamePath(DirectX11Fallback.CurrentPrimaryHostPath!, primaryPath) && !SamePath(Environment.ProcessPath!, primaryPath),
            "The smoke test must run in the declared companion of this fixture's original primary setup.");

        using var primaryLease = File.Open(primaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var packageLease = File.Open(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(primaryLease.Length == binding.PrimaryBytes && packageLease.Length == binding.PackageBytes
            && PackageSafety.HashEquals(PackageSafety.HashFile(primaryPath), binding.PrimarySha256)
            && PackageSafety.HashEquals(DirectX11Fallback.CurrentPrimaryHostSha256 ?? "", binding.PrimarySha256)
            && PackageSafety.HashEquals(PackageSafety.HashFile(packagePath), binding.PackageSha256), "The pinned fixture inputs changed.");
        using var own = PackageReader.Open(primaryPath);
        using var package = PackageReader.Open(packagePath);
        own.VerifySignature(binding.PublisherKeySha256, requireSignature: true);
        package.VerifySignature(binding.PublisherKeySha256, requireSignature: true);
        Require(PackageSafety.HashEquals(own.ComputePackageSha256(), binding.PackageSha256)
            && PackageSafety.HashEquals(package.ComputePackageSha256(), binding.PackageSha256), "The fixture package must be the original primary's exact signed footer bytes.");
        ValidateManifest(own.Manifest);
        ValidateManifest(package.Manifest);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await package.VerifyAsync(cancellationToken: deadline.Token);
        Require(CountWindows(Environment.ProcessId) == 0 && !NativeGraphicsLoaded(), "The smoke route must run before any desktop or graphics initialization.");

        var options = new InstallOptions
        {
            TargetDirectory = target, StateRoot = state, Scope = "machine", Isolated = true, RegisterProduct = false, Launch = false,
            Silent = true, InstallerPath = primaryPath, RequireSignature = true, TrustedPublisherKeySha256 = binding.PublisherKeySha256
        };
        var engine = new InstallerEngine();
        var plan = engine.Plan(package, options);
        Require(plan.Scope == "machine" && plan.RequiresElevation && SamePath(plan.InstallDirectory, target)
            && plan.Files.Count == 1 && plan.Files[0].Path == "notes.txt", "The handoff plan exceeded the exact isolated data fixture.");
        bool workerObservedWithoutWindows = false;
        var progress = new ObservedProgress(() =>
        {
            if (ElevatedInstallWorker.LastObservation is not { } worker) return;
            Require(worker.ProcessId != Environment.ProcessId && worker.Isolated
                && PackageSafety.HashEquals(worker.ImageSha256, binding.PrimarySha256) && CountWindows(worker.ProcessId) == 0,
                "The actual original primary worker must remain windowless and bound to the reviewed fixture image.");
            workerObservedWithoutWindows = true;
        });
        var result = await ElevatedInstallWorker.InstallAsync(packagePath, plan, options,
            (_, _) => throw new InvalidOperationException("A fresh data-only fixture cannot request application closure."), progress, root, deadline.Token);
        var observation = ElevatedInstallWorker.LastObservation;
        Require(result.Success && result.ProductId == ProductId && result.Version == "1.0.0" && SamePath(result.ReceiptPath, Path.Combine(state, ProductId, "receipt.json"))
            && observation is { Completed: true, Isolated: true } && workerObservedWithoutWindows
            && PackageSafety.HashEquals(observation.ImageSha256, binding.PrimarySha256), "The real original primary worker did not complete its authenticated isolated transaction.");
        var product = InstallerEngine.ListInstalled(state).Single();
        Require(product.Id == ProductId && product.Version == "1.0.0" && product.Scope == "machine" && product.Isolated && !product.RegisteredProduct
            && PackageSafety.HashEquals(product.PublisherKeySha256 ?? "", binding.PublisherKeySha256)
            && SamePath(product.InstallDirectory, target) && File.ReadAllText(Path.Combine(target, "notes.txt")) == Notes,
            "The signed fixture receipt and installed data do not match the isolated reviewed plan.");
        var rejected = await ElevatedInstallWorker.VerifyProtocolRejectionsAsync(root, packagePath, plan, options,
            DirectX11Fallback.CurrentPrimaryProcessId!.Value);
        Require(rejected.Count == 7 && CountWindows(Environment.ProcessId) == 0 && !NativeGraphicsLoaded()
            && PackageSafety.HashEquals(PackageSafety.HashFile(primaryPath), binding.PrimarySha256)
            && PackageSafety.HashEquals(PackageSafety.HashFile(packagePath), binding.PackageSha256), "The handoff fixture did not retain its complete negative-protocol and no-graphics boundary.");
        var receipt = new
        {
            result = "PASS", product = ProductId, version = "1.0.0", actualCompanionProcessId = Environment.ProcessId,
            originalPrimaryProcessId = DirectX11Fallback.CurrentPrimaryProcessId, originalPrimarySha256 = binding.PrimarySha256,
            originalPrimaryWorker = observation, exactBundledPackageSha256 = binding.PackageSha256, publisherKeySha256 = binding.PublisherKeySha256,
            actualWorkerTransactionVerified = true, rejectedProtocolCases = rejected, scope = "machine", isolated = true,
            receiptPath = result.ReceiptPath, installedPayloadFiles = 1, registryWrites = false, productionWrites = false,
            applicationLaunch = false, windowsShown = 0, nativeGraphicsLoaded = false, inputInjected = false, capture = false,
            uacRequested = false, outputsRetained = true, finishedUtc = DateTimeOffset.UtcNow
        };
        await using var output = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(output, receipt, InstallerJson.Options, deadline.Token);
        Console.WriteLine("PASS windowless DX11 companion -> original primary worker signed isolated handoff and seven protocol rejections.");
        return 0;
    }

    private static void ValidateManifest(PackageManifest manifest)
    {
        byte[] notes = Encoding.UTF8.GetBytes(Notes);
        Require(manifest.FormatVersion == 3 && manifest.Id == ProductId && manifest.Version == "1.0.0" && manifest.Scope == "machine"
            && manifest.EntryPoint.Length == 0 && manifest.InstallDirectory.Length == 0 && manifest.LaunchWorkingDirectory is null
            && manifest.UpdateUrl is null && manifest.Homepage is null && manifest.LicenseFile is null && manifest.LegalDocuments is not null
            && manifest.Registry.Count == 0 && manifest.Services.Count == 0 && manifest.Shortcuts.Count == 0 && manifest.Environment.Count == 0
            && manifest.Modules.Count == 0 && manifest.Prerequisites.Count == 0 && manifest.AffectedResources.Count == 0
            && manifest.LegacyMsi is null && manifest.LegacyExecutable is null && manifest.NativeMigration is null && manifest.UpdateCompanion is null
            && manifest.InstallationRoots is null && manifest.Features.Count == 0 && manifest.Payload.Count == 1,
            "Only the closed signed graphics-handoff data fixture may use this route.");
        var payload = manifest.Payload[0];
        Require(payload.Path == "notes.txt" && payload.Source == "notes.txt" && payload.Feature is null && payload.TargetRoot is null
            && payload.Size == notes.Length && PackageSafety.HashEquals(payload.Sha256, Convert.ToHexString(SHA256.HashData(notes))),
            "The handoff fixture must contain only the exact known notes.txt payload.");
        ValidateLegal(manifest.LegalDocuments!.PrivacyPolicy, "PrivacyPolicy", "privacy-policy.txt", Privacy);
        ValidateLegal(manifest.LegalDocuments.TermsAndConditions, "TermsAndConditions", "terms-and-conditions.txt", Terms);
    }

    private static void ValidateLegal(PackageLegalDocument? document, string route, string fileName, string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        Require(document is not null && document.Url == "https://socketjack.com/heirowInstaller/" + route + "/" + ProductId
            && document.FileName == fileName && document.MediaType == "text/plain" && document.Content == content && document.Size == bytes.Length
            && PackageSafety.HashEquals(document.Sha256, Convert.ToHexString(SHA256.HashData(bytes))), "The fixture legal document differs from its exact signed synthetic text.");
    }

    private static bool NativeGraphicsLoaded() => Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(module =>
        module.ModuleName.StartsWith("SDL", StringComparison.OrdinalIgnoreCase) || new[]
        { "mgruntime.dll", "d3d11.dll", "d3d12.dll", "dxgi.dll", "d3dcompiler_47.dll" }.Contains(module.ModuleName, StringComparer.OrdinalIgnoreCase));
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool SamePath(string left, string right) => Path.GetFullPath(left).TrimEnd('\\', '/').Equals(Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static int CountWindows(int processId)
    {
        int count = 0;
        if (!EnumWindows((window, _) => { GetWindowThreadProcessId(window, out uint owner); if (owner == (uint)processId) count++; return true; }, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate the fixture's top-level windows.");
        return count;
    }
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr value);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    private sealed class ObservedProgress(Action check) : IProgress<InstallProgress> { public void Report(InstallProgress _) => check(); }
    private sealed record FixtureBinding(int Schema, string Root, string ProductId, string Version, long PackageBytes,
        string PackageSha256, string PublisherKeySha256, long PrimaryBytes, string PrimarySha256);
}
#endif
