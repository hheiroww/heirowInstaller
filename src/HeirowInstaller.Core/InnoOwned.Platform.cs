using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace HeirowInstaller.Core;

internal sealed record InnoOwnedFileState(long Size, string Sha256, string Identity);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class InnoOwnedRegistryImage
{
    public bool Exists { get; set; }
    public SortedDictionary<string, InnoOwnedRegistryValue> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
internal sealed record InnoOwnedRegistryValue(string Kind, string Data);
internal sealed record InnoOwnedLinkInfo(string Target, string Arguments, string WorkingDirectory, string Icon, int IconIndex);

// Only internal fixture backends can substitute this platform; InstallOptions has no bypass.
internal interface IInnoOwnedPlatformProvider
{
    IInnoOwnedPlatform OwnedPlatform(NativeMigrationBaseline baseline, string stateRoot);
}
internal interface IInnoOwnedPlatform
{
    bool IsIsolated { get; }
    string UserSid { get; }
    string LocalAppData { get; }
    string StateRoot { get; }
    string Folder(string name);
    void ProtectDirectory(string path);
    byte[] Seal(byte[] bytes, byte[] binding);
    byte[] Unseal(byte[] bytes, byte[] binding);
    void CommitLedger(string temporary, string destination);
    IDisposable HoldDirectories(IEnumerable<string> paths);
    InnoOwnedFileState? Describe(string path);
    void Backup(string path, string backup, InnoOwnedFileState expected, string? identity, Action<string> created);
    void Delete(string path, InnoOwnedFileState expected);
    void Restore(string path, string backup, InnoOwnedFileState expected, string temporary, string? identity, Action<string> created);
    InnoOwnedLinkInfo ReadLink(string verifiedBackup);
    InnoOwnedRegistryImage ReadRegistry(bool sdk);
    void RetireRegistry(InnoOwnedRegistryImage before);
    void RestoreRegistry(bool sdk, InnoOwnedRegistryImage before, InnoOwnedRegistryImage? permittedAfter);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsInnoOwnedPlatform : IInnoOwnedPlatform
{
    private readonly InnoOwnedFootprintCatalog catalog;
    public bool IsIsolated => false;
    public string UserSid { get; }
    public string LocalAppData { get; }
    public string StateRoot { get; }
    internal WindowsInnoOwnedPlatform(InnoOwnedFootprintCatalog catalog, string stateRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Owned Inno retirement requires Windows.");
        this.catalog = catalog; StateRoot = Path.GetFullPath(stateRoot);
        using var identity = WindowsIdentity.GetCurrent();
        UserSid = identity.User?.Value ?? throw new UnauthorizedAccessException("The current user identity is unavailable.");
        LocalAppData = Folder("LocalAppData");
        if (!StateRoot.Equals(Path.GetFullPath(InstallerEngine.DefaultStateRoot("user")), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Owned Inno retirement initially requires the current user's default installer state root.");
        var appRoot = PackageSafety.UnderRoot(LocalAppData, catalog.RootRelativeToLocalAppData);
        if (!Directory.Exists(appRoot) || FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(appRoot), AccessControlSections.Owner)
            .GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier appOwner ||
            appOwner.Value != UserSid)
            throw new UnauthorizedAccessException("The recorded app root is not owned by the current user.");
    }
    public string Folder(string name)
    {
        var value = Environment.GetFolderPath(name switch
        {
            "LocalAppData" => Environment.SpecialFolder.LocalApplicationData,
            "Programs" => Environment.SpecialFolder.Programs, "Desktop" => Environment.SpecialFolder.DesktopDirectory,
            _ => throw new InvalidDataException("Unsupported owned known folder.")
        });
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("A current-user known folder is missing, redirected to a network, or unsupported.");
        PackageSafety.EnsureNoReparsePoints(value);
        return Path.GetFullPath(value);
    }
    public void ProtectDirectory(string path)
    {
        PackageSafety.EnsureNoReparsePoints(path);
        if (!Directory.Exists(path))
        {
            var security = new DirectorySecurity(); var user = new SecurityIdentifier(UserSid);
            security.SetOwner(user); security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                security.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            FileSystemAclExtensions.Create(new DirectoryInfo(path), security);
        }
        var actual = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path));
        if (actual.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier recoveryOwner ||
            recoveryOwner.Value != UserSid || !actual.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("The owned recovery directory is not protected for this user.");
        foreach (FileSystemAccessRule rule in actual.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && ((SecurityIdentifier)rule.IdentityReference).Value is var sid &&
                sid != UserSid && sid != "S-1-5-18" && sid != "S-1-5-32-544" &&
                ((int)rule.FileSystemRights & ((int)(FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                    FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership) | 0x50000000)) != 0)
                throw new UnauthorizedAccessException("Another identity can change the owned recovery directory.");
    }
    public byte[] Seal(byte[] bytes, byte[] binding) => InnoOwnedUserProtection.Transform(bytes, binding, protect: true);
    public byte[] Unseal(byte[] bytes, byte[] binding) => InnoOwnedUserProtection.Transform(bytes, binding, protect: false);
    public void CommitLedger(string temporary, string destination) => InnoOwnedUserProtection.CommitLedger(temporary, destination);
    public IDisposable HoldDirectories(IEnumerable<string> paths)
    {
        var handles = new List<LegacyStagingFileGuard>();
        try
        {
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Length))
                handles.Add(LegacyStagingFileGuard.NamespaceDirectory(path));
            return new DirectoryLease(handles);
        }
        catch { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); throw; }
    }
    private sealed class DirectoryLease(List<LegacyStagingFileGuard> handles) : IDisposable
    { public void Dispose() { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); } }
    public InnoOwnedFileState? Describe(string path)
    {
        PackageSafety.EnsureNoReparsePoints(path);
        if (Directory.Exists(path)) throw new IOException("A directory occupies a declared owned file.");
        if (!File.Exists(path)) return null;
        long size; string hash;
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        { size = input.Length; hash = Convert.ToHexString(SHA256.HashData(input)); }
        using var held = LegacyStagingFileGuard.File(path, size, hash);
        return new(size, hash, held.IdentityToken);
    }
    public void Backup(string path, string backup, InnoOwnedFileState expected, string? identity, Action<string> created)
    {
        using var source = LegacyStagingFileGuard.File(path, expected.Size, expected.Sha256);
        if (source.IdentityToken != expected.Identity) throw new IOException("The owned source changed before backup.");
        Copy(source, backup, identity, created);
    }
    private static void Copy(LegacyStagingFileGuard source, string destination, string? identity, Action<string> created)
    {
        PackageSafety.EnsureNoReparsePoints(destination);
        if (File.Exists(destination))
        {
            if (identity is null) throw new IOException("An unowned file occupies a recovery creation.");
            using var held = LegacyStagingFileGuard.File(destination, new FileInfo(destination).Length, PackageSafety.HashFile(destination));
            if (held.IdentityToken != identity || held.Size > source.Size) throw new IOException("A recovery creation changed identity.");
        }
        using (var output = new FileStream(destination, identity is null ? FileMode.CreateNew : FileMode.Open, FileAccess.ReadWrite, FileShare.None, 131072, FileOptions.WriteThrough))
        {
            var actual = LegacyStagingFileGuard.CaptureOrdinaryFileIdentity(output.SafeFileHandle);
            if (identity is not null && actual != identity) throw new IOException("A recovery creation was replaced.");
            if (identity is null) created(actual); // Durable creation intent before the first large write.
            output.SetLength(0); source.CopyDurableTo(output);
        }
        IncrementalPackageIO.VerifyFile(destination, source.Size, source.Sha256!);
    }
    public void Delete(string path, InnoOwnedFileState expected)
    {
        using var file = LegacyStagingFileGuard.File(path, expected.Size, expected.Sha256);
        if (file.IdentityToken != expected.Identity) throw new IOException("An owned file changed identity before retirement.");
        file.DeleteAndClose();
    }
    public void Restore(string path, string backup, InnoOwnedFileState expected, string temporary, string? identity, Action<string> created)
    {
        var current = Describe(path);
        if (current is not null)
        {
            if (current.Size == expected.Size && PackageSafety.HashEquals(current.Sha256, expected.Sha256)) return;
            throw new IOException("A restoration destination changed; its backup was preserved.");
        }
        using var source = LegacyStagingFileGuard.File(backup, expected.Size, expected.Sha256);
        Copy(source, temporary, identity, created);
        File.Move(temporary, path, overwrite: false);
    }
    public InnoOwnedLinkInfo ReadLink(string verifiedBackup) => InnoOwnedShortcutReader.Read(verifiedBackup);
    private string RegistryPath(bool sdk) => sdk ? BackendMaintenance.UninstallRoot + "heirowInstaller." + catalog.ProductId : catalog.Registration.UninstallKey!;
    public InnoOwnedRegistryImage ReadRegistry(bool sdk)
    {
        using var hive = WindowsIntegration.Hive("user", catalog.Registration.View);
        using var key = hive.OpenSubKey(RegistryPath(sdk));
        if (key is null) return new();
        if (key.SubKeyCount != 0 || key.ValueCount > 64) throw new InvalidDataException("An owned registration has unexpected children or values.");
        var result = new InnoOwnedRegistryImage { Exists = true };
        foreach (var name in key.GetValueNames())
        {
            var kind = key.GetValueKind(name);
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var data = kind switch
            {
                RegistryValueKind.String when value is string text && text.Length <= 4096 => new InnoOwnedRegistryValue("string", text),
                RegistryValueKind.DWord when value is int number => new InnoOwnedRegistryValue("dword", number.ToString(CultureInfo.InvariantCulture)),
                _ => throw new InvalidDataException("An owned registration contains an unsupported typed value.")
            };
            if (name.Length is < 1 or > 128) throw new InvalidDataException("An owned registration value name is unsupported.");
            result.Values.Add(name, data);
        }
        return result;
    }
    public void RetireRegistry(InnoOwnedRegistryImage before)
    {
        var current = ReadRegistry(sdk: false);
        if (!InnoOwnedRegistry.IsSubset(current, before)) throw new IOException("The Inno registration changed before retirement.");
        using var hive = WindowsIntegration.Hive("user", catalog.Registration.View);
        using (var key = hive.OpenSubKey(RegistryPath(false), writable: true))
        {
            foreach (var pair in current.Values)
            {
                if (!InnoOwnedRegistry.IsSubset(ReadRegistry(false), before)) throw new IOException("The Inno registration changed during retirement.");
                key!.DeleteValue(pair.Key, throwOnMissingValue: true);
            }
        }
        var empty = ReadRegistry(false);
        if (empty.Values.Count != 0) throw new IOException("The Inno registration gained an unexpected value.");
        if (empty.Exists) hive.DeleteSubKey(RegistryPath(false), throwOnMissingSubKey: true); // Exact attested key only, never a subtree.
    }
    public void RestoreRegistry(bool sdk, InnoOwnedRegistryImage before, InnoOwnedRegistryImage? permittedAfter)
    {
        var current = ReadRegistry(sdk);
        InnoOwnedRegistry.RequireRestorable(current, before, permittedAfter);
        using var hive = WindowsIntegration.Hive("user", catalog.Registration.View);
        if (before.Exists)
        {
            using var key = hive.CreateSubKey(RegistryPath(sdk), writable: true);
            foreach (var pair in current.Values.Where(x => !before.Values.ContainsKey(x.Key)))
            { InnoOwnedRegistry.RequireRestorable(ReadRegistry(sdk), before, permittedAfter); key.DeleteValue(pair.Key, true); }
            foreach (var pair in before.Values)
            {
                InnoOwnedRegistry.RequireRestorable(ReadRegistry(sdk), before, permittedAfter);
                key.SetValue(pair.Key, pair.Value.Kind == "dword" ? int.Parse(pair.Value.Data, CultureInfo.InvariantCulture) : pair.Value.Data,
                    pair.Value.Kind == "dword" ? RegistryValueKind.DWord : RegistryValueKind.String);
            }
        }
        else if (current.Exists)
        {
            using (var key = hive.OpenSubKey(RegistryPath(sdk), true))
                foreach (var pair in current.Values)
                { InnoOwnedRegistry.RequireRestorable(ReadRegistry(sdk), before, permittedAfter); key!.DeleteValue(pair.Key, true); }
            if (ReadRegistry(sdk).Values.Count != 0) throw new IOException("An unknown registry value was preserved.");
            hive.DeleteSubKey(RegistryPath(sdk), false);
        }
        if (!InnoOwnedRegistry.Equal(ReadRegistry(sdk), before)) throw new IOException("The exact registration could not be restored.");
    }
}

internal static class InnoOwnedRegistry
{
    internal static bool Equal(InnoOwnedRegistryImage left, InnoOwnedRegistryImage right) =>
        left.Exists == right.Exists && left.Values.Count == right.Values.Count && IsSubset(left, right);
    internal static bool IsSubset(InnoOwnedRegistryImage current, InnoOwnedRegistryImage before) =>
        (!current.Exists || before.Exists) && current.Values.All(x => before.Values.TryGetValue(x.Key, out var value) && value == x.Value);
    internal static void RequireRestorable(InnoOwnedRegistryImage current, InnoOwnedRegistryImage before, InnoOwnedRegistryImage? after)
    {
        if (current.Exists && !before.Exists && after is null)
            throw new IOException("An unexpected registration appeared before any native metadata plan.");
        if (current.Values.Any(x => !(before.Values.TryGetValue(x.Key, out var old) && old == x.Value) &&
            !(after is not null && after.Values.TryGetValue(x.Key, out var next) && next == x.Value)))
            throw new IOException("A registration changed outside the recorded before/after images; recovery was preserved.");
    }
    internal static InnoOwnedRegistryImage FromValues(IReadOnlyDictionary<string, object>? values)
    {
        var image = new InnoOwnedRegistryImage { Exists = values is not null };
        foreach (var pair in values ?? new Dictionary<string, object>())
            image.Values.Add(pair.Key, pair.Value is int number ? new("dword", number.ToString(CultureInfo.InvariantCulture)) : new("string", (string)pair.Value));
        return image;
    }
}
