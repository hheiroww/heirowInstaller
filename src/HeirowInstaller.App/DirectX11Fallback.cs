using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;
using HeirowInstaller.Core;

namespace HeirowInstaller.App;

internal sealed record DirectX11HostIdentity(long Bytes, string Sha256);

// Different MonoGame implementations have the same assembly identity. The DX11
// renderer therefore runs in its own process, never in the failed DX12 context.
internal static class DirectX11Fallback
{
    internal const int GraphicsStartupFailed = 7341;
    internal const string ResourceName = "HeirowInstaller.Graphics.DirectX11Host.exe";
    private static ChildContext? current;
    internal static bool HasChildContext => current is not null;
    internal static int? CurrentPrimaryProcessId => current?.ParentId;
    internal static string CurrentPrimaryHostPath => current?.PrimaryPath ?? Environment.ProcessPath
        ?? throw new InvalidOperationException("The installer host image is unavailable.");
    internal static string? CurrentPrimaryHostSha256 => current?.PrimaryHash;

    internal static DirectX11HostIdentity? EmbeddedIdentity
    {
        get
        {
            var metadata = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            if (!metadata.TryGetValue("InstallerDx11FallbackSha256", out var hash))
            {
                if (metadata.TryGetValue("InstallerDx11FallbackRequired", out var required) && required == "true")
                    throw new InvalidDataException("The required DirectX 11 host declaration is missing.");
                return null; // An explicitly unbundled development build may use CPU last.
            }
            if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit) ||
                !metadata.TryGetValue("InstallerDx11FallbackBytes", out var length) || !long.TryParse(length, out var bytes) || bytes is <= 0 or > 94371840)
                throw new InvalidDataException("The embedded DirectX 11 host declaration is invalid.");
            return new(bytes, hash.ToLowerInvariant());
        }
    }

    internal static bool MatchesIdentity(DirectX11HostIdentity expected, long actualBytes, string actualHash) =>
        expected.Bytes == actualBytes && expected.Sha256.Length == 64 && actualHash.Length == 64 && expected.Sha256.All(Uri.IsHexDigit) && actualHash.All(Uri.IsHexDigit) && PackageSafety.HashEquals(expected.Sha256, actualHash);
    internal static bool MatchesEmbeddedCompanion(long actualBytes, string actualHash) =>
        EmbeddedIdentity is { } expected && MatchesIdentity(expected, actualBytes, actualHash);

    internal static int? Run(AppArguments options, string[] originalArguments)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DirectX 11 fallback requires Windows.");
        if (options.DirectX11Child) throw new InvalidOperationException("A DirectX 11 child cannot start another renderer.");
        var identity = EmbeddedIdentity;
        if (identity is null) return null;
        using var host = OpenVerifiedHost(identity, options);
        var primary = Path.GetFullPath(CurrentPrimaryHostPath);
        PackageSafety.EnsureNoReparsePoints(primary);
        using var primaryLease = File.Open(primary, FileMode.Open, FileAccess.Read, FileShare.Read);
        string primaryHash = Hash(primaryLease);
        FileStream? carrierLease = null;
        try
        {
            string? carrierHash = null;
            if (options.PackagePath is { } package)
            {
                package = Path.GetFullPath(package);
                PackageSafety.EnsureNoReparsePoints(package);
                if (SamePath(package, primary)) carrierHash = primaryHash;
                else { carrierLease = File.Open(package, FileMode.Open, FileAccess.Read, FileShare.Read); carrierHash = Hash(carrierLease); }
                if (options.ExpectedSha256 is { } expected && !PackageSafety.HashEquals(carrierHash, expected))
                    throw new CryptographicException("The original package changed before the renderer handoff.");
            }
            using var process = Process.GetCurrentProcess();
            var args = BuildChildArguments(originalArguments, options.PackagePath, carrierHash, primary, primaryHash, process.Id, process.StartTime.ToUniversalTime().Ticks);
            var start = BuildStartInfo(host.Name, args);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the verified DirectX 11 host.");
            child.WaitForExit();
            return child.ExitCode; // Cancellation, operation errors and successful completion propagate unchanged.
        }
        finally { carrierLease?.Dispose(); }
    }

    internal static ProcessStartInfo BuildStartInfo(string host, string[] arguments)
    {
        // The original argv may contain relative target, state or log paths.
        // Reparse it from the caller's directory, exactly as the primary did.
        var start = new ProcessStartInfo(host) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(Path.GetDirectoryName(host)!, "bundle");
        return start;
    }

    internal static string[] BuildChildArguments(string[] original, string? package, string? packageHash, string primary, string primaryHash, int parentId, long parentTicks)
    {
        var result = new List<string>(original);
        result.AddRange(["--graphics-directx11-child", "--graphics-primary-host", primary, "--graphics-primary-sha256", primaryHash,
            "--graphics-parent-pid", parentId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--graphics-parent-start", parentTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (package is not null)
        {
            // --install would change consent by setting Install=true. This flag only
            // preserves the original carrier location; the original argv is retained.
            result.AddRange(["--graphics-package", package, "--graphics-package-sha256", packageHash ?? throw new InvalidDataException("Original package digest missing.")]);
        }
        return result.ToArray();
    }

    internal static IDisposable? EnterChildContext(AppArguments options)
    {
        if (!options.DirectX11Child)
        {
            if (options.GraphicsPrimaryHost is not null || options.GraphicsPrimarySha256 is not null || options.GraphicsParentPid is not null || options.GraphicsParentStart is not null || options.GraphicsPackageSha256 is not null)
                throw new InvalidDataException("Graphics handoff fields require the DirectX 11 child route.");
            return null;
        }
        if (!OperatingSystem.IsWindows() || ProjectZ.Shared.Drawing.GraphicsBackendInfo.Current != ProjectZ.Shared.Drawing.ProjectZGraphicsBackend.DirectX11)
            throw new InvalidDataException("The selected host is not the actual DirectX 11 backend.");
        if (current is not null) throw new InvalidOperationException("A graphics handoff context already exists.");
        var context = new ChildContext(options);
        current = context;
        return context;
    }

    private sealed class ChildContext : IDisposable
    {
        internal readonly string PrimaryPath, PrimaryHash;
        internal readonly int ParentId;
        readonly List<FileStream> leases = [];
        [SupportedOSPlatform("windows")]
        internal ChildContext(AppArguments options)
        {
            try
            {
                if (options.GraphicsPrimaryHost is not { } primary || !Path.IsPathFullyQualified(primary) ||
                    options.GraphicsPrimarySha256 is not { Length: 64 } expected || !expected.All(Uri.IsHexDigit) ||
                    options.GraphicsParentPid is not > 0 || options.GraphicsParentStart is not > 0)
                    throw new InvalidDataException("The original installer identity is missing.");
                PrimaryPath = Path.GetFullPath(primary); ParentId = options.GraphicsParentPid.Value;
                PackageSafety.EnsureNoReparsePoints(PrimaryPath);
                var file = File.Open(PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.Read); leases.Add(file); PrimaryHash = Hash(file);
                if (!PackageSafety.HashEquals(PrimaryHash, expected)) throw new CryptographicException("The original installer image changed during handoff.");
                using var parent = Process.GetProcessById(ParentId);
                if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != options.GraphicsParentStart || !SamePath(parent.MainModule?.FileName ?? "", PrimaryPath))
                    throw new InvalidDataException("The original installer process changed during handoff.");
                if (options.PackagePath is { } package)
                {
                    PackageSafety.EnsureNoReparsePoints(package);
                    if (options.GraphicsPackageSha256 is not { Length: 64 } packageHash || !packageHash.All(Uri.IsHexDigit)) throw new InvalidDataException("The original package handoff digest is missing.");
                    string actual;
                    if (SamePath(package, PrimaryPath)) actual = PrimaryHash;
                    else { var input = File.Open(package, FileMode.Open, FileAccess.Read, FileShare.Read); leases.Add(input); actual = Hash(input); }
                    if (!PackageSafety.HashEquals(actual, packageHash)) throw new CryptographicException("The original package changed during handoff.");
                }
                else if (options.GraphicsPackageSha256 is not null) throw new InvalidDataException("An original package digest has no carrier.");
            }
            catch { foreach (var input in leases) input.Dispose(); throw; }
        }
        public void Dispose() { if (ReferenceEquals(current, this)) current = null; foreach (var file in leases) file.Dispose(); }
    }

    [SupportedOSPlatform("windows")]
    private static FileStream OpenVerifiedHost(DirectX11HostIdentity identity, AppArguments options)
    {
        if (options.GraphicsHandoffSmokeRoot is not null && !options.Isolated) throw new InvalidDataException("Graphics handoff diagnostics require isolated state.");
        string root = options.Isolated && options.StateRoot is { } state
            ? Path.Combine(Path.GetFullPath(state), "graphics-hosts")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocketJack", "heirowInstaller", "GraphicsHosts");
        ProtectDirectory(root);
        string folder = Path.Combine(root, identity.Sha256); ProtectDirectory(folder);
        using var gate = new Mutex(false, @"Local\heirowInstaller.Graphics." + identity.Sha256);
        bool acquired;
        try { acquired = gate.WaitOne(TimeSpan.FromSeconds(30)); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new TimeoutException("Another process is preparing the DirectX 11 host.");
        try
        {
            string path = Path.Combine(folder, "heirowInstaller.Dx11.exe");
            PackageSafety.EnsureNoReparsePoints(path);
            if (!File.Exists(path))
            {
                string temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName) ?? throw new InvalidDataException("The declared DirectX 11 resource is missing."))
                    using (var output = File.Open(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    {
                        var buffer = new byte[131072]; long total = 0; int count;
                        while ((count = resource.Read(buffer)) != 0) { total += count; if (total > identity.Bytes) throw new InvalidDataException("The embedded DirectX 11 host exceeds its declaration."); output.Write(buffer, 0, count); }
                        output.Flush(true); output.Position = 0;
                        if (!MatchesIdentity(identity, total, Hash(output))) throw new CryptographicException("The embedded DirectX 11 host changed.");
                    }
                    File.Move(temporary, path, overwrite: false);
                }
                finally { if (File.Exists(temporary)) { PackageSafety.EnsureNoReparsePoints(temporary); File.Delete(temporary); } }
            }
            PackageSafety.EnsureNoReparsePoints(path);
            var lease = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!MatchesIdentity(identity, lease.Length, Hash(lease))) { lease.Dispose(); throw new CryptographicException("The cached DirectX 11 host changed."); }
            ProtectDirectory(Path.Combine(folder, "bundle"));
            return lease; // Held through child exit; no replacement or deletion can race launch.
        }
        finally { gate.ReleaseMutex(); }
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectDirectory(string path)
    {
        PackageSafety.EnsureNoReparsePoints(path);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The current user SID is unavailable.");
        if (!Directory.Exists(path))
        {
            var security = new DirectorySecurity(); security.SetOwner(user); security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                security.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            FileSystemAclExtensions.Create(new DirectoryInfo(path), security);
        }
        var actual = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path));
        if (actual.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner.Value != user.Value || !actual.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("The graphics cache is not protected for this user.");
        foreach (FileSystemAccessRule rule in actual.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference is SecurityIdentifier sid && sid.Value != user.Value && sid.Value is not ("S-1-5-18" or "S-1-5-32-544") &&
                ((int)rule.FileSystemRights & ((int)(FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership) | 0x50000000)) != 0)
                throw new UnauthorizedAccessException("Another identity can change the graphics cache.");
    }

    private static string Hash(Stream input) { input.Position = 0; string hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(); input.Position = 0; return hash; }
    private static bool SamePath(string first, string second) => Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
}
