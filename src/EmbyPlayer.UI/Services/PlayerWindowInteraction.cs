using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EmbyPlayer.UI.Services;

internal enum PlayerWindowHitTarget
{
    Caption,
    Left,
    Right,
    Top,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

internal interface IPlayerWindowNativeMessenger
{
    void ReleaseCapture();

    IntPtr SendMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);
}

internal sealed class PlayerWindowInteraction
{
    internal const uint NonClientLeftButtonDown = 0x00A1;
    internal const uint NonClientLeftButtonDoubleClick = 0x00A3;

    private readonly IPlayerWindowNativeMessenger nativeMessenger;

    public PlayerWindowInteraction()
        : this(new Win32PlayerWindowNativeMessenger())
    {
    }

    internal PlayerWindowInteraction(IPlayerWindowNativeMessenger nativeMessenger)
    {
        this.nativeMessenger = nativeMessenger;
    }

    public bool Begin(
        Window owner,
        PlayerWindowHitTarget target,
        bool isFullscreen,
        int clickCount = 1)
    {
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        return Begin(ownerHandle, owner.WindowState, owner.ResizeMode, target, isFullscreen, clickCount);
    }

    internal bool Begin(
        IntPtr ownerHandle,
        WindowState windowState,
        ResizeMode resizeMode,
        PlayerWindowHitTarget target,
        bool isFullscreen,
        int clickCount = 1)
    {
        if (ownerHandle == IntPtr.Zero
            || !CanBegin(windowState, resizeMode, target, isFullscreen))
        {
            return false;
        }

        var message = target == PlayerWindowHitTarget.Caption && clickCount > 1
            ? NonClientLeftButtonDoubleClick
            : NonClientLeftButtonDown;
        nativeMessenger.ReleaseCapture();
        nativeMessenger.SendMessage(
            ownerHandle,
            message,
            new IntPtr(GetNativeHitTest(target)),
            IntPtr.Zero);
        return true;
    }

    internal static bool CanBegin(
        WindowState windowState,
        ResizeMode resizeMode,
        PlayerWindowHitTarget target,
        bool isFullscreen)
    {
        if (isFullscreen)
        {
            return false;
        }

        if (target == PlayerWindowHitTarget.Caption)
        {
            return true;
        }

        return windowState != WindowState.Maximized
            && resizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;
    }

    internal static int GetNativeHitTest(PlayerWindowHitTarget target) => target switch
    {
        PlayerWindowHitTarget.Caption => 2,
        PlayerWindowHitTarget.Left => 10,
        PlayerWindowHitTarget.Right => 11,
        PlayerWindowHitTarget.Top => 12,
        PlayerWindowHitTarget.TopLeft => 13,
        PlayerWindowHitTarget.TopRight => 14,
        PlayerWindowHitTarget.Bottom => 15,
        PlayerWindowHitTarget.BottomLeft => 16,
        PlayerWindowHitTarget.BottomRight => 17,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
    };

    private sealed class Win32PlayerWindowNativeMessenger : IPlayerWindowNativeMessenger
    {
        public void ReleaseCapture() => ReleaseCaptureNative();

        public IntPtr SendMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam) =>
            SendMessageNative(windowHandle, message, wParam, lParam);

        [DllImport("user32.dll", EntryPoint = "ReleaseCapture")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReleaseCaptureNative();

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessageNative(
            IntPtr windowHandle,
            uint message,
            IntPtr wParam,
            IntPtr lParam);
    }
}
