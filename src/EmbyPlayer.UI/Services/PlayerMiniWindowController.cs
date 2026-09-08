using System.Windows;

namespace EmbyPlayer.UI.Services;

/// <summary>Owns the reversible window-only state of an explicit mini player.</summary>
public sealed class PlayerMiniWindowController(Window window)
{
    private SavedState? saved;
    public bool IsMini => saved is not null;

    public void Enter()
    {
        if (IsMini) return;
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height) : window.RestoreBounds;
        saved = new(bounds, window.WindowState, window.Topmost, window.MinWidth, window.MinHeight);
        window.WindowState = WindowState.Normal;
        window.MinWidth = 480;
        window.MinHeight = 270;
        window.Width = 480;
        window.Height = 270;
        window.Topmost = true;
    }

    public void Exit()
    {
        if (saved is not { } state) return;
        saved = null;
        window.WindowState = WindowState.Normal;
        window.MinWidth = state.MinWidth;
        window.MinHeight = state.MinHeight;
        window.Left = state.Bounds.Left;
        window.Top = state.Bounds.Top;
        window.Width = state.Bounds.Width;
        window.Height = state.Bounds.Height;
        window.Topmost = state.Topmost;
        window.WindowState = state.State;
    }

    private sealed record SavedState(Rect Bounds, WindowState State, bool Topmost, double MinWidth, double MinHeight);
}
