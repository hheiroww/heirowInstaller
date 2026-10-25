using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
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
            if (OperatingSystem.IsWindows())
            {
                var longArea = Path.Combine(area, new string('a', 80), new string('b', 80));
                var longRoot = Path.Combine(longArea, "app"); Directory.CreateDirectory(longRoot);
                var longProfile = new UpdateCompanionConfiguration { ProductId = profile.ProductId, InstallDirectory = longRoot,
                    Scope = "user", Isolated = true, StateRoot = Path.Combine(longArea, "state"), SharedRuntime = reference };
                var longRuntime = Path.Combine(SharedUpdaterRuntime.Root(longProfile), reference.Version + "-" + reference.Sha256.ToLowerInvariant(), "heirowUpdater.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(longRuntime)!); File.Copy(executable, longRuntime);
                True(longRuntime.Length > 260, "Each compiled bootstrap must verify real PE metadata beyond MAX_PATH.");
                Equal(reference.Sha256, PackageSafety.HashFile(longRuntime));
                var longConfiguration = Path.Combine(longRoot, "heirowUpdater.json");
                File.WriteAllText(longConfiguration, JsonSerializer.Serialize(longProfile, InstallerJson.Options));
                // Invoke the compiled dependency-free resolver without executing the long-path PE or opening any UI.
                var resolver = Assembly.LoadFrom(Path.Combine(source, name + ".dll")).GetType("SocketJack.HeirowUpdaterBootstrap")!
                    .GetMethod("Resolve", BindingFlags.NonPublic | BindingFlags.Static)!;
                Equal(longRuntime, (string)resolver.Invoke(null, [longRoot, longConfiguration])!);
                longProfile.SharedRuntime = new() { Version = reference.Version, Sha256 = reference.Sha256, Size = reference.Size + 1 };
                File.WriteAllText(longConfiguration, JsonSerializer.Serialize(longProfile, InstallerJson.Options));
                await Throws<CryptographicException>(() => Task.Run(() => ResolveWithUnwrappedFailure(resolver, longRoot, longConfiguration)));
                longProfile.SharedRuntime = reference;
                File.WriteAllText(longConfiguration, JsonSerializer.Serialize(longProfile, InstallerJson.Options));
                var originalBytes = File.ReadAllBytes(longRuntime);
                originalBytes[^1] ^= 1; File.WriteAllBytes(longRuntime, originalBytes);
                await Throws<CryptographicException>(() => Task.Run(() => ResolveWithUnwrappedFailure(resolver, longRoot, longConfiguration)));
                var wrongVersion = reference.Version == "0.0.0.0" ? "1.0.0.0" : "0.0.0.0";
                var mismatched = Path.Combine(SharedUpdaterRuntime.Root(longProfile), wrongVersion + "-" + reference.Sha256.ToLowerInvariant(), "heirowUpdater.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(mismatched)!); File.Copy(executable, mismatched);
                longProfile.SharedRuntime = new() { Version = wrongVersion, Sha256 = reference.Sha256, Size = reference.Size };
                File.WriteAllText(longConfiguration, JsonSerializer.Serialize(longProfile, InstallerJson.Options));
                await Throws<CryptographicException>(() => Task.Run(() => ResolveWithUnwrappedFailure(resolver, longRoot, longConfiguration)));
            }
        }
    }

    private static object? ResolveWithUnwrappedFailure(MethodInfo resolver, string root, string configuration)
    {
        try { return resolver.Invoke(null, [root, configuration]); }
        catch (TargetInvocationException failure) when (failure.InnerException is CryptographicException)
        { throw failure.InnerException; }
    }
}
