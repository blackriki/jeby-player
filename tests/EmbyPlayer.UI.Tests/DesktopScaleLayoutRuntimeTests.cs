using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EmbyPlayer.UI.Controls;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.Pages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class AppShellPersonIntegrationTests
{
    [DataTestMethod]
    [DataRow(1.25)]
    [DataRow(1.5)]
    [DataRow(2.0)]
    public void DesktopLayout_SimulatedScaleKeepsHomeAndPersonalListActionsReachable(double scale)
        => PersonalEntryTestHost.Run(() =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var icons = new ResourceDictionary { Source = new Uri("/EmbyPlayer.UI;component/Resources/Icons.xaml", UriKind.Relative) };
            var iconStyles = new ResourceDictionary { Source = new Uri("/EmbyPlayer.UI;component/Styles/IconButtons.xaml", UriKind.Relative) };
            app.Resources.MergedDictionaries.Add(icons);
            app.Resources.MergedDictionaries.Add(iconStyles);
            var resources = LoadResources();
            app.Resources.MergedDictionaries.Add(resources);
            var context = new Context();
            context.Shell.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            context.WatchLaterStore.Items.Add(new("saved", new string('长', 160), null, "Movie", 2026));
            context.Navigation.NavigateTo(AppPage.Home);
            var shell = new AppShell { DataContext = context.Shell, LayoutTransform = new ScaleTransform(scale, scale) };
            // This explicitly simulates content scaling; it does not change monitor DPI.
            var window = new Window { Width = 1100 * scale, Height = 700 * scale, WindowStyle = WindowStyle.None,
                Content = shell, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            try
            {
                window.Show(); Pump();
                context.Queue.Add(new("queued", new string('长', 160), "Movie")); Pump();
                var home = Descendants<HomePage>(shell).Single();
                var actions = (FrameworkElement)home.FindName("HomeHeaderActionsPanel");
                Assert.AreEqual(0, Grid.GetRow(actions));
                foreach (var name in new[] { "HomeWatchLaterButton", "HomeFavoritesButton", "HomeQueueButton" })
                {
                    var button = (Button)home.FindName(name);
                    var bounds = button.TransformToAncestor(home).TransformBounds(new Rect(button.RenderSize));
                    Assert.IsTrue(button.IsVisible && button.IsEnabled, $"{name}: visible={button.IsVisible}, enabled={button.IsEnabled}");
                    Assert.IsTrue(bounds.Left >= 0 && bounds.Right <= home.ActualWidth + 1, name);
                    Assert.IsTrue(button.ActualHeight >= 32, name);
                }
                context.Home.OpenWatchLaterCommand.Execute(null); Pump();
                var watch = Descendants<WatchLaterPage>(shell).Single();
                foreach (var button in Descendants<Button>(watch).Where(button => button.IsVisible
                             && button.Command == context.WatchLater.RemoveCommand))
                {
                    var bounds = button.TransformToAncestor(watch).TransformBounds(new Rect(button.RenderSize));
                    Assert.IsTrue(bounds.Left >= 0 && bounds.Right <= watch.ActualWidth + 1);
                    Assert.IsTrue(button.Focusable && button.IsEnabled);
                }
                context.Home.OpenPlaybackQueueCommand.Execute(null); Pump();
                var queue = Descendants<PlaybackQueuePage>(shell).Single();
                var controls = Descendants<Button>(queue).Where(button => button.IsVisible
                    && System.Windows.Automation.AutomationProperties.GetAutomationId(button).StartsWith("Queue.")).ToArray();
                Assert.IsTrue(controls.Length >= 3);
                foreach (var button in controls)
                {
                    var bounds = button.TransformToAncestor(queue).TransformBounds(new Rect(button.RenderSize));
                    Assert.IsTrue(bounds.Left >= 0 && bounds.Right <= queue.ActualWidth + 1);
                    Assert.IsTrue(button.Focusable);
                }
                context.Navigation.NavigateTo(AppPage.Search); Pump();
                var search = Descendants<SearchPage>(shell).Single();
                var searchBox = (TextBox)search.FindName("SearchTextBox");
                searchBox.Text = new string('长', 160); Pump();
                var searchBounds = searchBox.TransformToAncestor(search).TransformBounds(new Rect(searchBox.RenderSize));
                Assert.IsTrue(searchBounds.Left >= 0 && searchBounds.Right <= search.ActualWidth + 1);
                Assert.IsTrue(searchBox.ActualHeight >= 32 && searchBox.Focusable);
            }
            finally
            {
                window.Close();
                app.Resources.MergedDictionaries.Remove(resources);
                app.Resources.MergedDictionaries.Remove(icons);
                app.Resources.MergedDictionaries.Remove(iconStyles);
            }
        });
}
