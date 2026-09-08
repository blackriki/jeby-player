using System.Windows.Controls;

namespace EmbyPlayer.UI.Controls;

public partial class AppShell : UserControl
{
    public AppShell()
    {
        InitializeComponent();
        Unloaded += (_, _) => (DataContext as ViewModels.AppShellViewModel)?.CancelPendingOperations();
    }
}
