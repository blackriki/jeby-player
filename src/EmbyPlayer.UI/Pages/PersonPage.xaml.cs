using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EmbyPlayer.UI.ViewModels;

namespace EmbyPlayer.UI.Pages;

public partial class PersonPage : UserControl
{
    private PersonViewModel? viewModel;
    private bool isRestoringScrollOffset;

    public PersonPage()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        viewModel = DataContext as PersonViewModel;
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
                    PersonScrollViewer.ScrollToVerticalOffset(savedOffset);
                    isRestoringScrollOffset = false;
                }
            });
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (viewModel is not null)
        {
            viewModel.ScrollOffset = PersonScrollViewer.VerticalOffset;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel = null;
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (viewModel is not null && !isRestoringScrollOffset && e.VerticalChange != 0)
            viewModel.ScrollOffset = PersonScrollViewer.VerticalOffset;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PersonViewModel.ScrollOffset) && viewModel is not null)
            PersonScrollViewer.ScrollToVerticalOffset(viewModel.ScrollOffset);
    }
}
