using HeirowInstaller.Core;

namespace HeirowInstaller.App;

internal static class SignedWizardBindingSmoke
{
    internal static async Task<IReadOnlyList<string>> RunAsync(string root)
    {
        using var own = PackageReader.Open(Environment.ProcessPath!);
        if (own.Manifest.Id != "heirowInstaller.wizard-fixture" || own.PublisherKeySha256 is not { } pin)
            throw new InvalidDataException("Signed wizard acceptance requires its own signed fixture footer.");
        var packets = Path.GetFullPath(Environment.GetEnvironmentVariable("HEIROWINSTALLER_WIZARD_BINDING_DIRECTORY") ?? "");
        var expected = Path.Combine(Path.GetDirectoryName(root)!, "signature-cases");
        if (!packets.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Signed fixture packets must remain in this test's own output directory.");
        PackageSafety.EnsureNoReparsePoints(packets);
        var checks = new List<string> { "The compiled fixture's own footer supplied its verified publisher pin" };
        foreach (var test in new[] { "newer-same-publisher", "wrong-product", "wrong-publisher" })
        {
            var run = Path.Combine(root, "footer-" + test);
            Directory.CreateDirectory(run);
            await File.WriteAllTextAsync(Path.Combine(run, ".isolated-wizard-fixture"), "heirowInstaller isolated wizard acceptance v1\n");
            var path = Path.Combine(packets, test + ".hinst");
            PackageSafety.EnsureNoReparsePoints(path);
            using var package = PackageReader.Open(path);
            await package.VerifyAsync();
            var options = new InstallOptions { TargetDirectory = Path.Combine(run, "installed"), StateRoot = Path.Combine(run, "state"), Isolated = true, RegisterProduct = false, Silent = true, RequireSignature = true };
            var engine = new InstallerEngine(); var plan = engine.Plan(package, options);
            if (test == "newer-same-publisher")
            {
                var result = await ElevatedInstallWorker.InstallAsync(path, plan, options,
                    (_, _) => throw new InvalidOperationException("A data-only signed fixture cannot request app closure."), null, run, CancellationToken.None);
                var product = InstallerEngine.ListInstalled(options.StateRoot).Single();
                if (result.Version != "1.0.1" || product.Id != own.Manifest.Id || product.PublisherKeySha256 != pin || !result.Success)
                    throw new InvalidDataException("The newer same-publisher result did not retain its footer binding.");
                await engine.UninstallAsync(product.Id, new() { StateRoot = options.StateRoot, Isolated = true, RegisterProduct = false });
                if (File.Exists(result.ReceiptPath)) throw new InvalidOperationException("The signed data fixture did not clean up normally.");
                checks.Add("The actual bundled worker accepted a newer signed version of its own product and publisher, then normal isolated uninstall removed its receipt");
            }
            else
            {
                var rejected = false;
                try { await ElevatedInstallWorker.InstallAsync(path, plan, options, (_, _) => throw new InvalidOperationException("A rejected package cannot request closure."), null, run, CancellationToken.None); }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected || Directory.Exists(options.StateRoot) || Directory.Exists(options.TargetDirectory))
                    throw new InvalidOperationException("A different-product/publisher package was not rejected before mutation.");
                checks.Add($"The actual bundled worker rejected {test} signed input before creating installation or state directories");
            }
        }
        return checks;
    }
}
