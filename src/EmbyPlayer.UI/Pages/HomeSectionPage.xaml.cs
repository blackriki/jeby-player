using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EmbyPlayer.UI.ViewModels;

namespace EmbyPlayer.UI.Pages;

public partial class HomeSectionPage : UserControl
{
    private HomeSectionViewModel? viewModel;
    private bool isRestoringScrollOffset;

    public HomeSectionPage()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        viewModel = DataContext as HomeSectionViewModel;
        if (viewModel is not null)
        {
            var loadedViewModel = viewModel;
            var savedOffset = viewModel.ScrollOffset;
            isRestoringScrollOffset = true;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (ReferenceEquals(viewModel, loadedViewModel))
                {
                    SectionScrollViewer.ScrollToVerticalOffset(savedOffset);
                    isRestoringScrollOffset = false;
                }
            });
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (viewModel is not null)
        {
            viewModel.ScrollOffset = SectionScrollViewer.VerticalOffset;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel = null;
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (viewModel is not null && !isRestoringScrollOffset && e.VerticalChange != 0)
            viewModel.ScrollOffset = SectionScrollViewer.VerticalOffset;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeSectionViewModel.ScrollOffset) && viewModel is not null)
            SectionScrollViewer.ScrollToVerticalOffset(viewModel.ScrollOffset);
    }
}
