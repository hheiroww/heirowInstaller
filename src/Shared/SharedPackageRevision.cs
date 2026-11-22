using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HeirowInstaller.BootstrapContract;

// A revision is a performance hint for the administrator-owned shared store.
// Callers must validate the receipt and package ACLs before reusing one.
internal sealed class SharedPackageRevision
{
    internal readonly record struct Package(string Id, string Version, string Runtime, string Sha256, long Size);

    private readonly string root;
    private readonly string binding;
    private readonly string[] packageRoots;
    private readonly string receipt;

    internal SharedPackageRevision(string root, string verifier, string publisher, IEnumerable<Package> packages)
    {
        this.root = root;
        publisher = publisher.ToLowerInvariant();
        var ordered = packages.OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        binding = Digest(writer =>
        {
            writer.Write("socketjack-shared-revision-1");
            writer.Write(verifier);
            writer.Write(publisher);
            foreach (var package in ordered)
            {
                writer.Write(package.Id);
                writer.Write(package.Version);
                writer.Write(package.Runtime);
                writer.Write(package.Sha256.ToLowerInvariant());
                writer.Write(package.Size);
            }
        });
        packageRoots = ordered.Select(package => Path.Combine(root, "packages", publisher[..16], package.Sha256.ToLowerInvariant())).ToArray();
        receipt = Path.Combine(root, "verified-revisions", binding + ".sha256");
    }

    internal bool Matches(Action<string> validateReceipt, Action<string> validatePackage, CancellationToken token)
    {
        try
        {
            if (!File.Exists(receipt)) return false;
            validateReceipt(receipt);
            using var stream = new FileStream(receipt, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length != 64) return false;
            Span<byte> data = stackalloc byte[64];
            stream.ReadExactly(data);
            return Encoding.ASCII.GetString(data) == Snapshot(validatePackage, token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { return false; }
    }

    internal void Record(Action<string> validateReceiptDirectory, Action<string> validateReceipt,
        Action<string> validatePackage, CancellationToken token)
    {
        string? temporary = null;
        try
        {
            var snapshot = Snapshot(validatePackage, token);
            var directory = Path.GetDirectoryName(receipt)!;
            validateReceiptDirectory(directory);
            Directory.CreateDirectory(directory);
            validateReceiptDirectory(directory);
            if (File.Exists(receipt)) validateReceipt(receipt);
            temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.ASCII.GetBytes(snapshot));
                stream.Flush(flushToDisk: true);
            }
            validateReceipt(temporary);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, receipt, overwrite: true);
            temporary = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A failed cache write does not undo a completed signature and payload check.
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private string Snapshot(Action<string> validatePackage, CancellationToken token) => Digest(writer =>
    {
        writer.Write(binding);
        foreach (var package in packageRoots)
        {
            token.ThrowIfCancellationRequested();
            validatePackage(package);
            WriteMetadata(writer, new FileInfo(Path.Combine(package, "complete.json")));
            WriteMetadata(writer, new FileInfo(Path.Combine(package, "package.hinst")));
            var files = new DirectoryInfo(Path.Combine(package, "files"));
            if (!files.Exists) throw new DirectoryNotFoundException(files.FullName);
            WriteTree(writer, files, token);
        }
    });

    private void WriteTree(BinaryWriter writer, DirectoryInfo directory, CancellationToken token)
    {
        WriteMetadata(writer, directory);
        foreach (var child in directory.EnumerateFileSystemInfos().OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (child is DirectoryInfo subdirectory) WriteTree(writer, subdirectory, token);
            else WriteMetadata(writer, child);
        }
    }

    private void WriteMetadata(BinaryWriter writer, FileSystemInfo info)
    {
        if (!info.Exists) throw new FileNotFoundException("A shared package revision file is missing.", info.FullName);
        const uint readAttributes = 0x80;
        const uint openExisting = 3;
        const uint directoryHandle = 0x02000000;
        const uint doNotFollowLink = 0x00200000;
        using var handle = CreateFile(info.FullName, readAttributes, FileShare.Read | FileShare.Delete, 0,
            openExisting, directoryHandle | doNotFollowLink, 0);
        if (handle.IsInvalid ||
            !ReadBasicInfo(handle, 0, out var metadata, Marshal.SizeOf<FileBasicInfo>()) || metadata.ChangeTime <= 0 ||
            !ReadFileId(handle, 18, out var id, Marshal.SizeOf<FileIdInfo>()))
            throw new IOException("A shared package file does not expose stable Windows change metadata: " + info.FullName);
        var attributes = (FileAttributes)metadata.Attributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("A shared package revision cannot follow links.");
        if (((attributes & FileAttributes.Directory) != 0) != (info is DirectoryInfo))
            throw new IOException("A shared package path changed type: " + info.FullName);
        writer.Write(Path.GetRelativePath(root, info.FullName));
        writer.Write((int)attributes);
        writer.Write(metadata.CreationTime);
        writer.Write(metadata.LastWriteTime);
        writer.Write(metadata.ChangeTime);
        writer.Write(id.Volume);
        writer.Write(id.Low);
        writer.Write(id.High);
        if (info is FileInfo file) writer.Write(file.Length);
    }

    private static string Digest(Action<BinaryWriter> append)
    {
        using var algorithm = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, algorithm, CryptoStreamMode.Write);
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true)) append(writer);
        stream.FlushFinalBlock();
        return Convert.ToHexString(algorithm.Hash!);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong Volume;
        public ulong Low;
        public ulong High;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadBasicInfo(SafeFileHandle file, int informationClass, out FileBasicInfo info, int size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFileId(SafeFileHandle file, int informationClass, out FileIdInfo info, int size);
}
