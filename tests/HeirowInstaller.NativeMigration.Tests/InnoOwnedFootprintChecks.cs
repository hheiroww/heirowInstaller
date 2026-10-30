using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;

// Synthetic temp files and in-memory registry/link metadata only. No real Inno setup, DAT, key, user app or service runs.
internal static class InnoOwnedFootprintChecks
{
    internal static async Task Run()
    {
        await SuccessAndNativeRollback();
        await FaultBoundaries();
        await InterruptedPartialRetirement();
        await TamperingAndConflicts();
        await NativeCommitNeverRevivesLegacy();
        await LateNativeReceiptRecovery();
        await WrapperFeatureAndVisibility();
        await CancellationAndLocks();
        ReceiptBackendTampering();
        await HistoricalOptionalResources();
        await CommittedCleanupBoundaries();
        await CompletedMaintenanceCleanup();
        PolicyAndDraft();
    }
    private static async Task SuccessAndNativeRollback()
    {
        using var f = new OwnedFixture();
        using (var target = f.Target()) await f.Engine.InstallAsync(target, f.Options);
        var installed = InstallerEngine.ListInstalled(f.State).Single();
        Check(!installed.IsExecutable && !installed.IsMsi && installed.BackendRegistrations.Count == 0, "Native receipt must detach the old backend visibility identity.");
        Check(File.ReadAllText(f.AppFile) == "native application" && !File.Exists(f.Helper), "The complete old program footprint must retire before native ownership.");
        Check(!f.Platform.Inno.Exists && f.Platform.ExternalExecutions == 0, "No external setup or generated uninstaller may run.");
        Check(File.ReadAllText(f.Settings) == "preserved user settings", "Settings must survive retirement.");
        using var rollback = new OwnedFixture();
        var failure = new CallbackProgress(value => { if (value.Phase == "install") throw new IOException("Synthetic native-copy failure."); });
        using (var target = rollback.Target()) await Throws<IOException>(() => rollback.Engine.InstallAsync(target, rollback.Options, failure));
        rollback.AssertOriginal();
    }
    private static async Task FaultBoundaries()
    {
        foreach (var point in new[] { "snapshot-marker", "backup-intent:app:app.exe", "backup-created:app:app.exe", "backed-up:app:app.exe",
            "backups-complete", "remove-intent:app:app.exe", "removed:app:app.exe", "registration-intent", "registration-removed", "retirement-marker" })
        {
            using var f = new OwnedFixture(); var transaction = f.Open(); var once = false;
            transaction.Fault = stage => { if (!once && stage == point) { once = true; throw new IOException("Synthetic retirement interruption."); } };
            await Throws<IOException>(() => transaction.RetireAsync(null, default));
            Check(once, "The intended retirement boundary must be reached: " + point);
            f.AssertOriginal();
            var resumed = f.Open(); await resumed.RetireAsync(null, default); resumed.ConfirmRetired();
            await resumed.RestoreAsync(null, default); f.AssertOriginal();
        }
        foreach (var point in new[] { "restore-started", "restore-created:app:app.exe", "restored:app:app.exe", "registration-restored", "sdk-metadata-restored", "restore-marker" })
        {
            using var f = new OwnedFixture(); var transaction = f.Open(); await transaction.RetireAsync(null, default);
            var once = false; transaction.Fault = stage => { if (!once && stage == point) { once = true; throw new IOException("Synthetic restoration interruption."); } };
            await Throws<IOException>(() => transaction.RestoreAsync(null, default));
            Check(once, "The intended restoration boundary must be reached: " + point);
            var resumed = f.Open(); await resumed.RestoreAsync(null, default); f.AssertOriginal();
        }
    }
    private static async Task InterruptedPartialRetirement()
    {
        using var f = new OwnedFixture(); var transaction = f.Open();
        transaction.Fault = stage =>
        {
            if (stage == "removed:app:app.exe") { f.Platform.RefuseRestoration = true; throw new IOException("Synthetic loss after one removal."); }
        };
        await Throws<AggregateException>(() => transaction.RetireAsync(null, default));
        Check(transaction.NeedsRestoration && !File.Exists(f.AppFile) && f.Platform.Inno.Exists, "Partial retirement must remain recoverable while ARP still exists.");
        f.Platform.RefuseRestoration = false;
        var resumed = f.Open(); await resumed.RestoreAsync(null, default); f.AssertOriginal();
        using var partialKey = new OwnedFixture(); var keyTransaction = partialKey.Open();
        partialKey.Platform.FailMidRegistryRemoval = true;
        await Throws<AggregateException>(() => keyTransaction.RetireAsync(null, default));
        Check(keyTransaction.NeedsRestoration, "A partially removed typed key is not retirement completion.");
        partialKey.Platform.FailMidRegistryRemoval = partialKey.Platform.RefuseRestoration = false;
        await partialKey.Open().RestoreAsync(null, default); partialKey.AssertOriginal();
        using var missingKey = new OwnedFixture();
        missingKey.Platform.Inno = new();
        await Throws<NativeMigrationRepairRequiredException>(() => missingKey.Open().RetireAsync(null, default));
        Check(File.Exists(missingKey.AppFile), "An absent key alone cannot authorize removal.");
    }
    private static async Task TamperingAndConflicts()
    {
        using var f = new OwnedFixture(); var transaction = f.Open(); await transaction.RetireAsync(null, default);
        var ledger = Directory.GetFiles(f.OwnedDirectory, "ledger.bin").Single();
        var bytes = File.ReadAllBytes(ledger); bytes[^1] ^= 1; File.WriteAllBytes(ledger, bytes);
        Throws<CryptographicException>(() => f.Open());
        using var backup = new OwnedFixture(); var backed = backup.Open(); await backed.RetireAsync(null, default);
        var copy = Directory.GetFiles(Path.Combine(backup.OwnedDirectory, "backups")).First(); File.AppendAllText(copy, "tampered");
        await Throws<NativeMigrationRepairRequiredException>(() => backed.RestoreAsync(null, default));
        using var conflict = new OwnedFixture(); var retired = conflict.Open(); await retired.RetireAsync(null, default);
        File.WriteAllText(conflict.AppFile, "foreign replacement");
        await Throws<IOException>(() => retired.RestoreAsync(null, default));
        Check(File.ReadAllText(conflict.AppFile) == "foreign replacement" && !conflict.Platform.Inno.Exists, "A conflict must preserve its bytes and cannot advertise a restored backend.");
        using var link = new OwnedFixture();
        File.WriteAllText(link.Link, JsonSerializer.Serialize(new InnoOwnedLinkInfo(link.AppFile, "--foreign", link.App, link.AppFile, 0)));
        await Throws<NativeMigrationRepairRequiredException>(() => link.Open().RetireAsync(null, default));
        link.AssertProgramAndSettings();
        using var unknown = new OwnedFixture(); File.WriteAllText(Path.Combine(unknown.App, "unknown-old-program.dll"), "unexplained");
        await Throws<NativeMigrationRepairRequiredException>(() => unknown.Open().RetireAsync(null, default));
        unknown.AssertOriginal();
        using var changed = new OwnedFixture(); File.WriteAllText(changed.Helper, "modified legacy program");
        await Throws<NativeMigrationRepairRequiredException>(() => changed.Open().RetireAsync(null, default));
        Check(changed.Platform.Inno.Exists && File.Exists(changed.AppFile), "Unknown or changed ownership refuses before mutation.");
    }
    private static async Task NativeCommitNeverRevivesLegacy()
    {
        using var f = new OwnedFixture(); var transaction = f.Open(); await transaction.RetireAsync(null, default);
        transaction.PlanNativeMetadata(null); File.WriteAllText(f.AppFile, "native application");
        transaction.Fault = stage => { if (stage == "native-commit-marker") throw new IOException("Synthetic late native marker interruption."); };
        Throws<IOException>(transaction.MarkNativeCommitted);
        var resumed = f.Open(); Check(resumed.NativeCommitted, "The authenticated native marker must survive a late failure.");
        await Throws<NativeMigrationRepairRequiredException>(() => resumed.RestoreAsync(null, default));
        Check(File.ReadAllText(f.AppFile) == "native application" && !f.Platform.Inno.Exists, "A committed native receipt must never revive old bytes or registration.");
        using var metadata = new OwnedFixture(); var metaTransaction = metadata.Open(); await metaTransaction.RetireAsync(null, default);
        var after = new Dictionary<string, object> { ["HeirowInstallerProductId"] = OwnedFixture.Id, ["DisplayVersion"] = "2.0" };
        metaTransaction.PlanNativeMetadata(after); metadata.Platform.Sdk = InnoOwnedRegistry.FromValues(after);
        metaTransaction.RecoverNativeMetadata(); Check(!metadata.Platform.Sdk.Exists, "Recorded native SDK metadata must restore its absent before-image.");
        metadata.Platform.Sdk = InnoOwnedRegistry.FromValues(new Dictionary<string, object> { ["foreign"] = "preserve" });
        Throws<IOException>(metaTransaction.RecoverNativeMetadata);
        Check(metadata.Platform.Sdk.Values.ContainsKey("foreign"), "Unknown SDK metadata must not be swept.");
    }
    private static void PolicyAndDraft()
    {
        using var f = new OwnedFixture();
        foreach (var change in new Action<InnoOwnedFootprintCatalog>[] {
            c => c.Authority = "", c => c.Coverage = "", c => c.Lineage.Clear(), c => c.ReviewSha256 = "",
            c => c.UnsupportedBehaviors.Add("unknown-custom-action"), c => c.Files[0].Path = "../outside",
            c => c.Files[0].Required = false,
            c => c.PreservedDirectories.Add("app.exe"), c => c.RegistryValues.Add(c.RegistryValues[0]),
            c => c.GeneratedMetadata.Add(new() { FileName = "unins001.dat" }), c => c.Registration.Hive = "machine" })
        {
            var invalid = NativeMigrationValidation.Clone(f.Catalog); change(invalid);
            Throws<InvalidDataException>(() => NativeMigrationValidation.ValidateDefinition(f.TargetManifest(invalid)));
        }
        var unsupported = f.TargetManifest(); unsupported.Shortcuts.Add(new() { Name = "Unsupported integration", Target = "app.exe" });
        Throws<InvalidDataException>(() => NativeMigrationValidation.ValidateDefinition(unsupported));
        using var original = PackageReader.Open(f.Original.PackagePath);
        var draft = InnoOwnedFootprintDraft.Create(original, f.Frozen, "Programs/Fixture");
        Check(draft.Authority == "" && draft.Coverage == "" && draft.Lineage.Count == 0 && draft.ReviewSha256 == "", "A local inventory producer must never enroll ownership implicitly.");
        Throws<InvalidDataException>(() => NativeMigrationValidation.ValidateDefinition(f.TargetManifest(draft)));
        var foreign = NativeMigrationValidation.Clone(f.Original); foreign.Scope = "machine";
        Throws<InvalidDataException>(() => InnoOwnedPolicy.Bind(original.Manifest, foreign, f.Baseline()));
        var serialized = JsonSerializer.Serialize(new NativeMigrationBaselineDefinition(), InstallerJson.Options);
        Check(!serialized.Contains("ownedFootprint", StringComparison.Ordinal), "Legacy unset JSON must stay unchanged.");
        Throws<CryptographicException>(f.UnsignedTarget);
        var publisher = f.Catalog.PublisherKeySha256;
        f.Catalog.PublisherKeySha256 = new string('f', 64);
        try { Throws<CryptographicException>(() => { using var rejected = f.Target(); }); }
        finally { f.Catalog.PublisherKeySha256 = publisher; }
    }
    private static async Task WrapperFeatureAndVisibility()
    {
        using var f = new OwnedFixture(updates: true);
        using (var original = PackageReader.Open(f.Original.PackagePath)) InnoOwnedPolicy.Bind(original.Manifest, f.Original, f.Baseline());
        var oldSdk = NativeMigrationValidation.Clone(f.Platform.Sdk);
        var transaction = f.Open(); await transaction.RetireAsync(null, default);
        Check(!File.Exists(Path.Combine(f.App, "heirowUpdater.exe")) && !File.Exists(Path.Combine(f.App, "heirowUpdater.json")) &&
            !f.Platform.Inno.Exists && InnoOwnedRegistry.Equal(f.Platform.Sdk, oldSdk), "Old companion bytes and visibility must retire while the SDK ARP identity is preserved.");
        var after = oldSdk.Values.ToDictionary(x => x.Key, x => x.Value.Kind == "dword" ? (object)int.Parse(x.Value.Data, System.Globalization.CultureInfo.InvariantCulture) : x.Value.Data);
        after["DisplayVersion"] = "2.0";
        transaction.PlanNativeMetadata(after);
        f.Platform.Sdk.Values["DisplayVersion"] = new("string", "2.0"); // Partial native key update.
        transaction.RecoverNativeMetadata();
        Check(InnoOwnedRegistry.Equal(f.Platform.Sdk, oldSdk), "Partial native SDK metadata must restore its complete original typed image.");
        await transaction.RestoreAsync(null, default); f.AssertOriginal();
        Check(f.Platform.Inno.Values["SystemComponent"].Data == "1", "Rollback must restore the original hidden Inno visibility state exactly.");
    }
    private static async Task LateNativeReceiptRecovery()
    {
        using var f = new OwnedFixture(); f.Platform.FailAfterNativeReceipt = true;
        using var target = f.Target();
        await Throws<IOException>(() => f.Engine.InstallAsync(target, f.Options));
        Check(!InstallerEngine.ListInstalled(f.State).Single().IsExecutable && File.ReadAllText(f.AppFile) == "native application" &&
            !f.Platform.Inno.Exists, "A committed native receipt must survive a late observer failure.");
        f.Platform.FailAfterNativeReceipt = false;
        await f.Engine.InstallAsync(target, f.Options);
        Check(File.ReadAllText(f.AppFile) == "native application" && !File.Exists(f.Helper) && !f.Platform.Inno.Exists &&
            f.Platform.ExternalExecutions == 0, "Late native recovery must finalize without reinstalling the old backend.");
    }
    private static async Task CancellationAndLocks()
    {
        foreach (var checkpoint in new[] { "backed-up:app:app.exe", "removed:app:app.exe" })
        {
            using var f = new OwnedFixture(); using var cancellation = new CancellationTokenSource();
            var transaction = f.Open(); transaction.Fault = stage => { if (stage == checkpoint) cancellation.Cancel(); };
            await Throws<OperationCanceledException>(() => transaction.RetireAsync(null, cancellation.Token));
            f.AssertOriginal();
        }
        using var locked = new OwnedFixture(); locked.Platform.Locked.Add(locked.Helper);
        await Throws<IOException>(() => locked.Open().RetireAsync(null, default));
        locked.AssertOriginal(); locked.Platform.Locked.Clear();
        await locked.Open().RetireAsync(null, default);
        Check(!locked.Platform.Inno.Exists, "An explicit retry after releasing a lock may proceed; no forced closure is used.");
    }
    private static void ReceiptBackendTampering()
    {
        using var f = new OwnedFixture(); using var target = f.Target();
        var changed = NativeMigrationValidation.Clone(f.Original); changed.IsExecutable = false;
        File.WriteAllText(changed.ReceiptPath, JsonSerializer.Serialize(changed, InstallerJson.Options));
        Throws<CryptographicException>(() => f.Engine.Plan(target, f.Options));
        f.AssertOriginal();
    }
    private static async Task HistoricalOptionalResources()
    {
        foreach (var present in new[] { false, true })
        {
            using var f = new OwnedFixture(); var historicalSetup = new string('e', 64);
            f.Catalog.Lineage.Add(new() { Version = "0.9", SetupSha256 = historicalSetup, RecipeSha256 = new string('a', 64) });
            var content = Encoding.UTF8.GetBytes("known earlier launcher bytes");
            var oldProgram = Path.Combine(f.App, "earlier-launcher.exe");
            f.Catalog.Files.Add(new() { Path = "earlier-launcher.exe", Required = false, LineageSetupSha256 = historicalSetup,
                Size = content.Length, Sha256 = Convert.ToHexString(SHA256.HashData(content)) });
            for (var index = 0; index < 3; index++)
                f.Catalog.Shortcuts.Add(new() { KnownFolder = "Programs", RelativePath = "Earlier launch " + index + ".lnk",
                    Target = "earlier-launcher.exe", LineageSetupSha256 = historicalSetup });
            var oldLink = Path.Combine(f.Root, "links", "Programs", "Earlier launch 0.lnk");
            if (present)
            {
                File.WriteAllBytes(oldProgram, content);
                File.WriteAllText(oldLink, JsonSerializer.Serialize(new InnoOwnedLinkInfo(oldProgram, "", f.App, oldProgram, 0)));
            }
            NativeMigrationValidation.ValidateDefinition(f.TargetManifest());
            var transaction = f.Open(); await transaction.RetireAsync(null, default); transaction.ConfirmRetired();
            Check(!File.Exists(oldProgram) && !File.Exists(oldLink), "Finite reviewed historical program/link leaves must retire when present.");
            await transaction.RestoreAsync(null, default); f.AssertOriginal();
            Check(File.Exists(oldProgram) == present && File.Exists(oldLink) == present, "Rollback must reproduce historical presence and absence exactly.");
            if (present) Check(File.ReadAllBytes(oldProgram).SequenceEqual(content), "Historical rollback bytes must match their attested digest.");
            var invalid = NativeMigrationValidation.Clone(f.Catalog); invalid.Files[^1].LineageSetupSha256 = new string('f', 64);
            Throws<InvalidDataException>(() => NativeMigrationValidation.ValidateDefinition(f.TargetManifest(invalid)));
        }
    }
    private static async Task CommittedCleanupBoundaries()
    {
        foreach (var point in new[] { "cleanup-started", "cleanup-intent:app:app.exe", "cleanup-deleted:app:app.exe",
            "cleanup-recorded:app:app.exe", "cleanup-finished" })
        {
            using var f = new OwnedFixture(); var transaction = f.Open();
            await transaction.RetireAsync(null, default); transaction.PlanNativeMetadata(null);
            File.WriteAllText(f.AppFile, "native application"); transaction.MarkNativeCommitted();
            var once = false; transaction.Fault = stage =>
            { if (!once && stage == point) { once = true; throw new IOException("Synthetic post-commit cleanup interruption."); } };
            try { await transaction.CleanupCommittedAsync(null, default); } catch (IOException) { }
            Check(once && transaction.NativeCommitted && !transaction.NeedsRestoration,
                "Every cleanup interruption must retain the irreversible native marker: " + point);
            var resumed = f.Open(); var result = await resumed.CleanupCommittedAsync(null, default);
            Check(result.Pending == 0 && Directory.GetFiles(Path.Combine(f.OwnedDirectory, "backups")).Length == 0,
                "Known cleanup must resume after each committed deletion boundary.");
            await Throws<NativeMigrationRepairRequiredException>(() => resumed.RestoreAsync(null, default));
            Check(File.ReadAllText(f.AppFile) == "native application" && !f.Platform.Inno.Exists &&
                File.ReadAllText(f.Settings) == "preserved user settings", "Cleanup cannot revive legacy files/key or change user data.");
        }
        using var conflict = new OwnedFixture(); var committed = conflict.Open();
        await committed.RetireAsync(null, default); committed.PlanNativeMetadata(null);
        File.WriteAllText(conflict.AppFile, "native application"); committed.MarkNativeCommitted();
        var backups = Path.Combine(conflict.OwnedDirectory, "backups");
        var replacement = Directory.GetFiles(backups).First(); var sameBytes = File.ReadAllBytes(replacement);
        conflict.Platform.ReplaceObject(replacement, sameBytes);
        var unknown = Path.Combine(backups, "unrecognized-user-object.bin"); File.WriteAllText(unknown, "preserve unknown object");
        var pending = await committed.CleanupCommittedAsync(null, default);
        Check(pending.Pending > 0 && File.ReadAllBytes(replacement).SequenceEqual(sameBytes) &&
            File.ReadAllText(unknown) == "preserve unknown object", "Equal bytes on a replaced identity never authorize cleanup, and unknown objects survive.");
        pending = await conflict.Open().CleanupCommittedAsync(null, default);
        Check(pending.Pending > 0 && File.Exists(replacement) && committed.NativeCommitted && !committed.NeedsRestoration,
            "Repeated cleanup conflicts cannot become a rollback obligation.");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        pending = await conflict.Open().CleanupCommittedAsync(null, canceled.Token);
        Check(pending.Pending > 0 && File.Exists(replacement), "Post-commit cancellation preserves pending staging without canceling installation.");
        using var changed = new OwnedFixture(); var changedCommit = changed.Open();
        await changedCommit.RetireAsync(null, default); changedCommit.PlanNativeMetadata(null);
        File.WriteAllText(changed.AppFile, "native application"); changedCommit.MarkNativeCommitted();
        var changedBackup = Directory.GetFiles(Path.Combine(changed.OwnedDirectory, "backups")).First();
        File.AppendAllText(changedBackup, "changed after commit");
        pending = await changedCommit.CleanupCommittedAsync(null, default);
        Check(pending.Pending > 0 && File.ReadAllText(changedBackup).EndsWith("changed after commit", StringComparison.Ordinal),
            "Changed backup bytes are preserved even when their file identity remains the same.");
        using var before = new OwnedFixture();
        await Throws<InvalidOperationException>(() => before.Open().CleanupCommittedAsync(null, default));
        before.AssertOriginal();
    }
    private static async Task CompletedMaintenanceCleanup()
    {
        using var f = new OwnedFixture(); f.Platform.BlockBackupDeletes = true;
        using (var target = f.Target()) await f.Engine.InstallAsync(target, f.Options);
        var backupRoot = Path.Combine(f.EngineOwnedDirectory, "backups");
        var outer = Path.Combine(f.State, ".native-migrations", OwnedFixture.Id, "current", "migration.json");
        using var completedOuter = JsonDocument.Parse(File.ReadAllText(outer));
        Check(completedOuter.RootElement.GetProperty("phase").GetString() == "complete" &&
            Directory.GetFiles(backupRoot).Length > 0 && !InstallerEngine.ListInstalled(f.State).Single().IsExecutable,
            "Cleanup locks must leave a successful native receipt and completed outer journal.");
        // A later native version need not retain the migration declaration in its manifest.
        f.Options.FileRecoveryContext = null;
        using (var newer = f.NativeUpdate()) await f.Engine.InstallAsync(newer, f.Options);
        f.Platform.BlockBackupDeletes = false;
        f.Options.FileRecoveryContext = null;
        var pinnedTarget = completedOuter.RootElement.GetProperty("nativePackagePath").GetString()!;
        var unavailable = pinnedTarget + ".cleanup-context-unavailable";
        File.Move(pinnedTarget, unavailable);
        try { await f.Engine.RepairAsync(OwnedFixture.Id, f.Options); }
        finally { File.Move(unavailable, pinnedTarget); }
        Check(Directory.GetFiles(backupRoot).Length > 0 && !f.Platform.Inno.Exists &&
            File.ReadAllText(f.AppFile) == "later native application", "Missing retained signature context preserves staging without failing native maintenance.");
        f.Options.FileRecoveryContext = null;
        await f.Engine.RepairAsync(OwnedFixture.Id, f.Options);
        Check(Directory.GetFiles(backupRoot).Length == 0 && !f.Platform.Inno.Exists &&
            File.ReadAllText(f.AppFile) == "later native application", "Repair retries completed cleanup using the pinned original migration target after a later native update.");
        using var uninstall = new OwnedFixture(); uninstall.Platform.BlockBackupDeletes = true;
        using (var target = uninstall.Target()) await uninstall.Engine.InstallAsync(target, uninstall.Options);
        uninstall.Platform.BlockBackupDeletes = false;
        await uninstall.Engine.UninstallAsync(OwnedFixture.Id, uninstall.Options);
        Check(Directory.GetFiles(Path.Combine(uninstall.EngineOwnedDirectory, "backups")).Length == 0 &&
            !uninstall.Platform.Inno.Exists && File.ReadAllText(uninstall.Settings) == "preserved user settings",
            "Uninstall retries exact committed staging cleanup before removing the native receipt, preserving user data.");
        using var callback = new OwnedFixture();
        using (var target = callback.Target()) await callback.Engine.InstallAsync(target, callback.Options,
            new CallbackProgress(value => { if (value.Phase == "migration-cleanup") throw new IOException("Synthetic cleanup progress failure."); }));
        Check(!InstallerEngine.ListInstalled(callback.State).Single().IsExecutable && !callback.Platform.Inno.Exists,
            "A cleanup progress callback failure cannot advertise rollback or fail the committed installation.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private sealed class OwnedFixture : IDisposable
    {
        internal const string Id = "example.owned-fixture";
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "heirow-owned-fixture-" + Guid.NewGuid().ToString("N"));
        internal string App => Path.Combine(Root, "local", "Programs", "Fixture");
        internal string State => Path.Combine(Root, "state");
        internal string Frozen => Path.Combine(Root, "frozen");
        internal string AppFile => Path.Combine(App, "app.exe");
        internal string Helper => Path.Combine(App, "helper.dll");
        internal string Settings => Path.Combine(App, "settings", "preferences.json");
        internal string Link => Path.Combine(Root, "links", "Programs", "Example.lnk");
        internal string OwnedDirectory => Path.Combine(State, "owned-direct");
        internal string EngineOwnedDirectory => Path.Combine(State, ".native-migrations", Id, "current", "owned-footprint");
        internal InstalledProduct Original { get; }
        internal InnoOwnedFootprintCatalog Catalog { get; }
        internal FixturePlatform Platform { get; }
        internal InstallerEngine Engine { get; }
        internal InstallOptions Options { get; }
        private readonly string key, digest;
        private readonly PackageManifest originalManifest;
        internal PackageManifest OriginalManifest => originalManifest;
        internal OwnedFixture(bool updates = false)
        {
            Directory.CreateDirectory(Root); File.WriteAllText(Path.Combine(Root, ".owned-fixture"), "owned footprint synthetic fixture\n");
            Directory.CreateDirectory(Path.GetDirectoryName(Settings)!); Directory.CreateDirectory(Path.GetDirectoryName(Link)!);
            Directory.CreateDirectory(State); Directory.CreateDirectory(Frozen);
            File.WriteAllText(AppFile, "old application"); File.WriteAllText(Helper, "old helper"); File.WriteAllText(Settings, "preserved user settings");
            File.WriteAllText(Path.Combine(App, "unins000.exe"), "non-executable opaque EXE fixture");
            File.WriteAllText(Path.Combine(App, "unins000.dat"), "opaque untrusted DAT fixture; never interpreted");
            File.WriteAllText(Link, JsonSerializer.Serialize(new InnoOwnedLinkInfo(AppFile, "", App, AppFile, 0)));
            File.Copy(AppFile, Path.Combine(Frozen, "app.exe")); File.Copy(Helper, Path.Combine(Frozen, "helper.dll"));
            using var rsa = RSA.Create(2048); key = Path.Combine(Root, "fixture-key.pem"); File.WriteAllText(key, rsa.ExportPkcs8PrivateKeyPem());
            var originalSource = Path.Combine(Root, "old-source"); Directory.CreateDirectory(originalSource);
            File.WriteAllText(Path.Combine(originalSource, "setup.exe"), "non-executable setup fixture");
            originalManifest = new() { Id = Id, Name = "Owned footprint fixture", Publisher = "Fixture", Version = "1.0", EntryPoint = "app.exe",
                InstallDirectory = "{LocalAppData}/Programs/Fixture", Payload = [new() { Path = "setup.exe" }],
                LegacyExecutable = new() { PayloadPath = "setup.exe", UninstallExecutable = "{InstallDir}/unins000.exe", UninstallArguments = ["/NORESTART"],
                    MaintenanceRegistration = new() { Hive = "user", View = "registry64", UninstallKey = BackendMaintenance.UninstallRoot + "example.owned_is1" } } };
            if (updates)
            {
                originalManifest.UpdateUrl = "https://example.invalid/update.json";
                originalManifest.Features.Add(new() { Id = "updates", Name = "Fixture updates", Default = true });
                originalManifest.UpdateCompanion = new() { Feature = "updates", RuntimeMode = "app-local" };
                File.WriteAllText(Path.Combine(originalSource, "heirowUpdater.exe"), "non-executable updater fixture");
                originalManifest.Payload.Add(new() { Path = "heirowUpdater.exe", Feature = "updates" });
            }
            var cache = Path.Combine(State, Id, "packages"); Directory.CreateDirectory(cache);
            var packagePath = Path.Combine(cache, "original.hinst"); Build(originalManifest, originalSource, packagePath);
            using var original = PackageReader.Open(packagePath); digest = NativeMigrationValidation.ManifestDigest(original);
            originalManifest = NativeMigrationValidation.Clone(original.Manifest);
            Original = new() { Id = Id, Name = originalManifest.Name, Version = "1.0", Publisher = "Fixture", Scope = "user", Isolated = true, IsExecutable = true,
                InstallDirectory = App, PackagePath = packagePath, PublisherKeySha256 = original.PublisherKeySha256,
                ReceiptPath = Path.Combine(State, Id, "receipt.json") };
            if (updates)
            {
                Original.Features.Add("updates"); Original.RegisteredProduct = true; Original.UninstallerPath = packagePath;
                Original.BackendRegistrations.Add(new() { Kind = "registry", Key = "SystemComponent", Values = new()
                { ["hive"] = "user", ["view"] = "registry64", ["path"] = originalManifest.LegacyExecutable!.MaintenanceRegistration!.UninstallKey } });
                File.Copy(Path.Combine(originalSource, "heirowUpdater.exe"), Path.Combine(App, "heirowUpdater.exe"));
                File.WriteAllText(Path.Combine(App, "heirowUpdater.json"), JsonSerializer.Serialize(new UpdateCompanionConfiguration
                { ProductId = Id, CurrentVersion = "1.0", Scope = "user", Isolated = true, StateRoot = State, InstallDirectory = App,
                    PublisherKeySha256 = original.PublisherKeySha256!, FeedUrl = originalManifest.UpdateUrl!, Features = ["updates"], EntryPoint = "app.exe" }, InstallerJson.Options));
                foreach (var leaf in new[] { "heirowUpdater.exe", "heirowUpdater.json" }) Original.Files.Add(new() { Path = leaf, Sha256 = PackageSafety.HashFile(Path.Combine(App, leaf)) });
            }
            File.WriteAllText(Original.ReceiptPath, JsonSerializer.Serialize(Original, InstallerJson.Options));
            Catalog = InnoOwnedFootprintDraft.Create(original, Frozen, "Programs/Fixture");
            Catalog.Authority = InnoOwnedPolicy.Authority; Catalog.Coverage = InnoOwnedPolicy.Coverage; Catalog.WrapperPolicy = InnoOwnedPolicy.WrapperPolicy;
            Catalog.AppId = "example.owned"; Catalog.ReviewSha256 = new string('b', 64);
            Catalog.Lineage.Add(new() { Version = "1.0", SetupSha256 = Catalog.SetupSha256, RecipeSha256 = new string('c', 64) });
            Catalog.GeneratedMetadata = [new() { FileName = "unins000.exe" }, new() { FileName = "unins000.dat" }];
            Catalog.RegistryValues = [
                new() { Name = "Inno Setup: App Path", Value = "{InstallDir}" },
                new() { Name = "DisplayVersion", Value = "1.0" },
                new() { Name = "UninstallString", Value = "\"{InstallDir}\\unins000.exe\"" } ];
            if (updates) Catalog.RegistryValues.Add(new() { Name = "SystemComponent", Kind = "dword", Match = "wrapper-visibility", Value = "0" });
            Catalog.Shortcuts.Add(new() { KnownFolder = "Programs", RelativePath = "Example.lnk", Target = "app.exe" });
            Catalog.PreservedDirectories.Add("settings");
            Platform = new(this); Engine = new(null, new FixtureBackend(Platform));
            Options = new() { Scope = "user", StateRoot = State, TargetDirectory = App, Isolated = true, RegisterProduct = false,
                InstallerPath = Path.Combine(Root, "nonexistent-maintenance-host.exe"),
                RequireSignature = true, TrustedPublisherKeySha256 = original.PublisherKeySha256 };
        }
        internal NativeMigrationBaselineDefinition Baseline() => new() { Backend = InnoOwnedPolicy.Backend, Scope = "user", FromVersion = "1.0",
            FromManifestSha256 = digest, OwnedFootprint = NativeMigrationValidation.Clone(Catalog) };
        internal PackageManifest TargetManifest(InnoOwnedFootprintCatalog? catalog = null) => new()
        {
            FormatVersion = 3, Id = Id, Name = Original.Name, Publisher = "Fixture", Version = "2.0", EntryPoint = "app.exe",
            Payload = [new() { Path = "app.exe" }], NativeMigration = new() { Baselines = [new() { Backend = InnoOwnedPolicy.Backend, Scope = "user", FromVersion = "1.0",
                FromManifestSha256 = digest, OwnedFootprint = NativeMigrationValidation.Clone(catalog ?? Catalog) }] }
        };
        internal PackageReader Target()
        {
            var source = Path.Combine(Root, "new-source"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "app.exe"), "native application");
            var path = Path.Combine(Root, "target.hinst"); Build(TargetManifest(), source, path); return PackageReader.Open(path);
        }
        internal void UnsignedTarget()
        {
            var source = Path.Combine(Root, "unsigned-source"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "app.exe"), "native application");
            var manifest = TargetManifest(); manifest.LegalDocuments = LegalFixture.For(Id);
            var json = Path.Combine(Root, "unsigned-target.json"); File.WriteAllText(json, JsonSerializer.Serialize(manifest, InstallerJson.Options));
            PackageBuilder.Build(json, source, Path.Combine(Root, "unsigned-target.hinst"));
        }
        internal PackageReader NativeUpdate()
        {
            var source = Path.Combine(Root, "later-source"); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "app.exe"), "later native application");
            var manifest = TargetManifest(); manifest.Version = "3.0"; manifest.NativeMigration = null;
            var path = Path.Combine(Root, "later-native.hinst"); Build(manifest, source, path); return PackageReader.Open(path);
        }
        private void Build(PackageManifest manifest, string source, string path)
        { manifest.LegalDocuments = LegalFixture.For(Id); var json = path + ".json"; File.WriteAllText(json, JsonSerializer.Serialize(manifest, InstallerJson.Options)); PackageBuilder.Build(json, source, path, new() { SigningKeyPath = key }); }
        internal InnoOwnedRetirement Open()
        {
            var baseline = new NativeMigrationBaseline(NativeMigrationValidation.Clone(Original), originalManifest, Baseline(), new string('d', 64), PackageSafety.HashFile(Original.PackagePath));
            return new(baseline, Platform, OwnedDirectory, PackageSafety.HashFile(Original.ReceiptPath));
        }
        internal void AssertProgramAndSettings()
        { Check(File.ReadAllText(AppFile) == "old application" && File.ReadAllText(Helper) == "old helper" && File.ReadAllText(Settings) == "preserved user settings", "Legacy program and user settings must be exact."); }
        internal void AssertOriginal()
        {
            AssertProgramAndSettings();
            Check(File.Exists(Link) && File.Exists(Path.Combine(App, "unins000.exe")) && File.Exists(Path.Combine(App, "unins000.dat")) &&
                Platform.Inno.Exists && Platform.Inno.Values.Count == Catalog.RegistryValues.Count && Platform.Sdk.Exists == Original.RegisteredProduct &&
                Original.Files.All(x => File.Exists(Path.Combine(App, x.Path)) && PackageSafety.HashEquals(PackageSafety.HashFile(Path.Combine(App, x.Path)), x.Sha256)) &&
                Platform.ExternalExecutions == 0, "The full original footprint and unrelated settings must survive a failed migration.");
        }
        internal void Guard(string? path = null)
        {
            if (Path.GetDirectoryName(Path.GetFullPath(Root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(Root).StartsWith("heirow-owned-fixture-", StringComparison.Ordinal) ||
                File.ReadAllText(Path.Combine(Root, ".owned-fixture")) != "owned footprint synthetic fixture\n" ||
                path is not null && !Path.GetFullPath(path).StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The synthetic owned fixture escaped its explicitly marked temp root.");
            PackageSafety.EnsureNoReparsePoints(Root);
        }
        public void Dispose() { Guard(); Directory.Delete(Path.GetFullPath(Root), recursive: true); }
    }
    private sealed class FixtureBackend(FixturePlatform platform) : INativeMigrationBackend, IInnoOwnedPlatformProvider
    {
        public IInnoOwnedPlatform OwnedPlatform(NativeMigrationBaseline baseline, string stateRoot) => platform;
        public NativeBackendState ObserveDeclared(NativeMigrationBaselineDefinition definition, bool isolated) => platform.Inno.Exists ? NativeBackendState.Installed : NativeBackendState.Absent;
        public NativeBackendState Observe(NativeMigrationBaseline baseline) => platform.Observe();
        public Task<string> BootIdentityAsync(CancellationToken token) => Task.FromResult("owned-fixture-boot");
        public Task<int> UninstallAsync(NativeMigrationBaseline baseline, string verifiedMedia, InstallOptions options, CancellationToken token)
        { platform.ExternalExecutions++; throw new InvalidOperationException("Owned retirement cannot execute an external backend."); }
        public Task<int> RestoreAsync(NativeMigrationBaseline baseline, string verifiedMedia, InstallOptions options, CancellationToken token)
        { platform.ExternalExecutions++; throw new InvalidOperationException("Owned restoration cannot execute setup."); }
    }
    private sealed class FixturePlatform : IInnoOwnedPlatform
    {
        private readonly OwnedFixture fixture;
        private readonly byte[] protectionKey = RandomNumberGenerator.GetBytes(32);
        private readonly Dictionary<string, string> identities = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal InnoOwnedRegistryImage Inno, Sdk = new();
        internal bool RefuseRestoration, FailMidRegistryRemoval, FailAfterNativeReceipt, BlockBackupDeletes;
        internal int ExternalExecutions;
        internal FixturePlatform(OwnedFixture fixture)
        {
            this.fixture = fixture; Inno = new() { Exists = true };
            foreach (var rule in fixture.Catalog.RegistryValues)
                Inno.Values.Add(rule.Name, new(rule.Kind, rule.Match == "wrapper-visibility" ? "1" : rule.Value.Replace("{InstallDir}", fixture.App, StringComparison.Ordinal)));
            if (fixture.Original.RegisteredProduct) Sdk = InnoOwnedRegistry.FromValues(WindowsIntegration.ProductRegistrationValues(fixture.Original, fixture.State, fixture.OriginalManifest));
        }
        public bool IsIsolated => true;
        public string UserSid => "synthetic-owned-user";
        public string LocalAppData => Path.Combine(fixture.Root, "local");
        public string StateRoot => fixture.State;
        public string Folder(string name)
        { var path = Path.Combine(fixture.Root, "links", name); fixture.Guard(path); Directory.CreateDirectory(path); return path; }
        public void ProtectDirectory(string path) { fixture.Guard(path); Directory.CreateDirectory(path); }
        public byte[] Seal(byte[] bytes, byte[] binding)
        { fixture.Guard(); return HMACSHA256.HashData(protectionKey, binding.Concat(bytes).ToArray()).Concat(bytes).ToArray(); }
        public byte[] Unseal(byte[] bytes, byte[] binding)
        {
            fixture.Guard();
            if (bytes.Length < 32 || !CryptographicOperations.FixedTimeEquals(bytes.AsSpan(0, 32), HMACSHA256.HashData(protectionKey, binding.Concat(bytes.Skip(32)).ToArray())))
                throw new CryptographicException("Synthetic authenticated journal was changed.");
            return bytes[32..];
        }
        public void CommitLedger(string temporary, string destination)
        { fixture.Guard(temporary); fixture.Guard(destination); File.Move(temporary, destination, true); }
        public IDisposable HoldDirectories(IEnumerable<string> paths) { foreach (var path in paths) fixture.Guard(path); return new EmptyLease(); }
        private sealed class EmptyLease : IDisposable { public void Dispose() { } }
        public InnoOwnedFileState? Describe(string path)
        {
            fixture.Guard(path); if (!File.Exists(path)) return null;
            if (Locked.Contains(path)) throw new IOException("Synthetic sharing lock; no process is closed.");
            if (!identities.TryGetValue(path, out var id)) identities[path] = id = Guid.NewGuid().ToString("N");
            return new(new FileInfo(path).Length, PackageSafety.HashFile(path), id);
        }
        public void Backup(string path, string backup, InnoOwnedFileState expected, string? identity, Action<string> created)
        {
            fixture.Guard(path); fixture.Guard(backup); Check(Describe(path) == expected, "Synthetic source changed before backup.");
            if (identity is null)
            { using (File.Create(backup)) { } identities[backup] = Guid.NewGuid().ToString("N"); created(identities[backup]); }
            else Check(Describe(backup)?.Identity == identity, "Synthetic backup creation was replaced.");
            File.Copy(path, backup, overwrite: true);
        }
        public void Delete(string path, InnoOwnedFileState expected)
        {
            fixture.Guard(path);
            if (BlockBackupDeletes && Path.GetFileName(Path.GetDirectoryName(path)) == "backups")
                throw new IOException("Synthetic committed backup sharing lock.");
            Check(Describe(path) == expected, "Synthetic source changed before deletion."); File.Delete(path); identities.Remove(path);
        }
        internal void ReplaceObject(string path, byte[] bytes)
        { fixture.Guard(path); File.Delete(path); identities.Remove(path); File.WriteAllBytes(path, bytes); _ = Describe(path); }
        public void Restore(string path, string backup, InnoOwnedFileState expected, string temporary, string? identity, Action<string> created)
        {
            fixture.Guard(path); fixture.Guard(backup); fixture.Guard(temporary);
            if (RefuseRestoration) throw new IOException("Synthetic restoration is interrupted.");
            var current = Describe(path);
            if (current is not null)
            { if (current.Size == expected.Size && PackageSafety.HashEquals(current.Sha256, expected.Sha256)) return; throw new IOException("Synthetic restoration conflict."); }
            if (identity is null)
            { using (File.Create(temporary)) { } identities[temporary] = Guid.NewGuid().ToString("N"); created(identities[temporary]); }
            else Check(Describe(temporary)?.Identity == identity, "Synthetic restore creation was replaced.");
            File.Copy(backup, temporary, true); File.Move(temporary, path, false); identities[path] = identities[temporary]; identities.Remove(temporary);
        }
        public InnoOwnedLinkInfo ReadLink(string verifiedBackup) { fixture.Guard(verifiedBackup); return JsonSerializer.Deserialize<InnoOwnedLinkInfo>(File.ReadAllText(verifiedBackup))!; }
        public InnoOwnedRegistryImage ReadRegistry(bool sdk) => NativeMigrationValidation.Clone(sdk ? Sdk : Inno);
        internal NativeBackendState Observe()
        {
            if (FailAfterNativeReceipt && !JsonSerializer.Deserialize<InstalledProduct>(File.ReadAllText(fixture.Original.ReceiptPath), InstallerJson.Options)!.IsExecutable)
                throw new IOException("Synthetic failure after the native receipt committed.");
            return Inno.Exists ? NativeBackendState.Installed : NativeBackendState.Absent;
        }
        public void RetireRegistry(InnoOwnedRegistryImage before)
        {
            Check(InnoOwnedRegistry.IsSubset(Inno, before), "Synthetic registry changed.");
            foreach (var name in Inno.Values.Keys.ToArray())
            { Inno.Values.Remove(name); if (FailMidRegistryRemoval) { RefuseRestoration = true; throw new IOException("Synthetic partial key removal."); } }
            Inno = new();
        }
        public void RestoreRegistry(bool sdk, InnoOwnedRegistryImage before, InnoOwnedRegistryImage? permittedAfter)
        {
            if (RefuseRestoration) throw new IOException("Synthetic registry restoration is interrupted.");
            InnoOwnedRegistry.RequireRestorable(sdk ? Sdk : Inno, before, permittedAfter);
            if (sdk) Sdk = NativeMigrationValidation.Clone(before); else Inno = NativeMigrationValidation.Clone(before);
        }
    }
}
