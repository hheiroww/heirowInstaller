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

var suite = new AcceptanceSuite();
return await suite.RunAsync();

internal sealed class AcceptanceSuite
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "heirowInstaller-tests-" + Guid.NewGuid().ToString("N"));
    private int passed;
    public async Task<int> RunAsync()
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
            ,("MSI arguments preserve original destination and features unless explicitly overridden", MsiArguments)
            ,("injected update transports validate redirects before making another request", TransportRedirects)
            ,("SocketJack updater streams signed updates through validated redirects and keeps credentials on their origin", SocketJackUpdates)
            ,("an attached signed package never authorizes execution of an untrusted executable prefix", UntrustedBundlePrefix)
            ,("the updater exits and a copied trusted helper replaces its own application safely", TrustedSelfHandoff)
            ,("legacy executable defaults and explicit silent, target, and log overrides reach the actual child process", LegacyArguments)
            ,("machine state rejects writable or user-owned ACLs without changing them and preserves explicit isolation", MachineStateAcls)
            ,("user scope rejects selected machine integrations and spoofed machine receipt snapshots", IntegrationScope)
        };
        try
        {
            foreach (var test in cases)
            {
                await test.Test(); passed++;
                Console.WriteLine("PASS " + test.Name);
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

    private Task MsiArguments()
    {
        var plan = new InstallPlan { InstallDirectory = @"C:\IgnoredFallback", UseOriginalInstallDirectory = true };
        var args = MsiAdapter.BuildArguments("install", @"C:\Original.msi", null, new(), plan, new());
        True(!args.Any(x => x.StartsWith("INSTALLFOLDER=", StringComparison.Ordinal) || x.StartsWith("ADDLOCAL=", StringComparison.Ordinal)));
        plan.UseOriginalInstallDirectory = false; plan.Features = ["Core", "Tools"];
        args = MsiAdapter.BuildArguments("install", @"C:\Original.msi", null, new() { InstallDirectoryProperty = "APPLICATIONFOLDER" }, plan, new());
        True(args.Contains(@"APPLICATIONFOLDER=C:\IgnoredFallback")); True(args.Contains("ADDLOCAL=Core,Tools"));
        args = MsiAdapter.BuildArguments("install", @"C:\Original.msi", null, new(), plan, new() { Silent = false });
        True(!args.Contains("/qn") && !args.Contains("/passive"));
        args = MsiAdapter.BuildArguments("repair", @"C:\Durable\Original.msi", "{11111111-1111-1111-1111-111111111111}", new(), plan, new());
        True(args.Contains(@"C:\Durable\Original.msi"));
        return Task.CompletedTask;
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
            return request.Path switch
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
            return request.Path == "/feed" ? new(302, [], new() { ["Location"] = "/cross" }) : new(307, [], new() { ["Location"] = destination.Origin + "feed" });
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
    private static void True(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected exception " + typeof(T).Name); }
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
        return Path.Combine(directory, "src", "HeirowUpdater", "bin", configuration, "net10.0", OperatingSystem.IsWindows() ? "heirowUpdater.exe" : "heirowUpdater");
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
