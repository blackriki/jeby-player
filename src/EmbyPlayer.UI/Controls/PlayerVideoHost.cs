using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EmbyPlayer.UI.Controls;

public sealed class PlayerVideoHost : HwndHost
{
    private const string HostWindowClassName = "EmbyPlayerMpvVideoHost";
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;
    private const int BlackBrush = 4;
    private const int ErrorClassAlreadyExists = 1410;
    private static readonly NativeWndProc HostWindowProcedure = DefWindowProcedure;
    private static bool isWindowClassRegistered;
    private IntPtr hostHandle;

    public event EventHandler? HostCreated;

    public event EventHandler<IntPtr>? HostDestroyed;

    public event EventHandler? HostMouseMoved;

    public IntPtr HostHandle => hostHandle;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureWindowClassRegistered();
        hostHandle = CreateWindowEx(
            0,
            HostWindowClassName,
            string.Empty,
            WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            0,
            0,
            1,
            1,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        HostCreated?.Invoke(this, EventArgs.Empty);
        return new HandleRef(this, hostHandle);
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        if (hostHandle != IntPtr.Zero)
        {
            _ = MoveWindow(
                hostHandle,
                0,
                0,
                Math.Max(1, (int)rcBoundingBox.Width),
                Math.Max(1, (int)rcBoundingBox.Height),
                true);
        }
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
        {
            HostDestroyed?.Invoke(this, hwnd.Handle);
            _ = DestroyWindow(hwnd.Handle);
        }

        hostHandle = IntPtr.Zero;
    }

    protected override IntPtr WndProc(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        const int WmMouseMove = 0x0200;
        if (msg == WmMouseMove)
        {
            HostMouseMoved?.Invoke(this, EventArgs.Empty);
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private static void EnsureWindowClassRegistered()
    {
        if (isWindowClassRegistered)
        {
            return;
        }

        var windowClass = new WndClassEx
        {
            CbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            LpfnWndProc = HostWindowProcedure,
            HInstance = GetModuleHandle(null),
            HbrBackground = GetStockObject(BlackBrush),
            LpszClassName = HostWindowClassName
        };

        var atom = RegisterClassEx(ref windowClass);
        if (atom == 0 && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
        {
            return;
        }

        isWindowClassRegistered = true;
    }

    private static IntPtr DefWindowProcedure(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam)
    {
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(
        IntPtr hwnd,
        int x,
        int y,
        int width,
        int height,
        bool repaint);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int objectType);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    private delegate IntPtr NativeWndProc(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint CbSize;
        public uint Style;
        public NativeWndProc LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public IntPtr HInstance;
        public IntPtr HIcon;
        public IntPtr HCursor;
        public IntPtr HbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? LpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string LpszClassName;
        public IntPtr HIconSm;
    }
}
