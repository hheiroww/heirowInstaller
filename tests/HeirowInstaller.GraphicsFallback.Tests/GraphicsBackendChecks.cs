using HeirowInstaller.App;
using Microsoft.Xna.Framework.Graphics;

internal static class GraphicsBackendChecks
{
    internal static void Run(List<string> checks)
    {
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        static Exception Device() => new NoSuitableGraphicsDeviceException("Failed to create graphics device!");
        int hardware = 0, alternate = 0, cpu = 0;
        var exit = GraphicsStartup.Run(() => hardware++, () => { alternate++; return 0; }, () => cpu++, () => false, true);
        Check(hardware == 1 && alternate == 0 && cpu == 0 && exit is null, "Working DX12 never starts another presentation path");

        hardware = alternate = cpu = 0;
        exit = GraphicsStartup.Run(() => { hardware++; throw Device(); }, () => { alternate++; return 3010; }, () => cpu++, () => false, true);
        Check(hardware == 1 && alternate == 1 && cpu == 0 && exit == 3010, "DX11 result is propagated once after pre-content DX12 failure");
        foreach (var status in new[] { 0, 1, 2, 1602, 1223, -1 })
        {
            cpu = 0; alternate = 0;
            exit = GraphicsStartup.Run(() => throw Device(), () => { alternate++; return status; }, () => cpu++, () => false, true);
            Check(exit == status && alternate == 1 && cpu == 0, "DX11 completion or cancellation never replays an operation: " + status);
        }
        cpu = alternate = 0;
        exit = GraphicsStartup.Run(() => throw Device(), () => { alternate++; return DirectX11Fallback.GraphicsStartupFailed; }, () => cpu++, () => false, true);
        Check(alternate == 1 && cpu == 1 && exit is null, "Only declared DX11 pre-content startup failure reaches CPU last");
        cpu = alternate = hardware = 0;
        GraphicsStartup.Run(() => hardware++, () => { alternate++; return 0; }, () => cpu++, () => false, true, forceSoftware: true);
        Check(hardware == 0 && alternate == 0 && cpu == 1, "Explicit CPU skips both GPU backends");

        var failure = Device(); bool same = false; cpu = alternate = 0;
        try { GraphicsStartup.Run(() => throw failure, () => { alternate++; return 0; }, () => cpu++, () => true, true); }
        catch (Exception error) { same = ReferenceEquals(error, failure); }
        Check(same && alternate == 0 && cpu == 0, "Post-content graphics failure never replays setup");
        var signature = new System.Security.Cryptography.CryptographicException("fixture signature error"); same = false;
        try { GraphicsStartup.Run(() => throw signature, () => 0, () => cpu++, () => false, true); }
        catch (Exception error) { same = ReferenceEquals(error, signature); }
        Check(same, "A signature failure cannot select another renderer");
        var alternateFailure = new InvalidDataException("fixture cache changed"); same = false; cpu = 0;
        try { GraphicsStartup.Run(() => throw Device(), () => throw alternateFailure, () => cpu++, () => false, true); }
        catch (Exception error) { same = ReferenceEquals(error, alternateFailure); }
        Check(same && cpu == 0, "Companion authenticity failure fails closed rather than hiding behind CPU");

        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "graphics-handoff-fixture", "original-setup.exe"));
        string package = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "graphics-handoff-fixture", "original.hinst"));
        string state = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "graphics-handoff-fixture", "state"));
        string digest = new('a', 64);
        var original = new[] { "--isolated", "--state-root", state, "--scope", "machine", "--features", "core,updates", "--trust-key", digest, "--require-signature", "--expected-product-id", "fixture", "--expected-version", "1.0", "--expected-sha256", digest };
        var child = DirectX11Fallback.BuildChildArguments(original, package, digest, parent, digest, 14591, 12345678);
        Check(child.Take(original.Length).SequenceEqual(original), "Child argument prefix preserves the caller's exact choices and trust restrictions");
        Check(!child.Contains("--install"), "Neutral package carrier handoff does not enable automatic installation");
        var parsed = AppArguments.Parse(child);
        Check(parsed.DirectX11Child && parsed.PackagePath == package && !parsed.Install && !parsed.AcceptLicense && !parsed.Launch && !parsed.Silent, "Child carrier preserves review, consent, launch and silent semantics");
        Check(parsed.ExpectedProductId == "fixture" && parsed.ExpectedVersion == "1.0" && parsed.ExpectedSha256 == digest && parsed.TrustedPublisherKeySha256 == digest && parsed.RequireSignature, "Child retains exact package identity and publisher pin");
        Check(parsed.GraphicsPrimaryHost == parent && parsed.GraphicsPrimarySha256 == digest && parsed.GraphicsParentPid == 14591 && parsed.GraphicsParentStart == 12345678 && parsed.GraphicsPackageSha256 == digest, "Child binds original process, executable and package identity");
        Check(parsed.Features!.SequenceEqual(new[] { "core", "updates" }) && parsed.Scope == "machine" && parsed.Isolated, "Child preserves features, selected scope and isolation");
        var relative = new[] { "--isolated", "--state-root", Path.Combine("relative", "state"), "--target", Path.Combine("relative", "installed"), "--log", Path.Combine("relative", "setup.log") };
        var relativeChild = DirectX11Fallback.BuildChildArguments(relative, package, digest, parent, digest, 14591, 12345678);
        string host = Path.Combine(Path.GetDirectoryName(parent)!, "cache", "heirowInstaller.Dx11.exe");
        var start = DirectX11Fallback.BuildStartInfo(host, relativeChild);
        Check(start.FileName == host && start.WorkingDirectory == Environment.CurrentDirectory, "Actual child start configuration preserves the caller working directory");
        Check(!start.UseShellExecute && start.CreateNoWindow && start.ArgumentList.SequenceEqual(relativeChild), "Actual child start configuration forwards arguments without shell quoting or a console");
        Check(start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] == Path.Combine(Path.GetDirectoryName(parent)!, "bundle"), "Actual child start configuration keeps bundle extraction outside the version-keyed cache directory");
        var relativeParsed = AppArguments.Parse(start.ArgumentList.ToArray());
        Check(relativeParsed.StateRoot == Path.GetFullPath(relative[2], start.WorkingDirectory) && relativeParsed.TargetDirectory == Path.GetFullPath(relative[4], start.WorkingDirectory) && relativeParsed.LogPath == Path.GetFullPath(relative[6], start.WorkingDirectory), "Relative state, target and log arguments retain their original absolute meanings");
        Check(relativeParsed.PackagePath == package && !relativeParsed.Install && !relativeParsed.AcceptLicense && !relativeParsed.Launch && !relativeParsed.Silent, "Relative argument handoff remains a neutral carrier without changing consent");
        string fixtureTail = Path.Combine("graphics-handoff-acceptance", "fixture-v1", "graphics-context", "graphics-hosts");
        string volume = Path.GetPathRoot(parent)!;
        string longCacheRoot = Path.Combine(volume, new string('x', 125 - volume.Length - fixtureTail.Length - 1), fixtureTail);
        string longHost = Path.Combine(longCacheRoot, digest, "heirowInstaller.Dx11.exe");
        var longStart = DirectX11Fallback.BuildStartInfo(longHost, relativeChild);
        string fontTail = Path.Combine("heirowInstaller.Dx11", "OqJA6ni2Cxym", "ThirdParty", "ProjectZSans", "OFL.txt");
        string fontPath = Path.Combine(longStart.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"]!, fontTail);
        string previousFontPath = Path.Combine(Path.GetDirectoryName(longHost)!, "bundle", fontTail);
        Check(longCacheRoot.Length == 125 && previousFontPath.Length == 263 && fontPath.Length == 198 && fontPath.Length < 260, "Observed long fixture root keeps bundled font extraction below MAX_PATH without shortening the fixture");
        Check(longStart.FileName == longHost && Path.GetFileName(Path.GetDirectoryName(longStart.FileName)) == digest && previousFontPath.Length - fontPath.Length == 65, "Short bundle base preserves the exact version-keyed executable cache and removes only its redundant extraction prefix");
        var auto = AppArguments.Parse(DirectX11Fallback.BuildChildArguments(["--install", package, "--accept-license", "--launch"], package, digest, parent, digest, 14591, 12345678));
        Check(auto.Install && auto.AcceptLicense && auto.Launch, "Explicit original auto-install and consent flags remain explicit");
        var maintenance = DirectX11Fallback.BuildChildArguments(["repair", "fixture", "--maintenance-confirmed"], null, null, parent, digest, 14591, 12345678);
        var repair = AppArguments.Parse(maintenance.Skip(2).ToArray(), maintenanceInvocation: true);
        Check(maintenance[0] == "repair" && maintenance[1] == "fixture" && repair.MaintenanceConfirmed && !repair.Install, "Maintenance command identity is preserved without install replay");
        var identity = new DirectX11HostIdentity(1234, digest);
        Check(DirectX11Fallback.MatchesIdentity(identity, 1234, digest), "Exact embedded companion length and digest match");
        Check(!DirectX11Fallback.MatchesIdentity(identity, 1235, digest), "Changed companion length is rejected");
        Check(!DirectX11Fallback.MatchesIdentity(identity, 1234, new string('b', 64)), "Changed companion digest is rejected");
        Check(!DirectX11Fallback.MatchesIdentity(identity, 1234, new string('z', 64)), "Malformed companion digest is rejected");
        Check(GraphicsStartup.ShouldUseSoftwareFallback(new NoSuitableGraphicsDeviceException("Adapter 'Intel HD Graphics 630' does not support the HiDef profile."), false, true), "Known DX11 unsupported profile remains a pre-content graphics failure");
    }
}
