using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EmbyPlayer.UI.Services;

internal sealed class WpfPlayerFullscreenWindow : IPlayerFullscreenWindow
{
    private const uint MonitorDefaultToNearest = 2;
    private readonly Window window;
    private Rect? restoreBounds;

    public WpfPlayerFullscreenWindow(Window window)
    {
        this.window = window ?? throw new ArgumentNullException(nameof(window));
    }

    public WindowState WindowState
    {
        get => window.WindowState;
        set => window.WindowState = value;
    }

    public bool Topmost
    {
        get => window.Topmost;
        set => window.Topmost = value;
    }

    public void PrepareForFullscreen()
    {
        restoreBounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;

        if (window is IPlayerCaptionHost captionHost)
        {
            captionHost.SetPlayerCaptionState(isVisible: false, isFullscreen: true);
        }
    }

    public void ApplyFullscreenBounds()
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMonitorInfo failed.");
        }

        var source = HwndSource.FromHwnd(handle)
            ?? throw new InvalidOperationException("The fullscreen window source is not available.");
        var fromDevice = source.CompositionTarget.TransformFromDevice;
        var topLeft = fromDevice.Transform(new Point(monitorInfo.Monitor.Left, monitorInfo.Monitor.Top));
        var bottomRight = fromDevice.Transform(new Point(monitorInfo.Monitor.Right, monitorInfo.Monitor.Bottom));

        window.Left = topLeft.X;
        window.Top = topLeft.Y;
        window.Width = bottomRight.X - topLeft.X;
        window.Height = bottomRight.Y - topLeft.Y;
    }

    public void RestoreWindowBounds()
    {
        if (restoreBounds is not { } bounds)
        {
            return;
        }

        window.Left = bounds.Left;
        window.Top = bounds.Top;
        window.Width = bounds.Width;
        window.Height = bounds.Height;
        restoreBounds = null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
