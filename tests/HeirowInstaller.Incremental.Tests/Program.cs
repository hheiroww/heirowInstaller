using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;

if (args is ["crash-download", var downloadRoot, _])
{
    var feed = JsonSerializer.Deserialize<UpdateFeed>(File.ReadAllText(Path.Combine(downloadRoot, "feed.json")), InstallerJson.Options)!;
    using var workspace = new IncrementalDownloadWorkspace(Path.Combine(downloadRoot, "downloads"), feed);
    File.WriteAllText(workspace.PackagePath + "." + Guid.NewGuid().ToString("N") + ".download", "incomplete owned download");
    Environment.Exit(91);
    return;
}

if (args.Length == 3 && args[0] is "crash-apply" or "crash-install")
{
    var applyRoot = args[1]; var applyPhase = args[2];
    using var old = PackageReader.Open(Path.Combine(applyRoot, "before.hinst"));
    var options = new InstallOptions
    {
        StateRoot = Path.Combine(applyRoot, "state"), TargetDirectory = Path.Combine(applyRoot, "app"), Scope = "user", Isolated = true,
        RegisterProduct = false, RequireSignature = true, TrustedPublisherKeySha256 = old.PublisherKeySha256, Features = []
    };
    if (old.Manifest.UpdateCompanion is not null) options.Features = [old.Manifest.UpdateCompanion.Feature];
    var progress = new ImmediateProgress(value =>
    {
        if ((applyPhase == "complete" && value.Phase == "complete") || (applyPhase == "install" && value.Phase == "install" && value.Message == "app.bin")) Environment.Exit(91);
    });
    if (args[0] == "crash-apply")
    {
        var feed = JsonSerializer.Deserialize<UpdateFeed>(File.ReadAllText(Path.Combine(applyRoot, "feed.json")), InstallerJson.Options)!;
        using var client = new UpdateClient(requireIncrementalUpdates: true);
        await client.ApplyAsync(Path.Combine(applyRoot, "update.hupd"), feed, options, progress);
    }
    else
    {
        var plan = new InstallerEngine().Plan(old, options);
        options.FileRecoveryContext = FileTransactionRecoveryContext.ForInstall(old, plan, null, options);
        await new InstallerEngine().InstallAsync(old, options, progress);
    }
    throw new Exception("Owned crash fixture missed its install checkpoint.");
}

if (args is ["crash", var crashRoot, var mode])
{
    var context = JsonSerializer.Deserialize<FileTransactionRecoveryContext>(File.ReadAllText(Path.Combine(crashRoot, "context.json")), InstallerJson.Options)!;
    using var transaction = new InstallTransaction(Path.Combine(context.StateRoot, ".transactions", Guid.NewGuid().ToString("N")), context);
    if (mode == "stage") { File.WriteAllText(Path.Combine(transaction.Root, "extracted-but-not-installed.bin"), "owned scratch"); Environment.Exit(91); }
    transaction.ReplaceFile(Path.Combine(context.InstallDirectory, "changed.txt"), Path.Combine(crashRoot, "new-changed.txt"));
    transaction.DeleteFile(Path.Combine(context.InstallDirectory, "removed.txt"));
    transaction.ReplaceFile(Path.Combine(context.InstallDirectory, "created.txt"), Path.Combine(crashRoot, "new-created.txt"));
    if (mode == "external") transaction.AddRollback(() => { });
    if (mode == "partial-temp")
    {
        var line = File.ReadLines(Path.Combine(transaction.Root, "file-transaction.jsonl")).First(value => value.Contains("\"temporary\""));
        using var parsed = JsonDocument.Parse(line);
        File.WriteAllText(parsed.RootElement.GetProperty("file").GetProperty("temporary").GetString()!, "partial");
    }
    // Abruptly exits this owned console fixture only, deliberately skipping using/finally to test restart recovery.
    Environment.Exit(91);
    return;
}

var output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("artifacts", "incremental-acceptance", Guid.NewGuid().ToString("N")));
if (Directory.Exists(output)) throw new IOException("Use a fresh incremental acceptance output directory.");
Directory.CreateDirectory(output);
var checks = 0;
var tests = new List<string>();
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
async Task Reject<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); } catch (T) { checks++; return; }
    throw new Exception(message);
}
Task RejectNow<T>(Action action, string message) where T : Exception => Reject<T>(() => { action(); return Task.CompletedTask; }, message);
async Task Test(string name, Func<Task> action)
{
    await action(); tests.Add(name); Console.WriteLine("PASS " + name);
}

await Test("changed-only compressed transition and exact original signed metadata", async () =>
{
    using var fixture = new PackageFixture(output);
    using var update = IncrementalUpdateReader.Open(fixture.Delta);
    update.VerifySignature(fixture.Pin); await update.VerifyAsync();
    var changes = update.Transition.Changes;
    Check(changes.Count == 3 && changes.Single(change => change.Path == "app.bin").Kind == "change" &&
        changes.Single(change => change.Path == "new/created.txt").Kind == "create" && changes.Single(change => change.Path == "removed.txt").Kind == "remove", "Create/change/remove metadata was not complete.");
    using var zip = ZipFile.OpenRead(fixture.Delta);
    Check(zip.Entries.Count == 8 && zip.GetEntry("payload/common.dat") is null && zip.GetEntry("payload/removed.txt") is null && zip.GetEntry("payload/addon.bin") is null, "Delta carried unchanged or removed payload.");
    var changed = zip.GetEntry("payload/app.bin")!;
    Check(changed.CompressedLength < changed.Length && new FileInfo(fixture.Delta).Length < new FileInfo(fixture.After).Length / 4, "Changed payload was not compressed or a complete installer was included.");
    await fixture.InstallBase();
    var reconstructed = Path.Combine(fixture.Root, "reconstructed.hinst");
    await update.ReconstructAsync(fixture.Installed, reconstructed, fixture.Pin);
    using var full = PackageReader.Open(reconstructed); full.VerifySignature(fixture.Pin, true); await full.VerifyAsync();
    Check(Entry(fixture.After, "manifest.json").SequenceEqual(Entry(reconstructed, "manifest.json")) && Entry(fixture.After, "signature.json").SequenceEqual(Entry(reconstructed, "signature.json")), "Reconstruction reserialized or resigned original target metadata.");
    Check(!File.Exists(Path.Combine(fixture.App, "addon.bin")) && Entry(reconstructed, "payload/addon.bin").SequenceEqual(Entry(fixture.Before, "payload/addon.bin")), "Uninstalled optional bytes were not safely retained in the full repair cache.");
});

await Test("signature, publisher, metadata, ZIP path, duplicate, link and payload tamper rejection", async () =>
{
    using var fixture = new PackageFixture(output);
    using var other = RSA.Create(2048);
    var wrongPin = Convert.ToHexString(SHA256.HashData(other.ExportSubjectPublicKeyInfo()));
    using (var update = IncrementalUpdateReader.Open(fixture.Delta))
        await RejectNow<CryptographicException>(() => update.VerifySignature(wrongPin), "A foreign pinned publisher was accepted.");
    foreach (var mutation in new[] { "signature", "transition", "payload", "duplicate", "traversal", "backslash", "link", "unchanged", "oversized", "json-duplicate" })
    {
        var bad = Path.Combine(fixture.Root, mutation + ".hupd");
        Rewrite(fixture.Delta, bad, entries =>
        {
            var index = entries.FindIndex(entry => entry.Name == "payload/app.bin");
            switch (mutation)
            {
                case "signature":
                    var signature = JsonSerializer.Deserialize<PackageSignature>(entries.Single(entry => entry.Name == "update-signature.json").Bytes, InstallerJson.Options)!;
                    signature.Signature = Convert.ToBase64String(new byte[256]);
                    entries.RemoveAll(entry => entry.Name == "update-signature.json"); entries.Add(new("update-signature.json", JsonSerializer.SerializeToUtf8Bytes(signature, InstallerJson.Options))); break;
                case "transition":
                    var transition = JsonSerializer.Deserialize<IncrementalUpdateManifest>(entries.Single(entry => entry.Name == "update.json").Bytes, InstallerJson.Options)!;
                    transition.Changes.RemoveAt(0);
                    entries.RemoveAll(entry => entry.Name is "update.json" or "update-signature.json");
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(transition, InstallerJson.Options);
                    entries.Add(new("update.json", bytes)); entries.Add(new("update-signature.json", fixture.Sign(bytes))); break;
                case "payload": entries[index].Bytes[0] ^= 1; break;
                case "duplicate": entries.Add(new("PAYLOAD/APP.BIN", entries[index].Bytes)); break;
                case "traversal": entries.Add(new("../escape", [1])); break;
                case "backslash": entries.Add(new("payload\\escape", [1])); break;
                case "link": entries[index] = entries[index] with { Attributes = unchecked((int)0xa1ff0000) }; break;
                case "unchanged": entries.Add(new("payload/common.dat", Entry(fixture.Before, "payload/common.dat"))); break;
                case "oversized": entries.RemoveAll(entry => entry.Name == "base/manifest.json"); entries.Add(new("base/manifest.json", new byte[4 * 1024 * 1024 + 1])); break;
                case "json-duplicate":
                    var raw = Encoding.UTF8.GetString(entries.Single(entry => entry.Name == "update.json").Bytes);
                    entries.RemoveAll(entry => entry.Name == "update.json"); entries.Add(new("update.json", Encoding.UTF8.GetBytes(raw.Insert(raw.IndexOf('{') + 1, "\"productId\":\"other\",")))); break;
            }
        });
        await Reject<Exception>(async () => { using var update = IncrementalUpdateReader.Open(bad); update.VerifySignature(fixture.Pin); await update.VerifyAsync(); }, "Accepted tampered update: " + mutation);
    }
});

await Test("base ownership, unchanged-file tamper and unowned create collisions fail before mutation", async () =>
{
    using var fixture = new PackageFixture(output); await fixture.InstallBase();
    using var update = IncrementalUpdateReader.Open(fixture.Delta);
    var common = Path.Combine(fixture.App, "common.dat"); var bytes = File.ReadAllBytes(common); bytes[0] ^= 1; File.WriteAllBytes(common, bytes);
    await Reject<CryptographicException>(() => update.ReconstructAsync(fixture.Installed, Path.Combine(fixture.Root, "bad.hinst"), fixture.Pin), "Changed unchanged file was silently accepted.");
    File.WriteAllBytes(common, Entry(fixture.Before, "payload/common.dat"));
    Directory.CreateDirectory(Path.Combine(fixture.App, "new")); var collision = Path.Combine(fixture.App, "new", "created.txt"); File.WriteAllText(collision, "unowned user data");
    await Reject<IOException>(() => update.ReconstructAsync(fixture.Installed, Path.Combine(fixture.Root, "blocked.hinst"), fixture.Pin), "Unowned new destination was overwritten.");
    Check(File.ReadAllText(collision) == "unowned user data" && fixture.Installed.Version == "1.0.0", "Failed preflight mutated the installation.");
    var wrongBase = fixture.Installed; wrongBase.Version = "0.9.0";
    await Reject<InvalidDataException>(() => update.ReconstructAsync(wrongBase, Path.Combine(fixture.Root, "wrong.hinst"), fixture.Pin), "A different installed base version was accepted.");
});

await Test("download/apply uses only .hupd, preserves unchanged files, repairs locally and cleans owned stages", async () =>
{
    using var fixture = new PackageFixture(output); await fixture.InstallBase();
    var stable = Path.Combine(fixture.App, "common.dat"); File.SetLastWriteTimeUtc(stable, new DateTime(2002, 2, 2, 0, 0, 0, DateTimeKind.Utc)); var timestamp = File.GetLastWriteTimeUtc(stable);
    var transport = new FixtureTransport(fixture.Feed, fixture.Delta);
    using var client = new UpdateClient(transport: transport, requireIncrementalUpdates: true);
    var check = await client.CheckAsync("https://updates.example/feed?channel=stable", "1.0.0", fixture.Id);
    Check(check.Available && transport.Requests[0].Query.Contains("fromVersion=1.0.0") && transport.Requests[0].Query.Contains("updateProtocol=hupd1") && transport.Requests[0].Query.Contains("channel=stable"), "Base/protocol selector did not preserve the original query.");
    var downloaded = await client.DownloadAsync(check.Feed, Path.Combine(fixture.Root, "downloads"));
    Check(downloaded.EndsWith(".hupd", StringComparison.Ordinal) && transport.Requests.Count == 2 && transport.Requests[1].AbsolutePath.EndsWith(".hupd"), "An installer was downloaded instead of the incremental package.");
    File.Delete(fixture.Key);
    var result = await client.ApplyAsync(downloaded, check.Feed, fixture.Options());
    Check(result.Version == "2.0.0" && File.ReadAllBytes(Path.Combine(fixture.App, "app.bin")).SequenceEqual(Entry(fixture.After, "payload/app.bin")), "Changed file was not applied.");
    Check(!File.Exists(Path.Combine(fixture.App, "removed.txt")) && File.Exists(Path.Combine(fixture.App, "new", "created.txt")), "Create/remove operations did not apply.");
    Check(File.GetLastWriteTimeUtc(stable) == timestamp, "An unchanged owned file was copied again.");
    var installed = fixture.Installed; using (var repair = PackageReader.Open(installed.PackagePath)) { repair.VerifySignature(fixture.Pin, true); await repair.VerifyAsync(); }
    File.Delete(Path.Combine(fixture.App, "app.bin"));
    await new InstallerEngine().RepairAsync(fixture.Id, fixture.Options());
    Check(File.Exists(Path.Combine(fixture.App, "app.bin")), "Reconstructed full package could not repair the installed app without a signing key or network installer.");
    Check(!Directory.EnumerateDirectories(Path.Combine(fixture.State, ".updates", fixture.Id)).Any() && !Directory.EnumerateDirectories(Path.Combine(fixture.State, ".transactions")).Any(), "Completed update stages were not cleaned.");
});

await Test("file mode rejects installer fallback and duplicate/conflicting feed selectors", async () =>
{
    using var fixture = new PackageFixture(output);
    var legacy = new UpdateFeed { ProductId = fixture.Id, Version = "2.0.0", InstallerUrl = "https://updates.example/Setup.exe", Sha256 = fixture.Feed.Sha256, PackageType = "exe" };
    var transport = new FixtureTransport(legacy, fixture.Delta);
    using var client = new UpdateClient(transport: transport, requireIncrementalUpdates: true);
    await Reject<InvalidDataException>(() => client.CheckAsync("https://updates.example/feed", "1.0.0", fixture.Id), "File-only check accepted an EXE fallback.");
    var count = transport.Requests.Count;
    await Reject<InvalidDataException>(() => client.DownloadAsync(legacy, Path.Combine(fixture.Root, "none")), "File-only download fetched Setup.");
    Check(transport.Requests.Count == count, "Rejected fallback still issued a network request.");
    foreach (var query in new[] { "?fromVersion=other", "?updateProtocol=old", "?fromVersion=1.0.0&fromVersion=1.0.0" })
        await Reject<InvalidDataException>(() => client.CheckAsync("https://updates.example/feed" + query, "1.0.0", fixture.Id), "Conflicting transition selector was accepted.");
    legacy.Version = "1.0.0";
    Check(!(await client.CheckAsync("https://updates.example/feed", "1.0.0", fixture.Id)).Available, "A current legacy feed became a false update error during a publication transition.");
    var oldJson = JsonSerializer.Serialize(new UpdateFeed(), InstallerJson.Options);
    Check(!oldJson.Contains("baseVersion") && !oldJson.Contains("baseManifestSha256") && !oldJson.Contains("targetManifestSha256"), "Nullable feed additions changed legacy JSON.");
});

await Test("interrupted downloads are collected while active and ready pending packages remain", async () =>
{
    using var fixture = new PackageFixture(output); await fixture.InstallBase();
    File.WriteAllText(Path.Combine(fixture.Root, "feed.json"), JsonSerializer.Serialize(fixture.Feed, InstallerJson.Options));
    var downloads = Path.Combine(fixture.Root, "downloads");
    using (var active = new IncrementalDownloadWorkspace(downloads, fixture.Feed))
        Check(IncrementalDownloadWorkspace.CleanupIncomplete(downloads, fixture.Id) == 0 && Directory.Exists(active.Root), "Cleanup removed an active download.");
    string pending;
    using (var ready = new IncrementalDownloadWorkspace(downloads, fixture.Feed)) { File.Copy(fixture.Delta, ready.PackagePath); ready.Complete(); pending = ready.PackagePath; }
    await Crash(fixture.Root, "unused", "crash-download");
    var parent = Path.Combine(downloads, ".hupd-downloads", fixture.Id); var foreign = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(foreign); File.WriteAllText(Path.Combine(foreign, "App.backup"), "foreign");
    Check(IncrementalDownloadWorkspace.CleanupIncomplete(downloads, fixture.Id) == 1 && File.Exists(pending) && File.Exists(Path.Combine(foreign, "App.backup")), "Download cleanup did not distinguish abandoned, ready and unowned data.");
    var transport = new FixtureTransport(fixture.Feed, fixture.Delta); using var client = new UpdateClient(transport: transport, requireIncrementalUpdates: true);
    var package = await client.DownloadAsync(fixture.Feed, downloads); var appliedRoot = Path.GetDirectoryName(package)!;
    await client.ApplyAsync(package, fixture.Feed, fixture.Options());
    Check(!Directory.Exists(appliedRoot) && File.Exists(pending), "Apply cleanup removed another ready pending package or kept its consumed download.");
});

await Test("real incremental apply restarts after both partial mutation and late receipt commit", async () =>
{
    foreach (var phase in new[] { "install", "complete" })
    {
        using var fixture = new PackageFixture(output); await fixture.InstallBase();
        File.WriteAllText(Path.Combine(fixture.Root, "feed.json"), JsonSerializer.Serialize(fixture.Feed, InstallerJson.Options));
        File.WriteAllText(Path.Combine(fixture.Root, "App.backup"), "unrelated user backup");
        await Crash(fixture.Root, phase, "crash-apply");
        Check(Directory.GetDirectories(Path.Combine(fixture.State, ".transactions")).Length == 1 && Directory.GetDirectories(Path.Combine(fixture.State, ".updates", fixture.Id)).Length == 1, "The actual interrupted updater did not leave its owned recovery workspace.");
        Check(fixture.Installed.Version == (phase == "complete" ? "2.0.0" : "1.0.0"), "Crash checkpoint did not bind the expected receipt.");
        using var client = new UpdateClient(requireIncrementalUpdates: true);
        var result = await client.ApplyAsync(fixture.Delta, fixture.Feed, fixture.Options());
        Check(result.Version == "2.0.0" && fixture.Installed.Version == "2.0.0", "Restart did not recover then finish the real incremental update.");
        Check(Directory.GetDirectories(Path.Combine(fixture.State, ".transactions")).Length == 0 && Directory.GetDirectories(Path.Combine(fixture.State, ".updates", fixture.Id)).Length == 0, "Restart did not clean exact owned stages.");
        Check(File.ReadAllText(Path.Combine(fixture.Root, "App.backup")) == "unrelated user backup", "Restart cleanup removed an unrelated backup name.");
    }
});

await Test("initial signed installation crash recovers its application files and extraction workspace", async () =>
{
    using var fixture = new PackageFixture(output);
    await Crash(fixture.Root, "install", "crash-install");
    using var package = PackageReader.Open(fixture.Before); var options = fixture.Options(); var plan = new InstallerEngine().Plan(package, options);
    var context = FileTransactionRecoveryContext.ForInstall(package, plan, null, options);
    var result = await FileTransactionRecovery.RecoverAsync(context);
    Check(result.RecoveredTransactions == 1 && !InstallerEngine.ListInstalled(fixture.State).Any() && !Directory.Exists(fixture.App), "Interrupted first-install payload was not rolled back.");
    Check(Directory.GetDirectories(Path.Combine(fixture.State, ".transactions")).Length == 0, "Initial extraction workspace was not removed.");
});

await Test("wrong feed manifest binding and opaque legacy backends are refused", async () =>
{
    using var fixture = new PackageFixture(output); await fixture.InstallBase();
    var feed = fixture.Feed; feed.TargetManifestSha256 = new string('0', 64);
    using var client = new UpdateClient(requireIncrementalUpdates: true);
    await Reject<InvalidDataException>(() => client.ApplyAsync(fixture.Delta, feed, fixture.Options()), "Feed/transition manifest mismatch was accepted.");
    using var update = IncrementalUpdateReader.Open(fixture.Delta); var installed = fixture.Installed; installed.IsMsi = true;
    await Reject<NotSupportedException>(() => update.ReconstructAsync(installed, Path.Combine(fixture.Root, "opaque.hinst"), fixture.Pin), "An opaque MSI receipt was treated as managed file ownership.");
    var legacySource = Path.Combine(fixture.Root, "legacy-source"); Directory.CreateDirectory(legacySource); File.WriteAllText(Path.Combine(legacySource, "original.msi"), "fixture database bytes only");
    foreach (var version in new[] { "1.0.0", "2.0.0" })
    {
        var manifest = new PackageManifest { Id = "opaque.fixture", Name = "Opaque fixture", Publisher = "Fixture", Version = version, LegacyMsi = new() { PayloadPath = "original.msi" } };
        var file = Path.Combine(fixture.Root, "legacy-" + version + ".json"); File.WriteAllText(file, JsonSerializer.Serialize(manifest, InstallerJson.Options));
        PackageBuilder.Build(file, legacySource, Path.Combine(fixture.Root, "legacy-" + version + ".hinst"), new() { SigningKeyPath = fixture.Key });
    }
    await Reject<NotSupportedException>(() => IncrementalUpdateBuilder.BuildAsync(Path.Combine(fixture.Root, "legacy-1.0.0.hinst"), Path.Combine(fixture.Root, "legacy-2.0.0.hinst"), Path.Combine(fixture.Root, "legacy.hupd"), new() { SigningKeyPath = fixture.Key }), "Opaque MSI source produced a delta.");
});

await Test("signed component roots update and recover exact app plus plugin files without relocation authority", async () =>
{
    using var fixture = new PackageFixture(output, components: true); await fixture.InstallBase();
    var old = fixture.Installed; var plugin = old.Files.Single(file => file.Path.EndsWith("fixture.vst3")); var pluginPath = PayloadDestinations.ResolveInstalled(old, plugin);
    var common = old.Files.Single(file => file.Path == "vst3/Contents/common.bin"); var commonPath = PayloadDestinations.ResolveInstalled(old, common); File.SetLastWriteTimeUtc(commonPath, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)); var timestamp = File.GetLastWriteTimeUtc(commonPath);
    File.WriteAllText(Path.Combine(fixture.Root, "feed.json"), JsonSerializer.Serialize(fixture.Feed, InstallerJson.Options));
    await Crash(fixture.Root, "install", "crash-apply");
    using var client = new UpdateClient(requireIncrementalUpdates: true); await client.ApplyAsync(fixture.Delta, fixture.Feed, fixture.Options());
    var installed = fixture.Installed; var currentPlugin = installed.Files.Single(file => file.Path.EndsWith("fixture.vst3"));
    Check(currentPlugin.TargetRoot == "vst3" && File.ReadAllBytes(PayloadDestinations.ResolveInstalled(installed, currentPlugin)).SequenceEqual(Entry(fixture.After, "payload/" + currentPlugin.Path)), "Component payload was not updated at its signed root.");
    Check(File.GetLastWriteTimeUtc(commonPath) == timestamp && !File.Exists(Path.Combine(Path.GetDirectoryName(commonPath)!, "old.bin")) && File.Exists(Path.Combine(Path.GetDirectoryName(commonPath)!, "new.bin")), "Unchanged/create/remove component semantics failed.");
    using var update = IncrementalUpdateReader.Open(fixture.Delta);
    Check(update.Transition.Changes.Any(change => change.Path == currentPlugin.Path && change.OldTargetRoot == "vst3" && change.NewTargetRoot == "vst3"), "External per-file old/new roots were not included in transition metadata.");
    var moved = JsonSerializer.Deserialize<PackageManifest>(Entry(fixture.After, "manifest.json"), InstallerJson.Options)!; moved.Version = "3.0.0"; moved.InstallationRoots![0].RelativeDirectory = "VST3/OtherProduct.vst3";
    var manifest = Path.Combine(fixture.Root, "relocated.json"); File.WriteAllText(manifest, JsonSerializer.Serialize(moved, InstallerJson.Options)); var package = Path.Combine(fixture.Root, "relocated.hinst"); PackageBuilder.Build(manifest, Path.Combine(fixture.Root, "source-2.0.0"), package, new() { SigningKeyPath = fixture.Key });
    await Reject<InvalidDataException>(() => IncrementalUpdateBuilder.BuildAsync(fixture.After, package, Path.Combine(fixture.Root, "relocated.hupd"), new() { SigningKeyPath = fixture.Key }), "A signed update silently relocated an existing component root.");
});

await Test("shared updater bytes stay out of app roots and unchanged deltas while reference recovery stays bound", async () =>
{
    using var fixture = new PackageFixture(output, shared: true); await fixture.InstallBase();
    Check(!File.Exists(Path.Combine(fixture.App, "heirowUpdater.exe")) && !fixture.Installed.Files.Any(file => file.Path == "heirowUpdater.exe"), "Shared runtime was copied into the app root.");
    using (var zip = ZipFile.OpenRead(fixture.Delta)) Check(zip.GetEntry("payload/heirowUpdater.exe") is null, "Unchanged shared runtime was carried in the incremental payload.");
    File.WriteAllText(Path.Combine(fixture.Root, "feed.json"), JsonSerializer.Serialize(fixture.Feed, InstallerJson.Options));
    await Crash(fixture.Root, "complete", "crash-apply");
    using var client = new UpdateClient(requireIncrementalUpdates: true); await client.ApplyAsync(fixture.Delta, fixture.Feed, fixture.Options());
    Check(fixture.Installed.Version == "2.0.0" && !Directory.GetDirectories(Path.Combine(fixture.State, ".transactions")).Any(), "Late shared-runtime commit recovery failed.");
    var configuration = JsonSerializer.Deserialize<UpdateCompanionConfiguration>(File.ReadAllText(Path.Combine(fixture.App, "heirowUpdater.json")), InstallerJson.Options)!;
    using var package = PackageReader.Open(fixture.Installed.PackagePath); SharedUpdaterRuntime.ValidateBinding(configuration, package.Manifest); var executable = SharedUpdaterRuntime.Resolve(configuration);
    Check(File.Exists(executable) && configuration.UpdateMode == "files" && !File.Exists(Path.Combine(fixture.App, "heirowUpdater.exe")), "Shared runtime/profile binding did not survive the file update.");
});

await Test("crash restart restores changed/deleted files, removes created files and partial scratch", async () =>
{
    foreach (var mode in new[] { "files", "partial-temp", "stage" })
    {
        using var fixture = new RecoveryFixture(output); await Crash(fixture.Root, mode);
        var recovered = await FileTransactionRecovery.RecoverAsync(fixture.Context);
        Check(File.ReadAllText(Path.Combine(fixture.App, "changed.txt")) == "before changed" && File.ReadAllText(Path.Combine(fixture.App, "removed.txt")) == "before removed" && !File.Exists(Path.Combine(fixture.App, "created.txt")), "Restart recovery did not restore the original application.");
        Check(recovered.RemovedWorkspaces == 1 && !Directory.EnumerateDirectories(Path.Combine(fixture.State, ".transactions")).Any(), "Recovered workspace was left behind.");
        Check(!Directory.EnumerateFiles(fixture.App, "*.tmp").Any(), "Owned partial copy was not cleaned.");
        Check(File.ReadAllText(fixture.Foreign) == "unrelated App.backup data", "Recovery touched an unrelated backup name.");
    }
});

await Test("active lease and foreign/unmarked directories are preserved", async () =>
{
    using var fixture = new RecoveryFixture(output);
    var root = Path.Combine(fixture.State, ".transactions", Guid.NewGuid().ToString("N"));
    using (var transaction = new InstallTransaction(root, fixture.Context))
    {
        transaction.ReplaceFile(Path.Combine(fixture.App, "changed.txt"), Path.Combine(fixture.Root, "new-changed.txt"));
        var recovery = FileTransactionRecovery.Recover(fixture.Context);
        Check(recovery.ActiveOrUnrecognizedWorkspaces == 1 && File.ReadAllText(Path.Combine(fixture.App, "changed.txt")) == "after changed", "Recovery interfered with a live transaction.");
    }
    var foreign = Path.Combine(fixture.State, ".transactions", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(foreign); File.WriteAllText(Path.Combine(foreign, "App.backup"), "foreign");
    FileTransactionRecovery.Recover(fixture.Context);
    Check(File.Exists(Path.Combine(foreign, "App.backup")), "Cleanup deleted an unmarked directory.");
    using (var workspace = IncrementalUpdateWorkspace.Create(fixture.Context))
        Check(IncrementalUpdateWorkspace.CleanupAbandoned(fixture.Context).Preserved == 1, "Cleanup removed an active update workspace.");
    var abandoned = IncrementalUpdateWorkspace.Create(fixture.Context); abandoned.KeepForRecovery(); var abandonedPath = abandoned.Root; abandoned.Dispose();
    Check(IncrementalUpdateWorkspace.CleanupAbandoned(fixture.Context).Removed == 1 && !Directory.Exists(abandonedPath), "Abandoned owned reconstruction workspace was not collected.");
    await Task.CompletedTask;
});

await Test("tampered rollback backups and journal destinations cannot overwrite unrelated data", async () =>
{
    using (var fixture = new RecoveryFixture(output))
    {
        await Crash(fixture.Root, "files");
        var transaction = Directory.GetDirectories(Path.Combine(fixture.State, ".transactions")).Single();
        var backup = Directory.GetFiles(Path.Combine(transaction, "rollback")).First(); File.WriteAllText(backup, "tampered");
        await Reject<CryptographicException>(() => FileTransactionRecovery.RecoverAsync(fixture.Context), "Tampered rollback backup was restored.");
        Check(Directory.Exists(transaction) && File.ReadAllText(fixture.Foreign) == "unrelated App.backup data", "Failed recovery discarded evidence or unrelated data.");
    }
    using (var fixture = new RecoveryFixture(output))
    {
        await Crash(fixture.Root, "files");
        var transaction = Directory.GetDirectories(Path.Combine(fixture.State, ".transactions")).Single(); var journal = Path.Combine(transaction, "file-transaction.jsonl");
        var lines = File.ReadAllLines(journal).Select(line => JsonSerializer.Deserialize<FileTransactionEvent>(line, InstallerJson.Options)!).ToArray();
        lines.First(item => item.File is not null).File!.Destination = fixture.Foreign;
        File.WriteAllText(journal, string.Join('\n', lines.Select(item => JsonSerializer.Serialize(item, new JsonSerializerOptions(InstallerJson.Options) { WriteIndented = false }))) + "\n");
        await Reject<FileTransactionRecoveryRequiredException>(() => FileTransactionRecovery.RecoverAsync(fixture.Context), "A journal destination granted rollback authority.");
        Check(File.ReadAllText(fixture.Foreign) == "unrelated App.backup data" && Directory.Exists(transaction), "Journal tampering modified unrelated data or discarded recovery.");
    }
});

await Test("owned-workspace cleanup rejects a real directory reparse point", async () =>
{
    using var fixture = new RecoveryFixture(output);
    var target = Path.Combine(fixture.Root, "foreign-directory"); Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "must-stay.txt"), "foreign target");
    var parent = Path.Combine(fixture.State, ".updates", fixture.Context.ProductId); Directory.CreateDirectory(parent); var link = Path.Combine(parent, Guid.NewGuid().ToString("N"));
    if (OperatingSystem.IsWindows())
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new Exception("Owned junction fixture could not start: " + await process.StandardError.ReadToEndAsync());
    }
    else Directory.CreateSymbolicLink(link, target);
    try
    {
        await RejectNow<IOException>(() => IncrementalUpdateWorkspace.CleanupAbandoned(fixture.Context), "Cleanup followed a directory reparse point.");
        Check(File.ReadAllText(Path.Combine(target, "must-stay.txt")) == "foreign target", "Cleanup touched the reparse target.");
    }
    finally { Directory.Delete(link); }
});

await Test("external metadata uncertainty is surfaced and rollback errors retain original backups", async () =>
{
    using (var fixture = new RecoveryFixture(output))
    {
        await Crash(fixture.Root, "external");
        await Reject<FileTransactionRecoveryRequiredException>(() => FileTransactionRecovery.RecoverAsync(fixture.Context), "Arbitrary OS rollback callbacks were falsely claimed to be crash-recoverable.");
        Check(File.ReadAllText(Path.Combine(fixture.App, "changed.txt")) == "before changed" && Directory.GetDirectories(Path.Combine(fixture.State, ".transactions")).Length == 1, "Verified application files were not recovered or uncertain metadata evidence was discarded.");
    }
    using (var fixture = new RecoveryFixture(output))
    {
        var root = Path.Combine(fixture.State, ".transactions", Guid.NewGuid().ToString("N")); var transaction = new InstallTransaction(root, fixture.Context);
        transaction.ReplaceFile(Path.Combine(fixture.App, "changed.txt"), Path.Combine(fixture.Root, "new-changed.txt"));
        File.WriteAllText(Path.Combine(fixture.App, "changed.txt"), "new unrelated user content");
        await RejectNow<AggregateException>(transaction.Dispose, "A failed rollback silently overwrote concurrent user data.");
        Check(Directory.Exists(Path.Combine(root, "rollback")) && File.ReadAllText(Path.Combine(fixture.App, "changed.txt")) == "new unrelated user content", "Rollback failure destroyed its backup or new user data.");
    }
});

File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new { success = true, checks, tests, sourceOnly = true, isolated = true, realApplicationsTouched = false }, InstallerJson.Options));
Console.WriteLine($"PASS: {tests.Count} incremental acceptance cases, {checks} assertions. All processes/files were owned fixtures; no real application or service was changed. Receipt: {Path.Combine(output, "receipt.json")}");

static byte[] Entry(string path, string name)
{
    using var archive = ZipFile.OpenRead(path); using var source = archive.GetEntry(name)!.Open(); using var buffer = new MemoryStream(); source.CopyTo(buffer); return buffer.ToArray();
}
static void Rewrite(string source, string destination, Action<List<FixtureEntry>> mutate)
{
    List<FixtureEntry> entries;
    using (var input = ZipFile.OpenRead(source)) entries = input.Entries.Select(entry => { using var stream = entry.Open(); using var bytes = new MemoryStream(); stream.CopyTo(bytes); return new FixtureEntry(entry.FullName, bytes.ToArray(), entry.ExternalAttributes); }).ToList();
    mutate(entries);
    using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
    foreach (var item in entries) { var entry = archive.CreateEntry(item.Name, CompressionLevel.Optimal); entry.ExternalAttributes = item.Attributes; using var output = entry.Open(); output.Write(item.Bytes); }
}
static async Task Crash(string root, string mode, string command = "crash")
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); start.ArgumentList.Add(command); start.ArgumentList.Add(root); start.ArgumentList.Add(mode);
    using var process = Process.Start(start)!; await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
    if (process.ExitCode != 91) throw new Exception("Owned crash fixture did not reach its checkpoint: " + await process.StandardError.ReadToEndAsync());
}
record FixtureEntry(string Name, byte[] Bytes, int Attributes = 0);
sealed class ImmediateProgress(Action<InstallProgress> report) : IProgress<InstallProgress>
{
    public void Report(InstallProgress value) => report(value);
}

sealed class PackageFixture : IDisposable
{
    public string Root { get; } public string Before => Path.Combine(Root, "before.hinst"); public string After => Path.Combine(Root, "after.hinst"); public string Delta => Path.Combine(Root, "update.hupd");
    public string Key => Path.Combine(Root, "signing.pem"); public string App => Path.Combine(Root, "app"); public string State => Path.Combine(Root, "state"); public string Id => "incremental.fixture";
    public string Pin { get; } public IncrementalUpdateManifest Transition { get; } private readonly bool shared;
    public InstalledProduct Installed => InstallerEngine.ListInstalled(State).Single(product => product.Id == Id);
    public PackageFixture(string output, bool components = false, bool shared = false)
    {
        this.shared = shared;
        Root = Path.Combine(output, "case-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root);
        using var rsa = RSA.Create(2048); File.WriteAllText(Key, rsa.ExportPkcs8PrivateKeyPem()); Pin = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        var common = RandomNumberGenerator.GetBytes(256 * 1024); var optional = RandomNumberGenerator.GetBytes(8192);
        foreach (var version in new[] { "1.0.0", "2.0.0" })
        {
            var directory = Path.Combine(Root, "source-" + version); Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "common.dat"), common); File.WriteAllBytes(Path.Combine(directory, "addon.bin"), optional);
            File.WriteAllBytes(Path.Combine(directory, "app.bin"), Enumerable.Repeat(version == "1.0.0" ? (byte)'A' : (byte)'B', 8192).ToArray());
            var files = new List<PayloadFile> { new() { Source = "common.dat", Path = "common.dat" }, new() { Source = "app.bin", Path = "app.bin" }, new() { Source = "addon.bin", Path = "addon.bin", Feature = "addon" } };
            if (version == "1.0.0") { File.WriteAllText(Path.Combine(directory, "removed.txt"), "old owned file"); files.Add(new() { Source = "removed.txt", Path = "removed.txt" }); }
            else { Directory.CreateDirectory(Path.Combine(directory, "new")); File.WriteAllText(Path.Combine(directory, "new", "created.txt"), "new owned file"); files.Add(new() { Source = "new/created.txt", Path = "new/created.txt" }); }
            var manifest = new PackageManifest { Id = Id, Name = "Isolated incremental fixture", Version = version, Publisher = "Fixture", EntryPoint = "app.bin", Payload = files, Features = [new() { Id = "addon", Name = "Optional addon", Default = false }] };
            if (components)
            {
                manifest.InstallationRoots = [new() { Id = "vst3", KnownFolder = "commonFiles64", RelativeDirectory = "VST3/IncrementalFixture.vst3" }];
                foreach (var (relative, contents) in new[] { ("Contents/fixture.vst3", version == "1.0.0" ? "old plugin bytes" : "new plugin bytes"), ("Contents/common.bin", "unchanged plugin data"), (version == "1.0.0" ? "Contents/old.bin" : "Contents/new.bin", "component create/remove") })
                {
                    var source = "vst3/" + relative; Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(directory, source))!); File.WriteAllText(Path.Combine(directory, source), contents); files.Add(new() { Source = source, Path = source, TargetRoot = "vst3" });
                }
            }
            if (shared)
            {
                var ownExecutable = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, OperatingSystem.IsWindows() ? ".exe" : null);
                if (!File.Exists(ownExecutable)) throw new FileNotFoundException("Build the owned fixture apphost for shared-runtime validation.");
                File.Copy(ownExecutable, Path.Combine(directory, "heirowUpdater.exe")); files.Add(new() { Source = "heirowUpdater.exe", Path = "heirowUpdater.exe", Feature = "updates" });
                manifest.Features.Add(new() { Id = "updates", Name = "Shared file updates", Default = true }); manifest.UpdateUrl = "https://updates.example/feed";
                manifest.UpdateCompanion = new() { RuntimeMode = "shared", UpdateMode = "files" };
            }
            var path = Path.Combine(Root, "manifest-" + version + ".json"); File.WriteAllText(path, JsonSerializer.Serialize(manifest, InstallerJson.Options));
            PackageBuilder.Build(path, directory, version == "1.0.0" ? Before : After, new() { SigningKeyPath = Key });
        }
        Transition = IncrementalUpdateBuilder.Build(Before, After, Delta, new() { SigningKeyPath = Key });
    }
    public UpdateFeed Feed => new() { ProductId = Id, Version = "2.0.0", BaseVersion = "1.0.0", BaseManifestSha256 = Transition.BaseManifestSha256, TargetManifestSha256 = Transition.TargetManifestSha256, PackageType = "hupd", InstallerUrl = "https://updates.example/update.hupd", Size = new FileInfo(Delta).Length, Sha256 = PackageSafety.HashFile(Delta) };
    public InstallOptions Options() => new() { StateRoot = State, TargetDirectory = App, Scope = "user", Isolated = true, RegisterProduct = false, RequireSignature = true, TrustedPublisherKeySha256 = Pin, Features = shared ? ["updates"] : [] };
    public async Task InstallBase() { using var package = PackageReader.Open(Before); await new InstallerEngine().InstallAsync(package, Options()); }
    public byte[] Sign(byte[] data) { using var rsa = RSA.Create(); rsa.ImportFromPem(File.ReadAllText(Key)); return JsonSerializer.SerializeToUtf8Bytes(new PackageSignature { Algorithm = "RSA-PSS-SHA256", PublicKey = rsa.ExportSubjectPublicKeyInfoPem(), Signature = Convert.ToBase64String(rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) }, InstallerJson.Options); }
    public void Dispose() { PackageSafety.EnsureNoReparsePoints(Root); Directory.Delete(Root, true); }
}

sealed class RecoveryFixture : IDisposable
{
    public string Root { get; } public string App => Path.Combine(Root, "app"); public string State => Path.Combine(Root, "state"); public string Foreign => Path.Combine(Root, "App.backup");
    public FileTransactionRecoveryContext Context { get; }
    public RecoveryFixture(string output)
    {
        Root = Path.Combine(output, "case-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(App); Directory.CreateDirectory(State);
        File.WriteAllText(Path.Combine(App, "changed.txt"), "before changed"); File.WriteAllText(Path.Combine(App, "removed.txt"), "before removed");
        File.WriteAllText(Path.Combine(Root, "new-changed.txt"), "after changed"); File.WriteAllText(Path.Combine(Root, "new-created.txt"), "after created"); File.WriteAllText(Foreign, "unrelated App.backup data");
        FileRecoveryTarget Target(string path, string? before, string? after) => new() { Path = path, BeforeSha256 = before is null ? null : PackageSafety.HashFile(before), BeforeSize = before is null ? null : new FileInfo(before).Length, AfterSha256 = after is null ? null : PackageSafety.HashFile(after), AfterSize = after is null ? null : new FileInfo(after).Length };
        Context = new() { ProductId = "recovery.fixture", StateRoot = State, InstallDirectory = App, Scope = "user", Isolated = true, TargetVersion = "2.0.0", BaseVersion = "1.0.0", BaseManifestSha256 = new string('1', 64), TargetManifestSha256 = new string('2', 64), PublisherKeySha256 = new string('3', 64), RequireExactBase = true,
            Files = [Target("changed.txt", Path.Combine(App, "changed.txt"), Path.Combine(Root, "new-changed.txt")), Target("removed.txt", Path.Combine(App, "removed.txt"), null), Target("created.txt", null, Path.Combine(Root, "new-created.txt"))] };
        File.WriteAllText(Path.Combine(Root, "context.json"), JsonSerializer.Serialize(Context, InstallerJson.Options));
    }
    public void Dispose() { PackageSafety.EnsureNoReparsePoints(Root); Directory.Delete(Root, true); }
}

sealed class FixtureTransport(UpdateFeed feed, string packagePath) : IUpdateTransport
{
    public List<Uri> Requests { get; } = [];
    public Task<HttpResponseMessage> GetAsync(Uri uri, long maximumResponseBytes, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        Requests.Add(uri); HttpContent content = uri.AbsolutePath == "/feed" ? new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(feed, InstallerJson.Options)) : new StreamContent(File.OpenRead(packagePath));
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
    }
}
