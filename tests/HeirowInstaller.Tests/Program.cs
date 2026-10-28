using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;

if (args.FirstOrDefault() == "uninstall")
{
    var stateIndex = Array.IndexOf(args, "--state-root");
    if (args.Length < 4 || stateIndex < 2 || stateIndex + 1 >= args.Length ||
        !Path.GetFullPath(args[stateIndex + 1]).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
        !args[stateIndex + 1].Contains("heirowInstaller-tests-", StringComparison.Ordinal)) return 2;
    Console.WriteLine(JsonSerializer.Serialize(await new InstallerEngine().UninstallAsync(args[1], new() { StateRoot = args[stateIndex + 1], Scope = "user", Silent = true }), InstallerJson.Options));
    return 0;
}
var suite = new AcceptanceSuite();
return await suite.RunAsync(args is ["--filter", var filter] ? filter : null);

internal sealed partial class AcceptanceSuite
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "heirowInstaller-tests-" + Guid.NewGuid().ToString("N"));
    private int passed;
    private static int assertionCount;
    public async Task<int> RunAsync(string? filter = null)
    {
        Directory.CreateDirectory(root);
        var cases = new (string Name, Func<Task> Test)[]
        {
            ("install, selected features, upgrade, repair, downgrade guard, uninstall and original-file preservation", Lifecycle),
            ("rollback restores previous payload and receipt after an interrupted upgrade", Rollback),
            ("cancellation rolls back partially installed files", Cancellation),
            ("payload tampering, duplicate archive entries, traversal and invalid module paths are rejected", InvalidPackages),
            ("RSA package signatures require the configured trusted publisher key", Signatures),
            ("package creation rejects its known signing key as payload and preserves existing files", SigningKeyPrivacy),
            ("single executable bundle verifies its bounded package and detects overlay tampering", Bundles),
            ("HTTP loopback full-download update verifies SHA256 and installs the new version", Updates),
            ("explicit installer modules participate in planning, rollback, and uninstall", Modules),
            ("reparse points cannot redirect payload writes", ReparsePoints),
            ("semantic version and reserved Windows paths are validated", Versions)
            ,("MSI maintenance honors the recorded directory, preserves original mode and rejects context or source substitutions", MsiArguments)
            ,("injected update transports validate redirects before making another request", TransportRedirects)
            ,("SocketJack updater streams signed updates through validated redirects and keeps credentials on their origin", SocketJackUpdates)
            ,("an attached signed package never authorizes execution of an untrusted executable prefix", UntrustedBundlePrefix)
            ,("the updater exits and a copied trusted helper replaces its own application safely", TrustedSelfHandoff)
            ,("trusted helper staging keeps its boundary and rejects unsupported launch paths before download", TrustedStagingPaths)
            ,("legacy executable defaults and explicit silent, target, and log overrides reach the actual child process", LegacyArguments)
            ,("machine state rejects writable or user-owned ACLs without changing them and preserves explicit isolation", MachineStateAcls)
            ,("user scope rejects selected machine integrations and spoofed machine receipt snapshots", IntegrationScope)
            ,("updater host discovery prefers the installed process over single-file extraction directories", UpdaterHosts)
            ,("optional app-local updater profiles pin verified identity and participate in repair and uninstall", AppUpdateCompanion)
            ,("maintenance politely stops only the app-local updater before replacing or removing its files", CompanionMaintenance)
            ,("exact backend registrations route Windows Settings maintenance and reject stale staged updates", BackendRegistrations)
            ,("manual update feedback correlates direct, relayed, busy, coalesced and failed checks while automatic and headless checks stay quiet", ManualUpdateFeedback)
            ,("shared updater files are verified, reused across apps, scoped and removed only by their owning references", SharedUpdaterRuntimes)
            ,("current application session snapshots replace stale credentials without leaking across origins or persisted state", SessionSnapshots)
            ,("the bounded in-memory session relay mutually verifies peers and rejects stale, malformed and wrong-bound requests", SessionRelay)
            ,("new native sessions supersede rejected requests and obsolete browser approval without postponing or canceling active work", SessionAuthorizationTransitions)
            ,("both bootstrap languages launch the verified shared runtime with a pre-queued session capture and no inherited bearer", SharedBootstrapLanguages)
            ,("an exact updater dependency waits for one PID and never other apps sharing its image", ExactUpdaterDependencies)
            ,("the shared native updater applies signed file data and rejects wrong root, digest, base and tampering before mutation", NativeIncrementalHandoff)
            ,("shared GUI maintenance pooling strips application overlays and reuses only verified immutable prefixes", SharedMaintenanceHosts)
            ,("actual native watcher processes receive the current app cookie and explicit sign-out without a browser or credential persistence", NativeSessionRelay)
            ,("generic CLI, check-all, apply and handoff enforce the installed signed file-update policy before any full-installer download", GenericFileUpdatePolicy)
            ,("update checks cancel stalled headers and bodies at their deadline and preserve user cancellation", UpdateCheckDeadlines)
            ,("update checks run four feeds concurrently, isolate failures and report ordered completion counts", ConcurrentUpdateChecks)
            ,("version catalog compares the active published pointer numerically and pages history without installer executables", VersionCatalogComparison)
            ,("version catalog rejects wrong identity, malformed metadata, failed authorization, redirects and oversized responses", VersionCatalogValidation)
            ,("version catalog deadlines cancel stalled headers and bodies and preserve caller cancellation", VersionCatalogCancellation)
            ,("version catalog SocketJack checks use only the supplied session and never forward it through redirects", VersionCatalogSocketJack)
        };
        if (filter is not null)
        {
            cases = cases.Where(x => x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (cases.Length == 0) throw new ArgumentException("No integration test matches the filter.");
        }
        try
        {
            foreach (var test in cases)
            {
                var initialAssertions = Volatile.Read(ref assertionCount);
                await test.Test(); passed++;
                Console.WriteLine($"PASS {test.Name} ({Volatile.Read(ref assertionCount) - initialAssertions} assertions)");
            }
            Console.WriteLine($"Passed {passed}/{cases.Length} integration tests. No existing applications, services, or registry keys were modified.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"FAIL after {passed}/{cases.Length} tests: {ex}"); return 1; }
        finally
        {
            // The exact root is generated above and is always a direct child of the OS temporary directory.
            var full = Path.GetFullPath(root);
            var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (Path.GetDirectoryName(full)?.Equals(expectedParent, StringComparison.OrdinalIgnoreCase) == true &&
                Path.GetFileName(full).StartsWith("heirowInstaller-tests-", StringComparison.Ordinal))
                try { Directory.Delete(full, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task Lifecycle()
    {
        var area = Area("lifecycle");
        var target = Path.Combine(area, "app"); Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "app.txt"), "original user file");
        await File.WriteAllTextAsync(Path.Combine(target, "notes.txt"), "preserve unrelated data");
        var manifest = Manifest("test.lifecycle", "1.0.0");
        manifest.Features = [new() { Id = "core", Required = true }, new() { Id = "docs", Default = false }];
        manifest.Payload = [new() { Path = "app.txt", Feature = "core" }, new() { Path = "old.txt" }, new() { Path = "docs/readme.txt", Feature = "docs" }];
        var v1 = Build(area, "v1", manifest, new() { ["app.txt"] = "version one", ["old.txt"] = "removed in v2", ["docs/readme.txt"] = "manual one" });
        var options = Options(area, target); options.Features = [];
        var engine = new InstallerEngine();
        using (var package = PackageReader.Open(v1))
        {
            var plan = engine.Plan(package, options);
            Equal(2, plan.Files.Count); True(plan.Features.Contains("core"));
            await engine.InstallAsync(package, options);
        }
        Equal("version one", File.ReadAllText(Path.Combine(target, "app.txt")));
        True(!File.Exists(Path.Combine(target, "docs/readme.txt")));
        Equal(1, InstallerEngine.ListInstalled(options.StateRoot).Count);
        manifest.Version = "2.0.0";
        manifest.Payload.RemoveAll(x => x.Path == "old.txt");
        manifest.Payload.Add(new() { Path = "new.txt" });
        var v2 = Build(area, "v2", manifest, new() { ["app.txt"] = "version two", ["docs/readme.txt"] = "manual two", ["new.txt"] = "new" });
        options.Features = ["docs"];
        using (var package = PackageReader.Open(v2)) await engine.InstallAsync(package, options);
        True(!File.Exists(Path.Combine(target, "old.txt"))); True(File.Exists(Path.Combine(target, "docs/readme.txt")));
        await File.WriteAllTextAsync(Path.Combine(target, "app.txt"), "corrupted");
        await engine.RepairAsync(manifest.Id, Options(area, target));
        Equal("version two", File.ReadAllText(Path.Combine(target, "app.txt")));
        using (var package = PackageReader.Open(v1)) await Throws<InvalidOperationException>(() => engine.InstallAsync(package, options));
        await engine.UninstallAsync(manifest.Id, Options(area, target));
        Equal("original user file", File.ReadAllText(Path.Combine(target, "app.txt")));
        Equal("preserve unrelated data", File.ReadAllText(Path.Combine(target, "notes.txt")));
        True(!File.Exists(Path.Combine(target, "new.txt"))); True(!File.Exists(Path.Combine(target, "docs/readme.txt")));
        Equal(0, InstallerEngine.ListInstalled(options.StateRoot).Count);
    }

    private async Task Rollback()
    {
        var area = Area("rollback"); var target = Path.Combine(area, "app");
        var options = Options(area, target); var engine = new InstallerEngine();
        var manifest = Manifest("test.rollback", "1.0.0");
        var first = Build(area, "v1", manifest, new() { ["app.txt"] = "working" });
        using (var package = PackageReader.Open(first)) await engine.InstallAsync(package, options);
        await File.WriteAllTextAsync(Path.Combine(target, "blocked"), "do not replace");
        manifest.Version = "2.0.0";
        manifest.Payload.Add(new() { Path = "blocked/child.txt", Source = "blocked/child.txt" });
        var second = Build(area, "v2", manifest, new() { ["app.txt"] = "new", ["blocked/child.txt"] = "must fail" });
        var receiptBefore = File.ReadAllText(InstallerEngine.ListInstalled(options.StateRoot).Single().ReceiptPath);
        using (var package = PackageReader.Open(second)) await Throws<IOException>(() => engine.InstallAsync(package, options));
        Equal("working", File.ReadAllText(Path.Combine(target, "app.txt")));
        Equal("do not replace", File.ReadAllText(Path.Combine(target, "blocked")));
        Equal(receiptBefore, File.ReadAllText(InstallerEngine.ListInstalled(options.StateRoot).Single().ReceiptPath));
    }

    private async Task Cancellation()
    {
        var area = Area("cancel"); var target = Path.Combine(area, "app");
        var packagePath = Build(area, "v1", Manifest("test.cancel", "1"), new() { ["a.txt"] = "a", ["b.txt"] = "b" });
        using var cancellation = new CancellationTokenSource();
        using var package = PackageReader.Open(packagePath);
        var progress = new InlineProgress(value => { if (value.Phase == "install" && value.Completed == 1) cancellation.Cancel(); });
        await Throws<OperationCanceledException>(() => new InstallerEngine().InstallAsync(package, Options(area, target), progress, cancellation.Token));
        True(!File.Exists(Path.Combine(target, "a.txt"))); True(!File.Exists(Path.Combine(target, "b.txt")));
        Equal(0, InstallerEngine.ListInstalled(Path.Combine(area, "state")).Count);
    }

    private async Task InvalidPackages()
    {
        var area = Area("invalid");
        var valid = Build(area, "valid", Manifest("test.invalid", "1"), new() { ["a.txt"] = "okay" });
        var tampered = Path.Combine(area, "tampered.hinst"); File.Copy(valid, tampered);
        using (var zip = ZipFile.Open(tampered, ZipArchiveMode.Update))
        {
            zip.GetEntry("payload/a.txt")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("payload/a.txt").Open()); writer.Write("evil");
        }
        using (var package = PackageReader.Open(tampered))
            await Throws<CryptographicException>(() => new InstallerEngine().InstallAsync(package, Options(area, Path.Combine(area, "app"))));
        True(!Directory.Exists(Path.Combine(area, "app")));
        var traversal = Path.Combine(area, "traversal.hinst");
        var manifest = Manifest("test.traversal", "1"); manifest.Payload = [new() { Path = "../escape.txt", Size = 1, Sha256 = new string('0', 64) }];
        using (var zip = ZipFile.Open(traversal, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()); writer.Write(JsonSerializer.Serialize(manifest, InstallerJson.Options)); }
        await Throws<InvalidDataException>(() => { using var ignored = PackageReader.Open(traversal); return Task.CompletedTask; });
        var duplicate = Path.Combine(area, "duplicate.hinst"); File.Copy(valid, duplicate);
        using (var zip = ZipFile.Open(duplicate, ZipArchiveMode.Update)) { using var writer = new StreamWriter(zip.CreateEntry("PAYLOAD/A.TXT").Open()); writer.Write("okay"); }
        await Throws<InvalidDataException>(() => { using var ignored = PackageReader.Open(duplicate); return Task.CompletedTask; });
    }

    private async Task Signatures()
    {
        var area = Area("signatures"); using var rsa = RSA.Create(2048);
        var key = Path.Combine(area, "publisher.pem"); File.WriteAllText(key, rsa.ExportPkcs8PrivateKeyPem());
        var fingerprint = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        var packagePath = Build(area, "signed", Manifest("test.signature", "1"), new() { ["a.txt"] = "signed" }, key);
        using (var package = PackageReader.Open(packagePath))
        {
            package.VerifySignature(fingerprint, true); await package.VerifyAsync();
            await Throws<CryptographicException>(() => { package.VerifySignature(new string('0', 64), true); return Task.CompletedTask; });
            await new InstallerEngine().InstallAsync(package, Options(area, Path.Combine(area, "app")));
        }
        Equal(fingerprint, InstallerEngine.ListInstalled(Path.Combine(area, "state")).Single().PublisherKeySha256);
        using (var other = RSA.Create(2048))
        {
            var otherKey = Path.Combine(area, "other.pem"); File.WriteAllText(otherKey, other.ExportPkcs8PrivateKeyPem());
            var wrongPublisher = Build(area, "wrong-publisher", Manifest("test.signature", "2"), new() { ["a.txt"] = "wrong publisher" }, otherKey);
            using var package = PackageReader.Open(wrongPublisher);
            await Throws<CryptographicException>(() => new InstallerEngine().InstallAsync(package, Options(area, Path.Combine(area, "app"))));
        }
        var unsignedUpgrade = Build(area, "unsigned-upgrade", Manifest("test.signature", "2"), new() { ["a.txt"] = "unsigned upgrade" });
        using (var package = PackageReader.Open(unsignedUpgrade))
            await Throws<CryptographicException>(() => new InstallerEngine().InstallAsync(package, Options(area, Path.Combine(area, "app"))));
        var unsigned = Build(area, "unsigned", Manifest("test.unsigned", "1"), new() { ["a.txt"] = "unsigned" });
        using (var package = PackageReader.Open(unsigned)) await Throws<CryptographicException>(() => { package.VerifySignature(requireSignature: true); return Task.CompletedTask; });
        using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Update))
        {
            string json; using (var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open())) json = reader.ReadToEnd();
            zip.GetEntry("manifest.json")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()); writer.Write(json.Replace("test.signature", "test.attacker", StringComparison.Ordinal));
        }
        await Throws<CryptographicException>(() => { using var ignored = PackageReader.Open(packagePath); return Task.CompletedTask; });
    }

    private async Task SigningKeyPrivacy()
    {
        var area = Area("signing-key-privacy"); using var rsa = RSA.Create(2048);
        var source = Path.Combine(area, "source"); Directory.CreateDirectory(Path.Combine(source, "config"));
        var key = Path.Combine(source, "config", "publisher.bin");
        var keyBytes = Encoding.UTF8.GetBytes(rsa.ExportPkcs8PrivateKeyPem()); File.WriteAllBytes(key, keyBytes);
        File.WriteAllText(Path.Combine(source, "app.txt"), "application payload");
        var keyAlias = Path.Combine(source, "config", ".", "publisher.bin");
        if (OperatingSystem.IsWindows()) keyAlias = keyAlias.ToUpperInvariant();
        var options = new PackageBuildOptions { SigningKeyPath = keyAlias };
        var manifestPath = Path.Combine(area, "manifest.json");
        foreach (var explicitPayload in new[] { false, true })
        foreach (var existingOutput in new[] { false, true })
        {
            var manifest = Manifest("test.signing-key-privacy", "1");
            if (explicitPayload) manifest.Payload = [new() { Source = "config/publisher.bin", Path = "renamed-material.bin" }];
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options));
            var output = Path.Combine(area, $"rejected-{explicitPayload}-{existingOutput}.hinst");
            var sentinel = Encoding.UTF8.GetBytes("preserve the previous output");
            if (existingOutput) File.WriteAllBytes(output, sentinel);
            await Throws<InvalidDataException>(() => { PackageBuilder.Build(manifestPath, source, output, options); return Task.CompletedTask; });
            True(File.ReadAllBytes(key).SequenceEqual(keyBytes));
            if (existingOutput) True(File.ReadAllBytes(output).SequenceEqual(sentinel));
            else True(!File.Exists(output));
            True(!Directory.EnumerateFiles(area, Path.GetFileName(output) + ".*.tmp").Any());
        }
        // Unrelated application data remains valid, even when its filename matches the signing key.
        Directory.CreateDirectory(Path.Combine(source, "data")); File.WriteAllText(Path.Combine(source, "data", "publisher.bin"), "legitimate application data");
        var allowed = Manifest("test.signing-key-privacy", "1");
        allowed.Payload = [new() { Source = "app.txt", Path = "app.txt" }, new() { Source = "data/publisher.bin", Path = "data/publisher.bin" }];
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(allowed, InstallerJson.Options));
        await Throws<InvalidDataException>(() => { PackageBuilder.Build(manifestPath, source, key, options); return Task.CompletedTask; });
        True(File.ReadAllBytes(key).SequenceEqual(keyBytes));
        var valid = Path.Combine(area, "allowed.hinst"); PackageBuilder.Build(manifestPath, source, valid, options);
        using var package = PackageReader.Open(valid); await package.VerifyAsync();
        Equal(2, package.Manifest.Payload.Count);
        True(File.ReadAllBytes(key).SequenceEqual(keyBytes));
    }

    private async Task Bundles()
    {
        var area = Area("bundles");
        var package = Build(area, "payload", Manifest("test.bundle", "1"), new() { ["a.txt"] = "bundled payload" });
        var launcher = Path.Combine(area, "launcher.exe"); File.WriteAllBytes(launcher, Encoding.ASCII.GetBytes("MZ-test-launcher-fixture"));
        var bundle = Path.Combine(area, "setup.exe"); BundleBuilder.Build(launcher, package, bundle);
        True(PackageReader.HasEmbeddedPackage(bundle));
        using (var reader = PackageReader.Open(bundle)) { await reader.VerifyAsync(); Equal("test.bundle", reader.Manifest.Id); }
        using (var file = new FileStream(bundle, FileMode.Open, FileAccess.ReadWrite)) { file.Position = new FileInfo(launcher).Length + 10; file.WriteByte(0xff); }
        await Throws<CryptographicException>(() => { using var ignored = PackageReader.Open(bundle); return Task.CompletedTask; });
        // A synthetic PE security table exercises layout only; it is not a trusted Authenticode signature.
        var pe = new byte[512];
        using (var memory = new MemoryStream(pe))
        using (var writer = new BinaryWriter(memory))
        {
            writer.Write((ushort)0x5a4d); memory.Position = 0x3c; writer.Write(128);
            memory.Position = 128; writer.Write(0x00004550); memory.Position = 148; writer.Write((ushort)240);
            memory.Position = 152; writer.Write((ushort)0x20b); memory.Position = 260; writer.Write(16);
        }
        File.WriteAllBytes(launcher, pe); BundleBuilder.Build(launcher, package, bundle);
        using (var file = new FileStream(bundle, FileMode.Open, FileAccess.ReadWrite))
        using (var writer = new BinaryWriter(file))
        {
            file.Position = file.Length; while (file.Position % 8 != 0) writer.Write((byte)0);
            var certificateOffset = file.Position; writer.Write(new byte[16]);
            file.Position = 152 + 112 + 32; writer.Write((uint)certificateOffset); writer.Write((uint)16);
        }
        True(PackageReader.HasEmbeddedPackage(bundle));
        using (var reader = PackageReader.Open(bundle)) { await reader.VerifyAsync(); Equal("test.bundle", reader.Manifest.Id); }
    }

    private async Task Updates()
    {
        var area = Area("updates");
        var package = Build(area, "v2", Manifest("test.update", "2.0.0"), new() { ["app.txt"] = "updated" });
        var bytes = await File.ReadAllBytesAsync(package);
        var feed = new UpdateFeed { ProductId = "test.update", Version = "2.0.0", Sha256 = PackageSafety.HashFile(package), Size = bytes.Length, PackageType = "hinst" };
        await using var server = new LoopbackServer(path => path == "/setup.hinst" ? bytes : JsonSerializer.SerializeToUtf8Bytes(feed, InstallerJson.Options));
        feed.InstallerUrl = server.Origin + "setup.hinst";
        using var client = new UpdateClient(allowLoopback: true);
        var check = await client.CheckAsync(server.Origin + "update.json", "1.0.0", feed.ProductId);
        True(check.Available);
        var download = await client.DownloadAsync(check.Feed, Path.Combine(area, "downloads"));
        await client.ApplyAsync(download, check.Feed, Options(area, Path.Combine(area, "app")));
        Equal("updated", File.ReadAllText(Path.Combine(area, "app", "app.txt")));
        True(!(await client.CheckAsync(server.Origin + "update.json", "2.0.0", feed.ProductId)).Available);
        feed.Sha256 = new string('0', 64);
        await Throws<CryptographicException>(() => client.DownloadAsync(feed, Path.Combine(area, "bad-download")));
        using var productionClient = new UpdateClient();
        await Throws<InvalidDataException>(() => productionClient.CheckAsync(server.Origin + "update.json", "1", feed.ProductId));
    }

    private async Task Modules()
    {
        var area = Area("modules"); var target = Path.Combine(area, "app");
        var manifest = Manifest("test.modules", "1");
        manifest.Modules["marker"] = JsonSerializer.SerializeToElement(new { fail = false });
        var path = Build(area, "v1", manifest, new() { ["app.txt"] = "working" });
        var module = new MarkerModule(area);
        var engine = new InstallerEngine([module]);
        using (var package = PackageReader.Open(path))
        {
            True(engine.Plan(package, Options(area, target)).Operations.Contains("Create test marker"));
            await engine.InstallAsync(package, Options(area, target));
        }
        True(File.Exists(Path.Combine(target, "marker.txt")));
        manifest.Version = "2"; manifest.Modules["marker"] = JsonSerializer.SerializeToElement(new { fail = true });
        var bad = Build(area, "v2", manifest, new() { ["app.txt"] = "broken" });
        using (var package = PackageReader.Open(bad)) await Throws<InvalidOperationException>(() => engine.InstallAsync(package, Options(area, target)));
        Equal("working", File.ReadAllText(Path.Combine(target, "app.txt")));
        True(File.Exists(Path.Combine(target, "marker.txt")));
        await engine.UninstallAsync(manifest.Id, Options(area, target));
        True(!File.Exists(Path.Combine(target, "marker.txt")));
    }

    private async Task ReparsePoints()
    {
        var area = Area("reparse"); var outside = Path.Combine(area, "outside"); Directory.CreateDirectory(outside);
        var target = Path.Combine(area, "app"); Directory.CreateDirectory(target);
        var link = Path.Combine(target, "redirect");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            if (!OperatingSystem.IsWindows()) throw;
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "/c", "mklink", "/J", link, outside }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException("Could not create the isolated reparse-point fixture.");
        }
        var packagePath = Build(area, "v1", Manifest("test.reparse", "1"), new() { ["redirect/escape.txt"] = "must not escape" });
        using var package = PackageReader.Open(packagePath);
        await Throws<IOException>(() => new InstallerEngine().InstallAsync(package, Options(area, target)));
        True(!File.Exists(Path.Combine(outside, "escape.txt")));
        Directory.Delete(link);
    }

    private async Task Versions()
    {
        True(PackageVersion.Parse("2026.10.24.1").CompareTo(PackageVersion.Parse("2026.10.23.99")) > 0);
        True(PackageVersion.Parse("2.0.0").CompareTo(PackageVersion.Parse("2.0.0-rc.1")) > 0);
        True(PackageVersion.Parse("1.0.0-alpha.9").CompareTo(PackageVersion.Parse("1.0.0-alpha.10")) < 0);
        foreach (var path in new[] { "../bad", "C:/bad", "/bad", "file:stream", "CON.txt", "dir/.. /x", "bad.", "dir//x" })
            await Throws<InvalidDataException>(() => { PackageSafety.NormalizeRelativePath(path); return Task.CompletedTask; });
    }

    private async Task MsiArguments()
    {
        var plan = new InstallPlan { InstallDirectory = @"C:\IgnoredFallback", UseOriginalInstallDirectory = true };
        var args = MsiAdapter.BuildArguments("install", @"C:\Original.msi", null, new(), plan, new());
        True(!args.Any(x => x.StartsWith("INSTALLFOLDER=", StringComparison.Ordinal) || x.StartsWith("ADDLOCAL=", StringComparison.Ordinal)));
        plan.UseOriginalInstallDirectory = false; plan.Features = ["Core", "Tools"];
        args = MsiAdapter.BuildArguments("install", @"C:\Original.msi", null, new() { InstallDirectoryProperty = "APPLICATIONFOLDER" }, plan, new());
        True(args.Contains(@"APPLICATIONFOLDER=C:\IgnoredFallback")); True(args.Contains("ADDLOCAL=Core,Tools"));
        args = MsiAdapter.BuildArguments("install", @"C:\Original.msi", null, new(), plan, new() { Silent = false });
        True(!args.Contains("/qn") && !args.Contains("/passive"));
        var durable = Path.Combine(root, "Durable Media", "Original.msi");
        Directory.CreateDirectory(Path.GetDirectoryName(durable)!);
        File.WriteAllText(durable, "Owned argument-only fixture; never executed as MSI.");
        var recordedDirectory = Path.Combine(root, "Recorded Application Directory");
        var code = "{11111111-1111-1111-1111-111111111111}";
        var maintenance = new LegacyMsiDefinition { InstallDirectoryProperty = "APPLICATIONFOLDER",
            Arguments = ["ALLUSERS=1", "MSIINSTALLPERUSER=0", "ADDLOCAL=Unrelated", "EXTRA=not-forwarded"] };
        plan.InstallDirectory = recordedDirectory;
        args = MsiAdapter.BuildArguments("repair", durable, code, maintenance, plan, new() { TargetDirectory = @"C:\CallerMustNotRelocateMaintenance" });
        True(args[0] == "/i" && args[1] == durable && args.Contains("REINSTALL=ALL") && args.Contains("REINSTALLMODE=a") && !args.Contains("/fa"));
        True(args.Count(x => x == "APPLICATIONFOLDER=" + recordedDirectory) == 1);
        True(!args.Any(x => maintenance.Arguments.Contains(x) || x.Contains("CallerMustNotRelocateMaintenance", StringComparison.Ordinal)));
        args = MsiAdapter.BuildArguments("uninstall", @"C:\Cached\Original.hinst", code, maintenance, plan, new());
        True(args[0] == "/x" && args[1] == code && args.Count(x => x == "APPLICATIONFOLDER=" + recordedDirectory) == 1);
        True(!args.Any(x => maintenance.Arguments.Contains(x) || x.StartsWith("REINSTALL", StringComparison.Ordinal)));
        foreach (var invalidSource in new[] { code, @"C:\Original.hinst", "relative.msi", Path.Combine(root, "missing.msi") })
            await Throws<InvalidDataException>(() => Task.Run(() => MsiAdapter.BuildArguments("repair", invalidSource, code, maintenance, plan, new())));
        foreach (var property in new[] { "ALLUSERS", "MSIINSTALLPERUSER", "REINSTALL", "REINSTALLMODE", "ADDLOCAL", "REMOVE", "REBOOT", "private", "DIR=VALUE" })
            await Throws<InvalidDataException>(() => Task.Run(() => MsiAdapter.BuildArguments("uninstall", "unused", code, new() { InstallDirectoryProperty = property }, plan, new())));
        foreach (var invalidDirectory in new[] { "", "relative", Path.GetPathRoot(root)! })
        {
            plan.InstallDirectory = invalidDirectory;
            await Throws<InvalidDataException>(() => Task.Run(() => MsiAdapter.BuildArguments("uninstall", "unused", code, maintenance, plan, new())));
        }
        plan.UseOriginalInstallDirectory = true; plan.InstallDirectory = @"C:\IgnoredFallback";
        args = MsiAdapter.BuildArguments("repair", durable, code, maintenance, plan, new());
        True(args[0] == "/fa" && args[1] == durable && !args.Any(x => x.StartsWith("APPLICATIONFOLDER=", StringComparison.Ordinal) || x.StartsWith("REINSTALL", StringComparison.Ordinal) || maintenance.Arguments.Contains(x)));
        args = MsiAdapter.BuildArguments("repair", @"C:\Cached\Original.hinst", code, maintenance, plan, new());
        True(args[0] == "/fa" && args[1] == code);
        args = MsiAdapter.BuildArguments("uninstall", "unused", code, maintenance, plan, new());
        True(args[0] == "/x" && args[1] == code && !args.Any(x => x.Contains('=') || maintenance.Arguments.Contains(x)));
        plan.UseOriginalInstallDirectory = false;
        plan.Manifest.UpdateCompanion = new(); plan.Features = ["updates"];
        args = MsiAdapter.BuildArguments("install", @"C:\Original.msi", null, new(), plan, new());
        True(!args.Any(x => x.StartsWith("ADDLOCAL=", StringComparison.Ordinal)));
        plan.Features = ["Core", "updates"];
        args = MsiAdapter.BuildArguments("install", @"C:\Original.msi", null, new(), plan, new());
        True(args.Contains("ADDLOCAL=Core"));
        var commandLine = MsiAdapter.SerializeArguments(["/i", @"C:\Installer Source\Fixture.msi", @"INSTALLFOLDER=C:\Program Files\App\", "CUSTOM=Acme \"Widgets\"", "EMPTY=", "/qn", "/L*v", @"C:\Log Folder\setup.log"]);
        Equal("/i \"C:\\Installer Source\\Fixture.msi\" INSTALLFOLDER=\"C:\\Program Files\\App\\\" CUSTOM=\"Acme \"\"Widgets\"\"\" EMPTY=\"\" /qn /L*v \"C:\\Log Folder\\setup.log\"", commandLine);
    }

    private async Task TransportRedirects()
    {
        var transport = new RedirectTransport();
        using var client = new UpdateClient(transport: transport);
        await Throws<InvalidDataException>(() => client.CheckAsync("https://updates.example.invalid/feed", "1", "test.redirect"));
        Equal(1, transport.Requests);
    }

    private async Task SocketJackUpdates()
    {
        var area = Area("socketjack-updates"); using var rsa = RSA.Create(2048);
        var key = Path.Combine(area, "publisher.pem"); File.WriteAllText(key, rsa.ExportPkcs8PrivateKeyPem());
        var pin = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        var package = Build(area, "v2", Manifest("test.socketjack", "2"), new() { ["app.txt"] = new string('s', 131072) }, key);
        var packageBytes = File.ReadAllBytes(package);
        var feed = new UpdateFeed { ProductId = "test.socketjack", Version = "2", Sha256 = PackageSafety.HashFile(package), Size = packageBytes.Length, PackageType = "hinst" };
        var receivedCredentials = new List<bool>();
        await using var destination = new LoopbackServer((NetworkRequest request) =>
        {
            receivedCredentials.Add(request.Headers.ContainsKey("Cookie") || request.Headers.ContainsKey("Authorization"));
            return request.Path.Split('?', 2)[0] switch
            {
                "/setup.hinst" => new(200, packageBytes),
                "/oversized" => new(200, new byte[131073]),
                "/loop" => new(302, [], new() { ["Location"] = "/loop" }),
                _ => new(200, JsonSerializer.SerializeToUtf8Bytes(feed, InstallerJson.Options))
            };
        });
        feed.InstallerUrl = destination.Origin + "setup.hinst";
        var sourceCredentials = new List<bool>();
        await using var source = new LoopbackServer((NetworkRequest request) =>
        {
            sourceCredentials.Add(request.Headers.ContainsKey("Cookie") && request.Headers.ContainsKey("Authorization"));
            return request.Path.Split('?', 2)[0] == "/feed" ? new(302, [], new() { ["Location"] = "/cross" }) : new(307, [], new() { ["Location"] = destination.Origin + "feed" });
        });
        using var updater = new SocketJackUpdater(true, new Uri(source.Origin), "FixtureCookie=local-only", "local-fixture");
        var target = new UpdaterTarget { ProductId = feed.ProductId, Name = "SocketJack updater fixture", CurrentVersion = "1",
            FeedUrl = source.Origin + "feed", StateRoot = Path.Combine(area, "state"), InstallDirectory = Path.Combine(area, "app") };
        var check = await updater.CheckAsync(target); True(check.Available);
        True(sourceCredentials.Count == 2 && sourceCredentials.All(x => x));
        True(receivedCredentials.Count == 1 && receivedCredentials.All(x => !x));
        await Throws<InvalidOperationException>(() => updater.ApplyAsync(target, check, new() { Isolated = true, DownloadDirectory = Path.Combine(area, "downloads") }));
        target.PublisherKeySha256 = pin;
        await updater.ApplyAsync(target, check, new() { Isolated = true, DownloadDirectory = Path.Combine(area, "downloads") });
        Equal(new string('s', 131072), File.ReadAllText(Path.Combine(area, "app", "app.txt")));
        Equal("2", target.CurrentVersion);
        Equal(pin, InstallerEngine.ListInstalled(target.StateRoot).Single().PublisherKeySha256);
        True(receivedCredentials.All(x => !x));
        var transport = new SocketJackUpdateTransport();
        await Throws<InvalidDataException>(() => transport.GetAsync(new Uri(destination.Origin + "oversized"), 100, null, CancellationToken.None));
        using var loopClient = new UpdateClient(true, transport: transport);
        await Throws<HttpRequestException>(() => loopClient.CheckAsync(destination.Origin + "loop", "1", feed.ProductId));
        var cli = FindUpdaterCli();
        var checkResult = await RunCli(cli, ["check", "--feed", source.Origin + "feed", "--product-id", target.ProductId,
            "--current-version", "1", "--state-root", target.StateRoot!, "--allow-loopback", "--json", "--status-file", Path.Combine(area, "cli-status.json")]);
        using (var json = JsonDocument.Parse(checkResult)) Equal("current", json.RootElement[0].GetProperty("status").GetString());
        var third = Build(area, "v3", Manifest("test.socketjack", "3"), new() { ["app.txt"] = "CLI-installed version three" }, key);
        packageBytes = File.ReadAllBytes(third); feed.Version = "3"; feed.Sha256 = PackageSafety.HashFile(third); feed.Size = packageBytes.Length;
        var updateResult = await RunCli(cli, ["update", "--feed", source.Origin + "feed", "--product-id", target.ProductId,
            "--current-version", "2", "--state-root", target.StateRoot!, "--allow-loopback", "--json", "--isolated",
            "--download-directory", Path.Combine(area, "cli-downloads"), "--status-file", Path.Combine(area, "cli-status.json")]);
        using (var json = JsonDocument.Parse(updateResult)) Equal("updated", json.RootElement[0].GetProperty("status").GetString());
        Equal("CLI-installed version three", File.ReadAllText(Path.Combine(area, "app", "app.txt")));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Throws<OperationCanceledException>(() => updater.CheckAsync(target, cancellation.Token));
    }

    private async Task UntrustedBundlePrefix()
    {
        var area = Area("untrusted-prefix"); using var rsa = RSA.Create(2048);
        var key = Path.Combine(area, "publisher.pem"); File.WriteAllText(key, rsa.ExportPkcs8PrivateKeyPem());
        var pin = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        var package = Build(area, "v2", Manifest("test.untrusted-prefix", "2"), new() { ["app.txt"] = "trusted signed data" }, key);
        var prefix = Path.Combine(area, "untrusted-launcher.exe"); File.WriteAllText(prefix, "MZ-untrusted-launcher-that-must-never-be-executed");
        var bundle = Path.Combine(area, "update.exe"); BundleBuilder.Build(prefix, package, bundle);
        var feed = new UpdateFeed { ProductId = "test.untrusted-prefix", Version = "2", PackageType = "exe", InstallerUrl = "https://updates.example.invalid/setup.exe", Sha256 = PackageSafety.HashFile(bundle), Size = new FileInfo(bundle).Length };
        var options = Options(area, Path.Combine(area, "app")); options.RequireSignature = true; options.TrustedPublisherKeySha256 = pin;
        using var client = new UpdateClient();
        await client.ApplyAsync(bundle, feed, options);
        Equal("trusted signed data", File.ReadAllText(Path.Combine(area, "app", "app.txt")));
    }

    private async Task TrustedSelfHandoff()
    {
        var area = Area("trusted-handoff"); using var rsa = RSA.Create(2048);
        var key = Path.Combine(area, "publisher.pem"); File.WriteAllText(key, rsa.ExportPkcs8PrivateKeyPem());
        byte[] installer = [];
        var feed = new UpdateFeed { ProductId = "test.trusted-handoff", Version = "2", PackageType = "hinst" };
        await using var server = new LoopbackServer(path => path == "/setup.hinst" ? installer : JsonSerializer.SerializeToUtf8Bytes(feed, InstallerJson.Options));
        var source = Path.Combine(area, "source"); Directory.CreateDirectory(source);
        if (System.Environment.GetEnvironmentVariable("HEIROWUPDATER_TEST_EXECUTABLE") is { Length: > 0 } publishedUpdater)
            File.Copy(Path.GetFullPath(publishedUpdater), Path.Combine(source, "heirowUpdater.exe"));
        else
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(FindUpdaterCli())!, "*", SearchOption.TopDirectoryOnly))
                if (Path.GetExtension(file).ToLowerInvariant() is ".exe" or ".dll" or ".json") File.Copy(file, Path.Combine(source, Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(source, "version-marker.txt"), "version one");
        var manifest = Manifest(feed.ProductId, "1"); manifest.UpdateUrl = server.Origin + "feed.json";
        var manifestPath = Path.Combine(area, "manifest.json"); File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options));
        var first = Path.Combine(area, "v1.hinst"); PackageBuilder.Build(manifestPath, source, first, new() { SigningKeyPath = key });
        var target = Path.Combine(area, "installed"); var options = Options(area, target);
        using (var package = PackageReader.Open(first)) await new InstallerEngine().InstallAsync(package, options);
        manifest.Version = "2"; File.WriteAllText(Path.Combine(source, "version-marker.txt"), "version two");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options));
        var second = Path.Combine(area, "v2.hinst"); PackageBuilder.Build(manifestPath, source, second, new() { SigningKeyPath = key });
        installer = File.ReadAllBytes(second); feed.InstallerUrl = server.Origin + "setup.hinst"; feed.Sha256 = PackageSafety.HashFile(second); feed.Size = installer.Length;
        var output = await RunCli(Path.Combine(target, "heirowUpdater.exe"), ["update", feed.ProductId, "--state-root", options.StateRoot!, "--allow-loopback", "--isolated", "--json", "--download-directory", Path.Combine(area, "downloads"), "--status-file", Path.Combine(area, "last-check.json")]);
        using var result = JsonDocument.Parse(output); Equal("update-staged", result.RootElement[0].GetProperty("status").GetString());
        True(result.RootElement[0].GetProperty("requiresApplicationExit").GetBoolean());
        var helperPid = result.RootElement[0].GetProperty("helperProcessId").GetInt32();
        try { using var helper = Process.GetProcessById(helperPid); await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (ArgumentException) { }
        Equal("2", InstallerEngine.ListInstalled(options.StateRoot).Single().Version);
        Equal("version two", File.ReadAllText(Path.Combine(target, "version-marker.txt")));
        await new InstallerEngine().UninstallAsync(feed.ProductId, options);
        True(!File.Exists(Path.Combine(target, "heirowUpdater.exe")));
    }

    private async Task TrustedStagingPaths()
    {
        var area = Area("trusted-staging-paths");
        var directory = TrustedHelperStaging.NewDirectoryPath(area, "heirowUpdater.exe", "heirowInstaller.Cli.exe");
        Equal(Path.GetFullPath(area), Path.GetDirectoryName(directory));
        Equal(24, Path.GetFileName(directory).Length);
        True(Path.GetFileName(directory).StartsWith("h-", StringComparison.Ordinal));
        True(!Directory.Exists(directory));
        True(directory != TrustedHelperStaging.NewDirectoryPath(area, "heirowUpdater.exe"));
        await Throws<ArgumentException>(() => { TrustedHelperStaging.NewDirectoryPath(area, "../other.exe"); return Task.CompletedTask; });
        if (!OperatingSystem.IsWindows()) return;
        var tooLong = area;
        while (tooLong.Length < 230) tooLong = Path.Combine(tooLong, new string('x', 32));
        var requests = 0;
        await using var server = new LoopbackServer((string _) => { requests++; return []; });
        using var updater = new SocketJackUpdater(allowLoopback: true);
        var target = new UpdaterTarget { ProductId = "test.staging", Scope = "user" };
        var check = new UpdateCheckResult { Available = true, Feed = new() { ProductId = target.ProductId, Version = "2",
            InstallerUrl = server.Origin + "setup.hinst", Sha256 = new string('a', 64), Size = 1 } };
        using var current = Process.GetCurrentProcess();
        await Throws<PathTooLongException>(() => updater.PrepareInstallerHandoffAsync(target, check, current.Id,
            current.StartTime.ToUniversalTime().Ticks, new() { Isolated = true, DownloadDirectory = tooLong,
                TrustedPublisherKeySha256 = new string('b', 64) }));
        Equal(0, requests);
        True(!Directory.Exists(tooLong));
    }

    private async Task LegacyArguments()
    {
        if (!OperatingSystem.IsWindows()) return;
        var area = Area("legacy-arguments");
        var report = Path.Combine(area, "reported-arguments.json");
        var source = Path.Combine(area, "source"); Directory.CreateDirectory(source);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "LegacyArgumentProbe", "bin", configuration, "net10.0"));
        foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.TopDirectoryOnly))
            if (Path.GetExtension(file).ToLowerInvariant() is ".exe" or ".dll" or ".json") File.Copy(file, Path.Combine(source, Path.GetFileName(file)));
        var manifest = Manifest("test.legacy-arguments", "1");
        manifest.LegacyExecutable = new()
        {
            PayloadPath = "LegacyArgumentProbe.exe", InstallArguments = ["--report=" + report, "interactive"],
            SilentInstallArguments = ["--report=" + report, "silent"], InstallDirectoryArgument = "/DIR={InstallDir}",
            UninstallArguments = ["--report=" + report, "uninstall"]
        };
        var manifestPath = Path.Combine(area, "manifest.json"); File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options));
        var packagePath = Path.Combine(area, "fixture.hinst"); PackageBuilder.Build(manifestPath, source, packagePath);
        var options = new InstallOptions { StateRoot = Path.Combine(area, "state"), RegisterProduct = false, Silent = false };
        var engine = new InstallerEngine();
        using (var package = PackageReader.Open(packagePath)) await engine.InstallAsync(package, options);
        var args = JsonSerializer.Deserialize<string[]>(File.ReadAllText(report))!;
        True(args.Contains("interactive") && !args.Contains("silent") && !args.Any(x => x.StartsWith("/DIR=", StringComparison.Ordinal)));
        await engine.UninstallAsync(manifest.Id, options);
        options.TargetDirectory = Path.Combine(area, "explicit target"); options.Silent = true;
        var log = Path.Combine(area, "installer log.txt"); options.LegacyArguments = ["/LOG=" + log, "/SUPPRESSMSGBOXES"];
        using (var package = PackageReader.Open(packagePath)) await engine.InstallAsync(package, options);
        args = JsonSerializer.Deserialize<string[]>(File.ReadAllText(report))!;
        True(args.Contains("silent") && !args.Contains("interactive"));
        True(args.Contains("/DIR=" + options.TargetDirectory) && args.Contains("/LOG=" + log) && args.Contains("/SUPPRESSMSGBOXES"));
        await engine.UninstallAsync(manifest.Id, options);
    }

    private async Task MachineStateAcls()
    {
        if (!OperatingSystem.IsWindows()) return;
        var area = Area("machine-acl");
        const string secureRules = "(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";
        DirectorySecurity Descriptor(string sddl) { var result = new DirectorySecurity(); result.SetSecurityDescriptorSddlForm(sddl); return result; }
        True(MachineStateSecurity.HasTrustedOwnershipAndPermissions(Descriptor("O:BAG:BAD:P" + secureRules)));
        True(MachineStateSecurity.HasTrustedOwnershipAndPermissions(Descriptor("O:SYG:SYD:P" + secureRules)));
        True(!MachineStateSecurity.HasTrustedOwnershipAndPermissions(Descriptor("O:BAG:BAD:P" + secureRules + "(A;OICI;FW;;;WD)")));
        True(!MachineStateSecurity.HasTrustedOwnershipAndPermissions(Descriptor("O:BAG:BAD:P" + secureRules + "(A;OICI;GA;;;WD)")));
        True(!MachineStateSecurity.HasTrustedOwnershipAndPermissions(Descriptor("O:BAG:BAD:" + secureRules)));
        True(MachineStateSecurity.HasTrustedOwnershipAndPermissions(Descriptor("O:BAG:BAD:" + secureRules), requireProtectedDacl: false));
        True(!MachineStateSecurity.HasTrustedOwnershipAndPermissions(Descriptor("O:BAG:BAD:NO_ACCESS_CONTROL")));
        using var identity = WindowsIdentity.GetCurrent();
        True(!MachineStateSecurity.HasTrustedOwnershipAndPermissions(Descriptor("O:" + identity.User!.Value + "G:BAD:P" + secureRules)));
        var fixture = new DirectoryInfo(Path.Combine(area, "untrusted-state")); fixture.Create();
        var sections = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
        var before = fixture.GetAccessControl(sections).GetSecurityDescriptorSddlForm(sections);
        var originalBytes = fixture.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
        try
        {
            var modified = new DirectorySecurity(); modified.SetSecurityDescriptorBinaryForm(originalBytes, AccessControlSections.Access);
            modified.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Modify, AccessControlType.Allow));
            fixture.SetAccessControl(modified);
            var unsafeSddl = fixture.GetAccessControl(sections).GetSecurityDescriptorSddlForm(sections);
            await Throws<UnauthorizedAccessException>(() =>
            {
                if (OperatingSystem.IsWindows()) MachineStateSecurity.ValidateExistingRoot(fixture.FullName);
                return Task.CompletedTask;
            });
            Equal(unsafeSddl, fixture.GetAccessControl(sections).GetSecurityDescriptorSddlForm(sections));
            True(!MachineStateSecurity.IsStateObjectProtected(fixture.FullName));
        }
        finally
        {
            var restore = new DirectorySecurity(); restore.SetSecurityDescriptorBinaryForm(originalBytes, AccessControlSections.Access); fixture.SetAccessControl(restore);
        }
        string NormalizeRestoredAcl(string sddl)
        {
            var descriptor = new RawSecurityDescriptor(sddl);
            // Windows may set this bookkeeping flag when restoring an inherited DACL.
            descriptor.SetFlags(descriptor.ControlFlags & ~ControlFlags.DiscretionaryAclAutoInherited);
            return descriptor.GetSddlForm(sections);
        }
        Equal(NormalizeRestoredAcl(before), NormalizeRestoredAcl(fixture.GetAccessControl(sections).GetSecurityDescriptorSddlForm(sections)));
        var exclusivePath = Path.Combine(area, "exclusive-state.lock");
        using (var exclusive = new FileStream(exclusivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose))
            True(!MachineStateSecurity.IsStateObjectProtected(exclusivePath));
        var systemFixture = Path.Combine(System.Environment.SystemDirectory, "where.exe");
        if (File.Exists(systemFixture))
        {
            var copied = Path.Combine(area, "default-owner-copy.exe"); File.Copy(systemFixture, copied);
            var copiedOwner = new FileInfo(copied).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
            True(copiedOwner?.Equals(identity.Owner) == true);
        }

        var manifest = Manifest("test.machine-acl", "1"); manifest.Scope = "machine";
        var packagePath = Build(area, "machine", manifest, new() { ["app.txt"] = "isolated machine fixture" });
        var machineRoot = InstallerEngine.DefaultStateRoot("machine");
        using var package = PackageReader.Open(packagePath);
        if (!Directory.Exists(machineRoot))
        {
            var plan = new InstallerEngine().Plan(package, new() { TargetDirectory = Path.Combine(area, "not-installed") });
            True(plan.RequiresElevation && !Directory.Exists(machineRoot));
            if (!InstallerEngine.IsElevated)
            {
                await Throws<UnauthorizedAccessException>(() => new InstallerEngine().InstallAsync(package, new() { TargetDirectory = Path.Combine(area, "not-installed") }));
                True(!Directory.Exists(machineRoot));
            }
        }
        var isolated = Options(area, Path.Combine(area, "isolated-target"));
        var engine = new InstallerEngine(); await engine.InstallAsync(package, isolated);
        Equal("machine", InstallerEngine.ListInstalled(isolated.StateRoot).Single().Scope);
        await Throws<UnauthorizedAccessException>(() => engine.RepairAsync(manifest.Id, new() { StateRoot = isolated.StateRoot, Scope = "machine" }));
        await engine.RepairAsync(manifest.Id, isolated);
        await engine.UninstallAsync(manifest.Id, isolated);
    }

    private async Task IntegrationScope()
    {
        var area = Area("integration-scope"); var engine = new InstallerEngine();
        foreach (var kind in new[] { "registry", "service" })
        {
            var manifest = Manifest("test.scope-" + kind, "1");
            manifest.Features = [new() { Id = "system", Default = false }];
            if (kind == "registry") manifest.Registry = [new() { Hive = "machine", Key = "Software\\SocketJack\\heirowInstaller\\Tests", Name = "fixture", Value = "never written", Feature = "system" }];
            else manifest.Services = [new() { Name = "heirowInstallerScopeFixture", Executable = "app.txt", Feature = "system" }];
            var path = Build(area, kind, manifest, new() { ["app.txt"] = "plan only" });
            using var package = PackageReader.Open(path);
            var options = Options(area, Path.Combine(area, kind + "-target"));
            var excluded = engine.Plan(package, options);
            Equal("user", excluded.Scope); True(!excluded.RequiresElevation);
            options.Features = ["system"];
            await Throws<InvalidDataException>(() => { engine.Plan(package, options); return Task.CompletedTask; });
            await Throws<InvalidDataException>(() => engine.InstallAsync(package, options));
            True(!Directory.Exists(options.StateRoot) && !Directory.Exists(options.TargetDirectory));
            options.Scope = "machine";
            var machine = engine.Plan(package, options); Equal("machine", machine.Scope); True(machine.RequiresElevation);
            package.Manifest.Scope = "machine"; options.Scope = "user";
            await Throws<InvalidDataException>(() => { engine.Plan(package, options); return Task.CompletedTask; });
        }

        var safeManifest = Manifest("test.scope-receipt", "1");
        var safePath = Build(area, "safe", safeManifest, new() { ["app.txt"] = "unchanged installed payload" });
        using var safePackage = PackageReader.Open(safePath);
        var installedOptions = Options(area, Path.Combine(area, "installed"));
        await engine.InstallAsync(safePackage, installedOptions);
        var originalProduct = InstallerEngine.ListInstalled(installedOptions.StateRoot).Single();
        var originalReceipt = File.ReadAllText(originalProduct.ReceiptPath);
        foreach (var snapshot in new ModuleSnapshot[]
        {
            new() { Kind = "service", Key = "never touched" },
            new() { Kind = "registry", Values = new() { ["hive"] = "machine" } },
            new() { Kind = "environment", Values = new() { ["scope"] = "machine" } }
        })
        {
            var spoofed = JsonSerializer.Deserialize<InstalledProduct>(originalReceipt, InstallerJson.Options)!;
            spoofed.Modules = [snapshot];
            // Maintenance remains isolated even if this test runs against a regression.
            File.WriteAllText(originalProduct.ReceiptPath, JsonSerializer.Serialize(spoofed, InstallerJson.Options));
            var rejectedReceipt = File.ReadAllText(originalProduct.ReceiptPath);
            Equal(0, InstallerEngine.ListInstalled(installedOptions.StateRoot).Count);
            await Throws<InvalidDataException>(() => engine.RepairAsync(spoofed.Id, Options(area, originalProduct.InstallDirectory)));
            await Throws<InvalidDataException>(() => engine.UninstallAsync(spoofed.Id, Options(area, originalProduct.InstallDirectory)));
            await Throws<InvalidDataException>(() => engine.InstallAsync(safePackage, installedOptions));
            Equal(rejectedReceipt, File.ReadAllText(originalProduct.ReceiptPath));
            Equal("unchanged installed payload", File.ReadAllText(Path.Combine(originalProduct.InstallDirectory, "app.txt")));
            // Non-isolated receipt metadata is rejected by planning without invoking OS integrations.
            spoofed.Isolated = false;
            File.WriteAllText(originalProduct.ReceiptPath, JsonSerializer.Serialize(spoofed, InstallerJson.Options));
            await Throws<InvalidDataException>(() => { engine.Plan(safePackage, installedOptions); return Task.CompletedTask; });
            Equal(0, InstallerEngine.ListInstalled(installedOptions.StateRoot).Count);
        }
        File.WriteAllText(originalProduct.ReceiptPath, originalReceipt);
        await engine.UninstallAsync(originalProduct.Id, installedOptions);
    }

    private async Task AppUpdateCompanion()
    {
        var area = Area("app-companion"); using var rsa = RSA.Create(2048);
        var key = Path.Combine(area, "publisher.pem"); File.WriteAllText(key, rsa.ExportPkcs8PrivateKeyPem());
        var pin = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        var manifest = Manifest("test.app-companion", "1"); manifest.EntryPoint = "application.exe";
        manifest.UpdateUrl = "https://updates.example.invalid/application.json";
        manifest.Features = [new() { Id = "updates", Default = false }];
        manifest.UpdateCompanion = new() { RuntimeMode = "app-local", TokenEnvironmentVariable = "FIXTURE_UPDATE_TOKEN", AuthApplication = "example" };
        manifest.AffectedResources = [new() { Path = "{app}\\owned-plugin.vst3", Feature = "updates" }];
        manifest.Payload = [new() { Path = "application.exe" }, new() { Path = "heirowUpdater.exe", Feature = "updates" }];
        var files = new Dictionary<string, string> { ["application.exe"] = "non-executed application fixture", ["heirowUpdater.exe"] = "non-executed updater fixture" };
        var path = Build(area, "v1", manifest, files, key);
        var target = Path.Combine(area, "installed"); Directory.CreateDirectory(target);
        var configurationPath = Path.Combine(target, "heirowUpdater.json"); File.WriteAllText(configurationPath, "original application data");
        var engine = new InstallerEngine(); var options = Options(area, target);
        using var package = PackageReader.Open(path);
        await engine.InstallAsync(package, options);
        True(!File.Exists(Path.Combine(target, "heirowUpdater.exe")));
        Equal("original application data", File.ReadAllText(configurationPath));
        options.Features = ["updates"];
        True(engine.Plan(package, options).UpdateCompanionEnabled);
        await engine.InstallAsync(package, options);
        var config = JsonSerializer.Deserialize<UpdateCompanionConfiguration>(File.ReadAllText(configurationPath), InstallerJson.Options)!;
        Equal(manifest.Id, config.ProductId); Equal("1", config.CurrentVersion); Equal(pin, config.PublisherKeySha256);
        Equal(manifest.UpdateUrl, config.FeedUrl); Equal(target, config.InstallDirectory); Equal(options.StateRoot, config.StateRoot);
        Equal("application.exe", config.EntryPoint); Equal("after-exit", config.Mode); True(config.Isolated);
        Equal("FIXTURE_UPDATE_TOKEN", config.TokenEnvironmentVariable); True(config.Features.Contains("updates"));
        Equal("example", config.AuthApplication);
        True(UpdaterTarget.FromInstalled(InstallerEngine.ListInstalled(options.StateRoot).Single(), options.StateRoot).UpdateMode is null);
        Equal(Path.Combine(target, "owned-plugin.vst3"), config.AffectedResourceFiles.Single());
        config.AffectedResourceFiles = [Path.Combine(area, "unrelated.dll")];
        File.WriteAllText(configurationPath, JsonSerializer.Serialize(config, InstallerJson.Options));
        Equal(Path.Combine(target, "owned-plugin.vst3"), ApplicationUpdater.ReadConfiguration(configurationPath).AffectedResourceFiles.Single());
        config.AffectedResourceFiles = [];
        File.WriteAllText(configurationPath, JsonSerializer.Serialize(config, InstallerJson.Options));
        Equal(Path.Combine(target, "owned-plugin.vst3"), ApplicationUpdater.ReadConfiguration(configurationPath).AffectedResourceFiles.Single());
        config.EntryPoint = "unrelated.exe"; File.WriteAllText(Path.Combine(target, config.EntryPoint), "not an executed application");
        File.WriteAllText(configurationPath, JsonSerializer.Serialize(config, InstallerJson.Options));
        await Throws<InvalidDataException>(() => Task.Run(() => ApplicationUpdater.ReadConfiguration(configurationPath)));
        File.Delete(Path.Combine(target, config.EntryPoint));
        True(InstallerEngine.ListInstalled(options.StateRoot).Single().Files.Any(x => x.Path == "heirowUpdater.json"));
        File.WriteAllText(configurationPath, "corrupted");
        await engine.RepairAsync(manifest.Id, Options(area, target));
        config = JsonSerializer.Deserialize<UpdateCompanionConfiguration>(File.ReadAllText(configurationPath), InstallerJson.Options)!;
        Equal(pin, config.PublisherKeySha256);
        options.Features = [];
        await engine.InstallAsync(package, options);
        True(!File.Exists(Path.Combine(target, "heirowUpdater.exe")));
        Equal("original application data", File.ReadAllText(configurationPath));
        manifest.Version = "2";
        var upgraded = Build(area, "v2", manifest, files, key); options.Features = ["updates"];
        using (var second = PackageReader.Open(upgraded)) await engine.InstallAsync(second, options);
        config = JsonSerializer.Deserialize<UpdateCompanionConfiguration>(File.ReadAllText(configurationPath), InstallerJson.Options)!;
        Equal("2", config.CurrentVersion);
        await engine.UninstallAsync(manifest.Id, Options(area, target));
        Equal("original application data", File.ReadAllText(configurationPath)); True(!File.Exists(Path.Combine(target, "heirowUpdater.exe")));
        var unsigned = Build(area, "unsigned", manifest, files);
        using (var unsignedPackage = PackageReader.Open(unsigned))
        {
            await Throws<CryptographicException>(() => { engine.Plan(unsignedPackage, options); return Task.CompletedTask; });
            options.Features = []; True(!engine.Plan(unsignedPackage, options).UpdateCompanionEnabled);
        }
        var invalidConfiguration = JsonSerializer.Deserialize<PackageManifest>(JsonSerializer.Serialize(package.Manifest, InstallerJson.Options), InstallerJson.Options)!;
        invalidConfiguration.UpdateCompanion!.ConfigurationPath = "application.exe";
        await Throws<InvalidDataException>(() => { PackageSafety.ValidateManifest(invalidConfiguration, true); return Task.CompletedTask; });
        await Throws<InvalidDataException>(() => { StandaloneRuntimeInspector.RequireEmbeddedRuntime(FindUpdaterCli()); return Task.CompletedTask; });
        var legacy = Manifest("test.legacy-companion", "1"); legacy.EntryPoint = "App/application.exe";
        legacy.Features = [new() { Id = "updates", Default = false }]; legacy.UpdateCompanion = new() { RuntimeMode = "app-local" }; legacy.UpdateUrl = manifest.UpdateUrl;
        legacy.LegacyMsi = new() { PayloadPath = "fixture.msi", ProductCode = "{8F2FE249-C8CC-405B-A10B-FA9B789D334B}" };
        legacy.Payload = [new() { Path = "fixture.msi" }, new() { Path = "heirowUpdater.exe", Feature = "updates" }];
        var legacyPath = Build(area, "legacy", legacy, new() { ["fixture.msi"] = "non-executed MSI fixture", ["heirowUpdater.exe"] = "non-executed updater" }, key);
        using var legacyPackage = PackageReader.Open(legacyPath);
        var legacyOptions = new InstallOptions { StateRoot = Path.Combine(area, "legacy-state"), Features = ["updates"], RegisterProduct = true };
        await Throws<InvalidDataException>(() => { engine.Plan(legacyPackage, legacyOptions); return Task.CompletedTask; });
        True(!Directory.Exists(legacyOptions.StateRoot));
        legacyOptions.TargetDirectory = Path.Combine(area, "authored-legacy-root");
        True(engine.Plan(legacyPackage, legacyOptions).UpdateCompanionEnabled);
    }

    private async Task CompanionMaintenance()
    {
        if (!OperatingSystem.IsWindows()) return;
        var area = Area("companion-maintenance"); var target = Path.Combine(area, "installed");
        using var rsa = RSA.Create(2048); var key = Path.Combine(area, "publisher.pem"); File.WriteAllText(key, rsa.ExportPkcs8PrivateKeyPem());
        var manifest = Manifest("test.companion-maintenance", "1"); manifest.EntryPoint = "application.exe";
        manifest.UpdateUrl = "https://updates.example.invalid/feed.json"; manifest.Features = [new() { Id = "updates", Default = false }];
        manifest.UpdateCompanion = new() { RuntimeMode = "app-local" }; manifest.Payload = [new() { Path = "application.exe" }, new() { Path = "heirowUpdater.exe", Feature = "updates" }];
        var path = Build(area, "fixture", manifest, new() { ["application.exe"] = "non-executed app", ["heirowUpdater.exe"] = "non-executed updater" }, key);
        using var package = PackageReader.Open(path); var options = Options(area, target); options.Features = ["updates"];
        var engine = new InstallerEngine(); await engine.InstallAsync(package, options);
        True(!await UpdateCompanionControl.StopAsync(target));
        using (var helperObserver = new EventWaitHandle(false, EventResetMode.ManualReset, UpdateCompanionControl.GetStopEventName(target)))
        {
            options.CompanionStopAlreadyHandled = true;
            await engine.InstallAsync(package, options);
            True(!helperObserver.WaitOne(0));
            File.Delete(Path.Combine(target, "application.exe"));
            await Throws<InvalidOperationException>(() => engine.InstallAsync(package, options));
            True(!File.Exists(Path.Combine(target, "application.exe")));
            File.WriteAllText(Path.Combine(target, "application.exe"), "non-executed app");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Throws<OperationCanceledException>(() => engine.InstallAsync(package, options, null, cancelled.Token));
            True(!helperObserver.WaitOne(0));
            options.CompanionStopAlreadyHandled = false;
        }
        Equal(UpdateCompanionControl.GetStopEventName(target), UpdateCompanionControl.GetStopEventName(target.ToUpperInvariant() + Path.DirectorySeparatorChar));
        await Throws<InvalidDataException>(() => UpdateCompanionControl.StopAsync(target, "../unrelated.exe"));
        var unrelated = Path.Combine(area, "unrelated-application.exe"); File.WriteAllText(unrelated, "application continues running");
        using (var applicationLock = new FileStream(unrelated, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            async Task Watcher(Func<Task> maintenance)
            {
                using var signal = new EventWaitHandle(false, EventResetMode.ManualReset, UpdateCompanionControl.GetStopEventName(target));
                using var imageLock = new FileStream(Path.Combine(target, "heirowUpdater.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
                var stopped = Task.Run(async () =>
                {
                    True(signal.WaitOne(TimeSpan.FromSeconds(10)));
                    await Task.Delay(150); imageLock.Dispose();
                });
                await maintenance(); await stopped;
                True(signal.WaitOne(0));
                Equal(0L, applicationLock.Position);
            }
            await Watcher(async () => { await engine.RepairAsync(manifest.Id, Options(area, target)); });
            options.Features = [];
            await Watcher(async () => { await engine.InstallAsync(package, options); });
            True(!File.Exists(Path.Combine(target, "heirowUpdater.exe")));
            options.Features = ["updates"]; await engine.InstallAsync(package, options);
            await Watcher(async () => { await engine.UninstallAsync(manifest.Id, Options(area, target)); });
        }
        Equal("application continues running", File.ReadAllText(unrelated));
        True(!File.Exists(Path.Combine(target, "heirowUpdater.exe")));
        Equal(0, InstallerEngine.ListInstalled(options.StateRoot).Count);
        options.CompanionStopAlreadyHandled = true;
        await Throws<InvalidOperationException>(() => engine.InstallAsync(package, options));
        True(!File.Exists(Path.Combine(target, "application.exe")));
    }

    private async Task BackendRegistrations()
    {
        if (!OperatingSystem.IsWindows()) return;
        var area = Area("backend-registration"); using var rsa = RSA.Create(2048);
        var key = Path.Combine(area, "publisher.pem"); File.WriteAllText(key, rsa.ExportPkcs8PrivateKeyPem());
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var probe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "LegacyArgumentProbe", "bin", configuration, "net10.0"));
        var source = Path.Combine(area, "source"); Directory.CreateDirectory(source);
        foreach (var file in Directory.EnumerateFiles(probe)) if (Path.GetExtension(file) is ".exe" or ".dll" or ".json") File.Copy(file, Path.Combine(source, Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(source, "heirowUpdater.exe"), "non-executed updater fixture");
        var engine = new InstallerEngine();
        foreach (var burn in new[] { false, true })
        {
            var identity = Guid.NewGuid().ToString(); var registration = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\heirowInstaller.Tests." + identity;
            using var hive = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Registry64);
            True(hive.OpenSubKey(registration) is null);
            var productId = "test.backend." + identity; var target = Path.Combine(area, identity); var report = Path.Combine(area, identity + ".report.json");
            var definition = new BackendRegistrationDefinition { Hive = "user", View = "registry64", UninstallKey = registration };
            var upgrade = Guid.NewGuid().ToString("B"); if (burn) { definition.UninstallKey = null; definition.BundleUpgradeCode = upgrade; }
            var backendArguments = new List<string> { "--report=" + report, "--fixture-arp-key=" + registration, "--fixture-target={InstallDir}" };
            if (burn) backendArguments.Add("--fixture-burn-upgrade=" + upgrade);
            var manifest = Manifest(productId, "1"); manifest.EntryPoint = "application.exe"; manifest.UpdateUrl = "https://updates.example.invalid/feed.json";
            manifest.Features = [new() { Id = "updates", Default = false }]; manifest.UpdateCompanion = new() { RuntimeMode = "app-local" };
            manifest.Payload = Directory.EnumerateFiles(source).Select(file => new PayloadFile { Source = Path.GetFileName(file), Path = Path.GetFileName(file), Feature = Path.GetFileName(file) == "heirowUpdater.exe" ? "updates" : null }).ToList();
            manifest.LegacyExecutable = new() { PayloadPath = "LegacyArgumentProbe.exe", InstallArguments = backendArguments, RepairArguments = backendArguments,
                UninstallArguments = [.. backendArguments, "--fixture-uninstall"], InstallDirectoryArgument = "--fixture-override={InstallDir}" };
            var manifestPath = Path.Combine(area, identity + ".json"); var packagePath = Path.Combine(area, identity + ".hinst");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options));
            PackageBuilder.Build(manifestPath, source, packagePath, new() { SigningKeyPath = key });
            var options = new InstallOptions { TargetDirectory = target, StateRoot = Path.Combine(area, "state"), Features = ["updates"], Scope = "user", Silent = true,
                InstallerPath = Path.Combine(AppContext.BaseDirectory, "HeirowInstaller.Tests.exe") };
            using (var unknown = PackageReader.Open(packagePath)) await Throws<InvalidDataException>(() => { engine.Plan(unknown, options); return Task.CompletedTask; });
            manifest.LegacyExecutable.MaintenanceRegistration = definition;
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options)); PackageBuilder.Build(manifestPath, source, packagePath, new() { SigningKeyPath = key });
            using var package = PackageReader.Open(packagePath);
            var unsupportedDirectory = manifest.LegacyExecutable.InstallDirectoryArgument;
            manifest.LegacyExecutable.InstallDirectoryArgument = null;
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options));
            var fixedPackage = Path.Combine(area, identity + ".fixed.hinst"); PackageBuilder.Build(manifestPath, source, fixedPackage, new() { SigningKeyPath = key });
            using (var fixedBackend = PackageReader.Open(fixedPackage)) await Throws<InvalidDataException>(() => { engine.Plan(fixedBackend, options); return Task.CompletedTask; });
            manifest.LegacyExecutable.InstallDirectoryArgument = unsupportedDirectory;
            var ownArp = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\heirowInstaller." + productId;
            try
            {
                await engine.InstallAsync(package, options);
                using (var original = hive.OpenSubKey(registration)) Equal(1, original!.GetValue("SystemComponent"));
                var installed = InstallerEngine.ListInstalled(options.StateRoot).Single(); True(installed.RegisteredProduct); Equal(1, installed.BackendRegistrations.Count);
                Equal(registration, installed.BackendRegistrations[0].Values["path"]);
                using (var original = hive.OpenSubKey(registration, true)) original!.SetValue("SystemComponent", 0, Microsoft.Win32.RegistryValueKind.DWord);
                await engine.RepairAsync(productId, options);
                using (var original = hive.OpenSubKey(registration)) Equal(1, original!.GetValue("SystemComponent"));
                if (!burn)
                {
                    options.Features = []; await engine.InstallAsync(package, options);
                    using (var original = hive.OpenSubKey(registration)) Equal(0, original!.GetValue("SystemComponent"));
                    using (var own = hive.OpenSubKey(ownArp)) True(own is null);
                    True(!File.Exists(Path.Combine(target, "heirowUpdater.exe")));
                    options.Features = ["updates"]; await engine.InstallAsync(package, options);
                    installed = InstallerEngine.ListInstalled(options.StateRoot).Single();
                    using (var own = hive.OpenSubKey(ownArp))
                    {
                        True(own is not null);
                        Equal('"' + installed.UninstallerPath + "\" uninstall \"" + productId + "\" --state-root \"" + options.StateRoot + "\" --scope user --silent", own!.GetValue("QuietUninstallString"));
                    }
                    await RunCli(installed.UninstallerPath!, ["uninstall", productId, "--state-root", options.StateRoot!, "--scope", "user", "--silent"]);
                }
                else
                {
                    // Simulate an explicit external backend removal after the signed helper was staged.
                    hive.DeleteSubKeyTree(registration, false); File.Delete(Path.Combine(target, "application.exe"));
                    var before = File.ReadAllText(report); options.CompanionStopAlreadyHandled = true;
                    await Throws<InvalidOperationException>(() => engine.InstallAsync(package, options));
                    Equal(before, File.ReadAllText(report)); True(!File.Exists(Path.Combine(target, "application.exe")));
                    options.CompanionStopAlreadyHandled = false; await engine.UninstallAsync(productId, options);
                    Equal(before, File.ReadAllText(report));
                }
                using (var original = hive.OpenSubKey(registration)) True(original is null);
                using (var own = hive.OpenSubKey(ownArp)) True(own is null);
                True(!File.Exists(Path.Combine(target, "heirowUpdater.exe")) && !File.Exists(Path.Combine(target, "heirowUpdater.json")));
                Equal(0, InstallerEngine.ListInstalled(options.StateRoot).Count);
            }
            finally
            {
                options.CompanionStopAlreadyHandled = false;
                if (InstallerEngine.ListInstalled(options.StateRoot).Any(x => x.Id == productId)) try { await engine.UninstallAsync(productId, options); } catch { }
                using var leftover = hive.OpenSubKey(registration);
                if (leftover?.GetValue("HeirowInstallerAcceptanceFixture") as string == identity) hive.DeleteSubKeyTree(registration, false);
            }
        }
    }

    private async Task ManualUpdateFeedback()
    {
        var area = Area("manual-feedback");
        var mode = 0; var requests = 0; var blockNext = 0;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var feed = new UpdateFeed { ProductId = "test.manual-feedback", Name = "Manual feedback fixture", Version = "1", PackageType = "hinst", Size = 1, Sha256 = new string('a', 64) };
        await using var server = new LoopbackServer((NetworkRequest _) =>
        {
            Interlocked.Increment(ref requests);
            if (Interlocked.CompareExchange(ref blockNext, 0, 1) == 1) { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); }
            if (Volatile.Read(ref mode) == 2) return new(500, Encoding.UTF8.GetBytes("fixture response details must not enter feedback"));
            feed.Version = Volatile.Read(ref mode) == 1 ? "2" : "1";
            return new(200, JsonSerializer.SerializeToUtf8Bytes(feed, InstallerJson.Options));
        });
        feed.InstallerUrl = server.Origin + "setup.hinst";
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var probeDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "ManualCheckApplicationProbe", "bin", configuration, "net10.0"));
        var source = Path.Combine(area, "source"); Directory.CreateDirectory(source);
        foreach (var name in new[] { "ManualCheckApplicationProbe.exe", "ManualCheckApplicationProbe.dll", "ManualCheckApplicationProbe.deps.json", "ManualCheckApplicationProbe.runtimeconfig.json" })
            File.Copy(Path.Combine(probeDirectory, name), Path.Combine(source, name));
        File.WriteAllText(Path.Combine(source, "heirowUpdater.exe"), "non-executed SDK fixture updater");
        using var key = RSA.Create(2048); var keyPath = Path.Combine(area, "fixture-signing.pem"); File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        var manifest = Manifest(feed.ProductId, "1"); manifest.EntryPoint = "ManualCheckApplicationProbe.exe";
        manifest.UpdateUrl = server.Origin + "feed.json"; manifest.Features = [new() { Id = "updates", Default = true }];
        manifest.UpdateCompanion = new() { RuntimeMode = "app-local", CredentialOrigin = server.Origin };
        manifest.Payload = Directory.EnumerateFiles(source).Select(path => new PayloadFile
        { Path = Path.GetFileName(path), Feature = Path.GetFileName(path) == "heirowUpdater.exe" ? "updates" : null }).ToList();
        var manifestPath = Path.Combine(area, "manifest.json"); File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options));
        var packagePath = Path.Combine(area, "app.hinst"); PackageBuilder.Build(manifestPath, source, packagePath, new() { SigningKeyPath = keyPath });
        var target = Path.Combine(area, "app"); var install = Options(area, target);
        using (var package = PackageReader.Open(packagePath)) await new InstallerEngine().InstallAsync(package, install);
        var profile = Path.Combine(target, "heirowUpdater.json");
        var stateDirectory = ApplicationUpdater.StateDirectory(ApplicationUpdater.ReadConfiguration(profile, true));
        var feedback = new System.Collections.Concurrent.ConcurrentQueue<ApplicationLifecycleResult>();
        Task<ApplicationUpdateChoice> NoUpdatePrompt(ApplicationUpdatePrompt _, CancellationToken __) => throw new InvalidOperationException("A current feed cannot open an update prompt.");
        ApplicationLifecycleOptions Settings(Func<ApplicationLifecycleResult, CancellationToken, Task>? callback = null) => new()
        { AllowLoopback = true, StatusPath = Path.Combine(area, "owner-status.json"), ManualStatusChangedAsync = callback ?? ((status, _) => { feedback.Enqueue(status); return Task.CompletedTask; }) };
        async Task Wait(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Manual feedback fixture timed out."); await Task.Delay(30); }
        }
        async Task<ApplicationLifecycleResult> Relay()
        {
            return await ApplicationUpdater.RunAsync(profile, null, null, true,
                Settings((_, _) => throw new InvalidOperationException("The relay must never create a duplicate feedback window.")), NoUpdatePrompt);
        }
        using (var current = await ApplicationUpdater.RunAsync(profile, null, null, true, Settings(), NoUpdatePrompt))
        {
            Equal("current", current.Status); True(current.ManualCheck); True(Guid.TryParseExact(current.ManualRequestId, "N", out _));
            Equal("checking,current", string.Join(',', feedback.Select(x => x.Status)));
            True(feedback.All(x => x.ManualCheck && x.ManualRequestId == current.ManualRequestId));
        }
        var quietCallbacks = 0; var quiet = Settings((_, _) => { quietCallbacks++; return Task.CompletedTask; }); quiet.NoPrompt = true;
        using (var headless = await ApplicationUpdater.RunAsync(profile, null, null, true, quiet, NoUpdatePrompt)) Equal("current", headless.Status);
        Equal(0, quietCallbacks);

        var run = Path.Combine(area, "running-app"); Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, ".manual-check-probe"), "heirowInstaller manual feedback acceptance\n");
        var start = new ProcessStartInfo(Path.Combine(target, manifest.EntryPoint)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = target, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--run-directory"); start.ArgumentList.Add(run);
        using var application = Process.Start(start) ?? throw new InvalidOperationException("Could not start owned console fixture.");
        using var ownerCancellation = new CancellationTokenSource();
        CancellationTokenSource? queuedCancellation = null;
        Task<ApplicationLifecycleResult>? owner = null;
        var dismiss = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shown = new TaskCompletionSource<ApplicationLifecycleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerStatus = ""; var updatePrompts = 0;
        try
        {
            await Wait(() => File.Exists(Path.Combine(run, "ready.json")));
            var ticks = application.StartTime.ToUniversalTime().Ticks;
            // Pending updates also own one correlated manual information window, rather than silently returning or spawning duplicates.
            await ApplicationUpdater.MarkHandoffAsync(Path.Combine(stateDirectory, "state.json"), application);
            owner = ApplicationUpdater.RunAsync(profile, null, null, true, Settings(async (status, token) =>
            { feedback.Enqueue(status); shown.TrySetResult(status); await dismiss.Task.WaitAsync(token); }), NoUpdatePrompt, ownerCancellation.Token);
            var pendingVisible = await shown.Task.WaitAsync(TimeSpan.FromSeconds(15)); Equal("update-staged", pendingVisible.Status);
            using (var pendingRelay = await Relay()) Equal(pendingVisible.ManualRequestId, pendingRelay.ManualRequestId);
            dismiss.TrySetResult(); using (var pendingResult = await owner) Equal("update-staged", pendingResult.Status); owner = null;
            await ApplicationUpdater.CompleteHandoffAsync(Path.Combine(stateDirectory, "state.json"));
            dismiss = new(TaskCreationOptions.RunContinuationsAsynchronously); shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var ownerOptions = Settings(async (status, token) =>
            {
                feedback.Enqueue(status);
                if (status.Status is "current" or "error") { shown.TrySetResult(status); await dismiss.Task.WaitAsync(token); }
            });
            ownerOptions.StatusChanged = status => Volatile.Write(ref ownerStatus, status.Status);
            Task<ApplicationUpdateChoice> AvailablePrompt(ApplicationUpdatePrompt context, CancellationToken _)
            { True(context.Update.Available); Interlocked.Increment(ref updatePrompts); return Task.FromResult(ApplicationUpdateChoice.Postpone); }
            owner = ApplicationUpdater.RunAsync(profile, application.Id, ticks, false, ownerOptions, AvailablePrompt, ownerCancellation.Token);
            await Wait(() => Volatile.Read(ref ownerStatus) == "waiting-for-exit");
            var before = Volatile.Read(ref requests);
            using var relayed = await Relay();
            var visible = await shown.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Equal("current", visible.Status); Equal(relayed.ManualRequestId, visible.ManualRequestId); Equal(before + 1, Volatile.Read(ref requests));
            using (var repeated = await Relay()) Equal(relayed.ManualRequestId, repeated.ManualRequestId);
            Equal(before + 1, Volatile.Read(ref requests));
            using (var headless = await ApplicationUpdater.RunAsync(profile, null, null, true, quiet, NoUpdatePrompt)) Equal("current", headless.Status);
            Equal(0, quietCallbacks);
            using (var repeated = await Relay()) Equal(relayed.ManualRequestId, repeated.ManualRequestId);
            dismiss.TrySetResult(); await Wait(() => Volatile.Read(ref ownerStatus) == "waiting-for-exit"); True(!owner.IsCompleted && !application.HasExited);

            // A failed manual check leaves the owner available for a fresh retry after its error dialog is dismissed.
            shown = new(TaskCreationOptions.RunContinuationsAsynchronously); dismiss = new(TaskCreationOptions.RunContinuationsAsynchronously); Volatile.Write(ref mode, 2);
            using var failedRelay = await Relay();
            var error = await shown.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Equal("error", error.Status); Equal(failedRelay.ManualRequestId, error.ManualRequestId);
            True(error.Message is not null && !error.Message.Contains("fixture response details", StringComparison.Ordinal));
            using (var coalesced = await Relay()) Equal(failedRelay.ManualRequestId, coalesced.ManualRequestId);
            dismiss.TrySetResult(); await Wait(() => Volatile.Read(ref ownerStatus) == "waiting-for-exit"); True(!owner.IsCompleted);
            shown = new(TaskCreationOptions.RunContinuationsAsynchronously); dismiss = new(TaskCreationOptions.RunContinuationsAsynchronously); Volatile.Write(ref mode, 0);
            using var retry = await Relay();
            var retried = await shown.Task.WaitAsync(TimeSpan.FromSeconds(15)); Equal("current", retried.Status); Equal(retry.ManualRequestId, retried.ManualRequestId);
            True(retry.ManualRequestId != failedRelay.ManualRequestId); dismiss.TrySetResult(); await Wait(() => Volatile.Read(ref ownerStatus) == "waiting-for-exit");
            Volatile.Write(ref mode, 1);
            using var available = await Relay(); await Wait(() => Volatile.Read(ref updatePrompts) == 1 && Volatile.Read(ref ownerStatus) == "waiting-for-exit");
            True(feedback.Any(x => x.Status == "available" && x.ManualRequestId == available.ManualRequestId));
            ownerCancellation.Cancel(); await Throws<OperationCanceledException>(async () => { using var ignored = await owner; }); owner = null;

            // A request already queued while ownership initializes must not be discarded as an initial timestamp.
            using (var initializing = new FileStream(Path.Combine(stateDirectory, "lifecycle.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            { using var pending = await Relay(); True(pending.ManualRequestId is not null); }
            Volatile.Write(ref mode, 0); feedback.Clear();
            queuedCancellation = new CancellationTokenSource();
            owner = ApplicationUpdater.RunAsync(profile, application.Id, ticks, false, Settings(), NoUpdatePrompt, queuedCancellation.Token);
            await Wait(() => feedback.Any(x => x.Status == "current")); True(!owner.IsCompleted);
            var consumedQueuedId = feedback.First(x => x.Status == "current").ManualRequestId;
            queuedCancellation.Cancel(); await Throws<OperationCanceledException>(async () => { using var ignored = await owner; }); owner = null;
            string? QueuedAcknowledgement()
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(stateDirectory, "state.json")));
                return document.RootElement.GetProperty("lastManualRequestId").GetString();
            }
            Equal(consumedQueuedId, QueuedAcknowledgement());

            // A direct check D must not overwrite the acknowledgement of queued Q and replay Q on the next automatic startup.
            queuedCancellation.Dispose(); queuedCancellation = new CancellationTokenSource(); feedback.Clear();
            owner = ApplicationUpdater.RunAsync(profile, null, null, true, Settings(), NoUpdatePrompt, queuedCancellation.Token);
            await Wait(() => feedback.Any(x => x.Status == "current"));
            var directId = feedback.First(x => x.Status == "current").ManualRequestId;
            True(directId != consumedQueuedId); Equal(consumedQueuedId, QueuedAcknowledgement());
            queuedCancellation.Cancel(); await Throws<OperationCanceledException>(async () => { using var ignored = await owner; }); owner = null;
            await ApplicationUpdater.MarkHandoffAsync(Path.Combine(stateDirectory, "state.json"), application);
            using (var pendingDirect = await ApplicationUpdater.RunAsync(profile, null, null, true, Settings(), NoUpdatePrompt))
            { Equal("update-staged", pendingDirect.Status); True(pendingDirect.ManualRequestId != consumedQueuedId); Equal(consumedQueuedId, QueuedAcknowledgement()); }
            await ApplicationUpdater.CompleteHandoffAsync(Path.Combine(stateDirectory, "state.json")); // Reset only the isolated fixture's earlier Postpone choice.

            queuedCancellation.Dispose(); queuedCancellation = new CancellationTokenSource(); feedback.Clear();
            var beforeFreshAutomatic = Volatile.Read(ref requests); Volatile.Write(ref ownerStatus, "");
            var freshAutomaticOptions = Settings(); freshAutomaticOptions.StatusChanged = status => Volatile.Write(ref ownerStatus, status.Status);
            owner = ApplicationUpdater.RunAsync(profile, application.Id, ticks, false, freshAutomaticOptions, NoUpdatePrompt, queuedCancellation.Token);
            await Wait(() => Volatile.Read(ref ownerStatus) == "waiting-for-exit");
            True(feedback.IsEmpty); Equal(beforeFreshAutomatic, Volatile.Read(ref requests));
            using (var freshQueued = await Relay())
            {
                True(freshQueued.ManualRequestId != consumedQueuedId && freshQueued.ManualRequestId != directId);
                await Wait(() => feedback.Any(x => x.Status == "current" && x.ManualRequestId == freshQueued.ManualRequestId));
                Equal(beforeFreshAutomatic + 1, Volatile.Read(ref requests)); Equal(freshQueued.ManualRequestId, QueuedAcknowledgement());
            }
            queuedCancellation.Cancel(); await Throws<OperationCanceledException>(async () => { using var ignored = await owner; }); owner = null;
            File.WriteAllText(Path.Combine(run, "exit.flag"), "owned fixture release"); await application.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            feedback.Clear();
            using (var automatic = await ApplicationUpdater.RunAsync(profile, application.Id, ticks, false, Settings(), NoUpdatePrompt)) { Equal("current", automatic.Status); True(!automatic.ManualCheck); }
            True(feedback.IsEmpty);

            // A newer manual request arriving during a busy automatic HTTP check must get its own visible check before the owner exits.
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously); Volatile.Write(ref blockNext, 1);
            var busy = ApplicationUpdater.RunAsync(profile, application.Id, ticks, false, Settings(), NoUpdatePrompt);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var busyRelay = await Relay(); release.TrySetResult();
            using var busyResult = await busy.WaitAsync(TimeSpan.FromSeconds(15));
            Equal("current", busyResult.Status); True(busyResult.ManualCheck); Equal(busyRelay.ManualRequestId, busyResult.ManualRequestId);
            True(feedback.Any(x => x.Status == "checking" && x.ManualRequestId == busyRelay.ManualRequestId));
            // A manual request arriving while an automatic update prompt is open survives its Postpone result.
            feedback.Clear(); Volatile.Write(ref mode, 1);
            var automaticPrompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closeAutomaticPrompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var busyPrompt = ApplicationUpdater.RunAsync(profile, application.Id, ticks, false, Settings(), async (_, token) =>
            { automaticPrompt.TrySetResult(); await closeAutomaticPrompt.Task.WaitAsync(token); return ApplicationUpdateChoice.Postpone; });
            await automaticPrompt.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var promptRelay = await Relay(); Volatile.Write(ref mode, 0); closeAutomaticPrompt.TrySetResult();
            using (var promptResult = await busyPrompt.WaitAsync(TimeSpan.FromSeconds(15)))
            { Equal("current", promptResult.Status); True(promptResult.ManualCheck); Equal(promptRelay.ManualRequestId, promptResult.ManualRequestId); }
            feedback.Clear(); Volatile.Write(ref mode, 2);
            await Throws<HttpRequestException>(async () => { using var ignored = await ApplicationUpdater.RunAsync(profile, null, null, true, Settings(), NoUpdatePrompt); });
            Equal("checking,error", string.Join(',', feedback.Select(x => x.Status)));
            await Throws<HttpRequestException>(async () => { using var ignored = await ApplicationUpdater.RunAsync(profile, null, null, true,
                Settings((status, _) => status.Status == "error" ? throw new InvalidOperationException("fixture feedback failure") : Task.CompletedTask), NoUpdatePrompt); });
        }
        finally
        {
            release.TrySetResult(); dismiss.TrySetResult(); ownerCancellation.Cancel(); queuedCancellation?.Cancel();
            try { if (owner is not null) try { using var ignored = await owner.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { } }
            finally
            {
                queuedCancellation?.Dispose();
                if (!application.HasExited) { File.WriteAllText(Path.Combine(run, "exit.flag"), "owned fixture cleanup"); await application.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            }
        }
    }

    private Task UpdaterHosts()
    {
        var area = Area("updater-hosts");
        var installed = Path.Combine(area, "installed"); var extracted = Path.Combine(area, "extracted");
        var dotnet = Path.Combine(area, "dotnet"); var missing = Path.Combine(area, "missing");
        foreach (var directory in new[] { installed, extracted, dotnet, missing }) Directory.CreateDirectory(directory);
        string Fixture(string directory, string name) { var path = Path.Combine(directory, name); File.WriteAllText(path, "host discovery fixture"); return path; }
        var updater = Fixture(installed, "heirowUpdater.exe");
        var application = Fixture(installed, "CustomApplication.exe");
        var fallback = Fixture(extracted, "heirowUpdater.exe");
        var sharedHost = Fixture(dotnet, "dotnet.exe"); Fixture(dotnet, "heirowUpdater.exe");
        Equal(updater, UpdaterHostResolver.FindExecutable(updater, extracted));
        Equal(updater, UpdaterHostResolver.FindExecutable(application, extracted));
        Equal(fallback, UpdaterHostResolver.FindExecutable(sharedHost, extracted));
        Equal(fallback, UpdaterHostResolver.FindExecutable(null, extracted));
        True(UpdaterHostResolver.FindExecutable(sharedHost, missing) is null);
        True(UpdaterHostResolver.IsProcessInsideDirectory(installed, updater));
        True(!UpdaterHostResolver.IsProcessInsideDirectory(installed, Path.Combine(area, "installed-other", "heirowUpdater.exe")));
        True(!UpdaterHostResolver.IsProcessInsideDirectory(installed, sharedHost));
        return Task.CompletedTask;
    }

    private string Area(string name) { var path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    private static PackageManifest Manifest(string id, string version) => new() { Id = id, Name = "Isolated integration fixture", Version = version, Publisher = "SocketJack test fixture", Scope = "user" };
    private static InstallOptions Options(string area, string target) => new() { TargetDirectory = target, StateRoot = Path.Combine(area, "state"), Isolated = true, RegisterProduct = false, Launch = false };
    private static string Build(string area, string name, PackageManifest manifest, Dictionary<string, string> files, string? signingKey = null)
    {
        var source = Path.Combine(area, name + "-source"); Directory.CreateDirectory(source);
        foreach (var file in files) { var path = PackageSafety.UnderRoot(source, file.Key); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, file.Value); }
        if (manifest.Payload.Count == 0) manifest.Payload = files.Keys.Select(x => new PayloadFile { Source = x, Path = x }).ToList();
        else foreach (var file in manifest.Payload) if (file.Source.Length == 0) file.Source = file.Path;
        var manifestPath = Path.Combine(area, name + ".json"); File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options));
        var output = Path.Combine(area, name + ".hinst"); PackageBuilder.Build(manifestPath, source, output, new() { SigningKeyPath = signingKey }); return output;
    }
    private static void True(bool condition, string? message = null) { if (!condition) throw new Exception("Assertion failed." + (message is null ? "" : " " + message)); Interlocked.Increment(ref assertionCount); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); Interlocked.Increment(ref assertionCount); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { Interlocked.Increment(ref assertionCount); return; } throw new Exception("Expected exception " + typeof(T).Name); }
    private static string FindUpdaterCli()
    {
        var directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "src", "HeirowUpdater", "HeirowUpdater.csproj")))
            directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar)) ?? throw new DirectoryNotFoundException("Cannot find updater project root.");
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        return Path.Combine(directory, "src", "HeirowUpdater", "bin", configuration, "net10.0-windows7.0", OperatingSystem.IsWindows() ? "heirowUpdater.exe" : "heirowUpdater");
    }
    private static async Task<string> RunCli(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception("Updater CLI failed: " + await error);
        return await output;
    }
    private sealed class InlineProgress(Action<InstallProgress> report) : IProgress<InstallProgress> { public void Report(InstallProgress value) => report(value); }
    private sealed class RedirectTransport : IUpdateTransport
    {
        public int Requests { get; private set; }
        public Task<HttpResponseMessage> GetAsync(Uri uri, long maximumResponseBytes, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
        {
            Requests++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("http://insecure.example.invalid/feed");
            return Task.FromResult(response);
        }
    }

    private sealed class MarkerModule(string area) : IInstallerModule
    {
        public string Id => "marker";
        public IReadOnlyList<string> Plan(JsonElement configuration, InstallerModuleContext context) => ["Create test marker"];
        public Task<ModuleSnapshot> InstallAsync(JsonElement configuration, InstallerModuleContext context, CancellationToken cancellationToken)
        {
            var source = Path.Combine(area, "marker-source.txt"); File.WriteAllText(source, "module-owned marker");
            context.ReplaceFile("marker.txt", source);
            if (configuration.GetProperty("fail").GetBoolean()) throw new InvalidOperationException("Intentional module failure.");
            return Task.FromResult(new ModuleSnapshot { Key = "marker.txt" });
        }
        public Task UninstallAsync(ModuleSnapshot originalState, InstallerModuleContext context, CancellationToken cancellationToken)
        { context.DeleteFile(originalState.Key); return Task.CompletedTask; }
    }

    private sealed record NetworkRequest(string Path, Dictionary<string, string> Headers);
    private sealed record NetworkResponse(int Status, byte[] Body, Dictionary<string, string>? Headers = null);
    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task run;
        public string Origin { get; }
        public LoopbackServer(Func<string, byte[]> response) : this(request => new NetworkResponse(200, response(request.Path))) { }
        public LoopbackServer(Func<NetworkRequest, NetworkResponse> response)
        {
            listener.Start(); Origin = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
            run = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        using var client = await listener.AcceptTcpClientAsync(stop.Token);
                        using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, false, leaveOpen: true);
                        var request = await reader.ReadLineAsync(stop.Token) ?? "";
                        var requestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        string? line;
                        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(stop.Token)))
                        {
                            var separator = line.IndexOf(':');
                            if (separator > 0) requestHeaders[line[..separator]] = line[(separator + 1)..].Trim();
                        }
                        var path = request.Split(' ').ElementAtOrDefault(1) ?? "/";
                        var result = response(new(path, requestHeaders));
                        var extra = result.Headers is null ? "" : string.Join("", result.Headers.Select(x => x.Key + ": " + x.Value + "\r\n"));
                        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {result.Status} Fixture\r\nContent-Length: {result.Body.Length}\r\n{extra}Connection: close\r\n\r\n");
                        await stream.WriteAsync(headers, stop.Token); await stream.WriteAsync(result.Body, stop.Token);
                    }
                }
                catch (OperationCanceledException) { } catch (SocketException) when (stop.IsCancellationRequested) { }
            });
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await run; stop.Dispose(); }
    }
}
