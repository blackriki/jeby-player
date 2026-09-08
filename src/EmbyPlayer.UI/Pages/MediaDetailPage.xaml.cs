using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EmbyPlayer.UI.ViewModels;

namespace EmbyPlayer.UI.Pages;

public partial class MediaDetailPage : UserControl
{
    private const double FocusedEpisodeSectionTop = 180d;
    private const double HorizontalScrollPageRatio = 0.95d;
    private static readonly Duration HorizontalScrollAnimationDuration = TimeSpan.FromMilliseconds(220);
    private readonly ScrollScrimController? scrollScrimController;
    private readonly EpisodeTrackPositionGate episodePositionGate = new();
    private readonly Dictionary<ScrollViewer, long> horizontalScrollAnimationVersions = new();
    private readonly HashSet<ScrollViewer> animatedHorizontalScrollViewers = new();
    private MediaDetailViewModel? attachedViewModel;
    private DispatcherOperation? episodeCenteringOperation;
    private DispatcherOperation? episodeSectionPositionOperation;
    private EpisodeTrackPositionRequest? activeEpisodePositionRequest;
    private int episodeSectionPositionGeneration;
    private bool episodeSectionPositionApplied;
    private bool episodeWaitingForContainers;
    private bool numberScrollIntoViewRequested;
    private bool cardScrollIntoViewRequested;

    private static readonly DependencyProperty AnimatedHorizontalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "AnimatedHorizontalOffset",
            typeof(double),
            typeof(MediaDetailPage),
            new PropertyMetadata(0d, OnAnimatedHorizontalOffsetChanged));

    public MediaDetailPage()
    {
        InitializeComponent();
        scrollScrimController = new ScrollScrimController(FixedBackScrim);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EpisodeNumberListBox.ItemContainerGenerator.StatusChanged += OnEpisodeContainersStatusChanged;
        EpisodeCardListBox.ItemContainerGenerator.StatusChanged += OnEpisodeContainersStatusChanged;
        AttachViewModel(DataContext as MediaDetailViewModel);
        BringEpisodeSectionIntoView();
        CenterSelectedEpisodeInTracks();
        UpdateAllHorizontalRowArrowStates();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        EpisodeNumberListBox.ItemContainerGenerator.StatusChanged -= OnEpisodeContainersStatusChanged;
        EpisodeCardListBox.ItemContainerGenerator.StatusChanged -= OnEpisodeContainersStatusChanged;
        CancelAllHorizontalScrollAnimations();
        AttachViewModel(null);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
        {
            AttachViewModel(e.NewValue as MediaDetailViewModel);
        }
    }

    private void AttachViewModel(MediaDetailViewModel? viewModel)
    {
        if (ReferenceEquals(attachedViewModel, viewModel))
        {
            return;
        }

        if (attachedViewModel is not null)
        {
            attachedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        ResetEpisodePositioning();
        attachedViewModel = viewModel;
        if (attachedViewModel is not null)
        {
            attachedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaDetailViewModel.Detail))
        {
            ResetEpisodePositioning();
            if (attachedViewModel?.ShouldBringEpisodeSectionIntoView != true)
            {
                ScrollDetailToTop();
            }
        }
        else if (e.PropertyName == nameof(MediaDetailViewModel.Episodes))
        {
            AdvanceEpisodeTrackGeneration();
            BringEpisodeSectionIntoView();
            CenterSelectedEpisodeInTracks();
        }
        else if (e.PropertyName == nameof(MediaDetailViewModel.ShouldBringEpisodeSectionIntoView))
        {
            BringEpisodeSectionIntoView();
        }
        else if (e.PropertyName == nameof(MediaDetailViewModel.SelectedEpisode))
        {
            CenterSelectedEpisodeInTracks();
        }
        else if (e.PropertyName == nameof(MediaDetailViewModel.SimilarItems))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                CancelHorizontalScrollAnimation(SimilarScrollViewer);
                SimilarScrollViewer.ScrollToLeftEnd();
                UpdateAllHorizontalRowArrowStates();
            });
        }
    }

    private void OnEpisodeContainersStatusChanged(object? sender, EventArgs e)
    {
        if (!episodeWaitingForContainers
            || activeEpisodePositionRequest is not { } request
            || sender is not ItemContainerGenerator generator
            || generator.Status != GeneratorStatus.ContainersGenerated
            || !episodePositionGate.IsCurrent(request))
        {
            return;
        }

        QueueEpisodeCenteringOperation(request);
    }

    private void BringEpisodeSectionIntoView()
    {
        if (attachedViewModel?.ShouldBringEpisodeSectionIntoView != true
            || attachedViewModel.Episodes.Count is not > 0
            || !IsLoaded
            || episodeSectionPositionApplied
            || episodeSectionPositionOperation is { Status: DispatcherOperationStatus.Pending })
        {
            return;
        }

        var generation = episodeSectionPositionGeneration;
        episodeSectionPositionOperation = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            episodeSectionPositionOperation = null;
            if (generation != episodeSectionPositionGeneration
                || attachedViewModel?.ShouldBringEpisodeSectionIntoView != true
                || attachedViewModel.Episodes.Count is not > 0)
            {
                return;
            }

            var sectionTop = EpisodeSectionRoot
                .TransformToAncestor(MediaDetailScrollViewer)
                .Transform(new Point())
                .Y;
            var target = CalculateEpisodeSectionVerticalOffset(
                MediaDetailScrollViewer.VerticalOffset,
                sectionTop,
                FocusedEpisodeSectionTop,
                MediaDetailScrollViewer.ScrollableHeight);
            if (ShouldWriteScrollOffset(MediaDetailScrollViewer.VerticalOffset, target))
            {
                MediaDetailScrollViewer.ScrollToVerticalOffset(target);
            }

            episodeSectionPositionApplied = true;
        });
    }

    private void CenterSelectedEpisodeInTracks()
    {
        var episode = attachedViewModel?.SelectedEpisode;
        if (episode is null || !IsLoaded)
        {
            return;
        }

        var numberViewportWidth = GetEpisodeTrackStableWidth(EpisodeNumberListBox);
        var cardViewportWidth = GetEpisodeTrackStableWidth(EpisodeCardListBox);
        if (!episodePositionGate.TryQueue(
                episode.Id,
                numberViewportWidth,
                cardViewportWidth,
                out var request))
        {
            return;
        }

        CancelPendingEpisodeCenteringOperation();
        activeEpisodePositionRequest = request;
        episodeWaitingForContainers = false;
        numberScrollIntoViewRequested = false;
        cardScrollIntoViewRequested = false;
        QueueEpisodeCenteringOperation(request);
    }

    private void QueueEpisodeCenteringOperation(EpisodeTrackPositionRequest request)
    {
        if (episodeCenteringOperation is { Status: DispatcherOperationStatus.Pending })
        {
            return;
        }

        episodeCenteringOperation = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            episodeCenteringOperation = null;
            if (!episodePositionGate.IsCurrent(request))
            {
                return;
            }

            var selectedEpisode = attachedViewModel?.SelectedEpisode;
            if (selectedEpisode is null
                || !string.Equals(selectedEpisode.Id, request.EpisodeId, StringComparison.Ordinal))
            {
                episodePositionGate.Complete(request, applied: false);
                return;
            }

            var numberViewportWidth = GetEpisodeTrackStableWidth(EpisodeNumberListBox);
            var cardViewportWidth = GetEpisodeTrackStableWidth(EpisodeCardListBox);
            if (!episodePositionGate.Matches(
                    request,
                    selectedEpisode.Id,
                    numberViewportWidth,
                    cardViewportWidth))
            {
                episodePositionGate.Complete(request, applied: false);
                CenterSelectedEpisodeInTracks();
                return;
            }

            episodeWaitingForContainers = false;
            var numberCentered = CenterSelectedItem(
                EpisodeNumberListBox,
                selectedEpisode,
                48,
                ref numberScrollIntoViewRequested);
            var cardCentered = CenterSelectedItem(
                EpisodeCardListBox,
                selectedEpisode,
                292,
                ref cardScrollIntoViewRequested);
            if (!numberCentered || !cardCentered)
            {
                return;
            }

            episodePositionGate.Complete(request, applied: true);
            activeEpisodePositionRequest = null;
            episodeWaitingForContainers = false;
            CancelPendingEpisodeCenteringOperation();
            UpdateAllHorizontalRowArrowStates();
        });
    }

    private bool CenterSelectedItem(
        ListBox listBox,
        object episode,
        double itemWidth,
        ref bool scrollIntoViewRequested)
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(listBox);
        if (scrollViewer is null)
        {
            return false;
        }

        CancelHorizontalScrollAnimation(scrollViewer);

        var viewportWidth = scrollViewer.ActualWidth > 0
            ? scrollViewer.ActualWidth
            : listBox.ActualWidth;
        if (listBox.ItemContainerGenerator.ContainerFromItem(episode) is not FrameworkElement container)
        {
            episodeWaitingForContainers = true;
            if (!scrollIntoViewRequested)
            {
                scrollIntoViewRequested = true;
                listBox.ScrollIntoView(episode);
            }

            return false;
        }

        var itemLeft = container.TransformToAncestor(scrollViewer).Transform(new Point()).X;
        var target = CalculateCenteredHorizontalOffset(
            scrollViewer.HorizontalOffset,
            itemLeft,
            container.ActualWidth > 0 ? container.ActualWidth : itemWidth,
            viewportWidth,
            scrollViewer.ScrollableWidth);
        if (ShouldWriteScrollOffset(scrollViewer.HorizontalOffset, target))
        {
            scrollViewer.ScrollToHorizontalOffset(target);
        }

        UpdateAllHorizontalRowArrowStates();
        return true;
    }

    private static double GetEpisodeTrackStableWidth(ListBox listBox)
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(listBox);
        return scrollViewer?.ActualWidth > 0
            ? scrollViewer.ActualWidth
            : listBox.ActualWidth;
    }

    private void OnSeasonSelectorPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer seasonScrollViewer)
        {
            return;
        }

        if (ShouldScrollSeasonHorizontally(Keyboard.Modifiers, seasonScrollViewer.ScrollableWidth))
        {
            CancelHorizontalScrollAnimation(seasonScrollViewer);
            seasonScrollViewer.ScrollToHorizontalOffset(CalculateSeasonHorizontalWheelTarget(
                seasonScrollViewer.HorizontalOffset,
                e.Delta,
                seasonScrollViewer.ScrollableWidth));
            UpdateAllHorizontalRowArrowStates();
            e.Handled = true;
            return;
        }

        ForwardWheelToOuterScrollViewer(e);
    }

    private void OnEpisodeTrackPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }

        var scrollViewer = FindVisualChild<ScrollViewer>(listBox);
        if (scrollViewer is not null
            && ShouldScrollEpisodeTrackHorizontally(Keyboard.Modifiers, scrollViewer.ScrollableWidth))
        {
            CancelHorizontalScrollAnimation(scrollViewer);
            scrollViewer.ScrollToHorizontalOffset(CalculateSeasonHorizontalWheelTarget(
                scrollViewer.HorizontalOffset,
                e.Delta,
                scrollViewer.ScrollableWidth));
            UpdateAllHorizontalRowArrowStates();
            e.Handled = true;
            return;
        }

        ForwardWheelToOuterScrollViewer(e);
    }

    private void OnHorizontalRowPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        if (ShouldScrollSeasonHorizontally(Keyboard.Modifiers, scrollViewer.ScrollableWidth))
        {
            CancelHorizontalScrollAnimation(scrollViewer);
            scrollViewer.ScrollToHorizontalOffset(CalculateSeasonHorizontalWheelTarget(
                scrollViewer.HorizontalOffset,
                e.Delta,
                scrollViewer.ScrollableWidth));
            UpdateAllHorizontalRowArrowStates();
            e.Handled = true;
            return;
        }

        ForwardWheelToOuterScrollViewer(e);
    }

    private void ForwardWheelToOuterScrollViewer(MouseWheelEventArgs e)
    {
        e.Handled = true;
        MediaDetailScrollViewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent,
            Source = MediaDetailScrollViewer,
        });
    }

    private void OnEpisodeTrackRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        e.Handled = true;
    }

    private void OnEpisodeTrackSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            CenterSelectedEpisodeInTracks();
            UpdateAllHorizontalRowArrowStates();
        }
    }

    private void ScrollDetailToTop()
    {
        if (!IsLoaded)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (attachedViewModel?.ShouldBringEpisodeSectionIntoView != true)
            {
                MediaDetailScrollViewer.ScrollToTop();
            }
        });
    }

    private void OnEpisodeTrackScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateAllHorizontalRowArrowStates();
    }

    private void OnHorizontalRowLoaded(object sender, RoutedEventArgs e)
    {
        UpdateAllHorizontalRowArrowStates();
    }

    private void OnHorizontalRowScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateAllHorizontalRowArrowStates();
    }

    private void OnHorizontalRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            UpdateAllHorizontalRowArrowStates();
        }
    }

    private void OnHorizontalRowMouseEnter(object sender, MouseEventArgs e)
    {
        UpdateAllHorizontalRowArrowStates();
    }

    private void OnHorizontalRowMouseLeave(object sender, MouseEventArgs e)
    {
        UpdateAllHorizontalRowArrowStates();
    }

    private void OnHorizontalRowKeyboardFocusWithinChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        UpdateAllHorizontalRowArrowStates();
    }

    private void OnHorizontalRowLeftClick(object sender, RoutedEventArgs e)
    {
        ScrollHorizontalRow(sender, -1);
    }

    private void OnHorizontalRowRightClick(object sender, RoutedEventArgs e)
    {
        ScrollHorizontalRow(sender, 1);
    }

    private void ScrollHorizontalRow(object sender, double direction)
    {
        if (sender is not Button button
            || ResolveHorizontalScrollViewer(button.CommandParameter) is not { } scrollViewer)
        {
            return;
        }

        var targetOffset = CalculateEpisodeTrackArrowTarget(
            scrollViewer.HorizontalOffset,
            scrollViewer.ViewportWidth > 0 ? scrollViewer.ViewportWidth : scrollViewer.ActualWidth,
            direction,
            scrollViewer.ScrollableWidth);
        AnimateHorizontalOffset(scrollViewer, targetOffset);
    }

    private void OnHorizontalRowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || e.Key is not (Key.Left or Key.Right))
        {
            return;
        }

        var targetOffset = CalculateEpisodeTrackArrowTarget(
            scrollViewer.HorizontalOffset,
            scrollViewer.ViewportWidth > 0 ? scrollViewer.ViewportWidth : scrollViewer.ActualWidth,
            e.Key == Key.Left ? -1 : 1,
            scrollViewer.ScrollableWidth);
        AnimateHorizontalOffset(scrollViewer, targetOffset);
        e.Handled = true;
    }

    private void UpdateAllHorizontalRowArrowStates()
    {
        UpdateHorizontalRowArrowState(
            SeasonRow,
            SeasonScrollViewer,
            SeasonPreviousButton,
            SeasonNextButton);
        UpdateListBoxRowArrowState(
            EpisodeNumberRow,
            EpisodeNumberListBox,
            EpisodeNumberPreviousButton,
            EpisodeNumberNextButton);
        UpdateListBoxRowArrowState(
            EpisodeCardRow,
            EpisodeCardListBox,
            EpisodeCardPreviousButton,
            EpisodeCardNextButton);
        UpdateHorizontalRowArrowState(
            PeopleRow,
            PeopleScrollViewer,
            PeoplePreviousButton,
            PeopleNextButton);
        UpdateHorizontalRowArrowState(
            ArtworkRow,
            ArtworkScrollViewer,
            ArtworkPreviousButton,
            ArtworkNextButton);
        UpdateHorizontalRowArrowState(
            SimilarRow,
            SimilarScrollViewer,
            SimilarPreviousButton,
            SimilarNextButton);
    }

    private static void UpdateListBoxRowArrowState(
        FrameworkElement row,
        ListBox listBox,
        Button previousButton,
        Button nextButton)
    {
        if (FindVisualChild<ScrollViewer>(listBox) is { } scrollViewer)
        {
            UpdateHorizontalRowArrowState(row, scrollViewer, previousButton, nextButton);
            return;
        }

        HideHorizontalRowArrows(previousButton, nextButton);
    }

    private static void UpdateHorizontalRowArrowState(
        FrameworkElement row,
        ScrollViewer scrollViewer,
        Button previousButton,
        Button nextButton)
    {
        var visibility = CalculateHorizontalRowArrowVisibility(
            row.IsMouseOver || row.IsKeyboardFocusWithin,
            scrollViewer.HorizontalOffset,
            scrollViewer.ScrollableWidth);
        previousButton.Visibility = visibility.ShowPrevious
            ? Visibility.Visible
            : Visibility.Collapsed;
        nextButton.Visibility = visibility.ShowNext
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static void HideHorizontalRowArrows(Button previousButton, Button nextButton)
    {
        previousButton.Visibility = Visibility.Collapsed;
        nextButton.Visibility = Visibility.Collapsed;
    }

    private static ScrollViewer? ResolveHorizontalScrollViewer(object? source)
    {
        return source switch
        {
            ScrollViewer scrollViewer => scrollViewer,
            ListBox listBox => FindVisualChild<ScrollViewer>(listBox),
            _ => null,
        };
    }

    private void OnEpisodeTrackPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox listBox || listBox.Items.Count == 0)
        {
            return;
        }

        if (e.Key is Key.Left or Key.Right)
        {
            var direction = e.Key == Key.Left ? -1 : 1;
            var currentIndex = listBox.SelectedIndex < 0 ? 0 : listBox.SelectedIndex;
            var targetIndex = Math.Clamp(currentIndex + direction, 0, listBox.Items.Count - 1);
            listBox.SelectedIndex = targetIndex;
            if (listBox.ItemContainerGenerator.ContainerFromIndex(targetIndex) is ListBoxItem container)
            {
                if (ReferenceEquals(listBox, EpisodeCardListBox)
                    && attachedViewModel is not null
                    && FindEpisodeCardActionButton(
                        container,
                        attachedViewModel.PlayEpisodeCommand) is { } episodeCardButton)
                {
                    episodeCardButton.Focus();
                }
                else
                {
                    container.Focus();
                }
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter
            && attachedViewModel?.SelectedEpisode is { } selectedEpisode
            && attachedViewModel.PlayEpisodeCommand.CanExecute(selectedEpisode))
        {
            attachedViewModel.PlayEpisodeCommand.Execute(selectedEpisode);
            e.Handled = true;
        }
    }

    private void OnMediaDetailScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MediaDetailScrollViewer))
        {
            return;
        }

        scrollScrimController?.Update(e.VerticalOffset);
    }

    private void AnimateHorizontalOffset(ScrollViewer scrollViewer, double targetOffset)
    {
        var currentOffset = scrollViewer.HorizontalOffset;
        CancelHorizontalScrollAnimation(scrollViewer);
        if (!SystemParameters.ClientAreaAnimation
            || !ShouldWriteScrollOffset(currentOffset, targetOffset))
        {
            scrollViewer.ScrollToHorizontalOffset(targetOffset);
            UpdateAllHorizontalRowArrowStates();
            return;
        }

        var animationVersion = NextHorizontalScrollAnimationVersion(scrollViewer);
        animatedHorizontalScrollViewers.Add(scrollViewer);
        SetAnimatedHorizontalOffset(scrollViewer, currentOffset);

        var animation = new DoubleAnimation
        {
            From = currentOffset,
            To = targetOffset,
            Duration = HorizontalScrollAnimationDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        animation.Completed += (_, _) =>
        {
            if (!horizontalScrollAnimationVersions.TryGetValue(scrollViewer, out var currentVersion)
                || currentVersion != animationVersion)
            {
                return;
            }

            animatedHorizontalScrollViewers.Remove(scrollViewer);
            scrollViewer.BeginAnimation(AnimatedHorizontalOffsetProperty, null);
            SetAnimatedHorizontalOffset(scrollViewer, targetOffset);
            scrollViewer.ScrollToHorizontalOffset(targetOffset);
            UpdateAllHorizontalRowArrowStates();
        };

        scrollViewer.BeginAnimation(
            AnimatedHorizontalOffsetProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void CancelHorizontalScrollAnimation(ScrollViewer scrollViewer)
    {
        NextHorizontalScrollAnimationVersion(scrollViewer);
        if (!animatedHorizontalScrollViewers.Remove(scrollViewer))
        {
            return;
        }

        var currentOffset = scrollViewer.HorizontalOffset;
        scrollViewer.BeginAnimation(AnimatedHorizontalOffsetProperty, null);
        SetAnimatedHorizontalOffset(scrollViewer, currentOffset);
        scrollViewer.ScrollToHorizontalOffset(currentOffset);
    }

    private void CancelAllHorizontalScrollAnimations()
    {
        foreach (var scrollViewer in animatedHorizontalScrollViewers.ToArray())
        {
            CancelHorizontalScrollAnimation(scrollViewer);
        }

        horizontalScrollAnimationVersions.Clear();
    }

    private long NextHorizontalScrollAnimationVersion(ScrollViewer scrollViewer)
    {
        horizontalScrollAnimationVersions.TryGetValue(scrollViewer, out var version);
        version++;
        horizontalScrollAnimationVersions[scrollViewer] = version;
        return version;
    }

    private static void OnAnimatedHorizontalOffsetChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is ScrollViewer scrollViewer)
        {
            scrollViewer.ScrollToHorizontalOffset((double)e.NewValue);
        }
    }

    private static void SetAnimatedHorizontalOffset(DependencyObject dependencyObject, double value)
    {
        dependencyObject.SetValue(AnimatedHorizontalOffsetProperty, value);
    }

    public static bool ShouldScrollSeasonHorizontally(ModifierKeys modifiers, double scrollableWidth)
    {
        return (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift && scrollableWidth > 0;
    }

    public static bool ShouldScrollEpisodeTrackHorizontally(ModifierKeys modifiers, double scrollableWidth)
    {
        return ShouldScrollSeasonHorizontally(modifiers, scrollableWidth);
    }

    public static double CalculateSeasonHorizontalWheelTarget(
        double currentOffset,
        double wheelDelta,
        double scrollableWidth)
    {
        return Math.Clamp(currentOffset - wheelDelta, 0, Math.Max(0, scrollableWidth));
    }

    public static double CalculateEpisodeTrackArrowTarget(
        double currentOffset,
        double viewportWidth,
        double direction,
        double scrollableWidth)
    {
        var distance = Math.Max(0, viewportWidth) * HorizontalScrollPageRatio;
        return Math.Clamp(
            currentOffset + (distance * Math.Sign(direction)),
            0,
            Math.Max(0, scrollableWidth));
    }

    public static (bool ShowPrevious, bool ShowNext) CalculateHorizontalRowArrowVisibility(
        bool isRowActive,
        double horizontalOffset,
        double scrollableWidth)
    {
        if (!isRowActive || scrollableWidth <= 0.5)
        {
            return (false, false);
        }

        return (
            horizontalOffset > 0.5,
            horizontalOffset < scrollableWidth - 0.5);
    }

    public static double CalculateEpisodeSectionVerticalOffset(
        double currentOffset,
        double sectionTopInViewport,
        double desiredTop,
        double scrollableHeight)
    {
        return Math.Clamp(
            currentOffset + sectionTopInViewport - desiredTop,
            0,
            Math.Max(0, scrollableHeight));
    }

    public static double CalculateCenteredHorizontalOffset(
        double currentOffset,
        double itemLeft,
        double itemWidth,
        double viewportWidth,
        double scrollableWidth)
    {
        var target = currentOffset + itemLeft + (itemWidth / 2d) - (viewportWidth / 2d);
        return Math.Clamp(target, 0, Math.Max(0, scrollableWidth));
    }

    public static bool ShouldWriteScrollOffset(double currentOffset, double targetOffset)
    {
        return Math.Abs(currentOffset - targetOffset) >= EpisodeTrackPositionGate.DimensionTolerance;
    }

    private void AdvanceEpisodeTrackGeneration()
    {
        CancelPendingEpisodeCenteringOperation();
        activeEpisodePositionRequest = null;
        episodeWaitingForContainers = false;
        episodePositionGate.AdvanceGeneration();
    }

    private void ResetEpisodePositioning()
    {
        AdvanceEpisodeTrackGeneration();
        if (episodeSectionPositionOperation is { Status: DispatcherOperationStatus.Pending })
        {
            episodeSectionPositionOperation.Abort();
        }

        episodeSectionPositionOperation = null;
        episodeSectionPositionGeneration++;
        episodeSectionPositionApplied = false;
    }

    private void CancelPendingEpisodeCenteringOperation()
    {
        if (episodeCenteringOperation is { Status: DispatcherOperationStatus.Pending })
        {
            episodeCenteringOperation.Abort();
        }

        episodeCenteringOperation = null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    internal static Button? FindEpisodeCardActionButton(
        DependencyObject parent,
        ICommand playEpisodeCommand)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(playEpisodeCommand);
        return FindVisualChild(
            parent,
            (Button button) => ReferenceEquals(button.Command, playEpisodeCommand));
    }

    private static T? FindVisualChild<T>(DependencyObject parent, Predicate<T> predicate)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match && predicate(match))
            {
                return match;
            }

            var descendant = FindVisualChild(child, predicate);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}

internal readonly record struct EpisodeTrackPositionRequest(
    int Generation,
    long Version,
    string EpisodeId,
    double NumberViewportWidth,
    double CardViewportWidth);

internal sealed class EpisodeTrackPositionGate
{
    internal const double DimensionTolerance = 0.5d;

    private int generation;
    private long version;
    private EpisodeTrackPositionRequest? pending;
    private EpisodeTrackPositionRequest? applied;

    public void AdvanceGeneration()
    {
        generation++;
        version++;
        pending = null;
        applied = null;
    }

    public bool TryQueue(
        string episodeId,
        double numberViewportWidth,
        double cardViewportWidth,
        out EpisodeTrackPositionRequest request)
    {
        var candidate = new EpisodeTrackPositionRequest(
            generation,
            ++version,
            episodeId,
            numberViewportWidth,
            cardViewportWidth);
        if (Matches(pending, candidate) || Matches(applied, candidate))
        {
            request = default;
            return false;
        }

        pending = candidate;
        request = candidate;
        return true;
    }

    public bool IsCurrent(EpisodeTrackPositionRequest request)
    {
        return pending is { } current && current.Version == request.Version;
    }

    public bool Matches(
        EpisodeTrackPositionRequest request,
        string episodeId,
        double numberViewportWidth,
        double cardViewportWidth)
    {
        return Matches(
            request,
            new EpisodeTrackPositionRequest(
                generation,
                request.Version,
                episodeId,
                numberViewportWidth,
                cardViewportWidth));
    }

    public void Complete(EpisodeTrackPositionRequest request, bool applied)
    {
        if (!IsCurrent(request))
        {
            return;
        }

        pending = null;
        if (applied)
        {
            this.applied = request;
        }
    }

    private static bool Matches(
        EpisodeTrackPositionRequest? left,
        EpisodeTrackPositionRequest right)
    {
        return left is { } value && Matches(value, right);
    }

    private static bool Matches(
        EpisodeTrackPositionRequest left,
        EpisodeTrackPositionRequest right)
    {
        return left.Generation == right.Generation
            && string.Equals(left.EpisodeId, right.EpisodeId, StringComparison.Ordinal)
            && Math.Abs(left.NumberViewportWidth - right.NumberViewportWidth) < DimensionTolerance
            && Math.Abs(left.CardViewportWidth - right.CardViewportWidth) < DimensionTolerance;
    }
}
