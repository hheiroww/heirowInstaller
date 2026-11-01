using System.Diagnostics;
using System.Text.Json;
using HeirowInstaller.Core;

internal static class CliWindowsSigningChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        var cli = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cli-fixture-path.txt")).Trim();
        var directory = Path.Combine(root, "cli-windows-signing");
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "existing setup.exe");
        byte[] retained = [7, 11, 29, 41];
        await File.WriteAllBytesAsync(output, retained);

        async Task<(int ExitCode, string Output, string Error)> Run(params string[] arguments)
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
            start.ArgumentList.Add(cli);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var child = Process.Start(start) ?? throw new IOException("The CLI signing fixture did not start.");
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); throw; }
            return (child.ExitCode, await stdout, await stderr);
        }

        var unsignedVerification = await Run("verify-windows-signature", output);
        check(unsignedVerification.ExitCode != 0 && File.ReadAllBytes(output).SequenceEqual(retained),
            "CLI trust verification rejects unsigned input without changing it or needing signing credentials");

        var missingProvider = await Run("sign", output);
        check(missingProvider.ExitCode != 0 && File.ReadAllBytes(output).SequenceEqual(retained),
            "CLI signing requires an explicit certificate or service profile and preserves the input on failure");

        var noSigning = await Run("sign", output, "--windows-signing", "none");
        check(noSigning.ExitCode != 0 && File.ReadAllBytes(output).SequenceEqual(retained),
            "CLI sign never reports unsigned input as a successful signing operation");

        var package = Path.Combine(directory, "application.hinst");
        var invalidPackageOption = await Run("pack", "missing.json", "--source", directory, "--output", package,
            "--certificate-thumbprint", new string('A', 40));
        check(invalidPackageOption.ExitCode != 0 && !File.Exists(package) && invalidPackageOption.Error.Contains(".hinst", StringComparison.Ordinal),
            "Windows signing settings cannot silently pass through the package-only CLI command");

        var source = Path.Combine(directory, "source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "app.txt"), "CLI Windows signing fixture");
        var manifest = Path.Combine(directory, "installer.json");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new PackageManifest
        {
            Id = "cli.signing.fixture", Name = "CLI signing fixture", Publisher = "Fixture",
            LegalDocuments = LegalFixture.For("cli.signing.fixture")
        }, InstallerJson.Options));
        PackageBuilder.Build(manifest, source, package);
        var launcher = Path.Combine(directory, "launcher.exe");
        await File.WriteAllBytesAsync(launcher, new byte[512]);
        var bundled = Path.Combine(directory, "unsigned setup.exe");
        var plainBundle = await Run("bundle", package, "--launcher", launcher, "--output", bundled);
        check(plainBundle.ExitCode == 0 && File.Exists(bundled) && PackageReader.HasEmbeddedPackage(bundled),
            "Existing unsigned CLI bundling still produces a readable complete package");

        string[] unavailable = ["--windows-signing", "microsoft", "--artifact-endpoint", "https://eus.codesigning.azure.net",
            "--artifact-account", "fixture-account", "--artifact-profile", "fixture-profile", "--signtool", Path.Combine(directory, "missing-signtool.exe")];
        var failedBundle = await Run(["bundle", package, "--launcher", launcher, "--output", output, .. unavailable]);
        check(failedBundle.ExitCode != 0 && File.ReadAllBytes(output).SequenceEqual(retained),
            "Missing signing tools fail CLI bundling without replacing a previous installer with unsigned bytes");

        var before = PackageSafety.HashFile(bundled);
        var failedSign = await Run(["sign", bundled, .. unavailable]);
        check(failedSign.ExitCode != 0 && PackageSafety.HashFile(bundled) == before,
            "Unavailable Windows signing leaves an existing bundled installer byte-for-byte unchanged");

        var conflicting = await Run(["signing-check", .. unavailable, "--certificate-thumbprint", new string('A', 40)]);
        check(conflicting.ExitCode != 0, "CLI rejects mixed Microsoft and purchased-certificate signing settings");
        check(!Directory.EnumerateFiles(directory).Any(path => Path.GetFileName(path).Contains("metadata", StringComparison.OrdinalIgnoreCase)),
            "CLI prerequisite failures leave no Microsoft metadata or credentials beside installers");
    }
}
