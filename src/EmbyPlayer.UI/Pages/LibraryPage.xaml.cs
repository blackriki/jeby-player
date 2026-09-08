using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace EmbyPlayer.UI.Pages;

public partial class LibraryPage : UserControl
{
    private readonly ScrollScrimController? scrollScrimController;
    private ContextMenu? activeQueryMenu;
    private Button? pendingQueryMenuButton;

    public LibraryPage()
    {
        InitializeComponent();
        scrollScrimController = new ScrollScrimController(FixedBackScrim);
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        scrollScrimController?.Update(e.VerticalOffset);
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        pendingQueryMenuButton = null;
    }

    private void OnQueryMenuButtonClick(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        pendingQueryMenuButton = button;
        if (activeQueryMenu is not null)
        {
            if (activeQueryMenu == button.ContextMenu && activeQueryMenu.IsOpen)
            {
                pendingQueryMenuButton = null;
                return;
            }
            activeQueryMenu.IsOpen = false;
            return;
        }

        OpenPendingQueryMenu();
    }

    private void OpenPendingQueryMenu()
    {
        if (activeQueryMenu is not null || pendingQueryMenuButton is null) return;
        var button = pendingQueryMenuButton;
        pendingQueryMenuButton = null;
        if (!IsLoaded || !button.IsEnabled) return;

        button.Focus();
        button.ContextMenu.PlacementTarget = button;
        activeQueryMenu = button.ContextMenu;
        button.ContextMenu.IsOpen = true;
    }

    private void OnQueryMenuButtonKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            OnQueryMenuButtonClick(sender, e);
            e.Handled = true;
        }
    }

    private void OnQueryMenuOpened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Items.OfType<MenuItem>().First(item => item.IsCheckable && item.IsChecked).Focus();
    }

    private void OnQueryOptionClick(object sender, RoutedEventArgs e)
    {
        // A radio-style option remains selected when the current option is invoked again.
        ((MenuItem)sender).GetBindingExpression(MenuItem.IsCheckedProperty)?.UpdateTarget();
    }

    private void OnQueryMenuClosed(object sender, RoutedEventArgs e)
    {
        if (activeQueryMenu != sender) return;
        activeQueryMenu = null;
        // Finish native popup cleanup before opening another menu or reopening this one.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(OpenPendingQueryMenu));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        pendingQueryMenuButton = null;
        SortButton.ContextMenu.IsOpen = false;
        FilterButton.ContextMenu.IsOpen = false;
    }
}

public sealed class LibraryQueryOptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Equals(value, parameter);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? parameter : Binding.DoNothing;
}

public sealed class LibraryContentWidthConverter : IValueConverter
{
    public double CardWidth { get; set; }

    public Thickness CardMargin { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var availableWidth = (double)value;
        if (availableWidth <= 0) return double.NaN;
        var stride = CardWidth + CardMargin.Left + CardMargin.Right;
        // Align to the last full poster column, independently of loading or result count.
        var columns = Math.Max(1, Math.Floor(availableWidth / stride));
        return columns * stride - CardMargin.Right;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
