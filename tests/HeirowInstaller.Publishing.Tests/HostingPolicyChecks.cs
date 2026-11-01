using HeirowInstaller.Core;
using HeirowInstaller.Publishing;

internal static class HostingPolicyChecks
{
    internal static void Run(Action<bool, string> check, string root)
    {
        void Reject(Action action, string message)
        {
            try { action(); } catch (InvalidDataException) { check(true, message); return; }
            throw new Exception(message);
        }
        var legacy = new PackageManifest();
        check(legacy.TargetPlatform == "windows", "Legacy manifests retain Windows hosting policy");
        Reject(() => SocketJackHostingPolicy.VerifyFullInstaller("fixture.hinst", legacy), "RSA-only Windows package cannot substitute for Windows publisher trust");
        foreach (var platform in new[] { "linux", "macos", "freebsd", "android", "ios", "other" })
        {
            var manifest = new PackageManifest { TargetPlatform = platform, EntryPoint = "app" };
            check(SocketJackHostingPolicy.VerifyFullInstaller("fixture.hinst", manifest) is null, platform + " hosting requires no Windows signature or Windows verifier");
            Reject(() => SocketJackHostingPolicy.VerifyFullInstaller("fixture.exe", manifest), "Setup.exe cannot claim a " + platform + " exemption");
        }
        Reject(() => PackagePlatform.Validate(new() { TargetPlatform = "linux", EntryPoint = "app.exe" }), "Windows executable cannot masquerade as a non-Windows app");
        Reject(() => PackagePlatform.Normalize(null), "Explicit null platform fails closed");
        Reject(() => PackagePlatform.Normalize("unknown"), "Unknown platform fails closed");
        var unsigned = Path.Combine(root, "unsigned-setup.exe"); File.WriteAllBytes(unsigned, [0x4d, 0x5a, 0, 0]);
        if (OperatingSystem.IsWindows())
        {
            Reject(() => SocketJackHostingPolicy.VerifyFullInstaller(unsigned, legacy), "Unsigned Windows installer is rejected by native trust verification");
            var signedTool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (File.Exists(signedTool))
            {
                var identity = WindowsPublisherTrust.Verify(signedTool);
                check(identity.Subject.Contains("Microsoft", StringComparison.OrdinalIgnoreCase), "Native verifier accepts a real Microsoft Authenticode signature without signing credentials");
                var tampered = Path.Combine(root, "tampered-setup.exe"); File.Copy(signedTool, tampered);
                using (var file = new FileStream(tampered, FileMode.Open, FileAccess.ReadWrite)) { file.Position = 256; var value = file.ReadByte(); file.Position--; file.WriteByte((byte)(value ^ 1)); }
                Reject(() => WindowsPublisherTrust.Verify(tampered), "Changing signed executable bytes invalidates publisher trust");
            }
        }
    }
}
