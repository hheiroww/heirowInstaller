using System.Text.Json;
using HeirowInstaller.Core;
using System.Diagnostics;

namespace HeirowInstaller.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 2 && args[0] is "uninstall" or "repair") return Maintain(args);
            var options = AppArguments.Parse(args);
            if (options.WaitForPid is { } waitForPid)
            {
                try
                {
                    using var previous = Process.GetProcessById(waitForPid);
                    if (options.WaitForStartTicks is null || previous.StartTime.ToUniversalTime().Ticks == options.WaitForStartTicks)
                        if (!previous.WaitForExit(120_000)) throw new TimeoutException("The previous application did not exit within two minutes.");
                }
                catch (ArgumentException) { }
            }
            if (options.Silent)
            {
                if (options.PackagePath is null) throw new ArgumentException("Silent installation requires a .hinst package or bundled installer.");
                using var package = PackageReader.Open(options.PackagePath);
                options.ValidatePackageIdentity(package);
                var engine = new InstallerEngine();
                var install = new InstallOptions { TargetDirectory = options.TargetDirectory, StateRoot = options.StateRoot, Scope = options.Scope, Features = options.Features, Silent = true, InstallerPath = Environment.ProcessPath, Isolated = options.Isolated, RegisterProduct = !options.Isolated && !options.NoRegister, Launch = options.Launch, TrustedPublisherKeySha256 = options.TrustedPublisherKeySha256, RequireSignature = options.RequireSignature, LegacyArguments = options.LegacyArguments, LogPath = options.LogPath };
                if (engine.Plan(package, install).RequiresElevation && !InstallerEngine.IsElevated) return Elevate(args);
                var result = engine.InstallAsync(package, install).GetAwaiter().GetResult();
                return result.RebootRequired ? 3010 : 0;
            }
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
            using var game = new InstallerGame(options);
            game.Run();
            return Environment.ExitCode;
        }
        catch (Exception error)
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "heirowInstaller", "Logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "desktop-error.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O}\n{error}\n\n");
            if (!args.Any(value => new[] { "--silent", "--quiet", "/quiet", "/qn", "/silent", "/verysilent", "--defer" }.Contains(value, StringComparer.OrdinalIgnoreCase)))
                System.Windows.Forms.MessageBox.Show($"heirowInstaller could not start.\n\n{error.Message}\n\nDetails: {path}", "heirowInstaller", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int Maintain(string[] args)
    {
        var options = AppArguments.Parse(args.Skip(2).ToArray());
        var install = new InstallOptions { StateRoot = options.StateRoot, Scope = options.Scope, Silent = options.Silent, InstallerPath = Environment.ProcessPath };
        var product = InstallerEngine.ListInstalled(options.StateRoot).FirstOrDefault(value => value.Id.Equals(args[1], StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("The application is not installed.");
        if (AppController.NeedsMaintenanceElevation(product)) return Elevate(args);
        if (args[0] == "uninstall" && !options.Silent && System.Windows.Forms.MessageBox.Show($"Uninstall {product.Name}?", "heirowInstaller", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Question) != System.Windows.Forms.DialogResult.Yes) return 0;
        var engine = new InstallerEngine();
        var result = args[0] == "uninstall" ? engine.UninstallAsync(args[1], install).GetAwaiter().GetResult() : engine.RepairAsync(args[1], install).GetAwaiter().GetResult();
        return result.RebootRequired ? 3010 : 0;
    }

    private static int Elevate(string[] args)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the elevated installer.");
        child.WaitForExit();
        return child.ExitCode;
    }
}

internal sealed record AppArguments(string? PackagePath, string? CapturePath, string? SmokeDirectory, bool Install, string? StateRoot, string? TargetDirectory, string? Scope, List<string>? Features, bool Silent, int? WaitForPid, bool Isolated, bool Launch, bool AcceptLicense, long? WaitForStartTicks, string? TrustedPublisherKeySha256, bool RequireSignature, string? SelfUpdateSmokeFeed, string? SelfUpdateSmokeReport, List<string> LegacyArguments, string? LogPath, string? ExpectedProductId, string? ExpectedVersion, string? ExpectedSha256, bool NoRegister)
{
    internal void ValidatePackageIdentity(PackageReader package)
    {
        if (ExpectedProductId is not null && !package.Manifest.Id.Equals(ExpectedProductId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installer package belongs to a different product than the requested update.");
        if (ExpectedVersion is not null && !package.Manifest.Version.Equals(ExpectedVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The installer package version does not match the requested update.");
        if (ExpectedSha256 is not null && !PackageSafety.HashEquals(PackageSafety.HashFile(package.PackagePath), ExpectedSha256))
            throw new System.Security.Cryptography.CryptographicException("The installer package changed after the update was downloaded.");
    }

    public static AppArguments Parse(string[] args)
    {
        string? package = null, capture = null, smoke = null, state = null, target = null, scope = null;
        List<string>? features = null;
        var install = false; var silent = false; var isolated = false; var launch = false; var acceptLicense = false; int? waitForPid = null; long? waitForStartTicks = null;
        string? trustedKey = null, selfUpdateFeed = null, selfUpdateReport = null; var requireSignature = false;
        List<string> legacyArguments = []; string? logPath = null;
        string? expectedProduct = null, expectedVersion = null, expectedSha256 = null; var noRegister = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} requires a value.");
            switch (args[i].ToLowerInvariant())
            {
                case "--capture": capture = Path.GetFullPath(Value()); break;
                case "--ui-smoke": smoke = Path.GetFullPath(Value()); break;
                case "--state-root": state = Path.GetFullPath(Value()); break;
                case "--install": install = true; package = Path.GetFullPath(Value()); break;
                case "--target": target = Path.GetFullPath(Value()); break;
                case "--scope": scope = Value(); break;
                case "--features": features = Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); break;
                case "--wait-for-pid": case "--wait-pid": waitForPid = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--wait-start": waitForStartTicks = long.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--defer": silent = true; break;
                case "--trust-key": trustedKey = Value(); break;
                case "--require-signature": requireSignature = true; break;
                case "--expected-product-id": expectedProduct = Value(); break;
                case "--expected-version": expectedVersion = Value(); break;
                case "--expected-sha256": expectedSha256 = Value(); break;
                case "--no-register": noRegister = true; break;
                case "--self-update-smoke-feed": selfUpdateFeed = Value(); break;
                case "--self-update-smoke-report": selfUpdateReport = Path.GetFullPath(Value()); break;
                case "--backend-arg": legacyArguments.Add(Value()); break;
                case "--log": logPath = Path.GetFullPath(Value()); break;
                case "--isolated": isolated = true; break;
                case "--launch": launch = true; break;
                case "--accept-license": acceptLicense = true; break;
                case "--silent": case "--quiet": case "/quiet": case "/qn": case "/silent": case "/verysilent": silent = true; break;
                case "/suppressmsgboxes": case "/norestart": case "/sp-": break;
                default:
                    if (args[i].StartsWith("/DIR=", StringComparison.OrdinalIgnoreCase)) { target = Path.GetFullPath(args[i][5..].Trim('"')); break; }
                    if (args[i].StartsWith("/LOG=", StringComparison.OrdinalIgnoreCase)) { logPath = Path.GetFullPath(args[i][5..].Trim('"')); legacyArguments.Add("/LOG=" + logPath); break; }
                    if (args[i].StartsWith('-')) throw new ArgumentException($"Unknown desktop option: {args[i]}");
                    package = Path.GetFullPath(args[i]);
                    break;
            }
        }
        if (package is null && Environment.ProcessPath is { } self && PackageReader.HasEmbeddedPackage(self)) package = self;
        if (scope is not null && scope is not ("user" or "machine")) throw new ArgumentException("Scope must be user or machine.");
        if (smoke is not null) { state = Path.Combine(smoke, "state"); target = Path.Combine(smoke, "installed"); isolated = true; }
        if (isolated && state is null) throw new ArgumentException("Isolated installation requires an explicit --state-root.");
        if (waitForPid is <= 0 || waitForPid == Environment.ProcessId) throw new ArgumentException("Choose a different valid process to wait for.");
        if (waitForStartTicks is <= 0 || (waitForStartTicks is not null && waitForPid is null)) throw new ArgumentException("--wait-start requires a valid --wait-pid.");
        if (trustedKey is not null && (trustedKey.Length != 64 || !trustedKey.All(Uri.IsHexDigit))) throw new ArgumentException("--trust-key requires a SHA-256 public-key fingerprint.");
        if ((expectedProduct is null) != (expectedVersion is null)) throw new ArgumentException("--expected-product-id and --expected-version must be supplied together.");
        if (expectedProduct is not null) PackageSafety.ValidateId(expectedProduct);
        if (expectedVersion is not null) _ = PackageVersion.Parse(expectedVersion);
        if (expectedSha256 is not null && (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))) throw new ArgumentException("--expected-sha256 requires a SHA-256 digest.");
        if (selfUpdateFeed is not null && (!isolated || state is null || target is null || selfUpdateReport is null ||
            !Uri.TryCreate(selfUpdateFeed, UriKind.Absolute, out var smokeUri) || !smokeUri.IsLoopback || smokeUri.Scheme is not ("http" or "https")))
            throw new ArgumentException("Self-update acceptance requires an explicit isolated state, target, report, and loopback feed.");
        return new(package, capture, smoke, install, state, target, scope, features, silent, waitForPid, isolated, launch, acceptLicense, waitForStartTicks, trustedKey, requireSignature, selfUpdateFeed, selfUpdateReport, legacyArguments, logPath, expectedProduct, expectedVersion, expectedSha256, noRegister);
    }
}
