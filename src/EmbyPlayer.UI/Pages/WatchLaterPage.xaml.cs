using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EmbyPlayer.UI.ViewModels;

namespace EmbyPlayer.UI.Pages;

public partial class WatchLaterPage : UserControl
{
    private WatchLaterViewModel? viewModel;
    private bool isRestoringScrollOffset;

    public WatchLaterPage() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        viewModel = DataContext as WatchLaterViewModel;
        if (viewModel is null) return;
        var loadedViewModel = viewModel;
        var savedOffset = viewModel.ScrollOffset;
        isRestoringScrollOffset = true;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!ReferenceEquals(viewModel, loadedViewModel)) return;
            WatchLaterScrollViewer.ScrollToVerticalOffset(savedOffset);
            isRestoringScrollOffset = false;
        });
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (viewModel is null) return;
        viewModel.ScrollOffset = WatchLaterScrollViewer.VerticalOffset;
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel = null;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (viewModel is not null && !isRestoringScrollOffset && e.VerticalChange != 0)
            viewModel.ScrollOffset = WatchLaterScrollViewer.VerticalOffset;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WatchLaterViewModel.ScrollOffset) && viewModel is not null)
            WatchLaterScrollViewer.ScrollToVerticalOffset(viewModel.ScrollOffset);
    }
}
