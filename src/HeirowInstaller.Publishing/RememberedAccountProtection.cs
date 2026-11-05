using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HeirowInstaller.Publishing;

internal static class RememberedAccountProtection
{
    internal static byte[] Transform(byte[] bytes, byte[] binding, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Remembered sign-in requires Windows current-user protection.");
        if (bytes.Length is < 1 or > 128 * 1024 || binding.Length != 32) throw new InvalidDataException("Remembered sign-in exceeds its protected size limit.");
        var input = Allocate(bytes); var entropy = Allocate(binding); Blob output = default;
        try
        {
            var success = protect
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException("The current user's remembered sign-in could not be protected or read.");
            if (output.Count is < 1 or > 128 * 1024) throw new CryptographicException("Remembered sign-in exceeds its protected size limit.");
            var result = new byte[output.Count]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            Clear(input); Marshal.FreeHGlobal(input.Data); Clear(entropy); Marshal.FreeHGlobal(entropy.Data);
            if (output.Data != IntPtr.Zero) { if (output.Count is > 0 and <= 128 * 1024) Clear(output); LocalFree(output.Data); }
        }
    }
    internal static void Commit(string temporary, string destination)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Remembered sign-in requires Windows.");
        if (!MoveFileExW(ExtendedPath(temporary), ExtendedPath(destination), 1 | 8))
            throw new IOException("Remembered sign-in could not be saved durably.", new Win32Exception(Marshal.GetLastWin32Error()));
    }
    private static string ExtendedPath(string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full : full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }
    private static Blob Allocate(byte[] bytes)
    { var pointer = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, pointer, bytes.Length); return new() { Count = bytes.Length, Data = pointer }; }
    private static void Clear(Blob blob) => Marshal.Copy(new byte[blob.Count], 0, blob.Data, blob.Count);
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Count; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string existing, string destination, int flags);
}
