using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HeirowInstaller.Core;

var root = Path.Combine(Path.GetTempPath(), "heirowinstaller-portable-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
void Reject<T>(Action action) where T : Exception
{ try { action(); } catch (T) { checks++; return; } throw new Exception("Expected " + typeof(T).Name); }
var previousXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
try
{
    var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
    var entry = OperatingSystem.IsWindows() ? "app.exe" : "app";
    File.WriteAllText(Path.Combine(source, entry), "#!/bin/sh\nif [ \"$1\" = wait ]; then sleep 30; else printf 'portable app'; fi\n");
    File.WriteAllText(Path.Combine(source, "data.txt"), "first");
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(source, entry), (UnixFileMode)493);
    using var rsa = RSA.Create(2048);
    var key = Path.Combine(root, "key.pem"); File.WriteAllText(key, rsa.ExportRSAPrivateKeyPem());
    var manifest = new PackageManifest { Id = "portable.fixture." + Guid.NewGuid().ToString("N"), Name = "Portable fixture", Publisher = "Fixture",
        Version = "1.0.0", TargetPlatform = InstallerPlatform.Current, TargetRuntime = InstallerPlatform.RuntimeIdentifier, EntryPoint = entry };
    manifest.LegalDocuments = LegalFixture.For(manifest.Id);
    var manifestPath = Path.Combine(root, "manifest.json");
    string Build(string version)
    {
        manifest.Version = version;
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options));
        var path = Path.Combine(root, version + ".hinst");
        PackageBuilder.Build(manifestPath, source, path, new() { SigningKeyPath = key }); return path;
    }
    var first = Build("1.0.0");
    using var package = PackageReader.Open(first);
    Check(package.Manifest.FormatVersion == 4, "Portable metadata must require format 4.");
    PackagePlatform.RequireCurrentHost(package.Manifest); checks++;
    var wrong = new PackageManifest { TargetPlatform = InstallerPlatform.Current == "windows" ? "linux" : "windows" };
    Reject<PlatformNotSupportedException>(() => PackagePlatform.RequireCurrentHost(wrong));
    wrong.TargetPlatform = InstallerPlatform.Current; wrong.TargetRuntime = "invalid";
    Reject<PlatformNotSupportedException>(() => PackagePlatform.RequireCurrentHost(wrong));
    Check(Path.IsPathFullyQualified(InstallerEngine.DefaultStateRoot()), "State root must be absolute.");
    Check(Path.IsPathFullyQualified(InstallerPlatform.DefaultInstallDirectory(manifest.Id, "user")), "Install root must be absolute.");
    var engine = new InstallerEngine();
    var options = new InstallOptions { StateRoot = Path.Combine(root, "state"), TargetDirectory = Path.Combine(root, "installed"),
        Isolated = true, RequireSignature = true, TrustedPublisherKeySha256 = package.PublisherKeySha256, RegisterProduct = false };
    await engine.InstallAsync(package, options); checks++;
    var installedEntry = Path.Combine(options.TargetDirectory, entry);
    Check(File.ReadAllText(installedEntry).StartsWith("#!/bin/sh"), "Verified payload must be installed.");
    if (!OperatingSystem.IsWindows())
    {
        Check((int)File.GetUnixFileMode(installedEntry) == 493, "Signed executable permissions must survive installation.");
        using var app = Process.Start(new ProcessStartInfo(installedEntry) { UseShellExecute = false, RedirectStandardOutput = true })!;
        Check(await app.StandardOutput.ReadToEndAsync() == "portable app", "Installed app must actually execute.");
        await app.WaitForExitAsync();
        File.SetUnixFileMode(installedEntry, (UnixFileMode)420);
    }
    File.WriteAllText(Path.Combine(options.TargetDirectory, "data.txt"), "damaged");
    await engine.RepairAsync(manifest.Id, options); checks++;
    Check(File.ReadAllText(Path.Combine(options.TargetDirectory, "data.txt")) == "first", "Repair must restore payload bytes.");
    if (!OperatingSystem.IsWindows()) Check((int)File.GetUnixFileMode(installedEntry) == 493, "Repair must restore executable permissions.");
    File.WriteAllText(Path.Combine(source, "data.txt"), "second");
    var second = Build("2.0.0");
    var update = Path.Combine(root, "update.hupd");
    await IncrementalUpdateBuilder.BuildAsync(first, second, update, new() { SigningKeyPath = key });
    using (var delta = IncrementalUpdateReader.Open(update))
    {
        var installed = InstallerEngine.ListInstalled(options.StateRoot).Single();
        var reconstructed = await delta.ReconstructAsync(installed, Path.Combine(root, "reconstructed.hinst"));
        using var next = PackageReader.Open(reconstructed);
        await engine.InstallAsync(next, options); checks++;
    }
    Check(File.ReadAllText(Path.Combine(options.TargetDirectory, "data.txt")) == "second", "Signed file updates must work on this host.");
    if (!OperatingSystem.IsWindows()) Check((int)File.GetUnixFileMode(installedEntry) == 493, "Unchanged files keep executable permissions through updates.");
    File.WriteAllText(Path.Combine(options.TargetDirectory, "user.txt"), "keep");
    await engine.UninstallAsync(manifest.Id, options); checks++;
    Check(!File.Exists(installedEntry) && File.Exists(Path.Combine(options.TargetDirectory, "user.txt")), "Uninstall must preserve unowned data.");
    Check(InstallerEngine.ListInstalled(options.StateRoot).Count == 0, "Uninstall must remove the receipt.");
    if (!OperatingSystem.IsWindows())
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(root, "xdg"));
        manifest.Shortcuts = [new() { Name = "Fixture $ with spaces", Target = entry, ArgumentList = ["literal $ and %"] }];
        var shortcutPackage = Build("3.0.0");
        using var shortcutReader = PackageReader.Open(shortcutPackage);
        options.Isolated = false;
        await engine.InstallAsync(shortcutReader, options);
        var product = InstallerEngine.ListInstalled(options.StateRoot).Single();
        Check(product.Modules.Count > 0 && product.Modules.All(x => File.Exists(x.Key)), "Native Linux/macOS launchers must be created.");
        var paths = product.Modules.Select(x => x.Key).ToArray();
        await engine.UninstallAsync(manifest.Id, options);
        Check(paths.All(x => !File.Exists(x)), "Uninstall must remove owned native launchers.");
        var link = Path.Combine(root, "link"); Directory.CreateSymbolicLink(link, source);
        Reject<IOException>(() => PackageSafety.UnderRoot(link, entry));
        Directory.Delete(link);
    }
    Console.WriteLine($"Passed {checks} cross-platform checks on {InstallerPlatform.RuntimeIdentifier}.");
}
finally
{
    Environment.SetEnvironmentVariable("XDG_DATA_HOME", previousXdg);
    if (Directory.Exists(root)) Directory.Delete(root, true);
}
