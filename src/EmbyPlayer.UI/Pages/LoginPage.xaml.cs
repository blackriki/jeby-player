using System.Windows;
using System.Windows.Controls;
using EmbyPlayer.UI.ViewModels;

namespace EmbyPlayer.UI.Pages;

public partial class LoginPage : UserControl
{
    public LoginPage()
    {
        InitializeComponent();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel viewModel && sender is PasswordBox passwordBox)
        {
            viewModel.Password = passwordBox.Password;
        }
    }
}
