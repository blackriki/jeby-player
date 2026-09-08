using System.Windows;
using System.Windows.Controls;
using EmbyPlayer.UI.Controls;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.Pages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class AppShellPersonIntegrationTests
{
    [TestMethod]
    public void PersonalListsRouteThroughShellAndResetOnLogout()
    {
        var context = new Context();
        context.WatchLaterStore.Items.Add(new("saved", "Saved", null, "Movie", 2025));
        context.Home.OpenWatchLaterCommand.Execute(null);
        Assert.AreSame(context.WatchLater, context.Shell.CurrentPageViewModel);
        context.WatchLater.OpenMediaCommand.Execute(context.WatchLater.Items.Single());
        Assert.AreEqual(AppPage.Detail, context.Shell.CurrentPage);
        context.Detail.BackCommand.Execute(null);
        Assert.AreEqual(AppPage.WatchLater, context.Shell.CurrentPage);
        context.Home.OpenPlaybackQueueCommand.Execute(null);
        context.Queue.Add(new("queued", "Queued", "Movie"));
        Assert.AreSame(context.Player.QueueViewModel, context.Shell.CurrentPageViewModel);
        Assert.AreEqual(1, context.Queue.Snapshot.Pending.Count);
        context.Navigation.NavigateTo(AppPage.Login);
        Assert.IsFalse(context.Queue.Snapshot.HasSession);
        Assert.AreEqual(0, context.Queue.Snapshot.Pending.Count);
        Assert.AreEqual(0, context.WatchLater.Items.Count);
    }

    [TestMethod]
    public void PersonalListTemplatesAndCompactHomeEntriesAreVisibleWithoutOverlap()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var icons = new ResourceDictionary { Source = new Uri("/EmbyPlayer.UI;component/Resources/Icons.xaml", UriKind.Relative) };
                app.Resources.MergedDictionaries.Add(icons);
                var resources = LoadResources();
                app.Resources.MergedDictionaries.Add(resources);
                var context = new Context();
                context.Shell.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
                context.Navigation.NavigateTo(AppPage.Home);
                var shell = new AppShell { DataContext = context.Shell };
                var window = new Window { Width = 900, Height = 700, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Content = shell };
                try
                {
                    window.Show(); Pump();
                    var home = Descendants<HomePage>(shell).Single();
                    var actions = (FrameworkElement)home.FindName("HomeHeaderActionsPanel");
                    var saved = (Button)home.FindName("HomeWatchLaterButton");
                    var favorites = (Button)home.FindName("HomeFavoritesButton");
                    var queue = (Button)home.FindName("HomeQueueButton");
                    Assert.AreEqual(0, Grid.GetRow(actions), "Header actions share one row at a 900 DIP window.");
                    Assert.AreEqual(saved.ActualWidth, favorites.ActualWidth);
                    Assert.AreEqual(saved.TranslatePoint(new Point(), home).Y, favorites.TranslatePoint(new Point(), home).Y);
                    Assert.AreEqual(Visibility.Collapsed, queue.Visibility);
                    Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)home.FindName("HomeWatchLaterSection")).Visibility);
                    context.Queue.Add(new("queued", "Queued", "Movie")); Pump();
                    Assert.AreEqual(Visibility.Visible, queue.Visibility);
                    Assert.AreEqual("待播 1 项", queue.Content);
                    Assert.IsTrue(queue.TranslatePoint(new Point(queue.ActualWidth, 0), home).X <= home.ActualWidth);
                    for (var index = 0; index < 14; index++)
                        context.WatchLaterStore.Items.Add(new($"saved-{index}", $"稍后观看作品 {index + 1}", null, "Movie", 2025));
                    context.Home.NavigateAsync().GetAwaiter().GetResult(); Pump();
                    var section = (FrameworkElement)home.FindName("HomeWatchLaterSection");
                    var continueRow = (FrameworkElement)home.FindName("ContinueWatchingRow");
                    Assert.AreEqual(Visibility.Visible, section.Visibility);
                    Assert.IsTrue(section.TranslatePoint(new Point(), home).Y >= continueRow.TranslatePoint(new Point(0, continueRow.ActualHeight), home).Y);
                    var posters = Descendants<Button>(section).Where(button => button.Command == context.Home.OpenWatchLaterItemCommand).ToArray();
                    Assert.AreEqual(12, posters.Length);
                    var previewScroller = Descendants<ScrollViewer>(section).Single();
                    Assert.IsTrue(previewScroller.ScrollableWidth > 0);
                    foreach (var width in new[] { 1100d, 1440d })
                    {
                        window.Width = width; Pump();
                        Assert.AreEqual(0, Grid.GetRow(actions));
                        Assert.AreEqual(saved.TranslatePoint(new Point(), home).Y, queue.TranslatePoint(new Point(), home).Y);
                        Assert.IsTrue(queue.TranslatePoint(new Point(queue.ActualWidth, 0), home).X <= home.ActualWidth);
                    }
                    window.Width = 1100; Pump();
                    var pageScroller = (ScrollViewer)home.FindName("HomePageScrollViewer");
                    pageScroller.ScrollToVerticalOffset(section.TranslatePoint(new Point(), (FrameworkElement)pageScroller.Content).Y - 20); Pump();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)home.ActualWidth, (int)home.ActualHeight,
                        96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(home);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    var root = new DirectoryInfo(AppContext.BaseDirectory);
                    while (root is not null && !File.Exists(Path.Combine(root.FullName, "EmbyPlayer.sln"))) root = root.Parent;
                    var screenshotPath = Path.Combine(root!.FullName, ".tmp", "personal-library-layout", "home-preview.png");
                    Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
                    using (var stream = File.Create(screenshotPath)) encoder.Save(stream);
                    saved.Command.Execute(null); Pump();
                    Assert.AreSame(context.WatchLater, Descendants<WatchLaterPage>(shell).Single().DataContext);
                    context.Home.OpenPlaybackQueueCommand.Execute(null); Pump();
                    Assert.AreSame(context.Player.QueueViewModel, Descendants<PlaybackQueuePage>(shell).Single().DataContext);
                }
                finally { window.Close(); app.Resources.MergedDictionaries.Remove(resources); app.Resources.MergedDictionaries.Remove(icons); }
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }
}
