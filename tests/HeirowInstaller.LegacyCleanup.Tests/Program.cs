using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HeirowInstaller.Core;

if (args is ["--hold", var heldPath, var readyPath, var exitPath])
{
    using var held = new FileStream(heldPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    File.WriteAllText(readyPath, "ready");
    var timer = Stopwatch.StartNew();
    while (!File.Exists(exitPath) && timer.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(25);
    return 0;
}
if (args is ["--interrupt", var caseRoot])
{
    using var fixture = Fixture.Reopen(caseRoot);
    await fixture.Execute(commit: false, interrupt: true);
    return 9;
}
if (args is ["--interrupt-partial", var partialRoot])
{
    using var fixture = Fixture.Reopen(partialRoot); fixture.InterruptPartial(); return 9;
}
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The guarded Windows deletion acceptance requires Windows.");
var output = args is ["--output", var directory] ? Path.GetFullPath(directory) : Path.Combine(Path.GetTempPath(), "heirowInstaller-legacy-tests-" + Guid.NewGuid().ToString("N"));
if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Acceptance output must be a fresh directory.");
Directory.CreateDirectory(output);
var results = new List<object>(); var failures = 0;
var observations = new Dictionary<string, object>();
async Task Run(string name, Func<Task> test)
{
    var selected = Environment.GetEnvironmentVariable("HEIROWINSTALLER_LEGACY_TEST_CASE");
    if (selected is not null && selected != name) return;
    var timer = Stopwatch.StartNew();
    try { await test(); results.Add(new { name, passed = true, milliseconds = timer.ElapsedMilliseconds }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; results.Add(new { name, passed = false, error = error.GetType().Name + ": " + error.Message }); Console.WriteLine("FAIL " + name + ": " + error.GetType().Name + ": " + error.Message); }
}

await Run("signed completed backup removal and deterministic ledger", async () =>
{
    using var fixture = new Fixture(output);
    var result = await fixture.Execute();
    Check(result.Values[fixture.Tree.Leaf]!.StartsWith("removed:"), "Completed inventory was not removed.");
    Check(!Directory.Exists(fixture.TreeRoot) && File.Exists(fixture.Ledger), "Backup removal/ledger missing.");
    Check(File.ReadAllBytes(fixture.Ledger).SequenceEqual(fixture.LedgerBytes), "Ledger is not publisher/inventory bound.");
    Check(File.ReadAllText(fixture.Unrelated) == "keep unrelated", "Unrelated application data changed.");
});
await Run("unproven incoming quarantine retains exact bytes outside product cache", async () =>
{
    using var fixture = new Fixture(output, incoming: true);
    var bytes = File.ReadAllBytes(fixture.FilePath);
    var result = await fixture.Execute();
    Check(result.Values[fixture.Tree.Leaf]!.StartsWith("quarantined:"), "Incoming data was not quarantined.");
    Check(!Directory.Exists(fixture.TreeRoot) && File.ReadAllBytes(fixture.QuarantinedFile).SequenceEqual(bytes), "Incoming bytes were not retained.");
    Check(!fixture.QuarantinedFile.StartsWith(Path.Combine(fixture.State, fixture.Id) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Quarantine is inside uninstall cache.");
    using var package = PackageReader.Open(fixture.Package);
    var context = InstallerModuleContext.ForVerifiedPackage(fixture.Plan(package), null, package, fixture.Options());
    await new LegacyStagingCleanupModule().UninstallAsync(result, context, default);
    Check(File.Exists(fixture.QuarantinedFile) && File.Exists(fixture.Ledger), "Uninstall removed review data.");
});
await Run("one-time import refuses reappearing staging", async () =>
{
    using var fixture = new Fixture(output);
    await fixture.Execute(); fixture.WriteTree();
    var second = await fixture.Execute();
    Check(second.Values[fixture.Tree.Leaf]!.Contains("reappeared") && File.Exists(fixture.FilePath), "Reappeared files were silently adopted.");
});
await Run("a later signed redeclaration cannot reset the same leaf's one-time import", async () =>
{
    using var fixture = new Fixture(output); await fixture.Execute();
    var ledger = fixture.Ledger; var before = PackageSafety.HashFile(ledger); fixture.WriteTree();
    fixture.Tree.CompletedReceiptSha256 = new string('9', 64); fixture.Build();
    Check(fixture.Ledger == ledger, "Redeclaration changed the canonical leaf ledger key.");
    await fixture.ExpectPreserved(); Check(PackageSafety.HashFile(ledger) == before && File.Exists(fixture.FilePath), "Signed redeclaration adopted a reappeared leaf.");
});
await Run("existing unknown quarantine is preserved", async () =>
{
    using var fixture = new Fixture(output, incoming: true);
    Directory.CreateDirectory(Path.GetDirectoryName(fixture.QuarantinedFile)!); File.WriteAllText(fixture.QuarantinedFile, "unreviewed");
    await fixture.ExpectPreserved(); Check(File.ReadAllText(fixture.QuarantinedFile) == "unreviewed", "Unknown quarantine was overwritten.");
});
await Run("unknown ledger is preserved", async () =>
{
    using var fixture = new Fixture(output); Directory.CreateDirectory(Path.GetDirectoryName(fixture.Ledger)!); File.WriteAllText(fixture.Ledger, "unknown");
    await fixture.ExpectPreserved(); Check(File.ReadAllText(fixture.Ledger) == "unknown", "Unknown ledger was replaced.");
});
await Run("missing staging is not adopted", async () =>
{
    using var fixture = new Fixture(output); Directory.Delete(fixture.TreeRoot, true);
    var result = await fixture.Execute(); Check(result.Values[fixture.Tree.Leaf] == "absent" && !File.Exists(fixture.Ledger), "Absent tree was recorded as owned.");
});
foreach (var variant in new[] { "extra-hidden-file", "extra-empty-directory", "missing-file", "changed-size", "changed-same-size", "read-only-file" })
    await Run("preserve " + variant, async () =>
    {
        using var fixture = new Fixture(output);
        switch (variant)
        {
            case "extra-hidden-file": var hidden = Path.Combine(fixture.TreeRoot, ".unknown"); File.WriteAllText(hidden, "private data"); File.SetAttributes(hidden, FileAttributes.Hidden); break;
            case "extra-empty-directory": Directory.CreateDirectory(Path.Combine(fixture.TreeRoot, "unknown")); break;
            case "missing-file": File.Delete(fixture.SecondPath); break;
            case "changed-size": File.AppendAllText(fixture.FilePath, "changed"); break;
            case "changed-same-size": File.WriteAllText(fixture.FilePath, "changed data!"); break;
            case "read-only-file": File.SetAttributes(fixture.FilePath, FileAttributes.ReadOnly); break;
        }
        await fixture.ExpectPreserved();
    });
await Run("hard-linked file preserves both identities", async () =>
{
    using var fixture = new Fixture(output); var secondLink = Path.Combine(fixture.Root, "hard-link.txt");
    Check(Native.CreateHardLinkW(secondLink, fixture.FilePath, IntPtr.Zero), "Fixture hard-link creation failed.");
    await fixture.ExpectPreserved(); Check(File.ReadAllText(secondLink) == "fixture data!", "A hard-linked target changed.");
});
await Run("undeclared NTFS named stream preserves source bytes", async () =>
{
    using var fixture = new Fixture(output); File.WriteAllText(fixture.FilePath + ":owned-test-stream", "unknown stream data");
    await fixture.ExpectPreserved(); Check(File.ReadAllText(fixture.FilePath + ":owned-test-stream") == "unknown stream data", "Named stream changed.");
});
await Run("undeclared directory stream preserves the entire source tree", async () =>
{
    using var fixture = new Fixture(output); File.WriteAllText(fixture.TreeRoot + ":owned-test-stream", "unknown directory stream");
    await fixture.ExpectPreserved(); Check(File.ReadAllText(fixture.TreeRoot + ":owned-test-stream") == "unknown directory stream", "Directory stream changed.");
});
await Run("long paths support extended handles and preserve data if Windows activity query rejects them", async () =>
{
    using var fixture = new Fixture(output, incoming: true);
    var relative = "nested/" + new string('x', 105) + ".bin"; var path = PackageSafety.UnderRoot(fixture.TreeRoot, relative);
    Check(path.Length >= 260, "Fixture did not reach the Windows path boundary."); File.WriteAllText(path, "long-path fixture");
    fixture.Tree.Files.Add(new() { Path = relative, Size = new FileInfo(path).Length, Sha256 = PackageSafety.HashFile(path) }); fixture.Build();
    using (var held = LegacyStagingFileGuard.File(path, new FileInfo(path).Length, PackageSafety.HashFile(path))) held.Recheck();
    Win32Exception? unsupported = null;
    try { Check(!RunningApplications.HasResourceUsers(fixture.Tree.Files.Select(file => PackageSafety.UnderRoot(fixture.TreeRoot, file.Path)).ToArray()), "The owned long-path fixture unexpectedly has resource users."); }
    catch (Win32Exception error) { unsupported = error; observations["longPathRestartManagerNativeErrorCode"] = error.NativeErrorCode; }
    var result = await fixture.Execute();
    if (unsupported is not null)
    {
        Check(result.Values[fixture.Tree.Leaf]!.StartsWith("preserved:") && File.ReadAllText(path) == "long-path fixture" && File.Exists(fixture.FilePath) &&
            File.Exists(fixture.SecondPath) && !File.Exists(fixture.Ledger) && !Directory.Exists(LegacyStagingDefinition.QuarantineRoot(fixture.Persistent, fixture.Tree)),
            "Unsupported Windows activity query failed to preserve the complete long-path source.");
        return;
    }
    var quarantined = PackageSafety.UnderRoot(LegacyStagingDefinition.QuarantineRoot(fixture.Persistent, fixture.Tree), relative);
    Check(result.Values[fixture.Tree.Leaf]!.StartsWith("quarantined:") && File.ReadAllText(quarantined) == "long-path fixture", "Long exact paths were not retained: " + result.Values[fixture.Tree.Leaf]);
});
await Run("reparse subtree is preserved without traversal", async () =>
{
    using var fixture = new Fixture(output); var outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside);
    File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside data");
    var link = Path.Combine(fixture.TreeRoot, "unexpected-link");
    // Junction creation does not need symlink privilege and targets only this test's new directory.
    var junction = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
    junction.ArgumentList.Add("/d"); junction.ArgumentList.Add("/c"); junction.ArgumentList.Add("mklink"); junction.ArgumentList.Add("/J"); junction.ArgumentList.Add(link); junction.ArgumentList.Add(outside);
    using var process = Process.Start(junction)!; await process.WaitForExitAsync(); Check(process.ExitCode == 0 && Directory.Exists(link), "Fixture junction creation failed.");
    try { await fixture.ExpectPreserved(); Check(File.ReadAllText(Path.Combine(outside, "keep.txt")) == "outside data", "Reparse destination changed."); }
    finally { Directory.Delete(link); }
});
await Run("active owned console resource preserves whole staging tree", async () =>
{
    using var fixture = new Fixture(output);
    var ready = Path.Combine(fixture.Root, "ready"); var stop = Path.Combine(fixture.Root, "stop");
    using var child = StartSelf("--hold", fixture.FilePath, ready, stop);
    try
    {
        await WaitFor(() => File.Exists(ready), "Resource holder did not start.");
        Check(RunningApplications.HasResourceUsers([fixture.FilePath]), "Restart Manager did not report the owned active resource.");
        await fixture.ExpectPreserved(); Check(!child.HasExited, "Cleanup closed a process without consent.");
    }
    finally { File.WriteAllText(stop, "exit"); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
});
await Run("exclusive handle prevents writes and rename through deletion", async () =>
{
    using var fixture = new Fixture(output);
    using var held = LegacyStagingFileGuard.File(fixture.FilePath, fixture.Tree.Files[0].Size, fixture.Tree.Files[0].Sha256);
    await Reject<IOException>(() => { File.WriteAllText(fixture.FilePath, "mutated"); return Task.CompletedTask; });
    await Reject<IOException>(() => { File.Move(fixture.FilePath, fixture.FilePath + ".moved"); return Task.CompletedTask; });
    Check(!File.Exists(fixture.FilePath + ".moved"), "Held identity was renamed.");
});
await Run("transaction rollback restores backup and removes quarantine and ledger", async () =>
{
    using var fixture = new Fixture(output, incoming: true);
    await fixture.Execute(commit: false);
    Check(File.ReadAllText(fixture.FilePath) == "fixture data!" && File.Exists(fixture.SecondPath), "Rollback did not restore source bytes.");
    Check(!File.Exists(fixture.QuarantinedFile) && !File.Exists(fixture.Ledger), "Rollback retained newly created metadata.");
});
await Run("durable crash recovery uses exact caller-verified signed targets", async () =>
{
    using var fixture = new Fixture(output, incoming: true);
    using (var child = StartSelf("--interrupt", fixture.Root)) { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); Check(child.ExitCode == 0, "Interrupted fixture failed."); }
    Check(!File.Exists(fixture.FilePath) && File.Exists(fixture.Ledger), "Fixture did not reach the interrupted state.");
    using var package = PackageReader.Open(fixture.Package);
    var recovery = FileTransactionRecoveryContext.ForInstall(package, fixture.Plan(package), null, fixture.Options());
    var result = FileTransactionRecovery.Recover(recovery);
    Check(result.RecoveredTransactions == 1 && File.ReadAllText(fixture.FilePath) == "fixture data!", "Signed crash recovery did not restore the source.");
    Check(!File.Exists(fixture.QuarantinedFile) && !File.Exists(fixture.Ledger), "Crash rollback retained quarantine data.");
});
await Run("same-version repair cannot finalize a partially removed tree from its old receipt", async () =>
{
    using var fixture = new Fixture(output); Directory.Delete(fixture.TreeRoot, true);
    using var package = PackageReader.Open(fixture.Package);
    await new InstallerEngine().InstallAsync(package, fixture.Options()); fixture.WriteTree();
    var previous = InstallerEngine.ListInstalled(fixture.State).Single();
    using (var child = StartSelf("--interrupt-partial", fixture.Root)) { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); Check(child.ExitCode == 0, "Interrupted repair fixture failed."); }
    Check(!File.Exists(fixture.FilePath) && File.Exists(fixture.SecondPath) && !File.Exists(fixture.Ledger), "Partial fixture did not interrupt before the ledger.");
    var recovery = FileTransactionRecoveryContext.ForInstall(package, fixture.Plan(package), previous, fixture.Options());
    Check(FileTransactionRecovery.Recover(recovery).RecoveredTransactions == 1 && File.ReadAllText(fixture.FilePath) == "fixture data!", "Old matching receipt discarded a partial cleanup backup.");
    Check(InstallerEngine.ListInstalled(fixture.State).Single().Version == "1.0.0", "Recovery altered the existing product receipt.");
});
foreach (var streamKind in new[] { "file", "directory" })
    await Run("post-crash unknown " + streamKind + " stream is preserved during rollback", async () =>
    {
        using var fixture = new Fixture(output, incoming: true);
        using (var child = StartSelf("--interrupt", fixture.Root)) { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); Check(child.ExitCode == 0, "Interrupted fixture failed."); }
        var path = (streamKind == "file" ? fixture.QuarantinedFile : LegacyStagingDefinition.QuarantineRoot(fixture.Persistent, fixture.Tree)) + ":owned-after-crash";
        File.WriteAllText(path, "unknown after crash");
        using var package = PackageReader.Open(fixture.Package);
        var recovery = FileTransactionRecoveryContext.ForInstall(package, fixture.Plan(package), null, fixture.Options());
        await Reject<IOException>(() => { FileTransactionRecovery.Recover(recovery); return Task.CompletedTask; });
        Check(File.ReadAllText(path) == "unknown after crash" && Directory.GetDirectories(Path.Combine(fixture.State, ".transactions")).Length == 1, "Recovery removed unknown streams or its evidence.");
        File.Delete(path); // Remove only the exact stream just created by this fixture, then finish its normal recovery.
        Check(FileTransactionRecovery.Recover(recovery).RecoveredTransactions == 1 && File.ReadAllText(fixture.FilePath) == "fixture data!", "Owned stream teardown did not permit trusted recovery.");
    });
await Run("changed recovery publisher leaves interrupted transaction untouched", async () =>
{
    using var fixture = new Fixture(output);
    using (var child = StartSelf("--interrupt", fixture.Root)) { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); Check(child.ExitCode == 0, "Interrupted fixture failed."); }
    using var package = PackageReader.Open(fixture.Package);
    var valid = FileTransactionRecoveryContext.ForInstall(package, fixture.Plan(package), null, fixture.Options());
    var altered = JsonSerializer.Deserialize<FileTransactionRecoveryContext>(JsonSerializer.Serialize(valid, InstallerJson.Options).Replace(valid.PublisherKeySha256!, new string('a', 64)), InstallerJson.Options)!;
    var result = FileTransactionRecovery.Recover(altered);
    Check(result.RecoveredTransactions == 0 && !File.Exists(fixture.FilePath), "Journal granted authority to a different publisher.");
    Check(FileTransactionRecovery.Recover(valid).RecoveredTransactions == 1, "Valid recovery was not retained.");
});
await Run("a different signed manifest leaves the unmatched journal untouched", async () =>
{
    using var fixture = new Fixture(output);
    using (var child = StartSelf("--interrupt", fixture.Root)) { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); Check(child.ExitCode == 0, "Interrupted fixture failed."); }
    using var package = PackageReader.Open(fixture.Package);
    var recovery = FileTransactionRecoveryContext.ForInstall(package, fixture.Plan(package), null, fixture.Options());
    var changed = JsonSerializer.Deserialize<FileTransactionRecoveryContext>(JsonSerializer.Serialize(recovery, InstallerJson.Options).Replace(recovery.TargetManifestSha256, new string('b', 64)), InstallerJson.Options)!;
    Check(FileTransactionRecovery.Recover(changed).RecoveredTransactions == 0, "A different target manifest matched the interrupted transaction.");
    Check(!File.Exists(fixture.FilePath) && File.Exists(fixture.Ledger), "Mismatched signed target replayed the journal.");
    Check(FileTransactionRecovery.Recover(recovery).RecoveredTransactions == 1, "Valid recovery was not retained.");
});
await Run("same-identity recovery allowlist tampering is rejected before replay", async () =>
{
    using var fixture = new Fixture(output);
    using (var child = StartSelf("--interrupt", fixture.Root)) { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); Check(child.ExitCode == 0, "Interrupted fixture failed."); }
    using var package = PackageReader.Open(fixture.Package);
    var recovery = FileTransactionRecoveryContext.ForInstall(package, fixture.Plan(package), null, fixture.Options());
    var changed = JsonSerializer.Deserialize<FileTransactionRecoveryContext>(JsonSerializer.Serialize(recovery, InstallerJson.Options).Replace(fixture.Tree.Files[0].Sha256, new string('b', 64)), InstallerJson.Options)!;
    await Reject<CryptographicException>(() => { FileTransactionRecovery.Recover(changed); return Task.CompletedTask; });
    Check(!File.Exists(fixture.FilePath) && File.Exists(fixture.Ledger), "A modified allowlist replayed the journal.");
    Check(FileTransactionRecovery.Recover(recovery).RecoveredTransactions == 1, "Valid recovery was not retained.");
});
await Run("unsigned cleanup is rejected", async () =>
{
    using var fixture = new Fixture(output); fixture.Build(signed: false);
    using var package = PackageReader.Open(fixture.Package);
    await Reject<CryptographicException>(() => { InstallerModuleContext.ForVerifiedPackage(fixture.PlanDirect(package), null, package, fixture.Options()); return Task.CompletedTask; });
    Check(File.Exists(fixture.FilePath), "Unsigned declaration changed data.");
});
await Run("wrong external publisher pin is rejected", async () =>
{
    using var fixture = new Fixture(output); using var package = PackageReader.Open(fixture.Package); var options = fixture.Options(); options.TrustedPublisherKeySha256 = new string('c', 64);
    await Reject<CryptographicException>(() => { InstallerModuleContext.ForVerifiedPackage(fixture.PlanDirect(package), null, package, options); return Task.CompletedTask; });
});
await Run("self-signature without an expected publisher cannot grant cleanup", async () =>
{
    using var fixture = new Fixture(output); using var package = PackageReader.Open(fixture.Package); var options = fixture.Options(); options.TrustedPublisherKeySha256 = null;
    await Reject<CryptographicException>(() => { InstallerModuleContext.ForVerifiedPackage(fixture.PlanDirect(package), null, package, options); return Task.CompletedTask; });
    Check(File.Exists(fixture.FilePath), "An unpinned self-signature changed data.");
});
await Run("mutable in-memory configuration cannot replace signed authority", async () =>
{
    using var fixture = new Fixture(output); using var package = PackageReader.Open(fixture.Package);
    var plan = fixture.Plan(package); var context = InstallerModuleContext.ForVerifiedPackage(plan, null, package, fixture.Options());
    var modified = JsonSerializer.SerializeToElement(new LegacyStagingCleanupDefinition { Trees = [] }, InstallerJson.Options);
    await Reject<CryptographicException>(() => { new LegacyStagingCleanupModule().Plan(modified, context); return Task.CompletedTask; });
});
await Run("plain module context cannot grant cleanup authority", async () =>
{
    using var fixture = new Fixture(output); using var package = PackageReader.Open(fixture.Package);
    await Reject<CryptographicException>(() => { new LegacyStagingCleanupModule().Plan(fixture.Configuration(package), new(fixture.PlanDirect(package), null, true)); return Task.CompletedTask; });
});
foreach (var variant in new[] { "arbitrary-root", "glob-leaf", "traversal", "duplicate", "unproven-delete", "forged-receipt", "file-directory-overlap" })
    await Run("reject signed invalid declaration " + variant, async () =>
    {
        using var fixture = new Fixture(output);
        switch (variant)
        {
            case "arbitrary-root": fixture.ExtraConfiguration = ",\"root\":\"C:/Users\""; break;
            case "glob-leaf": fixture.Tree.Leaf = "App.backup-*"; break;
            case "traversal": fixture.Tree.Files[0].Path = "../outside.txt"; break;
            case "duplicate": fixture.Tree.Files.Add(fixture.Tree.Files[0]); break;
            case "unproven-delete": fixture.Tree.Kind = "unproven-incoming"; break;
            case "forged-receipt": fixture.Tree.CompletedReceiptId = "installed-unrelated"; break;
            case "file-directory-overlap": fixture.Tree.Files.Add(new() { Path = "nested", Size = 0, Sha256 = new string('d', 64) }); break;
        }
        fixture.Build();
        using var package = PackageReader.Open(fixture.Package);
        var context = InstallerModuleContext.ForVerifiedPackage(fixture.PlanDirect(package), null, package, fixture.Options());
        await Reject<InvalidDataException>(() => { new LegacyStagingCleanupModule().Plan(fixture.Configuration(package), context); return Task.CompletedTask; });
    });
await Run("missing product lock and missing journal cannot authorize mutations", async () =>
{
    using var fixture = new Fixture(output); using var package = PackageReader.Open(fixture.Package);
    var context = InstallerModuleContext.ForVerifiedPackage(fixture.Plan(package), null, package, fixture.Options());
    await Reject<InvalidOperationException>(() => new LegacyStagingCleanupModule().InstallAsync(fixture.Configuration(package), context, default));
    Check(File.Exists(fixture.FilePath), "Missing transaction guard mutated source.");
});
await Run("actual native engine installs cleanup module and uninstall preserves quarantine", async () =>
{
    using var fixture = new Fixture(output, incoming: true); using var package = PackageReader.Open(fixture.Package);
    await new InstallerEngine().InstallAsync(package, fixture.Options());
    Check(!File.Exists(fixture.FilePath) && File.Exists(fixture.QuarantinedFile), "Engine did not execute signed cleanup.");
    await new InstallerEngine().UninstallAsync(fixture.Id, fixture.Options());
    Check(File.Exists(fixture.QuarantinedFile) && File.Exists(fixture.Ledger) && File.ReadAllText(fixture.Unrelated) == "keep unrelated", "Uninstall removed persistent quarantine or unrelated data.");
});

File.WriteAllText(Path.Combine(output, "acceptance.json"), JsonSerializer.Serialize(new { passed = failures == 0, checks = results.Count, failures, observations, cases = results }, InstallerJson.Options));
Console.WriteLine($"Legacy cleanup acceptance: {results.Count - failures}/{results.Count} passed. Receipt: {Path.Combine(output, "acceptance.json")}");
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task Reject<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name + " was not raised.");
}
static Process StartSelf(params string[] arguments)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath)!.Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    return Process.Start(start) ?? throw new IOException("Could not start the owned console fixture.");
}
static async Task WaitFor(Func<bool> condition, string message)
{
    var timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25); Check(condition(), message);
}

sealed class Fixture : IDisposable
{
    private const string Marker = "heirowInstaller.LegacyCleanup.Tests.v1";
    private readonly bool reopened;
    public string Root { get; }
    public string Id => "legacy-cleanup.fixture";
    public string App => Path.Combine(Root, "application");
    public string State => Path.Combine(Root, "state");
    public string Package => Path.Combine(Root, "fixture.hinst");
    public string Pin { get; private set; } = "";
    public LegacyStagingTree Tree { get; private set; } = new();
    public string? ExtraConfiguration { get; set; }
    public string TreeRoot => PackageSafety.UnderRoot(App, Tree.Leaf);
    public string FilePath => Path.Combine(TreeRoot, "data.txt");
    public string SecondPath => Path.Combine(TreeRoot, "nested", "another.txt");
    public string Unrelated => Path.Combine(App, "unrelated-user-data.txt");
    public string Persistent => LegacyStagingDefinition.PersistentRoot(State, Id);
    public string Ledger => PackageSafety.UnderRoot(Persistent, LegacyStagingDefinition.LedgerRelative(Tree));
    public byte[] LedgerBytes => LegacyStagingDefinition.Ledger(Tree, Id, Pin);
    public string QuarantinedFile => PackageSafety.UnderRoot(LegacyStagingDefinition.QuarantineRoot(Persistent, Tree), "data.txt");
    public Fixture(string output, bool incoming = false)
    {
        Root = Path.Combine(output, "case-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); File.WriteAllText(Path.Combine(Root, "owner.txt"), Marker);
        using var rsa = RSA.Create(2048); File.WriteAllText(Path.Combine(Root, "test-key.pem"), rsa.ExportPkcs8PrivateKeyPem()); Pin = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        Tree = new() { Leaf = "App." + (incoming ? "incoming-" : "backup-") + "20000101-000000-" + Guid.NewGuid().ToString("N"), Kind = incoming ? "unproven-incoming" : "completed-backup" };
        if (!incoming) { Tree.CompletedReceiptId = "installed-" + Tree.Leaf[11..]; Tree.CompletedReceiptSha256 = new string('1', 64); Tree.SourceManifestSha256 = new string('2', 64); }
        WriteTree(); File.WriteAllText(Unrelated, "keep unrelated");
        Tree.Files = new[] { FilePath, SecondPath }.Select(path => new LegacyStagingFile { Path = Path.GetRelativePath(TreeRoot, path).Replace('\\', '/'), Size = new FileInfo(path).Length, Sha256 = PackageSafety.HashFile(path) }).OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
        Directory.CreateDirectory(Path.Combine(Root, "payload")); File.WriteAllText(Path.Combine(Root, "payload", "app.txt"), "new native application data"); Build();
    }
    private Fixture(string root)
    {
        Root = Path.GetFullPath(root); reopened = true;
        if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(Root), "^case-[a-f0-9]{32}$") || File.ReadAllText(Path.Combine(Root, "owner.txt")) != Marker) throw new InvalidDataException("Not an owned fixture.");
        using var package = PackageReader.Open(Package); Pin = package.PublisherKeySha256!;
        Tree = LegacyStagingDefinition.Read(Configuration(package)).Trees.Single();
    }
    public static Fixture Reopen(string root) => new(root);
    public void WriteTree() { Directory.CreateDirectory(Path.GetDirectoryName(SecondPath)!); File.WriteAllText(FilePath, "fixture data!"); File.WriteAllText(SecondPath, "nested fixture data"); }
    public void Build(bool signed = true)
    {
        var configuration = JsonSerializer.Serialize(new LegacyStagingCleanupDefinition { Trees = [Tree] }, InstallerJson.Options);
        if (ExtraConfiguration is not null) configuration = configuration[..configuration.LastIndexOf('}')] + ExtraConfiguration + "}";
        var manifest = new PackageManifest { Id = Id, Name = "Owned cleanup fixture", Publisher = "Fixture only", Version = "1.0.0", Scope = "user", LegalDocuments = LegalFixture.For(Id), Payload = [new() { Path = "app.txt" }], Modules = new() { [LegacyStagingCleanupModule.ModuleId] = JsonDocument.Parse(configuration).RootElement.Clone() } };
        var file = Path.Combine(Root, "manifest.json"); File.WriteAllText(file, JsonSerializer.Serialize(manifest, InstallerJson.Options));
        if (File.Exists(Package)) File.Delete(Package);
        PackageBuilder.Build(file, Path.Combine(Root, "payload"), Package, new() { SigningKeyPath = signed ? Path.Combine(Root, "test-key.pem") : null });
    }
    public JsonElement Configuration(PackageReader package) => package.Manifest.Modules[LegacyStagingCleanupModule.ModuleId];
    public InstallOptions Options() => new() { TargetDirectory = App, StateRoot = State, Scope = "user", Isolated = true, RegisterProduct = false, RequireSignature = true, TrustedPublisherKeySha256 = Pin, Features = [] };
    public InstallPlan Plan(PackageReader package) => new InstallerEngine().Plan(package, Options());
    public InstallPlan PlanDirect(PackageReader package) => new() { Manifest = package.Manifest, InstallDirectory = App, Scope = "user", Files = package.Manifest.Payload, Features = [] };
    public async Task<ModuleSnapshot> Execute(bool commit = true, bool interrupt = false)
    {
        using var package = PackageReader.Open(Package); var options = Options(); var plan = Plan(package);
        var previous = InstallerEngine.ListInstalled(State).SingleOrDefault();
        var recovery = FileTransactionRecoveryContext.ForInstall(package, plan, previous, options);
        var lockRoot = Path.Combine(State, ".locks"); Directory.CreateDirectory(lockRoot);
        using var productLock = new FileStream(Path.Combine(lockRoot, Id + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var transaction = new InstallTransaction(Path.Combine(State, ".transactions", Guid.NewGuid().ToString("N")), recovery);
        var context = InstallerModuleContext.ForVerifiedPackage(plan, transaction, package, options, previous, productLockHeld: true);
        var snapshot = await new LegacyStagingCleanupModule().InstallAsync(Configuration(package), context, default);
        if (interrupt) Environment.Exit(0); // Intentional abrupt owned child exit exercises durable recovery, never a product process.
        if (commit) transaction.Commit(); else transaction.Rollback();
        return snapshot;
    }
    public void InterruptPartial()
    {
        using var package = PackageReader.Open(Package); var options = Options(); var plan = Plan(package);
        var previous = InstallerEngine.ListInstalled(State).Single();
        var recovery = FileTransactionRecoveryContext.ForInstall(package, plan, previous, options);
        using var productLock = new FileStream(Path.Combine(State, ".locks", Id + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var transaction = new InstallTransaction(Path.Combine(State, ".transactions", Guid.NewGuid().ToString("N")), recovery);
        var declared = Tree.Files.Single(file => file.Path == "data.txt");
        using var file = LegacyStagingFileGuard.File(FilePath, declared.Size, declared.Sha256);
        transaction.DeleteHeldFile(file);
        Environment.Exit(0);
    }
    public async Task ExpectPreserved()
    {
        var beforeLedger = File.Exists(Ledger) ? PackageSafety.HashFile(Ledger) : null;
        var result = await Execute();
        var afterLedger = File.Exists(Ledger) ? PackageSafety.HashFile(Ledger) : null;
        if (!result.Values[Tree.Leaf]!.StartsWith("preserved:") || !Directory.Exists(TreeRoot) || beforeLedger != afterLedger)
            throw new InvalidOperationException("An unverified tree was not wholly preserved: " + result.Values[Tree.Leaf]);
    }
    public void Dispose()
    {
        if (reopened || !Directory.Exists(Root)) return;
        if (File.ReadAllText(Path.Combine(Root, "owner.txt")) != Marker) throw new IOException("Fixture owner marker changed; cleanup refused.");
        PackageSafety.EnsureNoReparsePoints(Root);
        foreach (var item in Directory.EnumerateFileSystemEntries(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            var attributes = File.GetAttributes(item);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture contains a remaining reparse point; cleanup refused.");
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(item, attributes & ~FileAttributes.ReadOnly); // Own test fixture teardown only.
        }
        Directory.Delete(Root, true);
    }
}
static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateHardLinkW(string newName, string existingName, IntPtr security);
}
