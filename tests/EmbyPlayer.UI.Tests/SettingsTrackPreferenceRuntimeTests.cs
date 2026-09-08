using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.Pages;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class SettingsCacheRuntimeTests
{
    [TestMethod]
    public void DefaultSubtitles_RealSettingsToggleAndSaveRemainReachableAtMinimumWindowSize()
    {
        RunTrackPreferencesOnSta(() =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var icons = new ResourceDictionary { Source = new Uri("/EmbyPlayer.UI;component/Resources/Icons.xaml", UriKind.Relative) };
            app.Resources.MergedDictionaries.Add(icons);
            var styles = new ResourceDictionary { Source = new Uri("/EmbyPlayer.UI;component/Styles/IconButtons.xaml", UriKind.Relative) };
            app.Resources.MergedDictionaries.Add(styles);
            var resources = LoadResources();
            app.Resources.MergedDictionaries.Add(resources);
            var preferences = new TestAppSettingsService();
            var sessions = new CurrentSessionService();
            var auth = new TestAuthSessionStore();
            var navigation = new NavigationService();
            navigation.NavigateTo(AppPage.Settings);
            var vm = new SettingsViewModel(navigation, preferences, sessions, new TestMediaLibraryScanService(),
                auth, new AccountSessionService(auth, sessions, preferences), _ => { });
            vm.LoadAsync().GetAwaiter().GetResult();
            var page = new SettingsPage { DataContext = vm };
            var window = new Window { Content = page, Width = 1100, Height = 700, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            try
            {
                window.Show();
                Pump();
                var category = (RadioButton)page.FindName("TracksCategoryButton");
                category.IsChecked = true;
                category.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                var toggle = (ToggleButton)page.FindName("DefaultSubtitlesToggle");
                Assert.IsTrue(toggle.IsVisible && toggle.IsEnabled && toggle.IsTabStop);
                Assert.IsTrue(toggle.IsChecked);
                ((IToggleProvider)new ToggleButtonAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
                Pump();
                Assert.IsFalse(vm.DefaultSubtitlesEnabled);
                Assert.IsTrue(vm.IsDirty);
                var bounds = toggle.TransformToAncestor(page).TransformBounds(new Rect(toggle.RenderSize));
                Assert.IsTrue(bounds.Left >= 0 && bounds.Right <= page.ActualWidth && bounds.Top >= 0 && bounds.Bottom <= page.ActualHeight);
                var save = Descendants<Button>(page).Single(button => AutomationProperties.GetName(button) == "保存更改");
                ActivateWithEnter(save);
                Pump();
                Assert.IsFalse(preferences.PlayerPreferences.DefaultSubtitlesEnabled);
                Assert.IsFalse(vm.IsDirty);
            }
            finally
            {
                window.Close();
                app.Resources.MergedDictionaries.Remove(resources);
                app.Resources.MergedDictionaries.Remove(styles);
                app.Resources.MergedDictionaries.Remove(icons);
            }
        });
    }

    private static void RunTrackPreferencesOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = exception; } })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "The settings interaction did not finish within 20 seconds.");
        if (error is not null) throw error;
    }
}
