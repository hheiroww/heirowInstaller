using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HeirowInstaller.Core;

if (args is ["--probe-boot"])
{
    var identity = await new WindowsNativeMigrationBackend().BootIdentityAsync(CancellationToken.None);
    if (!long.TryParse(identity, out var ticks) || ticks <= 0) throw new InvalidDataException("The read-only Windows boot probe did not return UTC ticks.");
    Console.WriteLine("PASS read-only Windows boot identity uses normalized UTC ticks."); return 0;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("format 3 is explicit, legacy serialization stays unchanged and unknown formats fail", Format),
    ("recorded MSI retires under one lock and commits a native receipt", () => Success("msi", "user")),
    ("recorded machine Burn retires without deleting installer registrations manually", () => Success("burn", "machine")),
    ("one immutable native target accepts two separately signed published baselines", MultipleBaselines),
    ("Burn child ownership must agree with bundle removal and boot identity ignores timezone formatting", BurnChildrenAndBoot),
    ("wrong signed baseline, backend, publisher, scope and root reject before mutation", RejectBindings),
    ("unknown legacy installations are never discovered or adopted", UnknownInstallation),
    ("default discovery follows one recorded scope, rejects ambiguity and fresh isolation never inspects a real backend", DiscoveryAndIsolation),
    ("an unauthorized backend change fails before extraction or prerequisites", UnauthorizedTransition),
    ("3010 stops native writes until a verified new boot", () => Restart(3010)),
    ("1641 stops native writes until a verified new boot", () => Restart(1641)),
    ("unknown external completion requires restart and exact registration reconciliation", InterruptedRetirement),
    ("native failure rolls back files and restores only verified original media", Rollback),
    ("journal, retained media and original receipt tampering fail closed", Tampering),
    ("prepare interruption resumes exact owned media while cancellation cleans and unknown, replaced or active files preserve", PreparingRecovery),
    ("a native commit interrupted before its completion marker never reinstalls the old backend", CommittedResume)
};
var passed = 0;
try
{
    foreach (var test in tests) { await test.Run(); passed++; Console.WriteLine("PASS " + test.Name); }
    Console.WriteLine($"Passed {passed}/{tests.Length} native migration cases. No real MSI, Burn, application, registry or service was changed.");
    return 0;
}
catch (Exception failure) { Console.Error.WriteLine($"FAIL after {passed}/{tests.Length}: {failure}"); return 1; }

static void True(bool value, string message = "Assertion failed") { if (!value) throw new InvalidOperationException(message); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{ try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
static async Task Format()
{
    using var fixture = new Fixture();
    True(!JsonSerializer.Serialize(new PackageManifest(), InstallerJson.Options).Contains("nativeMigration", StringComparison.Ordinal));
    using var target = fixture.Build();
    True(target.Manifest.FormatVersion == 3);
    var invalid = NativeMigrationValidation.Clone(target.Manifest); invalid.FormatVersion = 2;
    await Throws<InvalidDataException>(() => Task.Run(() => PackageSafety.ValidateManifest(invalid, true)));
    invalid.FormatVersion = 4;
    await Throws<InvalidDataException>(() => Task.Run(() => PackageSafety.ValidateManifest(invalid, true)));
    invalid.FormatVersion = 3; invalid.NativeMigration!.Baselines[0].FromManifestSha256 = "not-a-digest";
    await Throws<InvalidDataException>(() => Task.Run(() => PackageSafety.ValidateManifest(invalid, true)));
}
static async Task Success(string kind, string scope)
{
    using var fixture = new Fixture(kind, scope); using var target = fixture.Build();
    var result = await fixture.Engine.InstallAsync(target, fixture.Options);
    True(result.Version == "2.0" && !result.RebootRequired);
    True(File.ReadAllText(fixture.AppFile) == "native application");
    var receipt = InstallerEngine.ListInstalled(fixture.StateRoot).Single();
    True(!receipt.IsMsi && !receipt.IsExecutable && receipt.Version == "2.0");
    True(fixture.Backend.Uninstalls == 1 && fixture.Backend.Restores == 0 && fixture.Backend.LockHeld && fixture.Backend.ReceiptPreserved);
    True(File.Exists(fixture.Original.PackagePath) && fixture.Phase == "complete");
    True(!Directory.EnumerateFiles(Path.Combine(fixture.JournalDirectory, "media"), "*", SearchOption.AllDirectories).Any());
}
static async Task RejectBindings()
{
    foreach (var mutation in new Action<NativeMigrationBaselineDefinition>[]
    {
        definition => definition.FromVersion = "0.5",
        definition => definition.FromManifestSha256 = new string('a', 64),
        definition => definition.MsiProductCode = Guid.NewGuid().ToString("B"),
        definition => definition.Scope = "machine"
    })
    {
        using var fixture = new Fixture(); using var target = fixture.Build(mutate: mutation);
        await Throws<Exception>(() => fixture.Engine.InstallAsync(target, fixture.Options)); fixture.Unchanged();
    }
    using (var fixture = new Fixture())
    {
        using var target = fixture.Build(); fixture.Options.TargetDirectory = Path.Combine(fixture.Root, "other-app");
        await Throws<InvalidOperationException>(() => fixture.Engine.InstallAsync(target, fixture.Options)); fixture.Unchanged();
    }
    using (var fixture = new Fixture())
    {
        using var key = RSA.Create(2048); var other = Path.Combine(fixture.Root, "other-fixture.pem"); File.WriteAllText(other, key.ExportPkcs8PrivateKeyPem());
        using var target = fixture.Build(signingKey: other);
        await Throws<CryptographicException>(() => fixture.Engine.InstallAsync(target, fixture.Options)); fixture.Unchanged();
    }
    using (var fixture = new Fixture("burn"))
    {
        using var target = fixture.Build(mutate: definition => definition.BurnRegistration!.BundleUpgradeCode = Guid.NewGuid().ToString("B"));
        await Throws<CryptographicException>(() => fixture.Engine.InstallAsync(target, fixture.Options)); fixture.Unchanged();
    }
    using (var fixture = new Fixture())
    {
        using var target = fixture.Build(); target.Manifest.NativeMigration!.Baselines[0].MsiProductCode = Guid.NewGuid().ToString("B");
        await Throws<CryptographicException>(() => fixture.Engine.InstallAsync(target, fixture.Options)); fixture.Unchanged();
    }
    using (var fixture = new Fixture("burn"))
    {
        await Throws<InvalidDataException>(() => Task.Run(() => { using var rejected = fixture.Build(mutate: baseline => baseline.BurnMsiProductCodes = null); }));
        await Throws<InvalidDataException>(() => Task.Run(() => { using var rejected = fixture.Build(mutate: baseline => baseline.BurnMsiProductCodes!.Add(baseline.BurnMsiProductCodes[0])); }));
        fixture.Unchanged();
    }
}
static async Task MultipleBaselines()
{
    using var identity = RSA.Create(2048);
    using var first = new Fixture(signingIdentity: identity); using var second = new Fixture(version: "1.1", signingIdentity: identity);
    using var target = first.Build(additionalBaseline: second.Baseline());
    var immutableHash = PackageSafety.HashFile(target.PackagePath);
    await first.Engine.InstallAsync(target, first.Options); await second.Engine.InstallAsync(target, second.Options);
    True(first.Backend.Uninstalls == 1 && second.Backend.Uninstalls == 1 && first.Phase == "complete" && second.Phase == "complete");
    True(immutableHash == PackageSafety.HashFile(target.PackagePath));
    using var duplicate = new Fixture();
    await Throws<InvalidDataException>(() => Task.Run(() => { using var rejected = duplicate.Build(additionalBaseline: duplicate.Baseline()); }));
    duplicate.Unchanged();
}
static async Task UnknownInstallation()
{
    using var fixture = new Fixture(); using var target = fixture.Build();
    File.Delete(fixture.Original.ReceiptPath);
    await Throws<InvalidOperationException>(() => fixture.Engine.InstallAsync(target, fixture.Options));
    True(fixture.Backend.Uninstalls == 0 && File.ReadAllText(fixture.AppFile) == "original application");
    fixture.Options.TargetDirectory = Path.Combine(fixture.Root, "fresh-empty-target");
    fixture.Options.Isolated = false; // The test backend only observes synthetic state; Plan must reject before OS integration.
    await Throws<InvalidOperationException>(() => fixture.Engine.InstallAsync(target, fixture.Options));
    True(fixture.Backend.Uninstalls == 0 && !Directory.Exists(fixture.Options.TargetDirectory), "An empty fresh target must not bypass an existing backend.");
}
static async Task BurnChildrenAndBoot()
{
    True(WindowsNativeMigrationBackend.CombineBurnState(NativeBackendState.Absent, [NativeBackendState.Installed]) == NativeBackendState.Uncertain);
    True(WindowsNativeMigrationBackend.CombineBurnState(NativeBackendState.Absent, [NativeBackendState.Absent]) == NativeBackendState.Absent);
    True(WindowsNativeMigrationBackend.NormalizeBootIdentity("20261025100000.123456+000") ==
        WindowsNativeMigrationBackend.NormalizeBootIdentity("20261025050000.123456-300"));
    using var fixture = new Fixture("burn"); fixture.Backend.ChildRemains = true; using var target = fixture.Build();
    await Throws<NativeMigrationRepairRequiredException>(() => fixture.Engine.InstallAsync(target, fixture.Options));
    True(!File.Exists(fixture.AppFile) && InstallerEngine.ListInstalled(fixture.StateRoot).Single().IsExecutable && fixture.Backend.Restores == 0);
    using var raced = new Fixture(); using var raceTarget = raced.Build();
    var progress = new CallbackProgress(value => { if (value.Phase == "migration" && value.Completed == 1) raced.Backend.Reappear(); });
    await Throws<NativeMigrationRepairRequiredException>(() => raced.Engine.InstallAsync(raceTarget, raced.Options, progress));
    True(File.ReadAllText(raced.AppFile) == "original application" && raced.Backend.Restores == 0);
}
static async Task DiscoveryAndIsolation()
{
    using var fixture = new Fixture();
    var machine = NativeMigrationValidation.Clone(fixture.Original); machine.Scope = "machine";
    True(NativeMigrationValidation.SelectRecorded([machine], Fixture.Id)!.Scope == "machine");
    await Throws<InvalidOperationException>(() => Task.Run(() => NativeMigrationValidation.SelectRecorded([machine, fixture.Original], Fixture.Id)));
    True(NativeMigrationValidation.SelectRecorded([machine], "test.unrelated") is null);
    using var target = fixture.Build();
    await Throws<InvalidOperationException>(() => new InstallerEngine().InstallAsync(target, fixture.Options));
    fixture.Unchanged();
    File.Delete(fixture.Original.ReceiptPath); fixture.Options.TargetDirectory = Path.Combine(fixture.Root, "isolated-fresh");
    await new InstallerEngine().InstallAsync(target, fixture.Options);
    True(File.ReadAllText(Path.Combine(fixture.Options.TargetDirectory, "app.txt")) == "native application" && fixture.Backend.Uninstalls == 0);
    True(File.ReadAllText(fixture.AppFile) == "original application");
}
static async Task UnauthorizedTransition()
{
    using var fixture = new Fixture(); using var target = fixture.Build(withMigration: false, includePrerequisite: true);
    await Throws<InvalidOperationException>(() => fixture.Engine.InstallAsync(target, fixture.Options));
    fixture.Unchanged(); True(!Directory.Exists(Path.Combine(fixture.StateRoot, ".transactions")));
}
static async Task Restart(int code)
{
    using var fixture = new Fixture(); fixture.Backend.ExitCode = code; using var target = fixture.Build();
    await Throws<NativeMigrationRestartRequiredException>(() => fixture.Engine.InstallAsync(target, fixture.Options));
    True(!File.Exists(fixture.AppFile) && fixture.Backend.Restores == 0 && fixture.Phase == "await-restart");
    True(InstallerEngine.ListInstalled(fixture.StateRoot).Single().IsMsi && File.Exists(fixture.Original.PackagePath));
    await Throws<NativeMigrationRestartRequiredException>(() => fixture.Engine.InstallAsync(target, fixture.Options));
    True(fixture.Backend.Uninstalls == 1);
    await Throws<NativeMigrationRepairRequiredException>(() => fixture.Engine.UninstallAsync(fixture.Original.Id, fixture.Options));
    fixture.Backend.Boot = "boot-after-restart";
    await fixture.Engine.InstallAsync(target, fixture.Options);
    True(File.ReadAllText(fixture.AppFile) == "native application" && fixture.Backend.Uninstalls == 1 && fixture.Phase == "complete");
}
static async Task InterruptedRetirement()
{
    using var fixture = new Fixture(); fixture.Backend.FailAfterRemoval = true; using var target = fixture.Build();
    await Throws<NativeMigrationRepairRequiredException>(() => fixture.Engine.InstallAsync(target, fixture.Options));
    True(fixture.Phase == "repair-required" && !File.Exists(fixture.AppFile));
    await Throws<NativeMigrationRestartRequiredException>(() => fixture.Engine.InstallAsync(target, fixture.Options));
    fixture.Backend.Boot = "boot-after-crash";
    await fixture.Engine.InstallAsync(target, fixture.Options);
    True(fixture.Backend.Uninstalls == 1 && fixture.Backend.Restores == 0 && File.ReadAllText(fixture.AppFile) == "native application");
}
static async Task Rollback()
{
    using var fixture = new Fixture(); var module = new FailureModule(); fixture.Engine = new InstallerEngine([module], fixture.Backend);
    using var target = fixture.Build(module: module.Id);
    await Throws<InvalidOperationException>(() => fixture.Engine.InstallAsync(target, fixture.Options));
    True(fixture.Backend.Uninstalls == 1 && fixture.Backend.Restores == 1 && fixture.Phase == "restored");
    True(File.ReadAllText(fixture.AppFile) == "original application" && InstallerEngine.ListInstalled(fixture.StateRoot).Single().IsMsi);
    module.Fail = false;
    await fixture.Engine.InstallAsync(target, fixture.Options);
    True(fixture.Backend.Uninstalls == 2 && fixture.Phase == "complete");
}
static async Task Tampering()
{
    foreach (var item in new[] { "journal", "media", "receipt" })
    {
        using var fixture = new Fixture(); fixture.Backend.ExitCode = 3010; using var target = fixture.Build();
        await Throws<NativeMigrationRestartRequiredException>(() => fixture.Engine.InstallAsync(target, fixture.Options));
        if (item == "journal")
        {
            var path = Path.Combine(fixture.JournalDirectory, "migration.json"); var json = JsonNode.Parse(File.ReadAllText(path))!;
            json["productId"] = "test.someone-else"; File.WriteAllText(path, json.ToJsonString());
        }
        else File.AppendAllText(Path.Combine(fixture.JournalDirectory, item == "media" ? "media/legacy.msi" : "original-receipt.json"), "tampered");
        fixture.Backend.Boot = "boot-after-tamper";
        await Throws<Exception>(() => fixture.Engine.InstallAsync(target, fixture.Options));
        True(!File.Exists(fixture.AppFile) && fixture.Backend.Uninstalls == 1 && fixture.Backend.Restores == 0);
    }
}
static async Task CommittedResume()
{
    using var fixture = new Fixture(); fixture.Backend.FailAfterNativeReceipt = true; using var target = fixture.Build();
    await Throws<IOException>(() => fixture.Engine.InstallAsync(target, fixture.Options));
    True(!InstallerEngine.ListInstalled(fixture.StateRoot).Single().IsMsi && File.ReadAllText(fixture.AppFile) == "native application");
    True(fixture.Backend.Restores == 0 && fixture.Phase == "removed");
    fixture.Backend.FailAfterNativeReceipt = false;
    await fixture.Engine.InstallAsync(target, fixture.Options);
    True(fixture.Backend.Uninstalls == 1 && fixture.Backend.Restores == 0 && fixture.Phase == "complete");
}
static async Task PreparingRecovery()
{
    foreach (var mode in new[] { "resume", "partial", "cancel", "unknown", "replaced", "active" })
    {
        using var fixture = new Fixture(); using var target = fixture.Build(); using var cancellation = new CancellationTokenSource();
        var interrupt = new CallbackProgress(progress =>
        {
            if (progress.Phase != "migration-stage") return;
            if (mode == "cancel") cancellation.Cancel(); else throw new IOException("Isolated prepare interruption.");
        });
        await Throws<Exception>(() => fixture.Engine.InstallAsync(target, fixture.Options, interrupt, cancellation.Token));
        True(fixture.Backend.Uninstalls == 0 && fixture.Phase == "preparing");
        var media = Path.Combine(fixture.JournalDirectory, "media", "legacy.msi");
        if (mode == "cancel") True(!File.Exists(media), "Normal cancellation must remove its verified owned staged bytes.");
        if (mode == "partial") { using var partial = new FileStream(media, FileMode.Open, FileAccess.Write, FileShare.None); partial.SetLength(10); }
        if (mode == "unknown")
        {
            var unknown = Path.Combine(fixture.JournalDirectory, "media", "unknown.txt"); File.WriteAllText(unknown, "preserve unknown");
            await Throws<NativeMigrationRepairRequiredException>(() => fixture.Engine.InstallAsync(target, fixture.Options));
            True(File.ReadAllText(unknown) == "preserve unknown" && fixture.Backend.Uninstalls == 0); continue;
        }
        if (mode == "replaced")
        {
            var replacement = media + ".replacement"; File.Copy(media, replacement); File.Delete(media); File.Move(replacement, media);
            await Throws<NativeMigrationRepairRequiredException>(() => fixture.Engine.InstallAsync(target, fixture.Options));
            True(File.Exists(media) && fixture.Backend.Uninstalls == 0); continue;
        }
        if (mode == "active")
        {
            using (var busy = new FileStream(media, FileMode.Open, FileAccess.Read, FileShare.Read))
                await Throws<IOException>(() => fixture.Engine.InstallAsync(target, fixture.Options));
            True(fixture.Backend.Uninstalls == 0 && File.Exists(media));
        }
        await fixture.Engine.InstallAsync(target, fixture.Options);
        True(fixture.Backend.Uninstalls == 1 && fixture.Phase == "complete");
    }
}

sealed class Fixture : IDisposable
{
    internal const string Id = "test.native-migration";
    internal readonly string Root = Path.Combine(Path.GetTempPath(), "heirowInstaller-native-migration-" + Guid.NewGuid().ToString("N"));
    internal string StateRoot => Path.Combine(Root, "state");
    internal string AppRoot => Path.Combine(Root, "app");
    internal string AppFile => Path.Combine(AppRoot, "app.txt");
    internal string JournalDirectory => Path.Combine(StateRoot, ".native-migrations", Id, "current");
    internal string Phase => JsonNode.Parse(File.ReadAllText(Path.Combine(JournalDirectory, "migration.json")))!["phase"]!.GetValue<string>();
    internal readonly InstalledProduct Original;
    internal readonly InstallOptions Options;
    internal readonly FakeBackend Backend;
    internal InstallerEngine Engine;
    private readonly string keyPath, kind;
    internal Fixture(string kind = "msi", string scope = "user", string version = "1.0", RSA? signingIdentity = null)
    {
        this.kind = kind; Directory.CreateDirectory(Root); File.WriteAllText(Path.Combine(Root, ".native-migration-fixture"), "isolated native migration acceptance\n");
        Directory.CreateDirectory(AppRoot); File.WriteAllText(AppFile, "original application");
        keyPath = Path.Combine(Root, "fixture-private.pem"); using var key = signingIdentity is null ? RSA.Create(2048) : null;
        File.WriteAllText(keyPath, (signingIdentity ?? key!).ExportPkcs8PrivateKeyPem());
        var source = Path.Combine(Root, "original-source"); Directory.CreateDirectory(source);
        var payload = kind == "msi" ? "legacy.msi" : "legacy.exe"; File.WriteAllText(Path.Combine(source, payload), "non-executable signed legacy fixture");
        var manifest = new PackageManifest { Id = Id, Name = "Native migration fixture", Publisher = "Fixture", Version = version, Scope = scope, Payload = [new() { Path = payload }] };
        var code = "{A7FC88B2-99FD-4A04-94A1-B8918B4C11CA}";
        if (kind == "msi") manifest.LegacyMsi = new() { PayloadPath = payload, ProductCode = code };
        else manifest.LegacyExecutable = new() { PayloadPath = payload, InstallArguments = ["/install"], UninstallArguments = ["/uninstall", "/quiet", "/norestart"],
            MaintenanceRegistration = new() { Hive = scope, View = "registry64", BundleUpgradeCode = code } };
        var built = Path.Combine(Root, "original.hinst"); BuildPackage(manifest, source, built, keyPath);
        var cache = Path.Combine(StateRoot, Id); Directory.CreateDirectory(Path.Combine(cache, "packages"));
        var cached = Path.Combine(cache, "packages", PackageSafety.HashFile(built) + ".hinst"); File.Copy(built, cached);
        using var package = PackageReader.Open(cached);
        Original = new() { Id = Id, Name = manifest.Name, Version = version, Publisher = "Fixture", Scope = scope, Isolated = true,
            InstallDirectory = AppRoot, ReceiptPath = Path.Combine(cache, "receipt.json"), PackagePath = cached, PublisherKeySha256 = package.PublisherKeySha256,
            IsMsi = kind == "msi", IsExecutable = kind == "burn", MsiProductCode = kind == "msi" ? code : null };
        if (kind == "burn") Original.BackendRegistrations.Add(new() { Kind = "registry", Key = "SystemComponent", Values = new()
        { ["hive"] = scope, ["view"] = "registry64", ["path"] = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{B7A4D0A9-6717-4AE0-B0A8-82B7532C031F}" } });
        File.WriteAllText(Original.ReceiptPath, JsonSerializer.Serialize(Original, InstallerJson.Options));
        Options = new() { Scope = scope, StateRoot = StateRoot, TargetDirectory = AppRoot, Isolated = true, RegisterProduct = false,
            RequireSignature = true, TrustedPublisherKeySha256 = package.PublisherKeySha256 };
        Backend = new(this); Engine = new InstallerEngine(null, Backend);
    }
    internal NativeMigrationBaselineDefinition Baseline()
    {
        using var original = PackageReader.Open(Original.PackagePath);
        return new() { Backend = kind, Scope = Original.Scope, FromVersion = Original.Version,
            FromManifestSha256 = NativeMigrationValidation.ManifestDigest(original), MsiProductCode = Original.MsiProductCode,
            BurnRegistration = NativeMigrationValidation.Clone(original.Manifest.LegacyExecutable?.MaintenanceRegistration),
            BurnMsiProductCodes = kind == "burn" ? ["{418234D0-ACFD-47F9-BBAE-D5731409421E}"] : null };
    }
    internal PackageReader Build(Action<NativeMigrationBaselineDefinition>? mutate = null, string? signingKey = null, bool withMigration = true,
        bool includePrerequisite = false, string? module = null, NativeMigrationBaselineDefinition? additionalBaseline = null)
    {
        var migration = Baseline();
        mutate?.Invoke(migration);
        var source = Path.Combine(Root, "target-source"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "app.txt"), "native application");
        var manifest = new PackageManifest { Id = Id, Name = Original.Name, Publisher = "Fixture", Version = "2.0", Scope = Original.Scope,
            EntryPoint = "app.txt", Payload = [new() { Path = "app.txt" }], NativeMigration = withMigration ? new() { Baselines = [migration] } : null };
        if (additionalBaseline is not null) manifest.NativeMigration!.Baselines.Add(additionalBaseline);
        if (includePrerequisite) { File.WriteAllText(Path.Combine(source, "must-not-run.exe"), "non-executable prerequisite fixture");
            manifest.Payload.Add(new() { Path = "must-not-run.exe" }); manifest.Prerequisites.Add(new() { Id = "never", Executable = "must-not-run.exe" }); }
        if (module is not null) manifest.Modules[module] = JsonSerializer.SerializeToElement(new { });
        var output = Path.Combine(Root, "target-" + Guid.NewGuid().ToString("N") + ".hinst"); BuildPackage(manifest, source, output, signingKey ?? keyPath);
        return PackageReader.Open(output);
    }
    private static void BuildPackage(PackageManifest manifest, string source, string output, string key)
    { var json = output + ".json"; File.WriteAllText(json, JsonSerializer.Serialize(manifest, InstallerJson.Options)); PackageBuilder.Build(json, source, output, new() { SigningKeyPath = key }); }
    internal void Guard()
    {
        if (Path.GetDirectoryName(Path.GetFullPath(Root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
            File.ReadAllText(Path.Combine(Root, ".native-migration-fixture")) != "isolated native migration acceptance\n") throw new InvalidOperationException("Invalid isolated fixture root.");
        PackageSafety.EnsureNoReparsePoints(Root);
    }
    internal void Unchanged()
    { if (Backend.Uninstalls != 0 || File.ReadAllText(AppFile) != "original application" || !File.Exists(Original.ReceiptPath)) throw new InvalidOperationException("The rejected migration changed its fixture."); }
    public void Dispose() { Guard(); Directory.Delete(Root, recursive: true); }
}
sealed class FakeBackend(Fixture fixture) : INativeMigrationBackend
{
    internal int Uninstalls, Restores, ExitCode;
    internal bool LockHeld, ReceiptPreserved, FailAfterRemoval, ChildRemains, FailAfterNativeReceipt;
    internal string Boot = "boot-before";
    private NativeBackendState state = NativeBackendState.Installed;
    public NativeBackendState ObserveDeclared(NativeMigrationBaselineDefinition definition, bool isolated)
    { fixture.Guard(); return state; } // Read-only synthetic observation is safe even for a rejected non-isolated Plan.
    public NativeBackendState Observe(NativeMigrationBaseline baseline)
    { fixture.Guard(); if (!baseline.Product.Isolated) throw new InvalidOperationException("Fixture isolation is mandatory.");
        var receipt = JsonSerializer.Deserialize<InstalledProduct>(File.ReadAllText(fixture.Original.ReceiptPath), InstallerJson.Options)!;
        if (FailAfterNativeReceipt && !receipt.IsMsi && !receipt.IsExecutable) throw new IOException("Isolated completion marker fault."); return state; }
    internal void Reappear() { fixture.Guard(); File.WriteAllText(fixture.AppFile, "original application"); state = NativeBackendState.Installed; }
    public Task<string> BootIdentityAsync(CancellationToken token) { fixture.Guard(); token.ThrowIfCancellationRequested(); return Task.FromResult(Boot); }
    public Task<int> UninstallAsync(NativeMigrationBaseline baseline, string verifiedMedia, InstallOptions options, CancellationToken token)
    {
        fixture.Guard(); if (!options.Isolated || !baseline.Product.Isolated) throw new InvalidOperationException("Fixture isolation is mandatory.");
        var lockPath = Path.Combine(fixture.StateRoot, ".locks", Fixture.Id + ".lock");
        try { using var forbidden = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { LockHeld = true; }
        ReceiptPreserved = File.Exists(fixture.Original.ReceiptPath) && File.Exists(fixture.Original.PackagePath);
        if (!LockHeld || !ReceiptPreserved) throw new InvalidOperationException("Migration lost its lock or original receipt.");
        Uninstalls++; File.Delete(fixture.AppFile); state = ChildRemains ? NativeBackendState.Uncertain : NativeBackendState.Absent;
        if (FailAfterRemoval) throw new IOException("Isolated external completion fault.");
        return Task.FromResult(ExitCode);
    }
    public Task<int> RestoreAsync(NativeMigrationBaseline baseline, string verifiedMedia, InstallOptions options, CancellationToken token)
    { fixture.Guard(); if (!options.Isolated || !baseline.Product.Isolated) throw new InvalidOperationException("Fixture isolation is mandatory.");
        Restores++; File.WriteAllText(fixture.AppFile, "original application"); state = NativeBackendState.Installed; return Task.FromResult(0); }
}
sealed class FailureModule : IInstallerModule
{
    public string Id => "fixture-failure";
    internal bool Fail = true;
    public IReadOnlyList<string> Plan(JsonElement configuration, InstallerModuleContext context) => [];
    public Task<ModuleSnapshot> InstallAsync(JsonElement configuration, InstallerModuleContext context, CancellationToken token) =>
        Fail ? throw new InvalidOperationException("Isolated native installation fault.") : Task.FromResult(new ModuleSnapshot());
    public Task UninstallAsync(ModuleSnapshot snapshot, InstallerModuleContext context, CancellationToken token) => Task.CompletedTask;
}
sealed class CallbackProgress(Action<InstallProgress> callback) : IProgress<InstallProgress>
{ public void Report(InstallProgress value) => callback(value); }
