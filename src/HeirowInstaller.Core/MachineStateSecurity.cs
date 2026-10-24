using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace HeirowInstaller.Core;

/// <summary>Fail-closed ownership and ACL checks for receipts, original backups, and source media consumed by elevated maintenance.</summary>
[SupportedOSPlatform("windows")]
public static class MachineStateSecurity
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private const FileSystemRights MutationRights = FileSystemRights.WriteData | FileSystemRights.AppendData |
        FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
    private const FileSystemRights NamespaceMutationRights = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>Evaluates a descriptor without changing it. A null DACL, user owner, or write grant to any other identity fails.</summary>
    public static bool HasTrustedOwnershipAndPermissions(FileSystemSecurity security, bool requireProtectedDacl = true)
    {
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (!IsPrivileged(raw.Owner) || raw.DiscretionaryAcl is null || (requireProtectedDacl && !security.AreAccessRulesProtected)) return false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !IsPrivileged((SecurityIdentifier)rule.IdentityReference) &&
                (((int)rule.FileSystemRights & ((int)MutationRights | 0x50000000)) != 0)) return false;
        return true;
    }

    /// <summary>Read-only validation. Missing state is not created; unsafe existing state is never repaired automatically.</summary>
    public static void ValidateExistingRoot(string path) { using var lease = Acquire(path, create: false); }

    /// <summary>Read-only descriptor check for one state object. This does not replace guarding its containing namespace.</summary>
    public static bool IsStateObjectProtected(string path)
    {
        using var handle = OpenMetadata(path, FileShare.Read | FileShare.Write | FileShare.Delete);
        return HasTrustedOwnershipAndPermissions(ReadDescriptor(handle, path), requireProtectedDacl: false);
    }

    internal static StateLease Acquire(string path, bool create)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Path.GetDirectoryName(path) is null) throw Failure(path, "a drive root cannot be installer state");
        var lease = new StateLease();
        try
        {
            var paths = new Stack<string>();
            var current = path;
            while (current is not null) { paths.Push(current); current = Path.GetDirectoryName(current); }
            var defaultRoot = Path.GetFullPath(InstallerEngine.DefaultStateRoot("machine"));
            var ownContainer = Path.GetDirectoryName(defaultRoot)!;
            while (paths.TryPop(out var node))
            {
                if (!Directory.Exists(node))
                {
                    if (File.Exists(node)) throw Failure(node, "a file blocks the state directory");
                    if (!create) return lease;
                    EnsureTrustedMutationToken();
                    if (!node.Equals(path, StringComparison.OrdinalIgnoreCase) &&
                        !(path.Equals(defaultRoot, StringComparison.OrdinalIgnoreCase) && node.Equals(ownContainer, StringComparison.OrdinalIgnoreCase)))
                        throw Failure(node, "the custom state parent must already exist with a secure administrator-owned ACL");
                    CreateProtectedDirectory(node);
                }
                var handle = OpenMetadata(node, node.Equals(path, StringComparison.OrdinalIgnoreCase) ? FileShare.Read : FileShare.Read | FileShare.Write);
                lease.Add(handle);
                var descriptor = ReadDescriptor(handle, node);
                if (node.Equals(path, StringComparison.OrdinalIgnoreCase))
                {
                    if (!HasTrustedOwnershipAndPermissions(descriptor)) throw Failure(node, "state must have an administrator or SYSTEM owner, a protected DACL, and no other write access");
                    lease.RootExists = true;
                }
                else ValidateNamespace(node, descriptor);
            }
            if (create) EnsureTrustedMutationToken();
            ValidateContents(path);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static void ValidateContents(string root)
    {
        foreach (var node in Directory.EnumerateFileSystemEntries(root))
        {
            // Never follow a child link, even while only inspecting metadata.
            if (!IsStateObjectProtected(node))
                throw Failure(node, "a cached state object has an untrusted owner or write ACL");
            if (Directory.Exists(node)) ValidateContents(node);
        }
    }

    private static void ValidateNamespace(string path, DirectorySecurity descriptor)
    {
        var raw = new RawSecurityDescriptor(descriptor.GetSecurityDescriptorBinaryForm(), 0);
        if ((!IsPrivileged(raw.Owner) && raw.Owner?.Value != TrustedInstaller) || raw.DiscretionaryAcl is null)
            throw Failure(path, "the state namespace has an untrusted owner or null DACL");
        var commonData = Path.TrimEndingDirectorySeparator(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData));
        var osAnchor = path.Equals(Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase) || path.Equals(commonData, StringComparison.OrdinalIgnoreCase);
        foreach (FileSystemAccessRule rule in descriptor.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (IsPrivileged(sid) || sid.Value == TrustedInstaller) continue;
            // Windows' shared anchors permit new child creation. Protected state children cannot be replaced there.
            var denied = osAnchor ? (int)NamespaceMutationRights | 0x10000000 : (int)MutationRights | 0x50000000;
            if (((int)rule.FileSystemRights & denied) != 0) throw Failure(path, "an untrusted identity can alter the state namespace");
        }
    }

    private static void EnsureTrustedMutationToken()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) && !Equals(identity.User, SystemAccount))
            throw new UnauthorizedAccessException("Machine installer state can only be created or changed by an elevated administrator or SYSTEM.");
        // New descendants inherit the protected ACL and the token's default owner. A user SID would retain implicit WRITE_DAC even with a restrictive DACL.
        if (!IsPrivileged(identity.Owner)) throw new UnauthorizedAccessException("The elevated process must use Administrators or SYSTEM as its default object owner for machine installer state.");
    }

    private static void CreateProtectedDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new(Administrators, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new(SystemAccount, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new(Users, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, inheritance, PropagationFlags.None, AccessControlType.Allow));
        var bytes = security.GetSecurityDescriptorBinaryForm();
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), SecurityDescriptor = pointer };
            if (!CreateDirectory(path, ref attributes))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 183) throw new Win32Exception(error, "Could not create protected machine installer state.");
                // A racing precreator is not trusted: Acquire immediately checks the actual object owner and ACL.
            }
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static SafeFileHandle OpenMetadata(string path, FileShare share)
    {
        var handle = CreateFile(path, 0x00020080, share, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "Could not guard machine installer state metadata."); }
        return handle;
    }

    private static DirectorySecurity ReadDescriptor(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (((FileAttributes)info.Attributes & FileAttributes.ReparsePoint) != 0) throw Failure(path, "reparse points are forbidden in machine installer state");
        var error = GetSecurityInfo(handle, 1, 0x00000005, out _, out _, out _, out _, out var descriptor);
        if (error != 0) throw new Win32Exception((int)error, "Could not read machine installer state ACL.");
        try
        {
            var length = checked((int)GetSecurityDescriptorLength(descriptor));
            var binary = new byte[length]; Marshal.Copy(descriptor, binary, 0, length);
            var security = new DirectorySecurity(); security.SetSecurityDescriptorBinaryForm(binary, AccessControlSections.Owner | AccessControlSections.Access);
            return security;
        }
        finally { LocalFree(descriptor); }
    }

    private static bool IsPrivileged(SecurityIdentifier? sid) => sid is not null && (sid.Equals(Administrators) || sid.Equals(SystemAccount));
    private static UnauthorizedAccessException Failure(string path, string reason) => new($"Unsafe machine installer state at {path}: {reason}. Existing state was not changed. Use an administrator-managed state directory.");
    internal sealed class StateLease : IDisposable
    {
        private readonly List<SafeFileHandle> handles = [];
        public bool RootExists { get; internal set; }
        public void Add(SafeFileHandle handle) => handles.Add(handle);
        public void Dispose() { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); handles.Clear(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr SecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectory(string path, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr attributes, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafeFileHandle handle, uint objectType, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}
