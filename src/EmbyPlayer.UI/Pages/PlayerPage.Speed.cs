using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EmbyPlayer.UI.ViewModels;

namespace EmbyPlayer.UI.Pages;

public partial class PlayerPage
{
    private readonly DispatcherTimer videoPressTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private UIElement? videoPressSurface;
    private PlayerViewModel? videoPressViewModel;
    private Point videoPressOrigin;
    private bool videoPressBecameHold;

    private void OnSpeedMenuButtonClick(object sender, RoutedEventArgs e)
    {
        CancelVideoSpeedPress();
        TogglePopup(SpeedPopup);
    }

    private async void OnPlaybackSpeedClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PlayerSpeedOption option } && DataContext is PlayerViewModel viewModel)
        {
            await viewModel.SetPlaybackSpeedAsync(option.Speed);
            SpeedPopup.IsOpen = false;
        }
    }

    private void BeginVideoSpeedPress(UIElement surface, Point position)
    {
        CancelVideoSpeedPress();
        if (DataContext is not PlayerViewModel viewModel || !viewModel.TogglePlayPauseCommand.CanExecute(null)) return;
        if (!surface.CaptureMouse()) return;
        TrackAcceptedVideoPress(surface, position, viewModel);
    }

    private void TrackAcceptedVideoPress(UIElement surface, Point position, PlayerViewModel viewModel)
    {
        videoPressViewModel = viewModel;
        videoPressSurface = surface;
        videoPressOrigin = position;
        videoPressBecameHold = false;
        playbackStateBeforeFirstVideoClick = viewModel.IsPaused;
        videoPressTimer.Tick -= OnVideoPressTimerTick;
        videoPressTimer.Tick += OnVideoPressTimerTick;
        videoPressTimer.Start();
    }

    private async void OnVideoPressTimerTick(object? sender, EventArgs e)
    {
        await HandleVideoPressElapsedAsync(Mouse.LeftButton == MouseButtonState.Pressed);
    }

    private async Task HandleVideoPressElapsedAsync(bool leftButtonPressed)
    {
        videoPressTimer.Stop();
        if (!leftButtonPressed || videoPressViewModel is not { } viewModel)
        {
            CancelVideoSpeedPress();
            return;
        }
        videoPressBecameHold = true;
        await viewModel.BeginTemporaryDoubleSpeedAsync();
    }

    private void ObserveVideoSpeedPress(MouseEventArgs e)
    {
        if (videoPressSurface is not { } surface) return;
        var distance = e.GetPosition(surface) - videoPressOrigin;
        if (Mouse.LeftButton != MouseButtonState.Pressed || distance.Length > 12)
            CancelVideoSpeedPress();
    }

    private void OnVideoAreaMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (videoPressViewModel is not { } viewModel) return;
        var click = !videoPressBecameHold;
        CancelVideoSpeedPress();
        if (click && ReferenceEquals(DataContext, viewModel) && viewModel.TogglePlayPauseCommand.CanExecute(null))
            viewModel.TogglePlayPauseCommand.Execute(null);
        e.Handled = true;
    }

    private void OnVideoSurfaceLostMouseCapture(object sender, MouseEventArgs e) => CancelVideoSpeedPress();

    private void CancelVideoSpeedPress()
    {
        videoPressTimer.Stop();
        var viewModel = videoPressViewModel;
        var surface = videoPressSurface;
        videoPressViewModel = null;
        videoPressSurface = null;
        videoPressBecameHold = false;
        if (surface is not null && ReferenceEquals(Mouse.Captured, surface)) surface.ReleaseMouseCapture();
        if (viewModel is not null) _ = viewModel.EndTemporaryDoubleSpeedAsync();
    }
}
