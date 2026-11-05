using System.Runtime.InteropServices;

namespace HeirowInstaller.Core;

/// <summary>Initial Windows placement using the owner's or pointer's monitor work area.</summary>
public static class DesktopWindowPlacement
{
    public static bool Center(nint window, nint owner = default)
    {
        if (!OperatingSystem.IsWindows() || window == 0 || !IsWindow(window)) return false;
        if (owner == 0) owner = GetWindow(window, 4); // GW_OWNER
        var monitor = owner != 0 && IsWindow(owner) ? MonitorFromWindow(owner, 2)
            : GetCursorPos(out var pointer) ? MonitorFromPoint(pointer, 2) : MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return false;
        // Moving between monitors can synchronously change the DPI/window size.
        // Re-read once before the final placement; do this before the first frame.
        for (var pass = 0; pass < 2; pass++)
        {
            if (!GetWindowRect(window, out var bounds)) return false;
            var (x, y) = CenterPosition(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left,
                info.Work.Bottom - info.Work.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
            if (!SetWindowPos(window, 0, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010)) return false; // no size/z-order/activation
        }
        return true;
    }

    internal static (int X, int Y) CenterPosition(int left, int top, int width, int height, int windowWidth, int windowHeight) =>
        (left + Math.Max(0, (width - windowWidth) / 2), top + Math.Max(0, (height - windowHeight) / 2));

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
