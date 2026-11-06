using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HeirowInstaller.Bootstrap;

[SupportedOSPlatform("windows")]
internal static class BootstrapMachineStore
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private const FileSystemRights Mutation = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    internal static void Prepare(string root)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) || !Equals(identity.Owner, Administrators) && !Equals(identity.Owner, SystemAccount))
            throw new UnauthorizedAccessException("A protected administrator token is required to provision the shared host.");
        BootstrapEngine.NoLinks(root);
        foreach (var directory in new[] { Path.GetDirectoryName(root)!, root })
        {
            if (!Directory.Exists(directory))
            {
                var security = new DirectorySecurity(); security.SetOwner(Administrators); security.SetAccessRuleProtection(true, false);
                var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                security.AddAccessRule(new(Administrators, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
                security.AddAccessRule(new(SystemAccount, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
                security.AddAccessRule(new(Users, FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(directory).Create(security);
            }
            Validate(directory, directory: true, protectedDacl: true);
            // An older deployment may have created the shared SocketJack parent
            // with an administrator-only ACL. Normal launchers must be able to
            // inspect this namespace, but private children keep their own ACLs.
            if (directory.Equals(Path.GetDirectoryName(root), StringComparison.OrdinalIgnoreCase))
            {
                var info = new DirectoryInfo(directory);
                var security = info.GetAccessControl();
                GrantParentReadAccess(security);
                info.SetAccessControl(security);
                Validate(directory, directory: true, protectedDacl: true);
            }
        }
    }
    internal static void GrantParentReadAccess(DirectorySecurity security) =>
        security.AddAccessRule(new(Users, FileSystemRights.ReadAndExecute,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
    internal static void ValidateTree(string root, bool requireProtectedDacl = true)
    {
        Validate(root, true, requireProtectedDacl);
        ValidateChildren(root);
    }
    internal static void ValidateNamespace(string root, string path)
    {
        root = Path.GetFullPath(root); path = Path.GetFullPath(path); BootstrapEngine.NoLinks(path);
        if (!path.Equals(root, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The protected application namespace leaves shared machine storage.");
        Validate(root, true, true);
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return;
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current)) Validate(current, true, false);
            else if (File.Exists(current)) throw new InvalidDataException("A file occupies the protected namespace.");
        }
    }
    private static void ValidateChildren(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            BootstrapEngine.NoLinks(path); var directory = Directory.Exists(path); Validate(path, directory, false);
            if (directory) ValidateChildren(path);
        }
    }
    private static void Validate(string path, bool directory, bool protectedDacl)
    {
        FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (!Equals(owner, Administrators) && !Equals(owner, SystemAccount) || protectedDacl && !security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("The shared host namespace has an unsafe owner or access policy.");
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (raw.DiscretionaryAcl is null) throw new UnauthorizedAccessException("The shared host namespace has a null access policy.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !Equals(rule.IdentityReference, Administrators) && !Equals(rule.IdentityReference, SystemAccount) &&
                ((int)rule.FileSystemRights & ((int)Mutation | 0x50000000)) != 0) throw new UnauthorizedAccessException("Unprivileged users can change a protected host file.");
    }
}
