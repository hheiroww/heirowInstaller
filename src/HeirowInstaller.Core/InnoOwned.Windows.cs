using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace HeirowInstaller.Core;

[SupportedOSPlatform("windows")]
internal static class InnoOwnedUserProtection
{
    internal static void CommitLedger(string temporary, string destination)
    {
        PackageSafety.EnsureNoReparsePoints(temporary); PackageSafety.EnsureNoReparsePoints(destination);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(temporary)), Path.GetDirectoryName(Path.GetFullPath(destination)), StringComparison.OrdinalIgnoreCase) ||
            !MoveFileExW(temporary, destination, 1 | 8)) // Replace existing, write through; no copy-across-volume or delayed action.
            throw new IOException("The protected owned ledger could not be committed durably.", new Win32Exception(Marshal.GetLastWin32Error()));
    }
    // Current-user DPAPI, authenticated ciphertext, no machine scope and no UI.
    internal static byte[] Transform(byte[] bytes, byte[] binding, bool protect)
    {
        if (bytes.Length is < 1 or > 12 * 1024 * 1024 || binding.Length != 32)
            throw new InvalidDataException("Owned recovery protection input exceeds its bounds.");
        var input = Allocate(bytes); var entropy = Allocate(binding); Blob output = default;
        try
        {
            var success = protect
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException("The current user's authenticated owned recovery state cannot be read or written.", new Win32Exception(Marshal.GetLastWin32Error()));
            if (output.Count is < 1 or > 12 * 1024 * 1024) throw new CryptographicException("Owned recovery state exceeds its protected bound.");
            var result = new byte[output.Count]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data); Marshal.FreeHGlobal(entropy.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }
    private static Blob Allocate(byte[] data)
    { var pointer = Marshal.AllocHGlobal(data.Length); Marshal.Copy(data, 0, pointer, data.Length); return new() { Count = data.Length, Data = pointer }; }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Count; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string existing, string destination, int flags);
}

[SupportedOSPlatform("windows")]
internal static class InnoOwnedShortcutReader
{
    internal static InnoOwnedLinkInfo Read(string backup)
    {
        // Load a verified private backup, never Resolve, ShellExecute, BindToObject or Save.
        var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        try
        {
            ((IPersistFile)instance).Load(backup, 0x20); // Read, deny-write.
            var link = (IShellLinkW)instance;
            var target = new StringBuilder(1024); var args = new StringBuilder(4096);
            var working = new StringBuilder(1024); var icon = new StringBuilder(1024);
            if (link.GetPath(target, target.Capacity, IntPtr.Zero, 4) != 0 || target.Length == 0)
                throw new InvalidDataException("The launch link has no exact raw local path.");
            link.GetArguments(args, args.Capacity); link.GetWorkingDirectory(working, working.Capacity);
            link.GetIconLocation(icon, icon.Capacity, out var index);
            if (target.Length >= 1023 || working.Length >= 1023 || icon.Length >= 1023 || args.Length >= 4095)
                throw new InvalidDataException("A launch link exceeds its bounded property sizes.");
            return new(target.ToString(), args.ToString(), working.ToString(), icon.ToString(), index);
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr data, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
