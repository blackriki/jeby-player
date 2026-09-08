using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Settings;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.Pages;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class SettingsCacheRuntimeTests
{
    [TestMethod]
    public void ShortcutEditor_RealButtonsCaptureValidateCancelAndSaveAtSmallWindowSize()
    {
        RunOnSta(() =>
        {
            var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var geometries = new ResourceDictionary { Source = new Uri("/EmbyPlayer.UI;component/Resources/Icons.xaml", UriKind.Relative) };
            var icons = new ResourceDictionary { Source = new Uri("/EmbyPlayer.UI;component/Styles/IconButtons.xaml", UriKind.Relative) };
            application.Resources.MergedDictionaries.Add(geometries);
            application.Resources.MergedDictionaries.Add(icons);
            var resources = LoadResources();
            application.Resources.MergedDictionaries.Add(resources);
            var preferences = new TestAppSettingsService();
            var sessions = new CurrentSessionService();
            sessions.SetSession(CacheManagementViewModelTests.Session);
            var store = new TestAuthSessionStore();
            var navigation = new NavigationService();
            navigation.NavigateTo(AppPage.Settings);
            var viewModel = new SettingsViewModel(navigation, preferences, sessions, new TestMediaLibraryScanService(),
                store, new AccountSessionService(store, sessions, preferences), _ => { });
            viewModel.LoadAsync().GetAwaiter().GetResult();
            var page = new SettingsPage { DataContext = viewModel };
            var window = new Window
            {
                Content = page, Width = 980, Height = 680, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0
            };
            try
            {
                window.Show();
                Pump();
                var panel = (StackPanel)page.FindName("ShortcutSettingsPanel");
                Assert.IsTrue(panel.IsVisible);
                Assert.AreSame(viewModel.Shortcuts, panel.DataContext);
                var capture = Descendants<Button>(panel).Single(button => button.DataContext is ShortcutSettingRow row
                    && row.Action == PlayerShortcutAction.ToggleMute && button.Command == viewModel.Shortcuts.CaptureCommand);
                capture.BringIntoView();
                capture.Focus();
                Pump();
                Assert.IsTrue(capture.IsTabStop && capture.IsEnabled);
                ActivateWithEnter(capture);
                Pump();
                Assert.IsNotNull(viewModel.Shortcuts.CapturingRow);
                var conflict = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(page), 0, Key.Space)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                page.RaiseEvent(conflict);
                Pump();
                Assert.IsTrue(conflict.Handled);
                Assert.IsTrue(viewModel.Shortcuts.HasError);
                Assert.IsTrue(Descendants<TextBlock>(panel).Any(text => text.IsVisible && text.Text.Contains("已用于", StringComparison.Ordinal)));
                page.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(page), 0, Key.K)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Pump();
                Assert.AreEqual("K", viewModel.Shortcuts.Bindings.ToggleMute);
                Assert.IsTrue(viewModel.IsDirty);
                Assert.AreEqual("M", preferences.PlayerPreferences.EffectiveShortcuts.ToggleMute);
                var save = Descendants<Button>(page).Single(button => AutomationProperties.GetName(button) == "保存更改");
                ActivateWithEnter(save);
                Pump();
                Assert.AreEqual("K", preferences.PlayerPreferences.EffectiveShortcuts.ToggleMute);
                Assert.IsFalse(viewModel.IsDirty);
                ActivateWithEnter(capture);
                Pump();
                page.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(page), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Assert.IsNull(viewModel.Shortcuts.CapturingRow);
                Assert.AreEqual(AppPage.Settings, navigation.CurrentPage);
                Assert.AreEqual("K", viewModel.Shortcuts.Bindings.ToggleMute);
                var scroll = Ancestor<ScrollViewer>(panel)!;
                Assert.IsTrue(scroll.ScrollableHeight > 0);
                capture.Focus();
                ActivateWithEnter(capture);
                ((TextBox)page.FindName("SettingsSearchTextBox")).Focus();
                Pump();
                Assert.IsNull(viewModel.Shortcuts.CapturingRow, "Moving to text input must end key capture.");
                var typing = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(page), 0, Key.M)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                page.RaiseEvent(typing);
                Assert.IsFalse(typing.Handled);
                Assert.AreEqual("K", viewModel.Shortcuts.Bindings.ToggleMute);
            }
            finally
            {
                window.Close();
                application.Resources.MergedDictionaries.Remove(resources);
                application.Resources.MergedDictionaries.Remove(icons);
                application.Resources.MergedDictionaries.Remove(geometries);
            }
        });
    }
}
