using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EmbyPlayer.UI.ViewModels;

namespace EmbyPlayer.UI.Pages;

public partial class HomePage : UserControl
{
    private const double CompactHeaderThreshold = 760;
    private const double LibraryArrowColumnWidth = 52;
    private const double LibraryCardGap = 12;
    private const double LibraryCardMaxWidth = 300;
    private const double LibraryCardMinWidth = 176;
    private const int LibraryMaxVisibleCount = 5;
    private const double ScrollPageRatio = 0.95;
    private const double VerticalWheelStep = 120;
    private static readonly Duration ScrollAnimationDuration = TimeSpan.FromMilliseconds(220);
    private static readonly Duration VerticalScrollAnimationDuration = TimeSpan.FromMilliseconds(190);
    private readonly DispatcherTimer heroRotationTimer;
    private readonly HeroRotationInteractionState heroRotationInteraction = new();
    private HomeViewModel? attachedHomeViewModel;
    private Window? hostWindow;
    private bool isPageLoaded;
    private bool isUpdatingLibraryLayout;
    private int libraryCapacity = LibraryMaxVisibleCount;
    private double libraryCardWidth = LibraryCardMinWidth;
    private int libraryFirstVisibleIndex;
    private bool libraryHasOverflow;
    private long libraryLayoutVersion;
    private bool isVerticalScrollAnimating;
    private double verticalScrollTarget;
    private long verticalScrollAnimationVersion;
    private InputSource lastInputSource;

    public static readonly DependencyProperty AnimatedHorizontalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "AnimatedHorizontalOffset",
            typeof(double),
            typeof(HomePage),
            new PropertyMetadata(0d, OnAnimatedHorizontalOffsetChanged));

    private static readonly DependencyProperty AnimatedVerticalOffsetProperty =
        DependencyProperty.Register(
            "AnimatedVerticalOffset",
            typeof(double),
            typeof(HomePage),
            new PropertyMetadata(0d, OnAnimatedVerticalOffsetChanged));

    public static readonly DependencyProperty LibraryCardWidthProperty =
        DependencyProperty.Register(
            nameof(LibraryCardWidth),
            typeof(double),
            typeof(HomePage),
            new PropertyMetadata(LibraryCardMinWidth));

    public double LibraryCardWidth
    {
        get => (double)GetValue(LibraryCardWidthProperty);
        private set => SetValue(LibraryCardWidthProperty, value);
    }

    public HomePage()
    {
        InitializeComponent();

        heroRotationTimer = new DispatcherTimer
        {
            Interval = HeroRotationInteractionState.NormalDelay
        };
        heroRotationTimer.Tick += OnHeroRotationTimerTick;

        Loaded += OnHomePageLoaded;
        Unloaded += OnHomePageUnloaded;
        DataContextChanged += OnHomePageDataContextChanged;
        IsVisibleChanged += OnHomePageIsVisibleChanged;
        SizeChanged += OnHomePageSizeChanged;
        PreviewKeyDown += OnHomePagePreviewKeyDown;
        PreviewMouseDown += OnHomePagePreviewMouseDown;
    }

    private void OnHomePageLoaded(object sender, RoutedEventArgs e)
    {
        if (isPageLoaded)
        {
            return;
        }

        isPageLoaded = true;
        AttachHomeViewModel(DataContext as HomeViewModel);
        AttachHostWindow(Window.GetWindow(this));
        ApplyResponsiveLayout(ActualWidth);
        Dispatcher.BeginInvoke(UpdateLibraryLayout, DispatcherPriority.Loaded);
        ScheduleNextHero(HeroRotationInteractionState.NormalDelay);
    }

    private void OnHomePageUnloaded(object sender, RoutedEventArgs e)
    {
        isPageLoaded = false;
        ApplyHeroRotationDirective(heroRotationInteraction.OnUnloaded());
        AttachHomeViewModel(null);
        AttachHostWindow(null);
        StopHeroAnimation();
        StopVerticalScrollAnimation();
    }

    private void OnHomePageDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (isPageLoaded)
        {
            AttachHomeViewModel(e.NewValue as HomeViewModel);
            ScheduleNextHero(HeroRotationInteractionState.NormalDelay);
        }
    }

    private void OnHomePageIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            ScheduleNextHero(HeroRotationInteractionState.NormalDelay);
        }
        else
        {
            heroRotationTimer.Stop();
        }
    }

    private void OnHomePageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
        UpdateLibraryLayout();
    }

    private void ApplyResponsiveLayout(double width)
    {
        var useCompactLayout = UsesCompactHeaderLayout(width);
        Grid.SetRow(HomeHeaderActionsPanel, useCompactLayout ? 1 : 0);
        Grid.SetColumn(HomeHeaderActionsPanel, useCompactLayout ? 0 : 1);
        Grid.SetColumnSpan(HomeHeaderActionsPanel, useCompactLayout ? 2 : 1);
        HomeHeaderActionsPanel.HorizontalAlignment = useCompactLayout
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        HomeHeaderActionsPanel.Margin = useCompactLayout
            ? new Thickness(0, 14, 0, 0)
            : new Thickness(0);
        HomeHeaderActionsPanel.MaxWidth = double.PositiveInfinity;
        HomeSearchTextBox.Width = useCompactLayout
            ? CalculateCompactSearchWidth(width)
            : Math.Clamp(width - 720, 180, 320);
        HomeContentPanel.Margin = width < 900
            ? new Thickness(24, 20, 24, 32)
            : new Thickness(40, 24, 40, 36);
        HeroTitleTextBlock.FontSize = useCompactLayout ? 42 : 48;
    }

    public static bool UsesCompactHeaderLayout(double width)
    {
        return width > 0 && width < CompactHeaderThreshold;
    }

    public static double CalculateCompactSearchWidth(double width)
    {
        return Math.Clamp(width - 320, 260, 360);
    }

    private void AttachHomeViewModel(HomeViewModel? viewModel)
    {
        if (ReferenceEquals(attachedHomeViewModel, viewModel))
        {
            return;
        }

        if (attachedHomeViewModel is not null)
        {
            attachedHomeViewModel.PropertyChanged -= OnHomeViewModelPropertyChanged;
        }

        attachedHomeViewModel = viewModel;
        if (attachedHomeViewModel is not null)
        {
            attachedHomeViewModel.PropertyChanged += OnHomeViewModelPropertyChanged;
        }
    }

    private void AttachHostWindow(Window? window)
    {
        if (ReferenceEquals(hostWindow, window))
        {
            return;
        }

        if (hostWindow is not null)
        {
            hostWindow.Activated -= OnHostWindowActivated;
            hostWindow.Deactivated -= OnHostWindowDeactivated;
        }

        hostWindow = window;
        if (hostWindow is not null)
        {
            hostWindow.Activated += OnHostWindowActivated;
            hostWindow.Deactivated += OnHostWindowDeactivated;
        }
    }

    private void OnHomeViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnHomeViewModelPropertyChanged(sender, e));
            return;
        }

        if (e.PropertyName == nameof(HomeViewModel.Libraries))
        {
            Dispatcher.BeginInvoke(UpdateLibraryLayout, DispatcherPriority.Loaded);
            return;
        }

        if (e.PropertyName == nameof(HomeViewModel.HeroCard))
        {
            AnimateHeroVisual();
            return;
        }

        if (e.PropertyName is nameof(HomeViewModel.HeroItems)
            or nameof(HomeViewModel.HasMultipleHeroCards)
            or nameof(HomeViewModel.IsContentVisible))
        {
            ScheduleNextHero(HeroRotationInteractionState.NormalDelay);
        }
    }

    private void OnHostWindowActivated(object? sender, EventArgs e)
    {
        ApplyHeroRotationDirective(heroRotationInteraction.OnWindowActivated());
    }

    private void OnHostWindowDeactivated(object? sender, EventArgs e)
    {
        ApplyHeroRotationDirective(heroRotationInteraction.OnWindowDeactivated());
    }

    private void OnHeroPointerEntered(object sender, MouseEventArgs e)
    {
        ApplyHeroRotationDirective(heroRotationInteraction.OnPointerEntered());
    }

    private void OnHeroPointerExited(object sender, MouseEventArgs e)
    {
        ApplyHeroRotationDirective(heroRotationInteraction.OnPointerExited());
    }

    private void OnHeroKeyboardFocusWithinChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (HeroCarouselRoot.IsKeyboardFocusWithin)
        {
            if (lastInputSource == InputSource.Keyboard)
            {
                ApplyHeroRotationDirective(heroRotationInteraction.OnKeyboardInteraction());
            }
        }
        else
        {
            ApplyHeroRotationDirective(heroRotationInteraction.OnKeyboardFocusLeft());
        }
    }

    private void OnHeroPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        lastInputSource = InputSource.Pointer;
        ApplyHeroRotationDirective(heroRotationInteraction.OnPointerManualInteraction());
    }

    private void OnHeroPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (attachedHomeViewModel is null)
        {
            return;
        }

        lastInputSource = InputSource.Keyboard;
        ApplyHeroRotationDirective(heroRotationInteraction.OnKeyboardInteraction());

        if (e.Key == Key.Left && attachedHomeViewModel.PreviousHeroCommand.CanExecute(null))
        {
            attachedHomeViewModel.PreviousHeroCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right && attachedHomeViewModel.NextHeroCommand.CanExecute(null))
        {
            attachedHomeViewModel.NextHeroCommand.Execute(null);
            e.Handled = true;
            return;
        }

    }

    private void OnHeroRotationTimerTick(object? sender, EventArgs e)
    {
        heroRotationTimer.Stop();
        if (!CanScheduleHeroRotation() || attachedHomeViewModel is null)
        {
            return;
        }

        attachedHomeViewModel.NextHeroCommand.Execute(null);
        ScheduleNextHero(HeroRotationInteractionState.NormalDelay);
    }

    private void ApplyHeroRotationDirective(HeroRotationDirective directive)
    {
        if (directive.Kind == HeroRotationDirectiveKind.Stop)
        {
            heroRotationTimer.Stop();
            return;
        }

        if (directive.Kind == HeroRotationDirectiveKind.Schedule)
        {
            ScheduleNextHero(directive.Delay);
        }
    }

    private void ScheduleNextHero(TimeSpan delay)
    {
        heroRotationTimer.Stop();
        if (!CanScheduleHeroRotation())
        {
            return;
        }

        heroRotationTimer.Interval = delay;
        heroRotationTimer.Start();
    }

    private bool CanScheduleHeroRotation()
    {
        return CanScheduleHeroRotation(
            isPageLoaded,
            IsVisible,
            attachedHomeViewModel?.IsContentVisible == true,
            attachedHomeViewModel?.HasMultipleHeroCards == true,
            HeroCarouselRoot.IsMouseOver,
            heroRotationInteraction.HasKeyboardInteraction,
            hostWindow?.IsActive != false);
    }

    public static bool CanScheduleHeroRotation(
        bool isLoaded,
        bool isVisible,
        bool isContentVisible,
        bool hasMultipleItems,
        bool isPointerOver,
        bool hasKeyboardInteraction,
        bool isWindowActive)
    {
        return isLoaded
            && isVisible
            && isContentVisible
            && hasMultipleItems
            && !isPointerOver
            && !hasKeyboardInteraction
            && isWindowActive;
    }

    private void OnHomePagePreviewKeyDown(object sender, KeyEventArgs e)
    {
        StopVerticalScrollAnimation();
        lastInputSource = InputSource.Keyboard;
    }

    private void OnHomePagePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        StopVerticalScrollAnimation();
        lastInputSource = InputSource.Pointer;
    }

    private void AnimateHeroVisual()
    {
        if (!isPageLoaded || !SystemParameters.ClientAreaAnimation)
        {
            StopHeroAnimation();
            return;
        }

        HeroVisual.BeginAnimation(OpacityProperty, null);
        HeroVisual.Opacity = 1;
        HeroVisual.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation
            {
                From = 0.35,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void StopHeroAnimation()
    {
        HeroVisual.BeginAnimation(OpacityProperty, null);
        HeroVisual.Opacity = 1;
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

    private static void OnAnimatedVerticalOffsetChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is HomePage page)
        {
            page.HomePageScrollViewer.ScrollToVerticalOffset((double)e.NewValue);
        }
    }

    private static double GetAnimatedHorizontalOffset(DependencyObject dependencyObject)
    {
        return (double)dependencyObject.GetValue(AnimatedHorizontalOffsetProperty);
    }

    private static void SetAnimatedHorizontalOffset(
        DependencyObject dependencyObject,
        double value)
    {
        dependencyObject.SetValue(AnimatedHorizontalOffsetProperty, value);
    }

    private void OnContinueWatchingRowMouseEnter(object sender, MouseEventArgs e)
    {
        UpdateArrowVisibility(
            ContinueWatchingRow,
            ContinueWatchingScrollViewer,
            ContinueWatchingLeftButton,
            ContinueWatchingRightButton);
    }

    private void OnLibraryRowMouseEnter(object sender, MouseEventArgs e)
    {
        UpdateLibraryArrowVisibility();
    }

    private void OnLibraryRowMouseLeave(object sender, MouseEventArgs e)
    {
        UpdateLibraryArrowVisibility();
    }

    private void OnHorizontalRowKeyboardFocusWithinChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, LibraryRow))
        {
            UpdateLibraryArrowVisibility();
            return;
        }

        if (ReferenceEquals(sender, ContinueWatchingRow))
        {
            UpdateArrowVisibility(
                ContinueWatchingRow,
                ContinueWatchingScrollViewer,
                ContinueWatchingLeftButton,
                ContinueWatchingRightButton);
            return;
        }

        if (ReferenceEquals(sender, RecentlyAddedRow))
        {
            UpdateArrowVisibility(
                RecentlyAddedRow,
                RecentlyAddedScrollViewer,
                RecentlyAddedLeftButton,
                RecentlyAddedRightButton);
            return;
        }

        if (TryGetCategoryRowParts(sender, out var row, out var scrollViewer, out var leftButton, out var rightButton))
        {
            UpdateArrowVisibility(row, scrollViewer, leftButton, rightButton);
        }
    }

    private void OnContinueWatchingRowMouseLeave(object sender, MouseEventArgs e)
    {
        HideArrows(ContinueWatchingLeftButton, ContinueWatchingRightButton);
    }

    private void OnRecentlyAddedRowMouseEnter(object sender, MouseEventArgs e)
    {
        UpdateArrowVisibility(
            RecentlyAddedRow,
            RecentlyAddedScrollViewer,
            RecentlyAddedLeftButton,
            RecentlyAddedRightButton);
    }

    private void OnRecentlyAddedRowMouseLeave(object sender, MouseEventArgs e)
    {
        HideArrows(RecentlyAddedLeftButton, RecentlyAddedRightButton);
    }

    private void OnCategoryRowMouseEnter(object sender, MouseEventArgs e)
    {
        if (TryGetCategoryRowParts(sender, out var row, out var scrollViewer, out var leftButton, out var rightButton))
        {
            UpdateArrowVisibility(row, scrollViewer, leftButton, rightButton);
        }
    }

    private void OnCategoryRowMouseLeave(object sender, MouseEventArgs e)
    {
        if (TryGetCategoryRowParts(sender, out _, out _, out var leftButton, out var rightButton))
        {
            HideArrows(leftButton, rightButton);
        }
    }

    private void OnHorizontalRowScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateMatchingRow(sender);
    }

    private void OnHorizontalRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ReferenceEquals(sender, LibraryScrollViewer))
        {
            UpdateLibraryLayout();
            return;
        }

        UpdateMatchingRow(sender);
    }

    private void OnHorizontalRowPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        var shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (shiftPressed)
        {
            if (!ShouldRouteWheelHorizontally(
                    shiftPressed,
                    scrollViewer.HorizontalOffset,
                    e.Delta,
                    scrollViewer.ScrollableWidth))
            {
                return;
            }

            scrollViewer.ScrollToHorizontalOffset(CalculateHorizontalWheelTarget(
                scrollViewer.HorizontalOffset,
                e.Delta,
                scrollViewer.ScrollableWidth));
            e.Handled = true;
        }
    }

    private void OnHomePagePreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (!ShouldRouteWheelVertically(shiftPressed, e.Delta))
        {
            return;
        }

        var targetOffset = CalculateAccumulatedVerticalWheelTarget(
            HomePageScrollViewer.VerticalOffset,
            verticalScrollTarget,
            isVerticalScrollAnimating,
            e.Delta,
            HomePageScrollViewer.ScrollableHeight);
        AnimateVerticalOffset(targetOffset);
        e.Handled = true;
    }

    private void AnimateVerticalOffset(double targetOffset)
    {
        var currentOffset = HomePageScrollViewer.VerticalOffset;
        verticalScrollTarget = targetOffset;

        if (!SystemParameters.ClientAreaAnimation
            || Math.Abs(targetOffset - currentOffset) <= 0.5)
        {
            StopVerticalScrollAnimation();
            HomePageScrollViewer.ScrollToVerticalOffset(targetOffset);
            return;
        }

        isVerticalScrollAnimating = true;
        var animationVersion = ++verticalScrollAnimationVersion;
        SetValue(AnimatedVerticalOffsetProperty, currentOffset);

        var animation = new DoubleAnimation
        {
            From = currentOffset,
            To = targetOffset,
            Duration = VerticalScrollAnimationDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) =>
        {
            if (animationVersion != verticalScrollAnimationVersion)
            {
                return;
            }

            SetValue(AnimatedVerticalOffsetProperty, targetOffset);
            BeginAnimation(AnimatedVerticalOffsetProperty, null);
            verticalScrollTarget = targetOffset;
            isVerticalScrollAnimating = false;
        };

        BeginAnimation(
            AnimatedVerticalOffsetProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void StopVerticalScrollAnimation()
    {
        if (!isVerticalScrollAnimating)
        {
            return;
        }

        verticalScrollAnimationVersion++;
        var currentOffset = HomePageScrollViewer.VerticalOffset;
        SetValue(AnimatedVerticalOffsetProperty, currentOffset);
        BeginAnimation(AnimatedVerticalOffsetProperty, null);
        verticalScrollTarget = currentOffset;
        isVerticalScrollAnimating = false;
    }

    private void OnHorizontalRowLoaded(object sender, RoutedEventArgs e)
    {
        UpdateMatchingRow(sender);
    }

    private void OnHorizontalRowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || e.Key is not (Key.Left or Key.Right))
        {
            return;
        }

        if (ReferenceEquals(scrollViewer, LibraryScrollViewer))
        {
            ScrollLibrary(e.Key == Key.Left ? -1 : 1);
        }
        else
        {
            ScrollRow(scrollViewer, e.Key == Key.Left ? -1 : 1);
        }
        e.Handled = true;
    }

    private void OnContinueWatchingLeftClick(object sender, RoutedEventArgs e)
    {
        ScrollRow(ContinueWatchingScrollViewer, -1);
    }

    private void OnLibraryLeftClick(object sender, RoutedEventArgs e)
    {
        ScrollLibrary(-1);
    }

    private void OnLibraryRightClick(object sender, RoutedEventArgs e)
    {
        ScrollLibrary(1);
    }

    private void UpdateLibraryLayout()
    {
        if (!isPageLoaded || isUpdatingLibraryLayout || LibraryRow.ActualWidth <= 0)
        {
            return;
        }

        isUpdatingLibraryLayout = true;
        try
        {
            var itemCount = attachedHomeViewModel?.Libraries.Count ?? 0;
            var previousFirstVisibleIndex = CalculateLibraryFirstVisibleIndex(
                LibraryScrollViewer.HorizontalOffset,
                libraryCardWidth);
            var rowWidth = Math.Max(0, LibraryRow.ActualWidth);
            var capacity = CalculateLibraryCapacity(rowWidth);
            var hasOverflow = HasLibraryOverflow(itemCount, capacity);
            var availableWidth = rowWidth;

            if (hasOverflow)
            {
                availableWidth = Math.Max(0, rowWidth - (LibraryArrowColumnWidth * 2));
                capacity = CalculateLibraryCapacity(availableWidth);
                hasOverflow = HasLibraryOverflow(itemCount, capacity);
            }

            libraryCapacity = capacity;
            libraryHasOverflow = hasOverflow;
            libraryCardWidth = CalculateLibraryCardWidth(availableWidth, itemCount, capacity);
            libraryFirstVisibleIndex = ClampLibraryFirstVisibleIndex(
                previousFirstVisibleIndex,
                itemCount,
                capacity);

            LibraryLeftArrowColumn.Width = hasOverflow
                ? new GridLength(LibraryArrowColumnWidth)
                : new GridLength(0);
            LibraryRightArrowColumn.Width = hasOverflow
                ? new GridLength(LibraryArrowColumnWidth)
                : new GridLength(0);
            LibraryItemsControl.Margin = hasOverflow
                ? new Thickness(-6, 0, 18, 0)
                : new Thickness(-6, 0, -6, 0);
            LibraryCardWidth = libraryCardWidth;

            var layoutVersion = ++libraryLayoutVersion;
            Dispatcher.BeginInvoke(
                () => RestoreLibraryPosition(layoutVersion),
                DispatcherPriority.Loaded);
        }
        finally
        {
            isUpdatingLibraryLayout = false;
        }
    }

    private void RestoreLibraryPosition(long layoutVersion)
    {
        if (!isPageLoaded || layoutVersion != libraryLayoutVersion)
        {
            return;
        }

        var itemCount = attachedHomeViewModel?.Libraries.Count ?? 0;
        libraryFirstVisibleIndex = ClampLibraryFirstVisibleIndex(
            libraryFirstVisibleIndex,
            itemCount,
            libraryCapacity);
        var targetOffset = libraryFirstVisibleIndex * (libraryCardWidth + LibraryCardGap);

        LibraryScrollViewer.BeginAnimation(AnimatedHorizontalOffsetProperty, null);
        SetAnimatedHorizontalOffset(LibraryScrollViewer, targetOffset);
        LibraryScrollViewer.ScrollToHorizontalOffset(targetOffset);
        UpdateLibraryArrowVisibility();
    }

    private void UpdateLibraryArrowVisibility()
    {
        var itemCount = attachedHomeViewModel?.Libraries.Count ?? 0;
        if (!libraryHasOverflow || itemCount <= libraryCapacity)
        {
            HideArrows(LibraryLeftButton, LibraryRightButton);
            return;
        }

        libraryFirstVisibleIndex = ClampLibraryFirstVisibleIndex(
            CalculateLibraryFirstVisibleIndex(
                LibraryScrollViewer.HorizontalOffset,
                libraryCardWidth),
            itemCount,
            libraryCapacity);

        LibraryLeftButton.Visibility = CanScrollLibraryLeft(libraryFirstVisibleIndex)
            ? Visibility.Visible
            : Visibility.Collapsed;
        LibraryRightButton.Visibility = CanScrollLibraryRight(
                itemCount,
                libraryCapacity,
                libraryFirstVisibleIndex)
            ? Visibility.Visible
            : Visibility.Collapsed;

        var activeOpacity = LibraryRow.IsMouseOver || LibraryRow.IsKeyboardFocusWithin
            ? 1
            : 0.55;
        LibraryLeftButton.Opacity = activeOpacity;
        LibraryRightButton.Opacity = activeOpacity;
    }

    private void ScrollLibrary(int direction)
    {
        var itemCount = attachedHomeViewModel?.Libraries.Count ?? 0;
        if (!libraryHasOverflow || itemCount <= libraryCapacity || direction == 0)
        {
            return;
        }

        var currentIndex = ClampLibraryFirstVisibleIndex(
            CalculateLibraryFirstVisibleIndex(
                LibraryScrollViewer.HorizontalOffset,
                libraryCardWidth),
            itemCount,
            libraryCapacity);
        var targetIndex = ClampLibraryFirstVisibleIndex(
            currentIndex + (Math.Sign(direction) * CalculateLibraryScrollStep(libraryCapacity)),
            itemCount,
            libraryCapacity);
        if (targetIndex == currentIndex)
        {
            UpdateLibraryArrowVisibility();
            return;
        }

        libraryFirstVisibleIndex = targetIndex;
        AnimateHorizontalOffset(
            LibraryScrollViewer,
            targetIndex * (libraryCardWidth + LibraryCardGap));
    }

    private void OnContinueWatchingRightClick(object sender, RoutedEventArgs e)
    {
        ScrollRow(ContinueWatchingScrollViewer, 1);
    }

    private void OnRecentlyAddedLeftClick(object sender, RoutedEventArgs e)
    {
        ScrollRow(RecentlyAddedScrollViewer, -1);
    }

    private void OnRecentlyAddedRightClick(object sender, RoutedEventArgs e)
    {
        ScrollRow(RecentlyAddedScrollViewer, 1);
    }

    private void OnCategoryLeftClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScrollViewer scrollViewer })
        {
            ScrollRow(scrollViewer, -1);
        }
    }

    private void OnCategoryRightClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScrollViewer scrollViewer })
        {
            ScrollRow(scrollViewer, 1);
        }
    }

    private void UpdateMatchingRow(object sender)
    {
        if (ReferenceEquals(sender, LibraryScrollViewer))
        {
            UpdateLibraryArrowVisibility();
            return;
        }

        if (ReferenceEquals(sender, ContinueWatchingScrollViewer))
        {
            UpdateArrowVisibility(
                ContinueWatchingRow,
                ContinueWatchingScrollViewer,
                ContinueWatchingLeftButton,
                ContinueWatchingRightButton);
            return;
        }

        if (ReferenceEquals(sender, RecentlyAddedScrollViewer))
        {
            UpdateArrowVisibility(
                RecentlyAddedRow,
                RecentlyAddedScrollViewer,
                RecentlyAddedLeftButton,
                RecentlyAddedRightButton);
            return;
        }

        if (TryGetCategoryRowParts(sender, out var row, out var scrollViewer, out var leftButton, out var rightButton))
        {
            UpdateArrowVisibility(row, scrollViewer, leftButton, rightButton);
        }
    }

    private static bool TryGetCategoryRowParts(
        object source,
        out FrameworkElement row,
        out ScrollViewer scrollViewer,
        out Button leftButton,
        out Button rightButton)
    {
        var categoryRow = source as FrameworkElement;
        if (categoryRow is null || !Equals(categoryRow.Tag, "HomeMediaSectionRow"))
        {
            categoryRow = source is DependencyObject sourceElement
                ? VisualTreeHelper.GetParent(sourceElement) as FrameworkElement
                : null;
        }

        if (categoryRow is not null && Equals(categoryRow.Tag, "HomeMediaSectionRow"))
        {
            ScrollViewer? categoryScrollViewer = null;
            Button? categoryLeftButton = null;
            Button? categoryRightButton = null;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(categoryRow); index++)
            {
                switch (VisualTreeHelper.GetChild(categoryRow, index))
                {
                    case ScrollViewer childScrollViewer:
                        categoryScrollViewer = childScrollViewer;
                        break;
                    case Button childButton when childButton.HorizontalAlignment == HorizontalAlignment.Left:
                        categoryLeftButton = childButton;
                        break;
                    case Button childButton when childButton.HorizontalAlignment == HorizontalAlignment.Right:
                        categoryRightButton = childButton;
                        break;
                }
            }

            if (categoryScrollViewer is not null
                && categoryLeftButton is not null
                && categoryRightButton is not null)
            {
                row = categoryRow;
                scrollViewer = categoryScrollViewer;
                leftButton = categoryLeftButton;
                rightButton = categoryRightButton;
                return true;
            }
        }

        row = null!;
        scrollViewer = null!;
        leftButton = null!;
        rightButton = null!;
        return false;
    }

    private static void UpdateArrowVisibility(
        FrameworkElement row,
        ScrollViewer scrollViewer,
        Button leftButton,
        Button rightButton)
    {
        if ((!row.IsMouseOver && !row.IsKeyboardFocusWithin) || scrollViewer.ScrollableWidth <= 0)
        {
            HideArrows(leftButton, rightButton);
            return;
        }

        leftButton.Visibility = scrollViewer.HorizontalOffset > 0.5
            ? Visibility.Visible
            : Visibility.Collapsed;
        rightButton.Visibility = scrollViewer.HorizontalOffset < scrollViewer.ScrollableWidth - 0.5
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static void HideArrows(Button leftButton, Button rightButton)
    {
        leftButton.Visibility = Visibility.Collapsed;
        rightButton.Visibility = Visibility.Collapsed;
    }

    private static void ScrollRow(ScrollViewer scrollViewer, double direction)
    {
        if (scrollViewer.ScrollableWidth <= 0)
        {
            return;
        }

        var distance = CalculateHorizontalScrollDistance(
            scrollViewer.ViewportWidth,
            scrollViewer.ActualWidth);
        if (distance <= 0)
        {
            return;
        }

        var targetOffset = Math.Clamp(
            scrollViewer.HorizontalOffset + (distance * direction),
            0,
            scrollViewer.ScrollableWidth);

        AnimateHorizontalOffset(scrollViewer, targetOffset);
    }

    private static void AnimateHorizontalOffset(ScrollViewer scrollViewer, double targetOffset)
    {
        scrollViewer.BeginAnimation(AnimatedHorizontalOffsetProperty, null);
        SetAnimatedHorizontalOffset(scrollViewer, scrollViewer.HorizontalOffset);

        var animation = new DoubleAnimation
        {
            From = scrollViewer.HorizontalOffset,
            To = targetOffset,
            Duration = ScrollAnimationDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            SetAnimatedHorizontalOffset(scrollViewer, targetOffset);
            scrollViewer.ScrollToHorizontalOffset(targetOffset);
        };

        scrollViewer.BeginAnimation(
            AnimatedHorizontalOffsetProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    public static double CalculateHorizontalScrollDistance(
        double viewportWidth,
        double actualWidth)
    {
        var baseWidth = viewportWidth > 0 ? viewportWidth : actualWidth;
        return baseWidth > 0 ? baseWidth * ScrollPageRatio : 0;
    }

    public static int CalculateLibraryCapacity(double availableWidth)
    {
        var safeWidth = double.IsFinite(availableWidth)
            ? Math.Max(0, availableWidth)
            : 0;
        var capacity = (int)Math.Floor(
            (safeWidth + LibraryCardGap) / (LibraryCardMinWidth + LibraryCardGap));
        return Math.Clamp(capacity, 2, LibraryMaxVisibleCount);
    }

    public static int CalculateLibraryVisibleCount(int totalItemCount, int capacity)
    {
        return Math.Min(
            Math.Max(0, totalItemCount),
            Math.Clamp(capacity, 2, LibraryMaxVisibleCount));
    }

    public static bool HasLibraryOverflow(int totalItemCount, int capacity)
    {
        return Math.Max(0, totalItemCount)
            > Math.Clamp(capacity, 2, LibraryMaxVisibleCount);
    }

    public static int CalculateLibraryScrollStep(int capacity)
    {
        return Math.Max(1, Math.Clamp(capacity, 2, LibraryMaxVisibleCount) - 1);
    }

    public static double CalculateLibraryCardWidth(
        double availableWidth,
        int totalItemCount,
        int capacity)
    {
        var visibleCount = CalculateLibraryVisibleCount(totalItemCount, capacity);
        if (visibleCount == 0)
        {
            return LibraryCardMinWidth;
        }

        var safeWidth = double.IsFinite(availableWidth)
            ? Math.Max(0, availableWidth)
            : 0;
        var cardWidth = Math.Max(
            LibraryCardMinWidth,
            (safeWidth - ((visibleCount - 1) * LibraryCardGap)) / visibleCount);
        return totalItemCount < LibraryMaxVisibleCount
            ? Math.Min(cardWidth, LibraryCardMaxWidth)
            : cardWidth;
    }

    public static int CalculateLibraryFirstVisibleIndex(
        double horizontalOffset,
        double cardWidth)
    {
        var safeOffset = double.IsFinite(horizontalOffset)
            ? Math.Max(0, horizontalOffset)
            : 0;
        var safeCardWidth = double.IsFinite(cardWidth)
            ? Math.Max(LibraryCardMinWidth, cardWidth)
            : LibraryCardMinWidth;
        return (int)Math.Floor(safeOffset / (safeCardWidth + LibraryCardGap));
    }

    public static int ClampLibraryFirstVisibleIndex(
        int firstVisibleIndex,
        int totalItemCount,
        int capacity)
    {
        var maximumIndex = Math.Max(
            0,
            Math.Max(0, totalItemCount) - Math.Clamp(capacity, 2, LibraryMaxVisibleCount));
        return Math.Clamp(firstVisibleIndex, 0, maximumIndex);
    }

    public static bool CanScrollLibraryLeft(int firstVisibleIndex)
    {
        return firstVisibleIndex > 0;
    }

    public static bool CanScrollLibraryRight(
        int totalItemCount,
        int capacity,
        int firstVisibleIndex)
    {
        return firstVisibleIndex < Math.Max(
            0,
            Math.Max(0, totalItemCount) - Math.Clamp(capacity, 2, LibraryMaxVisibleCount));
    }

    public static double CalculateVerticalWheelTarget(
        double currentOffset,
        double wheelDelta,
        double scrollableHeight)
    {
        return Math.Clamp(
            currentOffset - ((wheelDelta / 120d) * VerticalWheelStep),
            0,
            Math.Max(0, scrollableHeight));
    }

    public static double CalculateAccumulatedVerticalWheelTarget(
        double currentOffset,
        double currentTarget,
        bool isAnimationActive,
        double wheelDelta,
        double scrollableHeight)
    {
        return CalculateVerticalWheelTarget(
            isAnimationActive ? currentTarget : currentOffset,
            wheelDelta,
            scrollableHeight);
    }

    public static bool ShouldRouteWheelVertically(bool shiftPressed, double wheelDelta)
    {
        return !shiftPressed && wheelDelta != 0;
    }

    public static double CalculateHorizontalWheelTarget(
        double currentOffset,
        double wheelDelta,
        double scrollableWidth)
    {
        return Math.Clamp(
            currentOffset - wheelDelta,
            0,
            Math.Max(0, scrollableWidth));
    }

    public static bool ShouldRouteWheelHorizontally(
        bool shiftPressed,
        double currentOffset,
        double wheelDelta,
        double scrollableWidth)
    {
        if (!shiftPressed || wheelDelta == 0 || scrollableWidth <= 0)
        {
            return false;
        }

        var targetOffset = CalculateHorizontalWheelTarget(
            currentOffset,
            wheelDelta,
            scrollableWidth);
        return Math.Abs(targetOffset - currentOffset) > 0.5;
    }

    private enum InputSource
    {
        None,
        Pointer,
        Keyboard
    }
}
