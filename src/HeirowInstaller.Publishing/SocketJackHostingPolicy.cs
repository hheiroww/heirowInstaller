using HeirowInstaller.Core;

namespace HeirowInstaller.Publishing;

/// <summary>Hosting permission is separate from Windows publisher identity.</summary>
public static class SocketJackHostingPolicy
{
    internal static bool IsComponentPreparation(PackageManifest manifest) =>
        manifest.Modules.TryGetValue("SocketJackSecurityLoader", out var module) && module.ValueKind == System.Text.Json.JsonValueKind.Object &&
        module.TryGetProperty("hostedSigning", out var hosted) && hosted.ValueKind == System.Text.Json.JsonValueKind.True &&
        module.TryGetProperty("preparationOnly", out var preparation) && preparation.ValueKind == System.Text.Json.JsonValueKind.True;

    public static WindowsPublisherIdentity? VerifyFullInstaller(string path, PackageManifest manifest)
    {
        PackagePlatform.Validate(manifest);
        var executable = Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);
        if (executable && manifest.TargetPlatform != "windows")
            throw new InvalidDataException("A Windows Setup.exe cannot claim a non-Windows target platform.");
        if (!executable && manifest.TargetPlatform != "windows") return null;
        if (!executable)
            throw new InvalidDataException("Host Windows apps as a trusted, Authenticode-signed Setup.exe. A .hinst package signature does not verify a Windows publisher. Non-Windows packages are exempt.");
        return WindowsPublisherTrust.Verify(path);
    }
}
