using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using EmbyPlayer.UI.Services;

namespace EmbyPlayer.UI.Pages;

public partial class PlayerPage
{
    private PlayerMiniWindowController? miniWindowController;
    private bool restoreMiniFullscreen;
    private bool? playerOriginalTopmost;
    private Window? playerPinOwner;
    private bool IsMiniPlayer => miniWindowController?.IsMini == true;

    private void OnMiniPlayerButtonClick(object sender, RoutedEventArgs e)
    {
        CancelKeyboardSeek();
        var owner = GetPlayerOwnerWindow();
        if (owner is null) return;
        if (IsMiniPlayer)
        {
            ExitMiniPlayer(restoreFullscreen: true);
            return;
        }
        restoreMiniFullscreen = fullscreenController?.IsFullscreen == true;
        if (restoreMiniFullscreen) ExitFullscreen();
        playerOriginalTopmost ??= owner.Topmost;
        playerPinOwner = owner;
        miniWindowController = new(owner);
        miniWindowController.Enter();
        RefreshMiniPlayerLayout();
    }

    private void ExitMiniPlayer(bool restoreFullscreen)
    {
        miniWindowController?.Exit();
        if (restoreFullscreen && restoreMiniFullscreen)
        {
            GetFullscreenController()?.Enter();
            SyncFullscreenState(true);
        }
        restoreMiniFullscreen = false;
        RefreshMiniPlayerLayout();
    }

    private void OnPinPlayerButtonClick(object sender, RoutedEventArgs e)
    {
        var owner = GetPlayerOwnerWindow();
        if (owner is null) return;
        if (fullscreenController?.IsFullscreen == true) return;
        playerOriginalTopmost ??= owner.Topmost;
        playerPinOwner = owner;
        owner.Topmost = !owner.Topmost;
        RefreshMiniPlayerLayout();
    }

    private void RefreshMiniPlayerLayout()
    {
        var mini = IsMiniPlayer;
        MiniPlayerButton.ToolTip = mini ? "恢复播放器窗口" : "迷你播放器";
        AutomationProperties.SetName(MiniPlayerButton, (string)MiniPlayerButton.ToolTip);
        MiniPlayerIcon.Data = mini ? (Geometry)FindResource("Icon.FullscreenExit")
            : Geometry.Parse("M 3,4 L 21,4 21,20 3,20 Z M 12,12 L 19,12 19,18 12,18 Z");
        var pinned = GetPlayerOwnerWindow()?.Topmost == true;
        PinPlayerButton.ToolTip = pinned ? "取消窗口置顶" : "窗口置顶";
        AutomationProperties.SetName(PinPlayerButton, (string)PinPlayerButton.ToolTip);
        PinPlayerButton.Foreground = pinned ? new SolidColorBrush(Color.FromRgb(82, 181, 65)) : Brushes.White;
        PlayerMetadata.Height = mini ? 0 : double.NaN;
        PlayerCaptionHitZone.Height = mini ? 60 : 103;
        // Leave the title row and transport controls clear even when the next-episode card appears.
        if (mini) NextEpisodeCard.Margin = new Thickness(0, 0, 16, 110);
        else NextEpisodeCard.ClearValue(MarginProperty);
        lastPlayerLayoutSize = default;
        UpdateCompactPlayerLayout();
        RequestControlsOverlayBoundsSync();
    }
}
