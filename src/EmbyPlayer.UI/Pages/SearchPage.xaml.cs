using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EmbyPlayer.UI.ViewModels;

namespace EmbyPlayer.UI.Pages;

public partial class SearchPage : UserControl
{
    private readonly ScrollScrimController? scrollScrimController;
    private SearchViewModel? viewModel;

    public SearchPage()
    {
        InitializeComponent();
        scrollScrimController = new ScrollScrimController(FixedBackScrim);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel(DataContext as SearchViewModel);
        Dispatcher.BeginInvoke(
            () =>
            {
                FocusSearchBox();
                if (viewModel is not null)
                {
                    SearchScrollViewer.ScrollToVerticalOffset(viewModel.ScrollOffset);
                }
            },
            DispatcherPriority.Loaded);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (viewModel is not null)
        {
            viewModel.ScrollOffset = SearchScrollViewer.VerticalOffset;
        }

        AttachViewModel(null);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachViewModel(e.NewValue as SearchViewModel);
    }

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (viewModel is null)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await viewModel.SearchNowAsync().ConfigureAwait(true);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (viewModel.HasKeyword)
            {
                viewModel.ClearSearch();
            }
            else
            {
                viewModel.NavigateHomeCommand.Execute(null);
            }
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        scrollScrimController?.Update(e.VerticalOffset);

        if (viewModel is not null)
        {
            viewModel.ScrollOffset = e.VerticalOffset;
        }
    }

    private void AttachViewModel(SearchViewModel? nextViewModel)
    {
        if (ReferenceEquals(viewModel, nextViewModel))
        {
            return;
        }

        if (viewModel is not null)
        {
            viewModel.SearchFocusRequested -= OnSearchFocusRequested;
        }

        viewModel = nextViewModel;
        if (viewModel is not null)
        {
            viewModel.SearchFocusRequested += OnSearchFocusRequested;
        }
    }

    private void OnSearchFocusRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(FocusSearchBox, DispatcherPriority.Input);
    }

    private void FocusSearchBox()
    {
        if (!SearchTextBox.IsKeyboardFocusWithin)
        {
            SearchTextBox.Focus();
            Keyboard.Focus(SearchTextBox);
        }
    }
}

internal sealed class ScrollScrimController
{
    internal const double VisibleOffsetThreshold = 6;
    private static readonly Duration TransitionDuration = new(TimeSpan.FromMilliseconds(140));

    private readonly UIElement scrim;
    private readonly DoubleAnimation fadeInAnimation = CreateAnimation(1);
    private readonly DoubleAnimation fadeOutAnimation = CreateAnimation(0);
    private bool isShown;

    internal ScrollScrimController(UIElement scrim)
    {
        this.scrim = scrim ?? throw new ArgumentNullException(nameof(scrim));
    }

    internal static bool ShouldShow(double verticalOffset)
    {
        return verticalOffset > VisibleOffsetThreshold;
    }

    internal void Update(double verticalOffset)
    {
        var shouldShow = ShouldShow(verticalOffset);
        if (shouldShow == isShown)
        {
            return;
        }

        isShown = shouldShow;
        scrim.BeginAnimation(
            UIElement.OpacityProperty,
            shouldShow ? fadeInAnimation : fadeOutAnimation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private static DoubleAnimation CreateAnimation(double targetOpacity)
    {
        var animation = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TransitionDuration,
            FillBehavior = FillBehavior.HoldEnd,
        };
        animation.Freeze();
        return animation;
    }
}
