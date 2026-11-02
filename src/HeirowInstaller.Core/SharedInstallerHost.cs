using System.Security.Cryptography;

namespace HeirowInstaller.Core;

/// <summary>Stores immutable GUI maintenance hosts per scope, preserving complete signed installers and pooling unsigned prefixes.</summary>
public static class SharedInstallerHost
{
    /// <returns>The shared GUI host, or null only for explicitly isolated framework-dependent development fixtures.</returns>
    public static string? Provision(string currentTrustedInstallerPath, string scope, string stateRoot, bool isolated)
    {
        var source = Path.GetFullPath(currentTrustedInstallerPath); PackageSafety.EnsureNoReparsePoints(source);
        try { StandaloneRuntimeInspector.RequireEmbeddedRuntime(source); }
        catch (InvalidDataException) when (isolated) { return null; }
        var launcherLength = BundleBuilder.LauncherLength(source);
        if (launcherLength is < 64 or > 512L * 1024 * 1024) throw new InvalidDataException("The trusted maintenance host has invalid executable bounds.");
        // Authenticode covers the entire Setup, including its payload overlay. A truncated
        // prefix cannot retain that signature, so signed sources must be cached unchanged.
        // The runtime-size bound applies to the prefix; an application's payload can be larger.
        var length = BundleBuilder.HasAuthenticodeTable(source) ? new FileInfo(source).Length : launcherLength;
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (length > input.Length) throw new InvalidDataException("The maintenance host prefix exceeds its source.");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[131072]; long remaining = length;
        while (remaining > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new EndOfStreamException("The trusted maintenance host changed during provisioning.");
            digest.AppendData(buffer, 0, read); remaining -= read;
        }
        var hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        using var protection = Guard(scope, stateRoot, isolated, create: true);
        var destination = PackageSafety.UnderRoot(Root(scope, stateRoot, isolated), hash + "/heirowInstaller" + InstallerPlatform.ExecutableSuffix);
        if (File.Exists(destination)) return Validate(destination, scope, stateRoot, isolated);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".incoming-" + Guid.NewGuid().ToString("N");
        try
        {
            input.Position = 0;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                remaining = length;
                while (remaining > 0)
                {
                    var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0) throw new EndOfStreamException("The trusted maintenance host changed during provisioning.");
                    output.Write(buffer, 0, read); remaining -= read;
                }
                output.Flush(flushToDisk: true);
            }
            if (!PackageSafety.HashEquals(PackageSafety.HashFile(temporary), hash)) throw new CryptographicException("The maintenance executable failed integrity verification.");
            StandaloneRuntimeInspector.RequireEmbeddedRuntime(temporary);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(source));
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination)) { }
            return Validate(destination, scope, stateRoot, isolated);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string Root(string scope, string stateRoot, bool isolated)
    {
        if (scope is not ("user" or "machine")) throw new InvalidDataException("Invalid maintenance host scope.");
        var parent = isolated ? Path.GetDirectoryName(Path.GetFullPath(stateRoot))! : InstallerEngine.DefaultStateRoot(scope);
        return PackageSafety.UnderRoot(parent, ".installer-hosts");
    }

    /// <summary>Read-only validation for receipt-bound shared maintenance paths.</summary>
    public static string Validate(string path, string scope, string stateRoot, bool isolated)
    {
        path = Path.GetFullPath(path); PackageSafety.EnsureNoReparsePoints(path);
        var parent = Path.GetDirectoryName(path); var hash = Path.GetFileName(parent) ?? "";
        if (parent is null || Path.GetDirectoryName(parent)?.Equals(Root(scope, stateRoot, isolated), StringComparison.OrdinalIgnoreCase) != true ||
            Path.GetFileName(path) != "heirowInstaller" + InstallerPlatform.ExecutableSuffix || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("The maintenance host is outside its scope's immutable pool.");
        using var protection = Guard(scope, stateRoot, isolated, create: false);
        if (!File.Exists(path) || BundleBuilder.LauncherLength(path) is < 64 or > 512L * 1024 * 1024 || !PackageSafety.HashEquals(PackageSafety.HashFile(path), hash))
            throw new CryptographicException("The shared maintenance host failed its content binding.");
        StandaloneRuntimeInspector.RequireEmbeddedRuntime(path);
        return path;
    }

    private static IDisposable? Guard(string scope, string stateRoot, bool isolated, bool create)
    {
        var root = Root(scope, stateRoot, isolated); PackageSafety.EnsureNoReparsePoints(root);
        if (!OperatingSystem.IsWindows() || isolated || scope != "machine") { if (!OperatingSystem.IsWindows() && scope == "machine" && !isolated) UnixStateSecurity.Validate(root); if (create) Directory.CreateDirectory(root); return null; }
        var lease = MachineStateSecurity.Acquire(InstallerEngine.DefaultStateRoot("machine"), create);
        try
        {
            if (!lease.RootExists) throw new InvalidDataException("The protected maintenance namespace is missing.");
            if (create) Directory.CreateDirectory(root);
            if (Directory.Exists(root) && !MachineStateSecurity.IsStateObjectProtected(root)) throw new UnauthorizedAccessException("The maintenance host namespace has unsafe ownership or permissions.");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
}
