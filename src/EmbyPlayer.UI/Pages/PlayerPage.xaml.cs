using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using EmbyPlayer.Core.Settings;
using EmbyPlayer.UI.Services;
using EmbyPlayer.UI.ViewModels;

namespace EmbyPlayer.UI.Pages;

public partial class PlayerPage : UserControl
{
    private bool isSeekDragging;
    private bool isVolumeDragging;
    private double volumeBeforeDrag;
    private bool isPointerOverControls;
    private bool isPointerOverPopup;
    private bool isPointerOverNextEpisodeCard;
    private bool? playbackStateBeforeFirstVideoClick;
    private bool isCursorHidden;
    private int visibilityShortcutActivityDepth;
    private readonly PhysicalPointerPositionTracker pointerPositionTracker = new();
    private readonly PlayerWindowInteraction windowInteraction = new();
    private PlayerViewModel? attachedViewModel;
    private System.Windows.Window? fullscreenWindow;
    private PlayerFullscreenController? fullscreenController;
    private System.Windows.Window? overlayOwnerWindow;
    private Application? applicationDeactivationSource;
    private PlayerControlsOverlayWindow? controlsOverlayWindow;
    private DoubleAnimation? activeControlsOpacityAnimation;
    private bool isControlsOverlayBoundsSyncPending;
    private readonly DispatcherTimer controlsHideTimer = new();
    private readonly DispatcherTimer cursorActivityTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(100)
    };

    public PlayerPage()
    {
        InitializeComponent();
        InitializeCompactPlayerLayout();
        InitializePlayerTools();
        controlsHideTimer.Tick += OnControlsHideTimerTick;
        cursorActivityTimer.Tick += OnCursorActivityTimerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        LostKeyboardFocus += OnPlayerKeyboardFocusLost;
        DataContextChanged += OnDataContextChanged;
        VideoHost.HostCreated += (_, _) => AttachVideoHost();
        VideoHost.HostDestroyed += (_, handle) => DetachVideoHost(handle);
        VideoHost.HostMouseMoved += (_, _) => ShowControlsFromPointerActivity();
        SeekSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnSeekDragStarted));
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnSeekDragCompleted));
        SeekSlider.TrackDragStarted += OnSeekDragStarted;
        SeekSlider.TrackDragCompleted += OnSeekDragCompleted;
        VolumeSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnVolumeDragStarted));
        VolumeSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnVolumeDragCompleted));
        VolumeSlider.TrackDragStarted += OnVolumeDragStarted;
        VolumeSlider.TrackDragCompleted += OnVolumeDragCompleted;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        RefreshPhysicalPointerBaseline();
        Focus();
        Keyboard.Focus(this);
        AttachVideoHost();
        if (DataContext is PlayerViewModel viewModel)
        {
            viewModel.ShowControlsOverlay();
        }

        ApplyFullscreenChromeVisibility();
        ResetControlsAutoHideTimer();
        ShowControlsOverlayWindow();
    }

    private async void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        CancelKeyboardSeek();
        HideSeekThumbnail();
        CancelVideoSpeedPress();
        controlsHideTimer.Stop();
        CancelSeekDrag();
        ClosePopups();
        CloseControlsOverlayWindow();
        StopCursorActivityMonitor();
        ShowMouseCursor();
        ExitFullscreen();
        ExitMiniPlayer(restoreFullscreen: false);
        if (playerOriginalTopmost is { } originalTopmost && playerPinOwner is { } owner)
            owner.Topmost = originalTopmost;
        if (DataContext is PlayerViewModel viewModel)
        {
            viewModel.DetachVideoHost(VideoHost.HostHandle);
            await viewModel.ReleasePlayerAsync().ConfigureAwait(true);
        }

        SetPlayerCaptionState(isVisible: true, isFullscreen: false);
        AttachViewModel(null);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        CancelKeyboardSeek();
        HideSeekThumbnail();
        CancelVideoSpeedPress();
        AttachViewModel(e.NewValue as PlayerViewModel);
        if (controlsOverlayWindow is not null)
        {
            controlsOverlayWindow.DataContext = e.NewValue;
            controlsOverlayWindow.OverlayView.DataContext = e.NewValue;
        }
        AttachVideoHost();
        ApplyFullscreenChromeVisibility();
    }

    private void AttachViewModel(PlayerViewModel? viewModel)
    {
        if (ReferenceEquals(attachedViewModel, viewModel))
        {
            return;
        }

        if (attachedViewModel is not null)
        {
            attachedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        attachedViewModel = viewModel;
        if (attachedViewModel is not null)
        {
            attachedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (keyboardSeekViewModel is not null && sender is PlayerViewModel seekingVm
            && (e.PropertyName == nameof(PlayerViewModel.PlaybackInfo) || !seekingVm.CanSeek)) CancelKeyboardSeek();
        if (e.PropertyName == nameof(PlayerViewModel.PlaybackInfo)
            || sender is PlayerViewModel previewVm && (!previewVm.CanSeek || !previewVm.IsControlsOverlayVisible || previewVm.HasError)) HideSeekThumbnail();
        if (sender is PlayerViewModel current && (!current.IsPlaying || current.IsLoading || current.HasError))
            CancelVideoSpeedPress();
        if (e.PropertyName is nameof(PlayerViewModel.IsControlsOverlayVisible)
            or nameof(PlayerViewModel.IsFullscreen)
            or nameof(PlayerViewModel.IsPlaying)
            or nameof(PlayerViewModel.IsPaused)
            or nameof(PlayerViewModel.IsSeeking)
            or nameof(PlayerViewModel.IsLoading)
            or nameof(PlayerViewModel.HasError)
            or nameof(PlayerViewModel.ControlsHideSeconds))
        {
            if (sender is PlayerViewModel viewModel
                && (viewModel.IsControlsOverlayVisible
                    || !viewModel.IsPlaying
                    || viewModel.IsPaused
                    || viewModel.IsLoading
                    || viewModel.HasError))
            {
                RestoreMouseCursor();
            }

            ApplyFullscreenChromeVisibility();

            if (e.PropertyName is nameof(PlayerViewModel.IsPlaying)
                or nameof(PlayerViewModel.IsPaused)
                or nameof(PlayerViewModel.ControlsHideSeconds))
            {
                ResetControlsAutoHideTimer();
            }
        }
    }

    private void AttachVideoHost()
    {
        if (DataContext is PlayerViewModel viewModel && VideoHost.HostHandle != IntPtr.Zero)
        {
            _ = viewModel.AttachVideoHostAsync(VideoHost.HostHandle);
        }
    }

    private void DetachVideoHost(IntPtr hostHandle)
    {
        if (DataContext is PlayerViewModel viewModel)
        {
            viewModel.DetachVideoHost(hostHandle);
        }
    }

    private void OnSeekDragStarted(object sender, DragStartedEventArgs e)
    {
        CancelKeyboardSeek();
        if (isSeekDragging)
        {
            return;
        }

        isSeekDragging = true;
        if (DataContext is PlayerViewModel viewModel)
        {
            viewModel.BeginSeekDrag();
        }
    }

    private async void OnSeekDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!isSeekDragging)
        {
            return;
        }

        isSeekDragging = false;
        if (DataContext is PlayerViewModel viewModel)
        {
            if (e.Canceled)
            {
                viewModel.CancelSeekDrag();
                return;
            }

            await viewModel.CompleteSeekDragAsync(SeekSlider.Value).ConfigureAwait(true);
        }
    }

    private void CancelSeekDrag()
    {
        if (!isSeekDragging)
        {
            return;
        }

        isSeekDragging = false;
        if (DataContext is PlayerViewModel viewModel)
        {
            viewModel.CancelSeekDrag();
        }
    }

    private void OnVolumeDragStarted(object sender, DragStartedEventArgs e)
    {
        volumeBeforeDrag = VolumeSlider.Value;
        isVolumeDragging = true;
    }

    private async void OnVolumeDragCompleted(object sender, DragCompletedEventArgs e)
    {
        isVolumeDragging = false;
        if (!TryCompleteVolumeDrag(VolumeSlider, volumeBeforeDrag, e.Canceled, out var valueToCommit))
        {
            return;
        }

        if (DataContext is PlayerViewModel viewModel)
        {
            await viewModel.SetVolumeAsync(valueToCommit).ConfigureAwait(true);
        }
    }

    internal static bool TryCompleteVolumeDrag(
        Slider slider,
        double valueBeforeDrag,
        bool canceled,
        out double valueToCommit)
    {
        if (!canceled)
        {
            valueToCommit = slider.Value;
            return true;
        }

        slider.SetCurrentValue(Slider.ValueProperty, valueBeforeDrag);
        valueToCommit = valueBeforeDrag;
        return false;
    }

    private void OnAudioMenuButtonClick(object sender, System.Windows.RoutedEventArgs e)
    {
        TogglePopup(AudioPopup);
    }

    private void OnSubtitleMenuButtonClick(object sender, System.Windows.RoutedEventArgs e)
    {
        TogglePopup(SubtitlePopup);
    }

    private void OnVolumeButtonClick(object sender, RoutedEventArgs e)
    {
        TogglePopup(VolumePopup);
    }

    private async void OnPlaybackInfoButtonClick(object sender, RoutedEventArgs e)
    {
        TogglePopup(PlaybackInfoPopup);
        if (PlaybackInfoPopup.IsOpen && DataContext is PlayerViewModel viewModel)
        {
            await viewModel.RefreshTechnicalInfoAsync().ConfigureAwait(true);
        }
    }

    private void OnQualityMenuButtonClick(object sender, RoutedEventArgs e) => TogglePopup(QualityPopup);
    private void OnQueueMenuButtonClick(object sender, RoutedEventArgs e) => TogglePopup(QueuePopup);

    private void OnPopupTriggerMouseDown(object sender, MouseButtonEventArgs e)
    {
        var isCurrentPopupOpen =
            ReferenceEquals(sender, VolumeButton) && VolumePopup.IsOpen
            || ReferenceEquals(sender, SubtitleMenuButton) && SubtitlePopup.IsOpen
            || ReferenceEquals(sender, AudioMenuButton) && AudioPopup.IsOpen
            || ReferenceEquals(sender, PlaybackInfoButton) && PlaybackInfoPopup.IsOpen
            || ReferenceEquals(sender, QualityMenuButton) && QualityPopup.IsOpen
            || ReferenceEquals(sender, QueueMenuButton) && QueuePopup.IsOpen
            || ReferenceEquals(sender, SpeedMenuButton) && SpeedPopup.IsOpen
            || ReferenceEquals(sender, MoreMenuButton) && MorePopup.IsOpen;
        if (!isCurrentPopupOpen)
        {
            return;
        }

        ClosePopups();
        e.Handled = true;
    }

    private void OnOverlayRootPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ShouldCloseVolumePopupBeforeOverlayInput(
                VolumePopup.IsOpen,
                e.OriginalSource as DependencyObject,
                VolumeButton,
                VolumePopup.Child))
        {
            ClosePopups();
        }
    }

    internal static bool ShouldCloseVolumePopupBeforeOverlayInput(
        bool isVolumePopupOpen,
        DependencyObject? originalSource,
        Button volumeButton,
        UIElement? volumePopupChild)
    {
        return isVolumePopupOpen
            && !IsInputDescendantOrSelf(originalSource, volumeButton)
            && !IsInputDescendantOrSelf(originalSource, volumePopupChild);
    }

    private static bool IsInputDescendantOrSelf(
        DependencyObject? source,
        DependencyObject? ancestor)
    {
        if (ancestor is null)
        {
            return false;
        }

        for (var current = source; current is not null; current = GetInputParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetInputParent(DependencyObject source)
    {
        if (source is FrameworkContentElement frameworkContentElement)
        {
            return frameworkContentElement.Parent
                ?? ContentOperations.GetParent(frameworkContentElement)
                ?? LogicalTreeHelper.GetParent(frameworkContentElement);
        }

        if (source is ContentElement contentElement)
        {
            return ContentOperations.GetParent(contentElement)
                ?? LogicalTreeHelper.GetParent(contentElement);
        }

        if (source is Visual or Visual3D)
        {
            return VisualTreeHelper.GetParent(source)
                ?? LogicalTreeHelper.GetParent(source);
        }

        return LogicalTreeHelper.GetParent(source);
    }

    private void OnFullscreenButtonClick(object sender, System.Windows.RoutedEventArgs e)
    {
        ToggleFullscreen();
        Dispatcher.BeginInvoke(FocusOverlaySurface, DispatcherPriority.Input);
    }

    private async void OnAudioTrackButtonClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PlayerAudioTrackViewModel track }
            && DataContext is PlayerViewModel viewModel)
        {
            await viewModel.SelectAudioTrackAsync(track).ConfigureAwait(true);
            ClosePopups();
        }
    }

    private async void OnSubtitleTrackButtonClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PlayerSubtitleTrackViewModel track }
            && DataContext is PlayerViewModel viewModel)
        {
            await viewModel.SelectSubtitleTrackAsync(track).ConfigureAwait(true);
            ClosePopups();
        }
    }

    private void OnVolumeSliderValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        // Track and thumb manipulation preview locally; MPV and preferences commit once on release.
    }

    private void OnSeekSliderValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (DataContext is PlayerViewModel viewModel)
        {
            viewModel.UpdateSeekDrag(e.NewValue);
        }
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsSeekModifierKey(e.Key == Key.System ? e.SystemKey : e.Key)) CancelKeyboardSeek();
        if (e.Key == Key.Escape) CancelKeyboardSeek();
        if (e.Key == Key.Escape && IsAnyPopupOpen)
        {
            e.Handled = true;
            ClosePopups();
            return;
        }

        if (e.Key == Key.Escape && fullscreenController?.IsFullscreen == true)
        {
            e.Handled = true;
            ExitFullscreen();
            return;
        }

        if (DataContext is not PlayerViewModel viewModel
            || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox)
        {
            return;
        }
        if (IsAnyPopupOpen && new[] { QueuePopup, SubtitlePopup, AudioPopup, QualityPopup, PlaybackInfoPopup, VolumePopup, SpeedPopup, MorePopup }
            .Any(popup => popup.Child?.IsKeyboardFocusWithin == true)) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        var action = viewModel.ResolveShortcut(key, modifiers);
        if (action is null && !(key == Key.Escape && modifiers == ModifierKeys.None)) return;
        e.Handled = true;
        if (action is PlayerShortcutAction.SeekBackward or PlayerShortcutAction.SeekForward)
        {
            BeginKeyboardSeek(key, action.Value, e.IsRepeat);
            return;
        }
        CancelKeyboardSeek();
        if (action == PlayerShortcutAction.ToggleFullscreen)
        {
            ToggleFullscreen();
            return;
        }
        var shouldRevealControls = ShouldRevealControlsForShortcut(action);
        if (shouldRevealControls)
        {
            BeginVisibilityShortcutActivity(viewModel);
        }

        try
        {
            await viewModel.HandleShortcutKeyAsync(key, modifiers).ConfigureAwait(true);
        }
        catch
        {
            // Shortcut handling must not crash playback UI.
        }
        finally
        {
            if (shouldRevealControls)
            {
                EndVisibilityShortcutActivity();
            }
        }
    }

    private void OnPlayerMouseMove(object sender, MouseEventArgs e)
    {
        ObserveVideoSpeedPress(e);
        ShowControlsFromPointerActivity();
    }

    private void OnPlayerMouseLeave(object sender, MouseEventArgs e)
    {
        RefreshPhysicalPointerBaseline();
        RestoreMouseCursor();
        ResetControlsAutoHideTimer();
    }

    private void ShowControlsFromPointerActivity(bool force = false)
    {
        var pointerMoved = TryObservePhysicalPointerMovement();
        if (!force && !pointerMoved)
        {
            return;
        }

        ShowControlsForPointerActivity();
    }

    private void ShowControlsForPointerActivity()
    {
        TraceFullscreen("MouseMove detected");
        StopCursorActivityMonitor();
        ShowMouseCursor();
        if (DataContext is PlayerViewModel viewModel)
        {
            viewModel.ShowControlsOverlay();
        }

        ApplyFullscreenChromeVisibility();
        ResetControlsAutoHideTimer();
    }

    private void OnVideoAreaMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && (FindAncestor<Button>(source) is not null
                || FindAncestor<Slider>(source) is not null))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            CancelVideoSpeedPress();
            var stateBeforeDoubleClick = playbackStateBeforeFirstVideoClick;
            playbackStateBeforeFirstVideoClick = null;
            e.Handled = true;
            ToggleFullscreen();
            Dispatcher.BeginInvoke(FocusOverlaySurface, DispatcherPriority.Input);
            if (stateBeforeDoubleClick.HasValue)
            {
                _ = RestorePlaybackStateAfterDoubleClickAsync(stateBeforeDoubleClick.Value);
            }
            return;
        }

        ShowControlsFromPointerActivity(force: true);
        FocusOverlaySurface();
        if (sender is UIElement surface)
            BeginVideoSpeedPress(surface, e.GetPosition(surface));
        e.Handled = true;
    }

    private async Task RestorePlaybackStateAfterDoubleClickAsync(bool wasPaused)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (DataContext is not PlayerViewModel viewModel)
            {
                return;
            }

            if (viewModel.IsPaused != wasPaused)
            {
                var command = wasPaused ? viewModel.PauseCommand : viewModel.PlayCommand;
                if (command.CanExecute(null))
                {
                    command.Execute(null);
                    return;
                }
            }
            else if (attempt >= 8)
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(true);
        }
    }

    private void OnControlsHideTimerTick(object? sender, EventArgs e)
    {
        controlsHideTimer.Stop();
        isPointerOverControls = TopChrome.IsMouseOver
            || BottomChrome.IsMouseOver
            || CenterPlayButton.IsMouseOver;
        if (ShouldDeferControlAutoHide())
        {
            ResetControlsAutoHideTimer();
            return;
        }

        if (DataContext is PlayerViewModel viewModel)
        {
            TraceFullscreen(
                $"AutoHideTimer tick IsFullscreen={viewModel.IsFullscreen} IsPaused={viewModel.IsPaused} IsSeeking={viewModel.IsSeeking} IsLoading={viewModel.IsLoading} HasError={viewModel.HasError}");
            viewModel.HideControlsOverlayIfAllowed();
            ApplyFullscreenChromeVisibility();
            TraceFullscreen($"CanAutoHideFullscreenChrome result hidden={!viewModel.IsControlsOverlayVisible}");
            if (!viewModel.IsControlsOverlayVisible && IsPointerWithinPlayer())
            {
                HideMouseCursor();
                StartCursorActivityMonitor();
                return;
            }
        }

        StopCursorActivityMonitor();
        ShowMouseCursor();
    }

    private void OnCursorActivityTimerTick(object? sender, EventArgs e)
    {
        if (DataContext is not PlayerViewModel
            {
                IsPlaying: true,
                IsPaused: false,
                IsControlsOverlayVisible: false
            }
            || !IsPointerWithinPlayer())
        {
            RestoreMouseCursor();
            return;
        }

        if (!TryObservePhysicalPointerMovement())
        {
            return;
        }

        TraceFullscreen("Native cursor activity detected");
        ShowControlsForPointerActivity();
    }

    private void BeginVisibilityShortcutActivity(PlayerViewModel viewModel)
    {
        visibilityShortcutActivityDepth++;
        if (visibilityShortcutActivityDepth > 1)
        {
            return;
        }

        controlsHideTimer.Stop();
        RestoreMouseCursor();
        viewModel.ShowControlsOverlay();
    }

    private void EndVisibilityShortcutActivity()
    {
        if (visibilityShortcutActivityDepth == 0)
        {
            return;
        }

        visibilityShortcutActivityDepth--;
        if (visibilityShortcutActivityDepth == 0 && IsLoaded)
        {
            ResetControlsAutoHideTimer();
        }
    }

    private void ResetControlsAutoHideTimer()
    {
        controlsHideTimer.Stop();
        StopCursorActivityMonitor();
        if (!IsLoaded)
        {
            return;
        }

        if (visibilityShortcutActivityDepth > 0)
        {
            return;
        }

        if (DataContext is PlayerViewModel { IsPlaying: true, IsPaused: false } viewModel)
        {
            controlsHideTimer.Interval = GetControlsAutoHideInterval(viewModel.ControlsHideSeconds);
            controlsHideTimer.Start();
            TraceFullscreen("AutoHideTimer started/restarted");
        }
    }

    internal static TimeSpan GetControlsAutoHideInterval(int controlsHideSeconds) =>
        TimeSpan.FromSeconds(controlsHideSeconds);

    internal static bool ShouldRevealControlsForShortcut(PlayerShortcutAction? action) =>
        action is PlayerShortcutAction.TogglePlayPause
            or PlayerShortcutAction.SeekBackward
            or PlayerShortcutAction.SeekForward
            or PlayerShortcutAction.VolumeUp
            or PlayerShortcutAction.VolumeDown
            or PlayerShortcutAction.NextSubtitle
            or PlayerShortcutAction.SubtitleEarlier
            or PlayerShortcutAction.SubtitleLater
            or PlayerShortcutAction.NextAudioTrack;

    private void ApplyFullscreenChromeVisibility()
    {
        UpdateWindowInteractionState();
        if (DataContext is not PlayerViewModel viewModel)
        {
            SetPlayerCaptionState(isVisible: true, isFullscreen: false);
            activeControlsOpacityAnimation = null;
            ControlsLayer.BeginAnimation(OpacityProperty, null);
            ControlsLayer.Visibility = Visibility.Visible;
            ControlsLayer.Opacity = 1;
            ControlsLayer.IsHitTestVisible = true;
            return;
        }

        var shouldShowChrome = viewModel.IsControlsOverlayVisible || viewModel.IsPaused || IsAnyPopupOpen;
        SetPlayerCaptionState(shouldShowChrome, viewModel.IsFullscreen);
        ControlsLayer.Visibility = Visibility.Visible;
        if (shouldShowChrome)
        {
            if (!ControlsLayer.IsHitTestVisible)
            {
                BeginControlsOpacityAnimation(shouldShow: true);
            }
        }
        else if (ControlsLayer.IsHitTestVisible)
        {
            BeginControlsOpacityAnimation(shouldShow: false);
        }

        TraceFullscreen($"Chrome applied visible={shouldShowChrome} opacity={ControlsLayer.Opacity}");
    }

    private void BeginControlsOpacityAnimation(bool shouldShow)
    {
        ControlsLayer.IsHitTestVisible = shouldShow;
        var animation = new DoubleAnimation(
            ControlsLayer.Opacity,
            shouldShow ? 1 : 0,
            TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        activeControlsOpacityAnimation = animation;
        animation.Completed += (_, _) => CompleteControlsOpacityAnimation(animation);
        ControlsLayer.BeginAnimation(OpacityProperty, animation);
    }

    private void CompleteControlsOpacityAnimation(DoubleAnimation animation)
    {
        if (!ReferenceEquals(activeControlsOpacityAnimation, animation))
        {
            return;
        }

        activeControlsOpacityAnimation = null;
        ApplyPendingControlsOverlayBoundsSync();
    }

    private bool IsAnyPopupOpen => VolumePopup.IsOpen || SubtitlePopup.IsOpen || AudioPopup.IsOpen || PlaybackInfoPopup.IsOpen || QualityPopup.IsOpen || QueuePopup.IsOpen || SpeedPopup.IsOpen || MorePopup.IsOpen;

    private bool ShouldDeferControlAutoHide() =>
        IsAnyPopupOpen
        || isPointerOverPopup
        || isPointerOverControls
        || isPointerOverNextEpisodeCard
        || isSeekDragging
        || isVolumeDragging;

    private void OnNextEpisodeCardMouseEnter(object sender, MouseEventArgs e)
    {
        isPointerOverNextEpisodeCard = true;
        controlsHideTimer.Stop();
    }

    private void OnNextEpisodeCardMouseLeave(object sender, MouseEventArgs e)
    {
        isPointerOverNextEpisodeCard = false;
        ResetControlsAutoHideTimer();
    }

    private void OnNextEpisodeActivationValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not ProgressBar progressBar || !progressBar.IsLoaded || e.NewValue <= e.OldValue)
        {
            return;
        }

        progressBar.BeginAnimation(
            RangeBase.ValueProperty,
            new DoubleAnimation(e.OldValue, e.NewValue, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
    }

    private void TogglePopup(Popup popup)
    {
        CancelKeyboardSeek();
        var shouldOpen = !popup.IsOpen;
        ClosePopups();
        if (shouldOpen)
        {
            PositionCompactPopup(popup);
            popup.IsOpen = true;
        }
    }

    private void ClosePopups()
    {
        VolumePopup.IsOpen = false;
        SubtitlePopup.IsOpen = false;
        AudioPopup.IsOpen = false;
        PlaybackInfoPopup.IsOpen = false;
        QualityPopup.IsOpen = false;
        QueuePopup.IsOpen = false;
        SpeedPopup.IsOpen = false;
        MorePopup.IsOpen = false;
        isPointerOverPopup = false;
        if (DataContext is PlayerViewModel viewModel)
        {
            viewModel.SetTrackMenuOpen(false);
        }
    }

    private void OnPopupOpened(object? sender, EventArgs e)
    {
        CancelKeyboardSeek();
        CancelVideoSpeedPress();
        controlsHideTimer.Stop();
        ShowMouseCursor();
        if (DataContext is PlayerViewModel viewModel)
        {
            viewModel.SetTrackMenuOpen(true);
            viewModel.ShowControlsOverlay();
        }

        ApplyFullscreenChromeVisibility();
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        isPointerOverPopup = false;
        if (!IsAnyPopupOpen && DataContext is PlayerViewModel viewModel)
        {
            viewModel.SetTrackMenuOpen(false);
        }

        ResetControlsAutoHideTimer();
        Dispatcher.BeginInvoke(
            () =>
            {
                if (!IsAnyPopupOpen)
                {
                    FocusOverlaySurface();
                }
            },
            DispatcherPriority.Input);
    }

    private void OnPopupMouseEnter(object sender, MouseEventArgs e)
    {
        isPointerOverPopup = true;
        controlsHideTimer.Stop();
    }

    private void OnPopupMouseLeave(object sender, MouseEventArgs e)
    {
        isPointerOverPopup = false;
        ResetControlsAutoHideTimer();
    }

    private void OnControlsMouseEnter(object sender, MouseEventArgs e)
    {
        isPointerOverControls = true;
        controlsHideTimer.Stop();
    }

    private void OnControlsMouseLeave(object sender, MouseEventArgs e)
    {
        isPointerOverControls = false;
        ResetControlsAutoHideTimer();
    }

    private void OnRootPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null)
        {
            return;
        }

        FocusOverlaySurface();
    }

    private void OnCloseWindowButtonClick(object sender, RoutedEventArgs e)
    {
        GetPlayerWindowHost()?.ClosePlayerWindow();
    }

    private void OnMinimizeWindowButtonClick(object sender, RoutedEventArgs e)
    {
        _ = TryMinimizePlayerWindow(GetPlayerWindowHost());
    }

    internal static bool TryMinimizePlayerWindow(IPlayerWindowHost? host)
    {
        if (host is null)
        {
            return false;
        }

        host.MinimizePlayerWindow();
        return true;
    }

    private void OnTopChromePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveWindowChromeSource(e.OriginalSource)
            || overlayOwnerWindow is null)
        {
            return;
        }

        if (windowInteraction.Begin(
            overlayOwnerWindow,
            PlayerWindowHitTarget.Caption,
            IsPlayerFullscreen,
            e.ClickCount))
        {
            e.Handled = true;
        }
    }

    private void OnResizeHitZoneMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }
            || !Enum.TryParse<PlayerWindowHitTarget>(tag, out var target)
            || overlayOwnerWindow is null)
        {
            return;
        }

        if (windowInteraction.Begin(overlayOwnerWindow, target, IsPlayerFullscreen, e.ClickCount))
        {
            e.Handled = true;
        }
    }

    private static bool IsInteractiveWindowChromeSource(object? originalSource)
    {
        var current = originalSource as DependencyObject;
        while (current is not null)
        {
            if (current is ButtonBase or Thumb or Slider)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private bool IsPlayerFullscreen =>
        fullscreenController?.IsFullscreen == true
        || DataContext is PlayerViewModel { IsFullscreen: true };

    private void UpdateWindowInteractionState()
    {
        WindowResizeHitZones.IsHitTestVisible = overlayOwnerWindow is { } owner
            && PlayerWindowInteraction.CanBegin(
                owner.WindowState,
                owner.ResizeMode,
                PlayerWindowHitTarget.Left,
                IsPlayerFullscreen);
    }

    private void FocusOverlaySurface()
    {
        OverlayRoot.Focus();
        Keyboard.Focus(OverlayRoot);
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private void ToggleFullscreen()
    {
        CancelKeyboardSeek();
        if (IsMiniPlayer) ExitMiniPlayer(restoreFullscreen: false);
        var controller = GetFullscreenController();
        if (controller is null)
        {
            return;
        }

        controller.Toggle();
        overlayOwnerWindow?.UpdateLayout();
        TraceFullscreen(controller.IsFullscreen ? "EnterFullscreen" : "ExitFullscreen");
        SyncFullscreenState(controller.IsFullscreen);
    }

    private void ExitFullscreen()
    {
        CancelKeyboardSeek();
        fullscreenController?.Exit();
        overlayOwnerWindow?.UpdateLayout();
        TraceFullscreen("ExitFullscreen");
        StopCursorActivityMonitor();
        ShowMouseCursor();
        SyncFullscreenState(false);
    }

    private void SyncFullscreenState(bool isFullscreen)
    {
        PinPlayerButton.IsEnabled = !isFullscreen;
        if (DataContext is PlayerViewModel viewModel)
        {
            viewModel.SetFullscreen(isFullscreen);
        }

        if (!isFullscreen)
        {
            ShowMouseCursor();
        }

        ResetControlsAutoHideTimer();
        RequestControlsOverlayBoundsSync();
    }

    private void ShowControlsOverlayWindow()
    {
        if (controlsOverlayWindow is not null || Window.GetWindow(this) is not { } owner)
        {
            return;
        }

        if (OverlayRoot.Parent is Panel parent)
        {
            parent.Children.Remove(OverlayRoot);
        }

        overlayOwnerWindow = owner;
        controlsOverlayWindow = new PlayerControlsOverlayWindow
        {
            Owner = owner
        };
        controlsOverlayWindow.Attach(OverlayRoot, DataContext);
        UpdateWindowInteractionState();
        controlsOverlayWindow.PreviewKeyDown += OnPreviewKeyDown;
        controlsOverlayWindow.PreviewKeyUp += OnPreviewKeyUp;
        controlsOverlayWindow.LostKeyboardFocus += OnPlayerKeyboardFocusLost;
        owner.LocationChanged += OnOverlayOwnerBoundsChanged;
        owner.SizeChanged += OnOverlayOwnerBoundsChanged;
        owner.StateChanged += OnOverlayOwnerStateChanged;
        owner.Closed += OnOverlayOwnerClosed;
        SubscribeToApplicationDeactivation();
        VideoHost.SizeChanged += OnVideoHostSizeChanged;
        SyncControlsOverlayBounds();
        controlsOverlayWindow.Show();
    }

    private void CloseControlsOverlayWindow()
    {
        UnsubscribeFromApplicationDeactivation();
        ClosePopups();
        if (overlayOwnerWindow is not null)
        {
            overlayOwnerWindow.LocationChanged -= OnOverlayOwnerBoundsChanged;
            overlayOwnerWindow.SizeChanged -= OnOverlayOwnerBoundsChanged;
            overlayOwnerWindow.StateChanged -= OnOverlayOwnerStateChanged;
            overlayOwnerWindow.Closed -= OnOverlayOwnerClosed;
        }

        VideoHost.SizeChanged -= OnVideoHostSizeChanged;
        activeControlsOpacityAnimation = null;
        isControlsOverlayBoundsSyncPending = false;
        ControlsLayer.BeginAnimation(OpacityProperty, null);
        ControlsLayer.Opacity = 1;
        ControlsLayer.IsHitTestVisible = true;
        if (controlsOverlayWindow is not null)
        {
            controlsOverlayWindow.PreviewKeyDown -= OnPreviewKeyDown;
            controlsOverlayWindow.PreviewKeyUp -= OnPreviewKeyUp;
            controlsOverlayWindow.LostKeyboardFocus -= OnPlayerKeyboardFocusLost;
            var overlay = controlsOverlayWindow.Detach();
            controlsOverlayWindow.Close();
            if (overlay is not null && !PlayerSurface.Children.Contains(overlay))
            {
                PlayerSurface.Children.Add(overlay);
            }
        }

        controlsOverlayWindow = null;
        overlayOwnerWindow = null;
        WindowResizeHitZones.IsHitTestVisible = false;
    }

    private void OnOverlayOwnerBoundsChanged(object? sender, EventArgs e) =>
        RequestControlsOverlayBoundsSync();

    private void OnOverlayOwnerStateChanged(object? sender, EventArgs e)
    {
        if (controlsOverlayWindow is null || overlayOwnerWindow is null)
        {
            return;
        }

        UpdateWindowInteractionState();
        if (overlayOwnerWindow.WindowState == WindowState.Minimized)
        {
            RestoreMouseCursor();
            ClosePopups();
            controlsOverlayWindow.Hide();
            return;
        }

        if (!controlsOverlayWindow.IsVisible)
        {
            isControlsOverlayBoundsSyncPending = false;
            SyncControlsOverlayBounds();
            controlsOverlayWindow.Show();
            return;
        }

        RequestControlsOverlayBoundsSync();
    }

    private void SubscribeToApplicationDeactivation()
    {
        if (applicationDeactivationSource is not null || Application.Current is not { } application)
        {
            return;
        }

        applicationDeactivationSource = application;
        applicationDeactivationSource.Deactivated += OnApplicationDeactivated;
    }

    private void UnsubscribeFromApplicationDeactivation()
    {
        if (applicationDeactivationSource is null)
        {
            return;
        }

        applicationDeactivationSource.Deactivated -= OnApplicationDeactivated;
        applicationDeactivationSource = null;
    }

    private void OnApplicationDeactivated(object? sender, EventArgs e)
    {
        CancelKeyboardSeek();
        CancelVideoSpeedPress();
        RestoreMouseCursor();
        ClosePopups();
    }

    private void OnOverlayOwnerClosed(object? sender, EventArgs e)
    {
        RestoreMouseCursor();
        CloseControlsOverlayWindow();
    }

    private void OnVideoHostSizeChanged(object sender, SizeChangedEventArgs e) =>
        RequestControlsOverlayBoundsSync();

    private void RequestControlsOverlayBoundsSync()
    {
        if (controlsOverlayWindow is null || isControlsOverlayBoundsSyncPending)
        {
            return;
        }

        isControlsOverlayBoundsSyncPending = true;
        if (activeControlsOpacityAnimation is null)
        {
            Dispatcher.BeginInvoke(ApplyPendingControlsOverlayBoundsSync, DispatcherPriority.Loaded);
        }
    }

    private void ApplyPendingControlsOverlayBoundsSync()
    {
        if (!isControlsOverlayBoundsSyncPending || activeControlsOpacityAnimation is not null)
        {
            return;
        }

        isControlsOverlayBoundsSyncPending = false;
        SyncControlsOverlayBounds();
    }

    private void SyncControlsOverlayBounds()
    {
        if (controlsOverlayWindow is null
            || overlayOwnerWindow is null
            || overlayOwnerWindow.WindowState == WindowState.Minimized
            || !VideoHost.IsVisible
            || VideoHost.ActualWidth <= 0
            || VideoHost.ActualHeight <= 0)
        {
            return;
        }

        var source = PresentationSource.FromVisual(VideoHost);
        if (source?.CompositionTarget is null)
        {
            return;
        }

        var screenPixels = VideoHost.PointToScreen(new Point(0, 0));
        UpdateCompactPlayerLayout();
        var screenDip = source.CompositionTarget.TransformFromDevice.Transform(screenPixels);
        var videoPixels = source.CompositionTarget.TransformToDevice.Transform(
            new Point(VideoHost.ActualWidth, VideoHost.ActualHeight));
        NextEpisodeCard.Width = videoPixels.X < 1500 ? 344 : 376;
        controlsOverlayWindow.SetBounds(
            new Rect(screenDip.X, screenDip.Y, VideoHost.ActualWidth, VideoHost.ActualHeight));
    }

    private void HideMouseCursor()
    {
        if (isCursorHidden)
        {
            return;
        }

        Mouse.OverrideCursor = Cursors.None;
        isCursorHidden = true;
        TraceFullscreen("Cursor hidden");
    }

    private void StartCursorActivityMonitor()
    {
        RefreshPhysicalPointerBaseline();

        if (!cursorActivityTimer.IsEnabled)
        {
            cursorActivityTimer.Start();
            TraceFullscreen("CursorActivityTimer started");
        }
    }

    private void StopCursorActivityMonitor()
    {
        if (cursorActivityTimer.IsEnabled)
        {
            cursorActivityTimer.Stop();
            TraceFullscreen("CursorActivityTimer stopped");
        }
    }

    private void ShowMouseCursor()
    {
        if (!isCursorHidden)
        {
            return;
        }

        Mouse.OverrideCursor = null;
        isCursorHidden = false;
        TraceFullscreen("Cursor shown");
    }

    private void RestoreMouseCursor()
    {
        StopCursorActivityMonitor();
        ShowMouseCursor();
    }

    private bool IsPointerWithinPlayer() => PlayerSurface.IsMouseOver || OverlayRoot.IsMouseOver;

    private static void TraceFullscreen(string message)
    {
        Debug.WriteLine($"[PlayerFullscreen] {message}");
    }

    private bool TryObservePhysicalPointerMovement()
    {
        if (!TryGetPhysicalCursorPosition(out var point))
        {
            return false;
        }

        return pointerPositionTracker.Observe(point);
    }

    private void RefreshPhysicalPointerBaseline()
    {
        if (TryGetPhysicalCursorPosition(out var point))
        {
            pointerPositionTracker.Refresh(point);
        }
    }

    private static bool TryGetPhysicalCursorPosition(out PhysicalPixelPoint point)
    {
        if (GetPhysicalCursorPos(out var nativePoint))
        {
            point = new PhysicalPixelPoint(nativePoint.X, nativePoint.Y);
            return true;
        }

        point = default;
        return false;
    }

    private PlayerFullscreenController? GetFullscreenController()
    {
        var window = System.Windows.Window.GetWindow(this);
        if (window is null)
        {
            return null;
        }

        if (!ReferenceEquals(fullscreenWindow, window))
        {
            fullscreenController?.Exit();
            fullscreenWindow = window;
            var windowAdapter = new WpfPlayerFullscreenWindow(window);
            fullscreenController = new PlayerFullscreenController(windowAdapter);
        }

        return fullscreenController;
    }

    private void SetPlayerCaptionState(bool isVisible, bool isFullscreen)
    {
        _ = TrySetPlayerCaptionState(
            GetPlayerOwnerWindow() as IPlayerCaptionHost,
            isVisible,
            isFullscreen);
    }

    internal static bool TrySetPlayerCaptionState(
        IPlayerCaptionHost? host,
        bool isVisible,
        bool isFullscreen)
    {
        if (host is null)
        {
            return false;
        }

        host.SetPlayerCaptionState(isVisible, isFullscreen);
        return true;
    }

    private IPlayerWindowHost? GetPlayerWindowHost() =>
        GetPlayerOwnerWindow() as IPlayerWindowHost;

    private System.Windows.Window? GetPlayerOwnerWindow() =>
        System.Windows.Window.GetWindow(this);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalCursorPos(out NativePoint point);


    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
