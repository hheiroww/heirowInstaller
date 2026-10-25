using System.Diagnostics;
using System.Text.Json;
using HeirowInstaller.Core;

internal sealed partial class AcceptanceSuite
{
    private async Task SharedBootstrapLanguages()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        foreach (var name in new[] { "BootstrapCSharpProbe", "BootstrapVisualBasicProbe" })
        {
            var area = Area(name); var root = Path.Combine(area, "app"); Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, ".bootstrap-probe"), "heirowInstaller bootstrap acceptance\n");
            var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", name, "bin", configuration, "net10.0"));
            var executable = Path.Combine(source, name + ".exe");
            var reference = new SharedUpdaterRuntimeReference { Version = FileVersionInfo.GetVersionInfo(executable).FileVersion!, Sha256 = PackageSafety.HashFile(executable), Size = new FileInfo(executable).Length };
            var profile = new UpdateCompanionConfiguration { ProductId = "test." + name, InstallDirectory = root, Scope = "user", Isolated = true, StateRoot = Path.Combine(area, "state"), SharedRuntime = reference };
            var runtime = Path.Combine(SharedUpdaterRuntime.Root(profile), reference.Version + "-" + reference.Sha256, "heirowUpdater.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(runtime)!);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly).Where(x => Path.GetExtension(x) is ".dll" or ".json")) File.Copy(file, Path.Combine(Path.GetDirectoryName(runtime)!, Path.GetFileName(file)));
            File.Copy(executable, runtime);
            File.WriteAllText(Path.Combine(root, "heirowUpdater.json"), JsonSerializer.Serialize(profile, InstallerJson.Options));
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = source, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--capture-root"); start.ArgumentList.Add(root);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start isolated bootstrap fixture.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)); _ = await stdout; _ = await stderr;
            Equal(0, process.ExitCode);
            True(!File.Exists(Path.Combine(root, "heirowUpdater.exe")), "Bootstrap must use the shared runtime outside the application root.");
            var contents = File.ReadAllText(Path.Combine(root, "child.json"));
            True(!contents.Contains("fixture-bootstrap-native", StringComparison.Ordinal) && !contents.Contains("fixture-inherited-browser", StringComparison.Ordinal));
        }
    }
}
