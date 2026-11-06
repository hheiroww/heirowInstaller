using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace HeirowInstaller.Bootstrap;

internal static class NativeProcessIdentity
{
    internal static (int Id, long StartTicks, string Image) Current()
    {
        var process = GetCurrentProcess();
        var image = new StringBuilder(32768); var length = image.Capacity;
        if (!GetProcessTimes(process, out var created, out _, out _, out _) ||
            !QueryFullProcessImageName(process, 0, image, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return (Environment.ProcessId, DateTime.FromFileTimeUtc(created).Ticks, Path.GetFullPath(image.ToString()));
    }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder image, ref int length);
}
