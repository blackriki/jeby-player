using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace EmbyPlayer.UI.Pages;

public partial class PlayerPage
{
    private readonly List<ResponsivePlayerPopup> responsivePlayerPopups = new();
    private Size lastPlayerLayoutSize;

    private void InitializeCompactPlayerLayout()
    {
        foreach (var popup in new[] { MorePopup, SpeedPopup, SubtitlePopup, AudioPopup, QualityPopup, QueuePopup, PlaybackInfoPopup })
        {
            var border = (Border)popup.Child;
            responsivePlayerPopups.Add(new(popup, border, border.Child, border.Width, border.MaxHeight,
                popup.PlacementTarget, popup.HorizontalOffset));
        }
    }

    private void UpdateCompactPlayerLayout()
    {
        var size = new Size(VideoHost.ActualWidth, VideoHost.ActualHeight);
        if (size.Width <= 0 || size.Height <= 0 || size == lastPlayerLayoutSize) return;
        lastPlayerLayoutSize = size;
        ClosePopups();
        var compact = size.Width < 900;
        var shortWindow = size.Height < 500;
        SecondaryPlayerActions.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PlaybackInfoButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        MoreMenuButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        PlayerLogoContainer.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PlayerHeader.ColumnDefinitions[1].Width = new GridLength(compact ? 8 : 16);
        PlayerHeader.ColumnDefinitions[3].Width = new GridLength(compact ? 0 : 16);
        PlayerHeader.Margin = compact || shortWindow ? new Thickness(12, 10, 10, 0) : new Thickness(24, 23, 18, 0);
        TopChrome.Height = IsMiniPlayer ? 68 : shortWindow ? 84 : 111;
        BottomChrome.Height = IsMiniPlayer ? 124 : shortWindow ? 148 : 238;
        PlayerBottomContent.Margin = compact || shortWindow ? new Thickness(16, 0, 16, 12) : new Thickness(30, 0, 30, 24);
        CenterPlayButton.Width = CenterPlayButton.Height = shortWindow ? 56 : 76;
        SpeedFeedback.Margin = new Thickness(0, shortWindow ? 72 : 120, 0, 0);

        foreach (var item in responsivePlayerPopups)
        {
            item.Border.Width = Math.Min(item.Width, size.Width - 32);
            item.Border.MaxHeight = Math.Min(item.MaxHeight, Math.Max(120, size.Height - 100));
            // Short windows scroll the whole menu, including its footer and queue header.
            // Larger windows retain their existing inner list scrolling behavior.
            if (shortWindow && item.Border.Child is not ScrollViewer)
            {
                item.Border.Child = null;
                item.Border.Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = item.Content
                };
            }
            else if (!shortWindow && item.Border.Child is ScrollViewer scroll && !ReferenceEquals(scroll, item.Content))
            {
                scroll.Content = null;
                item.Border.Child = item.Content;
            }
            item.Popup.PlacementTarget = compact ? MoreMenuButton : item.Target;
            item.Popup.HorizontalOffset = compact ? MoreMenuButton.Width - item.Border.Width : item.Offset;
        }
    }

    private void OnMoreMenuButtonClick(object sender, RoutedEventArgs e) => TogglePopup(MorePopup);

    private void PositionCompactPopup(Popup popup)
    {
        if (MoreMenuButton.Visibility != Visibility.Visible || popup.PlacementTarget != MoreMenuButton) return;
        var origin = MoreMenuButton.TransformToAncestor(OverlayRoot).Transform(new Point());
        var width = ((FrameworkElement)popup.Child).Width;
        var left = Math.Clamp(origin.X + MoreMenuButton.ActualWidth - width, 16, lastPlayerLayoutSize.Width - width - 16);
        popup.HorizontalOffset = left - origin.X;
    }

    private void OnMoreOptionClick(object sender, RoutedEventArgs e)
    {
        var popup = ((sender as FrameworkElement)?.Tag as string) switch
        {
            "Subtitle" => SubtitlePopup, "Audio" => AudioPopup, "Quality" => QualityPopup,
            "Speed" => SpeedPopup, "Queue" => QueuePopup, "Info" => PlaybackInfoPopup,
            _ => null
        };
        if (popup is not null) TogglePopup(popup);
    }

    private sealed record ResponsivePlayerPopup(Popup Popup, Border Border, UIElement Content,
        double Width, double MaxHeight, UIElement Target, double Offset);
}
