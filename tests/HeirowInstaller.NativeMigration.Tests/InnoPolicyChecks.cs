using System.Text.Json;
using HeirowInstaller.Core;

// Pure policy fixtures only: no registry, process, installed product or generated Inno log is created.
internal static class InnoPolicyChecks
{
    internal static Task Run()
    {
        var (original, installed, baseline) = Fixture();
        Check(InnoMigrationPolicy.RequireOriginalMedia(original, installed, baseline).Count == 3, "The complete policy fixture must bind all three declared entries; it is not signature or runtime acceptance.");
        var serialized = JsonSerializer.Serialize(new PackageManifest(), InstallerJson.Options);
        Check(!serialized.Contains("innoUninstallMedia", StringComparison.Ordinal) && !serialized.Contains("\"inno\"", StringComparison.Ordinal), "Unset fields must not change legacy JSON.");

        RejectOriginal(value => value.LegacyExecutable!.InnoUninstallMedia = null);
        RejectOriginal(value => value.LegacyExecutable!.InnoUninstallMedia!.Provenance = "current-live-file-hashes");
        RejectOriginal(value => value.LegacyExecutable!.InnoUninstallMedia!.Provenance = "repair-succeeded");
        RejectOriginal(value => value.Payload.RemoveAll(file => file.Path.EndsWith(".dat", StringComparison.Ordinal)));
        RejectOriginal(value => value.Payload.Add(NativeMigrationValidation.Clone(value.Payload[1])));
        RejectOriginal(value => value.Payload[1].Feature = "updates");
        RejectOriginal(value => value.Payload[1].TargetRoot = "another-product");
        RejectOriginal(value => value.Payload[1].Sha256 = "not-a-digest");
        RejectOriginal(value => value.LegacyExecutable!.InnoUninstallMedia!.InstallDirectory = @"C:\Example\OtherProduct");
        RejectOriginal(value => value.LegacyExecutable!.InnoUninstallMedia!.DataPath = "../unins000.dat");
        RejectOriginal(value => value.LegacyExecutable!.InnoUninstallMedia!.MessagesPath = "generated/unins001.msg");
        RejectOriginal(value => value.LegacyExecutable!.InnoUninstallMedia!.MessagesPath = null);
        RejectOriginal(value => value.Payload.Add(File("generated/unins001.dat")));
        RejectOriginal(value => value.LegacyExecutable!.UninstallExecutable = "{InstallDir}/foreign.exe");
        RejectOriginal(value => value.LegacyExecutable!.UninstallArguments.Clear());
        RejectOriginal(value => value.LegacyExecutable!.MaintenanceRegistration!.UninstallKey += ".foreign");

        foreach (var change in new Action<NativeMigrationBaselineDefinition>[]
        {
            value => value.Inno = null,
            value => value.Inno!.AppId = "example.foreign",
            value => value.Inno!.Registration.Hive = "machine",
            value => value.Inno!.Registration.UninstallKey += ".foreign",
            value => value.Inno!.Registration.BundleUpgradeCode = "{11111111-1111-1111-1111-111111111111}",
            value => value.MsiProductCode = "{11111111-1111-1111-1111-111111111111}",
            value => value.BurnMsiProductCodes = [],
            value => value.Scope = "machine",
            value => value.FromVersion = "0.5"
        })
        {
            var changed = NativeMigrationValidation.Clone(baseline); change(changed);
            Throws<InvalidDataException>(() => InnoMigrationPolicy.RequireOriginalMedia(original, installed, changed));
        }
        foreach (var backend in new[] { "msi", "burn", "inno", "inno-owned-footprint" })
        {
            var changed = NativeMigrationValidation.Clone(baseline); changed.Backend = backend;
            Throws<InvalidDataException>(() => NativeMigrationValidation.ValidateDefinition(new() { FormatVersion = 3, NativeMigration = new() { Baselines = [changed] } }));
        }
        var unsupportedTarget = new PackageManifest { FormatVersion = 3, Id = original.Id, Name = "Example Inno migration", Publisher = "Example", Version = "2.0",
            EntryPoint = "app.exe", Payload = [File("app.exe")], NativeMigration = new() { Baselines = [NativeMigrationValidation.Clone(baseline)] } };
        Throws<InvalidDataException>(() => PackageSafety.ValidateManifest(unsupportedTarget, requireHashes: true), "Inno native migration is not implemented");
        var windows = new WindowsNativeMigrationBackend();
        Throws<NotSupportedException>(() => windows.ObserveDeclared(baseline, isolated: false));
        Throws<NotSupportedException>(InnoMigrationPolicy.RequireExecutionImplementation);
        return Task.CompletedTask;

        void RejectOriginal(Action<PackageManifest> change)
        {
            var changed = NativeMigrationValidation.Clone(original); change(changed);
            Throws<InvalidDataException>(() => InnoMigrationPolicy.RequireOriginalMedia(changed, installed, baseline));
        }
    }

    private static (PackageManifest, InstalledProduct, NativeMigrationBaselineDefinition) Fixture()
    {
        const string appId = "example.inno", root = @"C:\Example\InnoProduct";
        var registration = new BackendRegistrationDefinition { Hive = "user", View = "registry64", UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\example.inno_is1" };
        var original = new PackageManifest { Id = "example.inno-product", Version = "1.0", Scope = "user",
            Payload = [File("setup.exe"), File("generated/unins000.exe"), File("generated/unins000.dat"), File("generated/unins000.msg")],
            LegacyExecutable = new() { PayloadPath = "setup.exe", UninstallExecutable = "{InstallDir}/unins000.exe", UninstallArguments = ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"],
                MaintenanceRegistration = registration,
                InnoUninstallMedia = new() { Provenance = InnoMigrationPolicy.SignedPayloadProvenance, AppId = appId, SetupVersion = "6.6.0", InstallDirectory = root,
                    ExecutablePath = "generated/unins000.exe", DataPath = "generated/unins000.dat", MessagesPath = "generated/unins000.msg" } } };
        return (original, new() { Id = original.Id, Version = original.Version, Scope = "user", InstallDirectory = root, IsExecutable = true },
            new() { Backend = "inno", Scope = "user", FromVersion = "1.0", FromManifestSha256 = new string('a', 64), Inno = new() { AppId = appId, Registration = NativeMigrationValidation.Clone(registration) } });
    }

    private static PayloadFile File(string path) => new() { Path = path, Size = 64, Sha256 = new string('a', 64) };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action, string? message = null) where T : Exception
    { try { action(); } catch (T error) { if (message is not null && !error.Message.Contains(message, StringComparison.Ordinal)) throw; return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
