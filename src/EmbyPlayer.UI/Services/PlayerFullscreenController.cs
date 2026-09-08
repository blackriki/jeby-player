using System.Windows;

namespace EmbyPlayer.UI.Services;

public sealed class PlayerFullscreenController
{
    private readonly IPlayerFullscreenWindow window;
    private SavedWindowState? savedWindowState;

    public PlayerFullscreenController(IPlayerFullscreenWindow window)
    {
        this.window = window ?? throw new ArgumentNullException(nameof(window));
    }

    public bool IsFullscreen => savedWindowState is not null;

    public void Toggle()
    {
        if (IsFullscreen)
        {
            Exit();
            return;
        }

        Enter();
    }

    public void Enter()
    {
        if (IsFullscreen)
        {
            return;
        }

        savedWindowState = new SavedWindowState(
            window.WindowState,
            window.Topmost);

        window.PrepareForFullscreen();
        window.WindowState = WindowState.Normal;
        window.Topmost = true;
        window.ApplyFullscreenBounds();
    }

    public void Exit()
    {
        if (savedWindowState is not { } saved)
        {
            return;
        }

        window.RestoreWindowBounds();
        window.Topmost = saved.Topmost;
        window.WindowState = saved.WindowState;
        savedWindowState = null;
    }

    private sealed record SavedWindowState(
        WindowState WindowState,
        bool Topmost);
}
