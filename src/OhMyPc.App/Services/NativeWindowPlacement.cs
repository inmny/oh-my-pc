using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace OhMyPc.App.Services;

internal static class NativeWindowPlacement
{
    private static readonly nint HwndTopmost = new(-1);
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint MonitorDefaultToNearest = 2;

    public static void MakeClickThrough(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new nint(style | WsExTransparent | WsExToolWindow | WsExNoActivate));
    }

    public static void FillCursorMonitor(Window window)
    {
        var cursor = Forms.Cursor.Position;
        var monitor = MonitorFromPoint(new NativePoint(cursor.X, cursor.Y), MonitorDefaultToNearest);
        var info = MonitorInfo.Create();
        GetMonitorInfo(monitor, ref info);
        var bounds = info.Monitor;
        SetWindowPos(
            new WindowInteropHelper(window).Handle,
            HwndTopmost,
            bounds.Left,
            bounds.Top,
            bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top,
            SwpNoActivate | SwpShowWindow);
    }

    public static void PlaceAtCursorWorkArea(Window window)
    {
        var cursor = Forms.Cursor.Position;
        var monitor = MonitorFromPoint(new NativePoint(cursor.X, cursor.Y), MonitorDefaultToNearest);
        var info = MonitorInfo.Create();
        GetMonitorInfo(monitor, ref info);
        GetDpiForMonitor(monitor, 0, out var dpiX, out _);
        var scale = dpiX / 96d;
        var margin = (int)Math.Round(12 * scale);
        var work = info.WorkArea;
        var maxHeight = Math.Max(1, (work.Bottom - work.Top - margin * 2) / scale);
        window.MaxHeight = maxHeight;
        window.UpdateLayout();
        var widthDip = double.IsNaN(window.ActualWidth) || window.ActualWidth <= 0
            ? window.Width
            : window.ActualWidth;
        var heightDip = window.ActualHeight > 0
            ? window.ActualHeight
            : (double.IsNaN(window.Height) ? 1 : window.Height);
        var width = (int)Math.Round(widthDip * scale);
        var height = (int)Math.Round(heightDip * scale);
        SetWindowPos(
            new WindowInteropHelper(window).Handle,
            HwndTopmost,
            work.Right - width - margin,
            work.Bottom - height - margin,
            width,
            height,
            SwpShowWindow);
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint newLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;

        public static MonitorInfo Create() => new() { Size = Marshal.SizeOf<MonitorInfo>() };
    }
}
