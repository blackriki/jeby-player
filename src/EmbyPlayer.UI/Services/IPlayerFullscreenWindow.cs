using System.Windows;

namespace EmbyPlayer.UI.Services;

public interface IPlayerFullscreenWindow
{
    WindowState WindowState { get; set; }

    bool Topmost { get; set; }

    void PrepareForFullscreen();

    void ApplyFullscreenBounds();

    void RestoreWindowBounds();
}
