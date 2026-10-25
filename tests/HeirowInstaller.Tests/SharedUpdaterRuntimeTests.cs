using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;

internal sealed partial class AcceptanceSuite
{
    private async Task SharedUpdaterRuntimes()
    {
        var area = Area("shared-updater");
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "RawMsiUpdateProbe", "bin", configuration, "net10.0", "RawMsiUpdateProbe.exe"));
        True(File.Exists(fixture));
        var source = Path.Combine(area, "source"); Directory.CreateDirectory(source);
        File.Copy(fixture, Path.Combine(source, "heirowUpdater.exe")); // Compiled PE only; it is never executed by this test.
        File.WriteAllText(Path.Combine(source, "application.exe"), "non-executed application fixture");
        using var key = RSA.Create(2048); var keyPath = Path.Combine(area, "fixture.pem"); File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        string Package(string id, string version, string mode = "shared")
        {
            var manifest = Manifest(id, version); manifest.EntryPoint = "application.exe"; manifest.UpdateUrl = "https://updates.example.invalid/feed.json";
            manifest.Features = [new() { Id = "updates", Default = true }];
            manifest.UpdateCompanion = new() { RuntimeMode = mode, CookieEnvironmentVariable = "HEIROWUPDATER_COOKIE", AuthApplication = "example" };
            manifest.Payload = [new() { Path = "application.exe" }, new() { Path = "heirowUpdater.exe", Feature = "updates" }];
            var path = Path.Combine(area, id + "-" + version + ".hinst"); var json = path + ".json";
            File.WriteAllText(json, JsonSerializer.Serialize(manifest, InstallerJson.Options));
            PackageBuilder.Build(json, source, path, new() { SigningKeyPath = keyPath }); return path;
        }
        var engine = new InstallerEngine(); var targetA = Path.Combine(area, "app-a"); var targetB = Path.Combine(area, "app-b");
        var optionsA = Options(area, targetA); var optionsB = Options(area, targetB);
        using var first = PackageReader.Open(Package("test.shared-a", "1"));
        using var second = PackageReader.Open(Package("test.shared-b", "1"));
        await engine.InstallAsync(first, optionsA); await engine.InstallAsync(second, optionsB);
        var profileA = Path.Combine(targetA, "heirowUpdater.json"); var profileB = Path.Combine(targetB, "heirowUpdater.json");
        var configA = ApplicationUpdater.ReadConfiguration(profileA); var configB = ApplicationUpdater.ReadConfiguration(profileB);
        True(configA.SharedRuntime is not null && configB.SharedRuntime is not null, "Both app profiles must bind the shared runtime.");
        True(!File.Exists(Path.Combine(targetA, "heirowUpdater.exe")) && !File.Exists(Path.Combine(targetB, "heirowUpdater.exe")), "Shared installs must not copy the updater into either app root.");
        var executable = SharedUpdaterRuntime.Resolve(configA); Equal(executable, SharedUpdaterRuntime.Resolve(configB));
        Equal(1, Directory.EnumerateFiles(SharedUpdaterRuntime.Root(configA), "heirowUpdater.exe", SearchOption.AllDirectories).Count());
        Equal(2, Directory.EnumerateFiles(Path.Combine(SharedUpdaterRuntime.Root(configA), "references"), "*.json").Count());
        Equal(PackageSafety.HashFile(fixture), PackageSafety.HashFile(executable));
        True(File.Exists(SharedUpdaterRuntime.ReferencePath(configA))); True(File.Exists(SharedUpdaterRuntime.ReferencePath(configB)));
        await LongPathSharedRuntime(first, configA, fixture);
        Equal(SharedUpdaterRuntime.ApplicationStateDirectory(configA), ApplicationUpdater.StateDirectory(configA));
        True(SharedUpdaterRuntime.ApplicationStateDirectory(configA) != SharedUpdaterRuntime.ApplicationStateDirectory(configB));

        var originalProfile = File.ReadAllText(profileA);
        configA.CredentialOrigin = "https://unrelated.example.invalid";
        File.WriteAllText(profileA, JsonSerializer.Serialize(configA, InstallerJson.Options));
        await Throws<InvalidDataException>(() => Task.Run(() => ApplicationUpdater.ReadConfiguration(profileA)));
        File.WriteAllText(profileA, originalProfile); configA = ApplicationUpdater.ReadConfiguration(profileA);
        configA.CookieEnvironmentVariable = "UNRELATED_SESSION";
        File.WriteAllText(profileA, JsonSerializer.Serialize(configA, InstallerJson.Options));
        await Throws<InvalidDataException>(() => Task.Run(() => ApplicationUpdater.ReadConfiguration(profileA)));
        File.WriteAllText(profileA, originalProfile); configA = ApplicationUpdater.ReadConfiguration(profileA);
        configA.SharedRuntime!.Sha256 = new string('a', 64);
        File.WriteAllText(profileA, JsonSerializer.Serialize(configA, InstallerJson.Options));
        await Throws<CryptographicException>(() => Task.Run(() => ApplicationUpdater.ReadConfiguration(profileA)));
        File.WriteAllText(profileA, originalProfile); configA = ApplicationUpdater.ReadConfiguration(profileA);
        var recovered = SharedUpdaterRuntime.RecoverReference(configA, first.Manifest);
        Equal(configA.SharedRuntime!.Sha256, recovered!.Sha256);
        configA.SharedRuntime = null;
        File.WriteAllText(profileA, JsonSerializer.Serialize(configA, InstallerJson.Options));
        await Throws<InvalidDataException>(() => Task.Run(() => ApplicationUpdater.ReadConfiguration(profileA)));
        File.WriteAllText(profileA, originalProfile); configA = ApplicationUpdater.ReadConfiguration(profileA);
        var originalRuntime = File.ReadAllBytes(executable);
        File.AppendAllText(executable, "tampering");
        await Throws<CryptographicException>(() => Task.Run(() => SharedUpdaterRuntime.Resolve(configB)));
        File.WriteAllBytes(executable, originalRuntime);

        if (OperatingSystem.IsWindows())
        {
            using var otherApplication = new ApplicationStopMonitor(configB, resetForOwner: true);
            var ownApplication = new ApplicationStopMonitor(configA, resetForOwner: true);
            var ownExit = Task.Run(async () =>
            {
                try { await Task.Delay(Timeout.Infinite, ownApplication.Token); }
                catch (OperationCanceledException) { }
                finally { ownApplication.Dispose(); }
            });
            var missingProfileIdentity = JsonSerializer.Deserialize<UpdateCompanionConfiguration>(originalProfile, InstallerJson.Options)!;
            missingProfileIdentity.SharedRuntime = null;
            File.Delete(profileA);
            True(await UpdateCompanionControl.StopAsync(missingProfileIdentity)); await ownExit.WaitAsync(TimeSpan.FromSeconds(5));
            File.WriteAllText(profileA, originalProfile);
            True(!otherApplication.StopRequested && !otherApplication.Token.IsCancellationRequested);
            Equal(executable, SharedUpdaterRuntime.Resolve(configB));
        }
        optionsA.Features = [];
        await engine.InstallAsync(first, optionsA);
        True(!File.Exists(SharedUpdaterRuntime.ReferencePath(configA)), "Disabling updates must release only the first app's reference.");
        True(File.Exists(SharedUpdaterRuntime.ReferencePath(configB)) && File.Exists(executable));
        await engine.UninstallAsync(second.Manifest.Id, optionsB);
        True(!File.Exists(SharedUpdaterRuntime.ReferencePath(configB)), "Uninstall must release the second app's reference."); True(File.Exists(executable));

        var legacyRoot = Path.Combine(area, "legacy-app"); var legacyOptions = Options(area, legacyRoot);
        using var legacy = PackageReader.Open(Package("test.shared-migration", "1", "app-local"));
        await engine.InstallAsync(legacy, legacyOptions);
        True(File.Exists(Path.Combine(legacyRoot, "heirowUpdater.exe")));
        using var migrated = PackageReader.Open(Package("test.shared-migration", "2"));
        await engine.InstallAsync(migrated, legacyOptions);
        True(!File.Exists(Path.Combine(legacyRoot, "heirowUpdater.exe")), "Migration must remove only the previous owned app-local updater.");
        var migratedConfig = ApplicationUpdater.ReadConfiguration(Path.Combine(legacyRoot, "heirowUpdater.json"));
        Equal(executable, SharedUpdaterRuntime.Resolve(migratedConfig));
        await engine.UninstallAsync(migrated.Manifest.Id, legacyOptions);
        True(File.Exists(executable));
        True(!JsonSerializer.Serialize(new UpdateCompanionDefinition(), InstallerJson.Options).Contains("runtimeMode", StringComparison.Ordinal));
    }

    private async Task LongPathSharedRuntime(PackageReader trustedPackage, UpdateCompanionConfiguration original, string fixture)
    {
        if (!OperatingSystem.IsWindows()) return;
        var area = Path.Combine(Area("long-runtime-version"), new string('a', 80), new string('b', 80));
        var source = Path.Combine(area, "source"); Directory.CreateDirectory(source);
        var longSource = Path.Combine(source, "heirowUpdater.exe"); File.Copy(fixture, longSource);
        True(longSource.Length > 260, "The PE metadata source must exceed MAX_PATH.");
        var identity = new InstalledProduct { Id = trustedPackage.Manifest.Id, Scope = "user", Isolated = true,
            InstallDirectory = Path.Combine(area, "app"), PublisherKeySha256 = original.PublisherKeySha256 };
        var state = Path.Combine(area, "state");
        var reference = SharedUpdaterRuntime.Provision(trustedPackage.Manifest, identity, source, state);
        Equal(original.SharedRuntime!.Version, reference.Version);
        var profile = JsonSerializer.Deserialize<UpdateCompanionConfiguration>(JsonSerializer.Serialize(original, InstallerJson.Options), InstallerJson.Options)!;
        profile.InstallDirectory = identity.InstallDirectory; profile.StateRoot = state; profile.SharedRuntime = reference;
        var resolved = SharedUpdaterRuntime.Resolve(profile);
        True(resolved.Length > 260, "The immutable runtime path must exceed MAX_PATH.");
        Equal(PackageSafety.HashFile(fixture), PackageSafety.HashFile(resolved));
        Equal(resolved, SharedUpdaterRuntime.Resolve(profile));
        var binding = SharedUpdaterRuntime.GetReferenceBinding(trustedPackage.Manifest, identity, state, enabled: true);
        Directory.CreateDirectory(Path.GetDirectoryName(binding.Path)!); File.WriteAllBytes(binding.Path, binding.Content!);
        var recovered = SharedUpdaterRuntime.RecoverReference(profile, trustedPackage.Manifest)!;
        Equal(reference.Version, recovered.Version); Equal(reference.Sha256, recovered.Sha256); Equal(reference.Size, recovered.Size);

        var wrongVersion = reference.Version == "0.0.0.0" ? "1.0.0.0" : "0.0.0.0";
        var mismatched = Path.Combine(SharedUpdaterRuntime.Root(profile), wrongVersion + "-" + reference.Sha256.ToLowerInvariant(), "heirowUpdater.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(mismatched)!); File.Copy(fixture, mismatched);
        profile.SharedRuntime = new() { Version = wrongVersion, Sha256 = reference.Sha256, Size = reference.Size };
        await Throws<InvalidDataException>(() => Task.Run(() => SharedUpdaterRuntime.Resolve(profile)));
        await Throws<InvalidDataException>(() => Task.Run(() => SharedUpdaterRuntime.RecoverReference(profile, trustedPackage.Manifest)));
    }
}
