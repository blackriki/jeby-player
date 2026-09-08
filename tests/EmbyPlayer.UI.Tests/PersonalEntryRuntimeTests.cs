using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EmbyPlayer.Core.Details;
using EmbyPlayer.Core.PlaybackQueue;
using EmbyPlayer.Core.Series;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.Pages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class MediaDetailViewModelTests
{
    [DataTestMethod]
    [DataRow("Movie", false)]
    [DataRow("Movie", true)]
    [DataRow("Series", true)]
    public void PersonalEntries_DetailKeepsRelatedActionsTogetherAndKeyboardOperable(string type, bool resume)
    {
        PersonalEntryTestHost.Run(() =>
        {
            var store = new TestWatchLaterStore();
            var queue = new InMemoryPlaybackQueueService();
            var context = CreateContext(store, queue);
            queue.SetSession(context.Session.ServerBase, context.Session.UserId);
            context.MediaDetailService.LoadDetailAsyncHandler = (_, id, _) => Task.FromResult(
                MediaDetailLoadResult.Success(CreateDetail(id, type) with
                { ResumePositionTicks = resume ? TimeSpan.FromMinutes(12).Ticks : 0, PlayedPercentage = resume ? 40 : 0 }));
            context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) => Task.FromResult(
                SeriesSeasonsLoadResult.Success(new[] { new SeasonInfo("season-1", "第 1 季", 1, true) }));
            context.SeriesService.LoadEpisodesAsyncHandler = (_, _, _, _) => Task.FromResult(
                SeriesEpisodesLoadResult.Success(new[] { new EpisodeInfo("episode-1", "第 1 集", 1, 1,
                    TimeSpan.FromMinutes(45).Ticks, null, 40, TimeSpan.FromMinutes(12).Ticks, null) }));
            context.ViewModel.LoadAsync("detail-1", AppPage.Library).GetAwaiter().GetResult();
            WithPersonalDetailPage(context.ViewModel, page =>
            {
                var buttons = PersonalEntryTestHost.Descendants<Button>(page).Where(button => button.IsVisible).ToArray();
                var watch = buttons.Single(button => button.Command == context.ViewModel.ToggleWatchLaterCommand);
                var queueButton = buttons.Single(button => button.Command == context.ViewModel.AddToQueueCommand);
                var favorite = buttons.Single(button => button.Command == context.ViewModel.ToggleFavoriteCommand);
                var play = buttons.Single(button => button.Command == (type == "Series"
                    ? context.ViewModel.SeriesPlayCommand : resume ? context.ViewModel.ContinuePlayCommand : context.ViewModel.PlayCommand));
                PersonalEntryTestHost.AssertAdjacent(play, queueButton, page);
                PersonalEntryTestHost.AssertAdjacent(favorite, watch, page);
                var actions = (StackPanel)page.FindName(type == "Series" ? "DetailSeriesActions" : "DetailMovieActions");
                foreach (var action in actions.Children.OfType<Button>().Where(button => button.IsVisible))
                {
                    var bounds = action.TransformToAncestor(actions).TransformBounds(new Rect(action.RenderSize));
                    Assert.AreEqual(0d, bounds.Top, 1d, "All detail actions must stay on one row.");
                    Assert.IsTrue(bounds.Right <= actions.ActualWidth + 1, "Actions must fit the detail text column at minimum window width.");
                    Assert.IsFalse(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(action)));
                    Assert.IsNotNull(action.ToolTip);
                }
                Assert.AreEqual(48d, watch.ActualWidth, 1d);
                Assert.AreEqual(48d, favorite.ActualWidth, 1d);
                PersonalEntryTestHost.PressEnter(watch);
                Assert.IsTrue(context.ViewModel.IsSavedForLater);
                Assert.AreEqual("detail-1", store.Items.Single().ItemId);
                Assert.AreEqual(0, context.ItemUserDataService.SetFavoriteCallCount);
                Assert.AreEqual("移出稍后观看", System.Windows.Automation.AutomationProperties.GetName(watch));
                Assert.AreEqual("移出稍后观看", watch.ToolTip);
                PersonalEntryTestHost.AssertAdjacent(favorite, watch, page);
                PersonalEntryTestHost.PressEnter(queueButton);
                Assert.AreEqual(type == "Series" ? "episode-1" : "detail-1", queue.Snapshot.Pending.Single().ItemId);
                Assert.AreEqual(0, context.PlaybackService.PrepareCallCount);
                var message = PersonalEntryTestHost.Descendants<TextBlock>(page)
                    .Single(text => text.Text == context.ViewModel.PersonalMediaMessage);
                Assert.IsTrue(message.IsVisible);
                PersonalEntryTestHost.Capture(page, $"detail-{type.ToLowerInvariant()}-{resume}.png");
            });
        });
    }

    [TestMethod]
    public void PersonalEntries_DetailKeepsOneReadFailureRetryAndEnablesRelocatedActionAfterRecovery()
    {
        PersonalEntryTestHost.Run(() =>
        {
            var fails = true;
            var store = new SavedStateWatchLaterStore((_, _, _) => fails
                ? Task.FromException<bool>(new IOException("Fixture read failure")) : Task.FromResult(false));
            var context = CreateContext(store);
            context.ViewModel.LoadAsync("detail-1", AppPage.Library).GetAwaiter().GetResult();
            WithPersonalDetailPage(context.ViewModel, page =>
            {
                var buttons = PersonalEntryTestHost.Descendants<Button>(page).Where(button => button.IsVisible).ToArray();
                var watch = buttons.Single(button => button.Command == context.ViewModel.ToggleWatchLaterCommand);
                var retry = buttons.Single(button => button.Command == context.ViewModel.RefreshWatchLaterCommand);
                Assert.IsFalse(watch.IsEnabled);
                Assert.IsTrue(retry.IsEnabled);
                fails = false;
                PersonalEntryTestHost.PressEnter(retry);
                Assert.IsTrue(watch.IsEnabled);
                Assert.IsFalse(retry.IsVisible);
                Assert.IsNull(context.ViewModel.PersonalMediaMessage);
            });
        });
    }

    private static void WithPersonalDetailPage(EmbyPlayer.UI.ViewModels.MediaDetailViewModel viewModel, Action<MediaDetailPage> assertions)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var icons = LoadIconSystemResources();
        app.Resources.MergedDictionaries.Add(icons);
        var resources = LoadApplicationResources();
        app.Resources.MergedDictionaries.Add(resources);
        var page = new MediaDetailPage { DataContext = viewModel };
        var window = new Window { Width = 1100, Height = 700, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Content = page };
        try { window.Show(); PersonalEntryTestHost.Pump(); assertions(page); }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Remove(icons);
            app.Resources.MergedDictionaries.Remove(resources);
        }
    }
}

public sealed partial class PlayerEnhancementRuntimeTests
{
    [TestMethod]
    public void PersonalEntries_PlayerQueueCountUpdatesWithoutClippingOrLosingEmptyEntry()
    {
        RunOnSta(() =>
        {
            var queue = CreateQueue(3);
            WithPage(2, (page, viewModel, player, preparation, window) =>
            {
                var button = (Button)page.FindName("QueueMenuButton");
                Assert.AreEqual("队列 · 3", button.Content);
                Assert.AreEqual(3, viewModel.PendingQueueCount);
                Assert.AreEqual(viewModel.QueueButtonToolTip, button.ToolTip);
                var chrome = (FrameworkElement)page.FindName("BottomChrome");
                foreach (var count in new[] { 3, 123 })
                {
                    while (queue.Snapshot.Pending.Count < count)
                    {
                        var id = queue.Snapshot.Pending.Count + 1;
                        queue.Add(new($"queued-{id}", "Movie", "Movie"));
                    }
                    Pump();
                    Assert.AreEqual($"队列 · {count}", button.Content);
                    var content = Descendants<ContentPresenter>(button).Single();
                    Assert.IsTrue(content.ActualWidth <= button.ActualWidth - 8, "The queue count needs breathing room.");
                    foreach (var control in Descendants<Button>(chrome).Where(item => item.IsVisible))
                    {
                        var bounds = control.TransformToAncestor(chrome).TransformBounds(new Rect(control.RenderSize));
                        Assert.IsTrue(bounds.Left >= -1 && bounds.Right <= chrome.ActualWidth + 1,
                            $"Player control must remain within the minimum window: {bounds}.");
                    }
                }
                queue.Remove("queued-1");
                Pump();
                Assert.AreEqual("队列 · 122", button.Content);
                queue.ClearPending();
                Pump();
                Assert.AreEqual("队列", button.Content);
                Assert.IsTrue(button.IsVisible && button.IsEnabled);
                Open(page, "QueueMenuButton", Popup(page, "QueuePopup"));
                Assert.IsNotNull(viewModel.QueueViewModel!.CurrentItem);
                Popup(page, "QueuePopup").IsOpen = false;
                queue.Add(new("later", "Next movie", "Movie"));
                Pump();
                Assert.AreEqual("队列 · 1", button.Content);
                viewModel.ShowControlsOverlay();
                Pump();
                var layer = (FrameworkElement)page.FindName("ControlsLayer");
                layer.BeginAnimation(UIElement.OpacityProperty, null);
                layer.Opacity = 1;
                PersonalEntryTestHost.Capture((FrameworkElement)page.FindName("OverlayRoot"), "player-queue-count.png");
                queue.SetSession(null, null);
                Pump();
                Assert.AreEqual("队列", button.Content);
            }, queue);
        });
    }
}

internal static class PersonalEntryTestHost
{
    public static void AssertAdjacent(Button first, Button second, FrameworkElement page)
    {
        Assert.AreSame(first.Parent, second.Parent, "Related actions must wrap together.");
        first.BringIntoView();
        Pump();
        var firstBounds = first.TransformToAncestor(page).TransformBounds(new Rect(first.RenderSize));
        var secondBounds = second.TransformToAncestor(page).TransformBounds(new Rect(second.RenderSize));
        Assert.AreEqual(firstBounds.Top, secondBounds.Top, 1);
        Assert.IsTrue(secondBounds.Left >= firstBounds.Right && secondBounds.Left - firstBounds.Right <= 16);
        Assert.IsTrue(firstBounds.Left >= 0 && secondBounds.Right <= page.ActualWidth);
        Assert.IsTrue(first.IsEnabled && second.IsEnabled && first.IsTabStop && second.IsTabStop);
    }

    public static void PressEnter(Button button)
    {
        Assert.IsTrue(button.IsEnabled);
        button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(button), 0, Key.Enter)
            { RoutedEvent = Keyboard.KeyDownEvent });
        Pump();
    }

    public static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    public static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    public static void Run(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = exception; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "Personal entry interaction exceeded its test deadline.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    public static void Capture(FrameworkElement element, string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbyPlayer.sln"))) directory = directory.Parent;
        var output = Path.Combine(directory!.FullName, ".tmp", "personal-library-layout", "detail-player");
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name));
        encoder.Save(file);
    }
}
