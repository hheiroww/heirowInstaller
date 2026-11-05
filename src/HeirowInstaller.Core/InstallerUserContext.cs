using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace HeirowInstaller.Core;

/// <summary>The original coordinator's kernel-verified user profile. Never deserialized from installation options or IPC.</summary>
[SupportedOSPlatform("windows")]
public sealed class InstallerUserContext : IDisposable
{
    private static readonly AsyncLocal<InstallerUserContext?> ambient = new();
    private readonly AsyncLocal<int> originalExecution = new();
    private readonly SafeAccessTokenHandle token;
    private readonly Dictionary<Environment.SpecialFolder, string> folders = [];
    private readonly Dictionary<string, string> environment = new(StringComparer.OrdinalIgnoreCase);
    public static InstallerUserContext? Current => ambient.Value;
    public string UserSid { get; }
    public int ProcessId { get; }
    public long ProcessStartTicks { get; }
    public string ProcessImagePath { get; }
    public bool IsDifferentAccount { get; }
    public bool IsExecutingAsOriginalUser => originalExecution.Value > 0;

    private InstallerUserContext(SafeAccessTokenHandle token, int pid, long start, string image)
    {
        this.token = token; ProcessId = pid; ProcessStartTicks = start; ProcessImagePath = image;
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        UserSid = identity.User?.Value ?? throw new InvalidDataException("The original Windows user is unavailable.");
        using var current = WindowsIdentity.GetCurrent(); IsDifferentAccount = current.User?.Value != UserSid;
        foreach (var (special, id) in new (Environment.SpecialFolder, string)[]
        {
            (Environment.SpecialFolder.UserProfile, "5E6C858F-0E22-4760-9AFE-EA3317B67173"),
            (Environment.SpecialFolder.LocalApplicationData, "F1B32785-6FBA-4FCF-9D55-7B8E7F157091"),
            (Environment.SpecialFolder.ApplicationData, "3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"),
            (Environment.SpecialFolder.DesktopDirectory, "B4BFCC3A-DB2C-424C-B029-7FE99A87C641"),
            (Environment.SpecialFolder.Programs, "A77F5D77-2E2B-44C3-A6A2-ABA601054A51"),
            (Environment.SpecialFolder.StartMenu, "625B53C3-AB48-4EC1-BA1F-A1EF4146FC19"),
            (Environment.SpecialFolder.Startup, "B97D20BB-F46A-4C97-BA10-5E3608430854")
        })
        {
            var folder = new Guid(id); var result = SHGetKnownFolderPath(in folder, 0x4000, token, out var text);
            try
            {
                if (result < 0) Marshal.ThrowExceptionForHR(result);
                folders.Add(special, Path.GetFullPath(Marshal.PtrToStringUni(text) ?? throw new InvalidDataException("An original-user known folder is unavailable.")));
            }
            finally { if (text != 0) Marshal.FreeCoTaskMem(text); }
        }
        if (!CreateEnvironmentBlock(out var block, token, false)) throw new Win32Exception(Marshal.GetLastWin32Error(), "The original user's environment is unavailable.");
        try
        {
            var offset = 0;
            while (Marshal.ReadInt16(block, offset) != 0)
            {
                var entry = new StringBuilder();
                while (Marshal.ReadInt16(block, offset) is var character && character != 0)
                {
                    if (entry.Length >= 32768 || offset >= 2 * 1024 * 1024) throw new InvalidDataException("The original user environment exceeds its supported size.");
                    entry.Append((char)character); offset += 2;
                }
                offset += 2;
                var value = entry.ToString(); var equals = value.IndexOf('=');
                if (equals > 0) environment[value[..equals]] = value[(equals + 1)..];
            }
        }
        finally { _ = DestroyEnvironmentBlock(block); }
    }

    public static InstallerUserContext OpenForProcess(int pid, long startTicks)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid || !GetProcessTimes(process, out var created, out _, out _, out _) || DateTime.FromFileTimeUtc(created).Ticks != startTicks)
            throw new InvalidDataException("The original installation coordinator changed or exited.");
        var image = new StringBuilder(32768); var imageLength = image.Capacity;
        if (!QueryFullProcessImageName(process, 0, image, ref imageLength)) throw new Win32Exception(Marshal.GetLastWin32Error(), "The original coordinator image cannot be inspected.");
        SafeAccessTokenHandle opened;
        if (!OpenProcessToken(process, 0x0e, out opened))
        {
            var error = Marshal.GetLastWin32Error(); opened.Dispose();
            if (error != 5 || !InstallerPlatform.IsElevated) throw new Win32Exception(error, "The original user token cannot be inspected.");
            // This is restricted to the already verified original coordinator PID.
            // Restore SeDebug immediately after obtaining its least-privilege token handle.
            using var privilege = DebugPrivilege.Enable();
            if (!OpenProcessToken(process, 0x0e, out opened)) throw new Win32Exception(Marshal.GetLastWin32Error(), "The original user token cannot be opened.");
        }
        try { return new(opened, pid, startTicks, Path.GetFullPath(image.ToString())); } catch { opened.Dispose(); throw; }
    }

    public IDisposable Enter()
    {
        var previous = ambient.Value; ambient.Value = this; return new ContextScope(previous);
    }
    public T RunAsOriginalUser<T>(Func<T> action) => WindowsIdentity.RunImpersonated(token, () =>
    {
        originalExecution.Value++;
        try { return action(); } finally { originalExecution.Value--; }
    });
    public void RunAsOriginalUser(Action action) => RunAsOriginalUser(() => { action(); return true; });
    public Task<T> RunAsOriginalUserAsync<T>(Func<Task<T>> action) => WindowsIdentity.RunImpersonatedAsync(token, async () =>
    {
        originalExecution.Value++;
        try { return await action(); } finally { originalExecution.Value--; }
    });
    public string GetFolderPath(Environment.SpecialFolder folder)
    {
        if (folders.TryGetValue(folder, out var value)) return value;
        var path = new StringBuilder(32768);
        var result = SHGetFolderPath(0, (int)folder | 0x4000, token, 0, path);
        if (result < 0 || path.Length == 0) throw new InvalidDataException("A requested original-user known folder is unavailable.");
        return Path.GetFullPath(path.ToString());
    }
    public string ExpandEnvironmentVariables(string value) => Regex.Replace(value, "%([^%]+)%", match => environment.TryGetValue(match.Groups[1].Value, out var item) ? item : match.Value);
    internal RegistryKey OpenUserHive(RegistryView view, bool writable = true)
    {
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, view);
        return users.OpenSubKey(UserSid, writable) ?? throw new InvalidDataException("The original user's registry profile is no longer loaded.");
    }
    internal string? GetUserEnvironment(string name)
    {
        if (!IsExecutingAsOriginalUser) return RunAsOriginalUser(() => GetUserEnvironment(name));
        using var hive = OpenUserHive(RegistryView.Default, false); using var key = hive.OpenSubKey("Environment", false);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }
    internal void SetUserEnvironment(string name, string? value)
    {
        if (!IsExecutingAsOriginalUser) { RunAsOriginalUser(() => SetUserEnvironment(name, value)); return; }
        using var hive = OpenUserHive(RegistryView.Default); using var key = hive.CreateSubKey("Environment", true);
        if (value is null) key.DeleteValue(name, false); else key.SetValue(name, value, RegistryValueKind.String);
    }
    public void Dispose() { token.Dispose(); environment.Clear(); }
    private sealed class ContextScope(InstallerUserContext? previous) : IDisposable { public void Dispose() => ambient.Value = previous; }

    private sealed class DebugPrivilege : IDisposable
    {
        private readonly SafeAccessTokenHandle token;
        private readonly TokenPrivileges previous;
        private DebugPrivilege(SafeAccessTokenHandle token, TokenPrivileges previous) { this.token = token; this.previous = previous; }
        internal static DebugPrivilege Enable()
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x28, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var requested = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
                if (!AdjustTokenPrivileges(token, false, in requested, Marshal.SizeOf<TokenPrivileges>(), out var previous, out _) || Marshal.GetLastWin32Error() == 1300)
                    throw new UnauthorizedAccessException("Windows did not permit inspection of the original installation coordinator.");
                return new(token, previous);
            }
            catch { token.Dispose(); throw; }
        }
        public void Dispose()
        {
            _ = AdjustTokenPrivileges(token, false, in previous, Marshal.SizeOf<TokenPrivileges>(), out _, out _); token.Dispose();
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref int length);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, in TokenPrivileges changes, int length, out TokenPrivileges previous, out int required);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(in Guid id, uint flags, SafeAccessTokenHandle token, out nint path);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFolderPathW")] private static extern int SHGetFolderPath(nint window, int folder, SafeAccessTokenHandle token, uint flags, StringBuilder path);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out nint environment, SafeAccessTokenHandle token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(nint environment);
}
