using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HeirowInstaller.Core;

/// <summary>Normal Windows sharing/deletion rules only. No POSIX disposition, privileges, attribute changes or ACL overrides.</summary>
internal sealed class LegacyStagingFileGuard : IDisposable
{
    private readonly SafeFileHandle handle;
    private readonly FileStream? stream;
    private readonly FileIdentity identity;
    private bool disposed;
    internal string Path { get; }
    internal long Size { get; }
    internal string? Sha256 { get; }
    internal bool IsDirectory { get; }
    internal string IdentityToken => Token(identity);
    internal static string CaptureIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new IOException("Cannot capture a newly created staging file identity.", new Win32Exception(Marshal.GetLastWin32Error()));
        return Token(information);
    }
    private static string Token(FileIdentity information) => $"{information.Volume:x8}:{information.IndexHigh:x8}{information.IndexLow:x8}:{information.CreationHigh:x8}{information.CreationLow:x8}";

    private LegacyStagingFileGuard(string path, bool directory, long? size, string? digest)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Guarded legacy staging cleanup requires Windows.");
        Path = System.IO.Path.GetFullPath(path); IsDirectory = directory;
        PackageSafety.EnsureNoReparsePoints(Path);
        // Directory handles prevent rename while permitting child enumeration; file handles deny all sharing.
        var nativePath = Path.StartsWith(@"\\?\", StringComparison.Ordinal) ? Path
            : Path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + Path[2..] : @"\\?\" + Path;
        handle = CreateFileW(nativePath, directory ? 0x00010080u : 0x80010000u, directory ? 3u : 0u, IntPtr.Zero, 3,
            0x00200000u | (directory ? 0x02000000u : 0u), IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("A declared legacy staging item is in use or cannot be held safely.", new Win32Exception(Marshal.GetLastWin32Error())); }
        try
        {
            identity = ReadIdentity();
            if (((identity.Attributes & 0x10) != 0) != directory || (identity.Attributes & (0x400u | 1u | 0x4000u)) != 0 || (!directory && identity.Links != 1))
                throw new IOException("Legacy cleanup preserves reparse, read-only, encrypted, hard-linked or unexpected items.");
            VerifyFinalPath();
            VerifyStreams();
            Size = ((long)identity.SizeHigh << 32) | identity.SizeLow;
            if (!directory)
            {
                stream = new FileStream(handle, FileAccess.Read, 131072, false);
                if (size is null || Size != size) throw new IOException("A legacy staging file size changed.");
                Sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); stream.Position = 0;
                if (digest is null || !PackageSafety.HashEquals(Sha256, digest)) throw new CryptographicException("A legacy staging file hash changed.");
            }
        }
        catch { stream?.Dispose(); handle.Dispose(); throw; }
    }

    internal static LegacyStagingFileGuard File(string path, long size, string digest) => new(path, false, size, digest);
    internal static LegacyStagingFileGuard Directory(string path) => new(path, true, null, null);
    internal void Recheck()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        VerifyFinalPath();
        VerifyStreams();
        var current = ReadIdentity();
        if (current.Volume != identity.Volume || current.IndexHigh != identity.IndexHigh || current.IndexLow != identity.IndexLow ||
            current.CreationHigh != identity.CreationHigh || current.CreationLow != identity.CreationLow ||
            ((current.Attributes & 0x10) != 0) != IsDirectory || (current.Attributes & (1u | 0x400u | 0x4000u)) != 0 ||
            (!IsDirectory && (current.Links != 1 || current.SizeHigh != identity.SizeHigh || current.SizeLow != identity.SizeLow ||
                current.WriteHigh != identity.WriteHigh || current.WriteLow != identity.WriteLow)))
            throw new IOException("A held legacy staging item changed; cleanup was stopped.");
    }
    private void VerifyFinalPath()
    {
        PackageSafety.EnsureNoReparsePoints(Path);
        var final = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, final, (uint)final.Capacity, 0);
        if (length == 0 || length >= final.Capacity) throw new IOException("Cannot verify a held legacy staging path.");
        var finalPath = final.ToString();
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) finalPath = @"\\" + finalPath[8..];
        else if (finalPath.StartsWith(@"\\?\", StringComparison.Ordinal)) finalPath = finalPath[4..];
        if (!System.IO.Path.GetFullPath(finalPath).Equals(Path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A held legacy staging path changed identity.");
    }
    private void VerifyStreams()
    {
        var buffer = new byte[4096];
        if (!GetFileInformationByHandleEx(handle, 7, buffer, (uint)buffer.Length))
        {
            var error = Marshal.GetLastWin32Error();
            if (IsDirectory && error == 38) return; // A directory with no data streams.
            throw new IOException("Cannot verify all legacy staging streams; data was preserved.", new Win32Exception(error));
        }
        var offset = 0; var found = false;
        for (var entry = 0; entry < 64; entry++)
        {
            if (offset > buffer.Length - 24) throw new IOException("Malformed legacy stream inventory.");
            var next = BitConverter.ToUInt32(buffer, offset); var length = BitConverter.ToUInt32(buffer, offset + 4);
            if (length == 0 || length % 2 != 0 || length > buffer.Length - offset - 24) throw new IOException("Malformed legacy stream name.");
            var name = Encoding.Unicode.GetString(buffer, offset + 24, (int)length);
            if (IsDirectory || found || name != "::$DATA") throw new IOException("Legacy cleanup preserves unknown named streams.");
            found = true;
            if (next == 0) return;
            if (next < 24 + length || next % 8 != 0 || next > buffer.Length - offset - 24) throw new IOException("Malformed legacy stream offsets.");
            offset += (int)next;
        }
        throw new IOException("Legacy stream inventory exceeds its bound.");
    }
    internal void CopyDurable(string destination)
    {
        Recheck();
        if (stream is null) throw new InvalidOperationException("A directory cannot be copied as a file.");
        PackageSafety.EnsureNoReparsePoints(destination);
        stream.Position = 0;
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.WriteThrough))
        { stream.CopyTo(output); output.Flush(true); }
        stream.Position = 0;
        if (new FileInfo(destination).Length != Size || !PackageSafety.HashEquals(PackageSafety.HashFile(destination), Sha256!))
            throw new IOException("A held legacy staging file changed during durable copy.");
    }
    internal void DeleteAndClose()
    {
        Recheck();
        var disposition = new Disposition { Delete = 1 };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<Disposition>()))
            throw new IOException("Windows refused normal legacy staging deletion; original data will be preserved.", new Win32Exception(Marshal.GetLastWin32Error()));
        Dispose();
    }
    private FileIdentity ReadIdentity()
    {
        if (!GetFileInformationByHandle(handle, out var result)) throw new IOException("Cannot verify a held legacy staging file.", new Win32Exception(Marshal.GetLastWin32Error()));
        return result;
    }
    public void Dispose() { if (disposed) return; disposed = true; stream?.Dispose(); handle.Dispose(); }

    [StructLayout(LayoutKind.Sequential)] private struct Disposition { public byte Delete; }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct FileIdentity(uint Attributes, uint CreationLow, uint CreationHigh,
        uint AccessLow, uint AccessHigh, uint WriteLow, uint WriteHigh, uint Volume, uint SizeHigh, uint SizeLow, uint Links, uint IndexHigh, uint IndexLow);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileIdentity information);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, [Out] byte[] information, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref Disposition information, uint size);
}
