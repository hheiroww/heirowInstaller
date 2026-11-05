using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HeirowInstaller.Bootstrap;

// Native controls let the first progress frame appear without a managed desktop framework or renderer.
internal sealed unsafe class BootstrapWindow : IProgress<BootstrapProgress>, IDisposable
{
    private static BootstrapWindow? current;
    private readonly CancellationTokenSource cancellation = new();
    private readonly string title;
    private nint window, heading, detail, bar, cancel;
    private BootstrapProgress latest = new("Preparing setup", "Checking the required shared files.");
    private Task<int>? operation;
    private Exception? failure;
    private long lastProgressTick;
    internal BootstrapWindow(string title) => this.title = title;
    public void Report(BootstrapProgress value)
    {
        Volatile.Write(ref latest, value);
        var tick = Environment.TickCount64;
        if (tick - Interlocked.Read(ref lastProgressTick) >= 50 || value.Completed == value.Total)
        { Interlocked.Exchange(ref lastProgressTick, tick); if (window != 0) PostMessageW(window, 0x8001, 0, 0); }
    }
    internal int Run(Func<CancellationToken, Task<int>> action)
    {
        current = this;
        var instance = GetModuleHandleW(null);
        var className = "SocketJack.HeirowBootstrap.Progress";
        fixed (char* name = className)
        {
            var cls = new WindowClass { Size = (uint)sizeof(WindowClass), Instance = instance, ClassName = name,
                Procedure = &WindowProc, Cursor = LoadCursorW(0, 32512), Background = 6 };
            if (RegisterClassExW(ref cls) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var controls = new CommonControls { Size = 8, Classes = 0x20 }; InitCommonControlsEx(ref controls);
            window = CreateWindowExW(0, className, title, 0x00ca0000, 0, 0, 620, 245, 0, 0, instance, 0);
            if (window == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var scale = GetDpiForWindow(window) / 96d;
            int D(int value) => (int)Math.Round(value * scale);
            heading = CreateWindowExW(0, "STATIC", latest.Phase, 0x50000000, D(24), D(22), D(550), D(26), window, 0, instance, 0);
            detail = CreateWindowExW(0, "STATIC", latest.Detail, 0x50000000, D(24), D(56), D(550), D(44), window, 0, instance, 0);
            bar = CreateWindowExW(0, "msctls_progress32", "", 0x50000000, D(24), D(110), D(550), D(22), window, 0, instance, 0);
            cancel = CreateWindowExW(0, "BUTTON", "Cancel", 0x50010000, D(454), D(151), D(120), D(30), window, 100, instance, 0);
            var font = GetStockObject(17);
            foreach (var control in new[] { heading, detail, cancel }) SendMessageW(control, 0x30, (nuint)font, 1);
            GetCursorPos(out var point); var monitor = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
            GetMonitorInfoW(MonitorFromPoint(point, 2), ref monitor);
            var width = Math.Min(D(620), monitor.Work.Right - monitor.Work.Left);
            var height = Math.Min(D(245), monitor.Work.Bottom - monitor.Work.Top);
            SetWindowPos(window, 0, monitor.Work.Left + (monitor.Work.Right - monitor.Work.Left - width) / 2,
                monitor.Work.Top + (monitor.Work.Bottom - monitor.Work.Top - height) / 2, width, height, 0x14);
            ShowWindow(window, 5); UpdateWindow(window);
            // Start after native controls have painted, so DNS, disk and extraction never delay the first frame.
            operation = Task.Run(() => RunOperation(action));
            while (GetMessageW(out var message, 0, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessageW(ref message); }
        }
        current = null;
        if (failure is not null) MessageBoxW(0, failure.Message, title, 0x10);
        return operation?.GetAwaiter().GetResult() ?? 1;
    }
    internal void Hide() { if (window != 0) PostMessageW(window, 0x8003, 0, 0); }
    private int RunOperation(Func<CancellationToken, Task<int>> action)
    {
        try { return action(cancellation.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 1223; }
        catch (Exception error) { failure = error; return 1; }
        finally { PostMessageW(window, 0x8002, 0, 0); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint message, nuint wparam, nint lparam)
    {
        var self = current;
        if (self is not null)
        {
            if (message == 0x10 || message == 0x111 && (wparam & 0xffff) == 100)
            { self.cancellation.Cancel(); EnableWindow(self.cancel, false); SetWindowTextW(self.detail, "Cancelling. Verified shared files are retained."); return 0; }
            if (message == 0x8001)
            {
                var value = Volatile.Read(ref self.latest);
                SetWindowTextW(self.heading, value.Phase);
                SetWindowTextW(self.detail, value.Total > 0 ? $"{value.Detail}\n{value.Completed:N0} / {value.Total:N0} bytes" : value.Detail);
                SendMessageW(self.bar, 0x402, (nuint)(value.Total > 0 ? Math.Clamp((int)(100d * value.Completed / value.Total), 0, 100) : 0), 0);
                return 0;
            }
            if (message == 0x8002) { DestroyWindow(hwnd); return 0; }
            if (message == 0x8003) { ShowWindow(hwnd, 0); return 0; }
            if (message == 2) { PostQuitMessage(0); return 0; }
        }
        return DefWindowProcW(hwnd, message, wparam, lparam);
    }
    public void Dispose() => cancellation.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct CommonControls { public uint Size, Classes; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Hwnd; public uint Id; public nuint Wparam; public nint Lparam; public uint Time; public Point Point; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowClass
    {
        public uint Size, Style; public delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> Procedure; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background; public char* MenuName; public char* ClassName; public nint SmallIcon;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll", SetLastError = true)] private static extern ushort RegisterClassExW(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint ex, string cls, string text, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint window, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo value);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(nint window);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int result);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Message value, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref Message message);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint window, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint window, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(nint window, string text);
    [DllImport("user32.dll")] private static extern bool EnableWindow(nint window, bool enabled);
    [DllImport("user32.dll")] private static extern nint LoadCursorW(nint instance, nint name);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int value);
    [DllImport("comctl32.dll")] private static extern bool InitCommonControlsEx(ref CommonControls value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int MessageBoxW(nint owner, string text, string caption, uint type);
}
