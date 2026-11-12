using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HeirowInstaller.App;
using HeirowInstaller.BootstrapContract;
using HeirowInstaller.Core;

internal static partial class Program
{
    private const string DescriptorName = "Signed Native Fixture";
    private const string RemoteUrl = "https://packages.example.invalid/app.hinst";
    private const long RemoteSize = 5000;
    private static readonly string DescriptorHash = new('b', 64);

    static void SetupNameChecks()
    {
        var fixture = Build("signed-presentation", includeLicense: false);
        var options = Arguments(fixture);
        using (var form = NewForm(options with { VerifiedBootstrapProductName = DescriptorName }))
        {
            Check(form.Text == DescriptorName + " Setup" && form.Controller.SetupProductName == DescriptorName && form.Controller.Manifest is null,
                "verified bootstrap name is available in the CPU title and shared desktop presentation before download");
            Pump(form, form.Controller.OpenPackageAsync(fixture.Package));
            Check(!form.Controller.Failed && form.Controller.Manifest?.Name == "Synthetic CPU wizard fixture" &&
                form.Text == DescriptorName + " Setup" && form.Controller.StatusDetail.Contains(DescriptorName, StringComparison.Ordinal),
                "a verified descriptor name remains consistent when the signed package opens");
        }
        using (var form = NewForm(options with { TrustedPublisherKeySha256 = new string('a', 64) }))
        {
            Pump(form, form.Controller.OpenPackageAsync(fixture.Package));
            Check(form.Controller.Failed && form.Controller.Manifest is null && form.Controller.SetupProductName is null &&
                form.Text == "Application setup", "a correctly signed package with the wrong publisher pin never publishes its title");
        }
        var fixtureRoot = Path.GetDirectoryName(fixture.Package)!;
        var unsigned = Path.Combine(fixtureRoot, "unsigned.hinst");
        PackageBuilder.Build(Path.Combine(fixtureRoot, "manifest.json"), Path.Combine(fixtureRoot, "source"), unsigned, new());
        using (var form = NewForm(options with { PackagePath = unsigned, TrustedPublisherKeySha256 = null, ExpectedSha256 = PackageSafety.HashFile(unsigned) }))
        {
            Pump(form, form.Controller.OpenPackageAsync(unsigned));
            Check(form.Controller.Failed && form.Controller.Manifest is null && form.Controller.SetupProductName is null,
                "unsigned packages never publish a title on a required-signature route");
        }
        using (var form = NewForm(options with { PackagePath = unsigned, RequireSignature = false, TrustedPublisherKeySha256 = null, ExpectedSha256 = null }))
        {
            Pump(form, form.Controller.OpenPackageAsync(unsigned));
            Check(!form.Controller.Failed && form.Text == "Synthetic CPU wizard fixture Setup",
                "explicitly allowed unsigned local packages remain inspectable after package verification");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Receipt)!);
        var cached = Path.Combine(Path.GetDirectoryName(fixture.Receipt)!, "package.hinst");
        File.Copy(fixture.Package, cached);
        File.WriteAllText(fixture.Receipt, JsonSerializer.Serialize(new InstalledProduct
        {
            Id = fixture.Id, Name = "Local receipt label", Version = "1.0.0", PublisherKeySha256 = signingPin,
            PackagePath = cached, ReceiptPath = fixture.Receipt, InstallDirectory = fixture.Target, Isolated = true
        }, InstallerJson.Options));
        using (var form = NewForm(options with { PackagePath = unsigned, RequireSignature = false, TrustedPublisherKeySha256 = null, ExpectedSha256 = null }))
        {
            Pump(form, form.Controller.OpenPackageAsync(unsigned));
            Check(form.Controller.Failed && form.Controller.Manifest is null && form.Text == "Application setup",
                "the installed publisher pin also protects branding when no explicit key is passed");
        }
        var maintenance = AppArguments.Parse(["--isolated", "--state-root", fixture.State, "--scope", "user"], maintenanceInvocation: true)
            with { MaintenanceProductId = fixture.Id, MaintenanceOperation = "repair" };
        using (var form = NewForm(maintenance))
        {
            Pump(form, form.Controller.InitializeMaintenanceWizardAsync());
            Check(!form.Controller.Failed && form.Controller.Stage == SetupStage.Review &&
                form.Text == "Synthetic CPU wizard fixture Setup" && form.Controller.StatusDetail.StartsWith("Synthetic CPU wizard fixture", StringComparison.Ordinal),
                "maintenance branding uses the verified cached manifest instead of the receipt label");
        }
        Check(!Directory.Exists(fixture.Target), "presentation checks never install or mutate application files");

        var ownImage = Environment.ProcessPath ?? throw new InvalidOperationException("The fixture apphost is unavailable.");
        var carrier = Path.Combine(Path.GetDirectoryName(ownImage)!, "setup-name-fixture-" + Guid.NewGuid().ToString("N") + ".exe");
        using var signer = RSA.Create();
        signer.ImportFromPem(File.ReadAllText(signingKey));
        var descriptor = new { formatVersion = 1, hostKind = "installer", productId = fixture.Id, name = DescriptorName, version = "1.0.0", publisherKeySha256 = signingPin,
            applicationPackage = new { sha256 = DescriptorHash, url = RemoteUrl, size = RemoteSize } };
        WebBootstrapBindings.WriteAsync(ownImage, JsonSerializer.SerializeToUtf8Bytes(descriptor), carrier, signer).GetAwaiter().GetResult();
        RunNameChild(carrier, ["--setup-name-carrier", ownImage, fixture.Id, signingPin], start => { });
        Check(true, "a live signed setup can brand both direct desktop and graphics-child handoffs; altered identities are rejected");
    }

    static int RunSetupNameProcess(string[] args)
    {
        try
        {
            var role = args[0];
            var ownImage = Environment.ProcessPath!;
            using var process = Process.GetCurrentProcess();
            if (role == "--setup-name-carrier")
            {
                var destination = args[1];
                foreach (var child in new[] { "--setup-name-child", "--setup-name-intermediate" })
                    RunNameChild(destination, [child, args[2], args[3]], start =>
                    {
                        start.Environment[BootstrapProcessTrust.ParentIdVariable] = process.Id.ToString(CultureInfo.InvariantCulture);
                        start.Environment[BootstrapProcessTrust.ParentStartVariable] = process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
                        start.Environment[BootstrapWorkerPromotion.LauncherVariable] = ownImage;
                    });
            }
            else if (role == "--setup-name-intermediate")
            {
                RunNameChild(ownImage, ["--setup-name-child", args[1], args[2], process.Id.ToString(CultureInfo.InvariantCulture)], _ => { });
            }
            else
            {
                var primary = args.Length == 4 ? int.Parse(args[3], CultureInfo.InvariantCulture) : (int?)null;
                string? Read(string? product = null, string version = "1.0.0", string? pin = null,
                    string? hash = null, string? url = RemoteUrl, long size = RemoteSize) =>
                    BootstrapProcessTrust.ReadBoundSetupName(product ?? args[1], version, hash ?? DescriptorHash, pin ?? args[2], url, size, primary);
                if (Read() != DescriptorName || Read(url: null, size: 0) != DescriptorName ||
                    Read(product: "other.app") is not null || Read(version: "2.0.0") is not null ||
                    Read(pin: new string('a', 64)) is not null || Read(hash: new string('a', 64)) is not null ||
                    Read(url: "https://other.example.invalid/app.hinst") is not null || Read(size: RemoteSize + 1) is not null)
                    throw new InvalidOperationException("The live signed descriptor was not bound to the exact setup arguments.");
                Environment.SetEnvironmentVariable(BootstrapProcessTrust.ParentStartVariable, "1");
                if (Read() is not null) throw new InvalidOperationException("A mismatched coordinator start time supplied a product name.");
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void RunNameChild(string image, string[] arguments, Action<ProcessStartInfo> configure)
    {
        var start = new ProcessStartInfo(image) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        configure(start);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("The isolated name fixture did not start.");
        var stderr = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(30_000)) throw new TimeoutException("The isolated name fixture did not exit.");
        if (child.ExitCode != 0) throw new InvalidOperationException(stderr.GetAwaiter().GetResult());
    }
}
