using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Playback;
using EmbyPlayer.Core.PlaybackQueue;
using EmbyPlayer.Player;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.Pages;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed partial class PlayerEnhancementRuntimeTests
{
    [TestMethod]
    public void SkipAndNextEpisodeTargetsStayFixedWhenControlsHideAndReappear()
    {
        RunOnSta(() => WithPage(2, (page, viewModel, player, preparation, window) =>
        {
            var skip = (Button)page.FindName("SkipSegmentButton");
            var next = (Border)page.FindName("NextEpisodeCard");
            // Expose both independent prompts to exercise their real layout and overlay bindings.
            skip.Visibility = Visibility.Visible;
            next.Visibility = Visibility.Visible;
            var nextButton = Descendants<Button>(next).Single(button => button.Command == viewModel.PlayNextEpisodeCommand);
            foreach (var size in new[] { new Size(1100, 700), new Size(1920, 1080) })
            {
                window.Width = size.Width;
                window.Height = size.Height;
                viewModel.ShowControlsOverlay();
                Pump();
                var skipShown = new Rect(skip.PointToScreen(new Point()), skip.PointToScreen(new Point(skip.ActualWidth, skip.ActualHeight)));
                var nextShown = new Rect(nextButton.PointToScreen(new Point()), nextButton.PointToScreen(new Point(nextButton.ActualWidth, nextButton.ActualHeight)));
                Assert.AreEqual(132d, skip.Margin.Bottom);
                Assert.AreEqual(132d, next.Margin.Bottom);
                viewModel.HideControlsOverlayIfAllowed();
                Pump();
                Assert.IsFalse(viewModel.IsControlsOverlayVisible);
                Assert.AreEqual(skipShown, new Rect(skip.PointToScreen(new Point()), skip.PointToScreen(new Point(skip.ActualWidth, skip.ActualHeight))));
                Assert.AreEqual(nextShown, new Rect(nextButton.PointToScreen(new Point()), nextButton.PointToScreen(new Point(nextButton.ActualWidth, nextButton.ActualHeight))));
                viewModel.ShowControlsOverlay();
                Pump();
                Assert.AreEqual(skipShown, new Rect(skip.PointToScreen(new Point()), skip.PointToScreen(new Point(skip.ActualWidth, skip.ActualHeight))));
                Assert.AreEqual(nextShown, new Rect(nextButton.PointToScreen(new Point()), nextButton.PointToScreen(new Point(nextButton.ActualWidth, nextButton.ActualHeight))));
            }
        }));
    }

    [TestMethod]
    public void EnhancementPopups_BindCommandsSwitchExclusivelyAndRestoreControlsOnClose()
    {
        RunOnSta(() => WithPage(2, (page, viewModel, player, preparation, window) =>
        {
            var subtitles = Popup(page, "SubtitlePopup");
            var info = Popup(page, "PlaybackInfoPopup");
            var quality = Popup(page, "QualityPopup");
            Open(page, "SubtitleMenuButton", subtitles);
            Assert.AreSame(viewModel, ((FrameworkElement)subtitles.Child).DataContext);
            var adjust = Descendants<Button>(subtitles.Child).Single(button => Equals(button.CommandParameter, "later"));
            Assert.AreSame(viewModel.AdjustSubtitleCommand, adjust.Command);
            Assert.IsTrue(adjust.IsEnabled && adjust.IsTabStop);
            ActivateWithEnter(adjust);
            Pump();
            Assert.AreEqual(0.1, viewModel.SubtitleDelaySeconds, 0.001);
            Assert.IsTrue(player.SubtitleAdjustments.Any(item => item.Kind == SubtitleAdjustmentKind.DelaySeconds && item.Value == 0.1));
            Assert.IsTrue(Descendants<TextBlock>(subtitles.Child).Any(text => text.Text == viewModel.SubtitleDelayText));
            viewModel.HideControlsOverlayIfAllowed();
            Assert.IsTrue(viewModel.IsControlsOverlayVisible, "An open popup must keep playback controls visible.");

            Open(page, "PlaybackInfoButton", info);
            Assert.IsFalse(subtitles.IsOpen);
            var text = Descendants<TextBlock>(info.Child)
                .Select(item => new System.Windows.Documents.TextRange(item.ContentStart, item.ContentEnd).Text).ToArray();
            CollectionAssert.Contains(text, "1280 × 720");
            CollectionAssert.Contains(text, "H264");
            CollectionAssert.Contains(text, "服务器转码");
            Assert.IsTrue(text.Any(item => item.Contains("3840 × 2160") && item.Contains("HEVC")),
                "Source media metadata must be rendered: " + string.Join(" | ", text));
            CollectionAssert.Contains(text, "25 Mbps");
            Assert.AreSame(viewModel.RefreshTechnicalInfoCommand,
                Descendants<Button>(info.Child).Single(button => Equals(button.Content, "刷新播放信息")).Command);

            Open(page, "QualityMenuButton", quality);
            Assert.IsFalse(info.IsOpen);
            var choices = Descendants<Button>(quality.Child).Where(button => button.Command == viewModel.SelectQualityCommand).ToArray();
            Assert.AreEqual(4, choices.Length);
            CollectionAssert.AreEquivalent(Enum.GetValues<PlaybackQuality>(), choices.Select(button => (PlaybackQuality)button.CommandParameter).ToArray());
            var selected = Descendants<TextBlock>(quality.Child).Where(item => item.Text == "✓" && item.Visibility == Visibility.Visible).ToArray();
            Assert.AreEqual(1, selected.Length);
            Assert.IsTrue(choices.All(button => button.IsEnabled));
            ActivateWithEnter(choices.Single(button => Equals(button.CommandParameter, PlaybackQuality.Hd720)));
            Pump();
            Assert.AreEqual(1, preparation.PrepareCallCount);
            Assert.IsTrue(viewModel.IsPlaying, "A recoverable preparation failure must keep the current playback.");
            Assert.IsTrue(Descendants<TextBlock>(quality.Child).Any(item => item.Text == viewModel.PlayerOptionsMessage));

            Open(page, "SubtitleMenuButton", subtitles);
            Assert.IsFalse(quality.IsOpen);
            page.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(page), 0, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Pump();
            Assert.IsFalse(AllPopups(page).Any(popup => popup.IsOpen));
            Assert.IsTrue(viewModel.IsControlsOverlayVisible);
            viewModel.HideControlsOverlayIfAllowed();
            Assert.IsFalse(viewModel.IsControlsOverlayVisible, "Closing popups must release the auto-hide gate.");
            Assert.AreEqual(1, player.LoadCallCount);
        }));
    }

    [TestMethod]
    public void EnhancementPopups_AtMinimumWindowSizeKeepAllOptionsReachable()
    {
        RunOnSta(() => WithPage(24, (page, viewModel, player, preparation, window) =>
        {
            foreach (var names in new[]
            {
                (Button: "QualityMenuButton", Popup: "QualityPopup"),
                (Button: "PlaybackInfoButton", Popup: "PlaybackInfoPopup"),
                (Button: "SubtitleMenuButton", Popup: "SubtitlePopup")
            })
            {
                var popup = Popup(page, names.Popup);
                Open(page, names.Button, popup);
                var root = (FrameworkElement)popup.Child;
                Assert.IsTrue(root.ActualWidth > 0 && root.ActualWidth <= page.ActualWidth, names.Popup);
                Assert.IsTrue(root.ActualHeight > 0 && root.ActualHeight <= page.ActualHeight, names.Popup);
                Assert.AreEqual(root.DesiredSize.Width, root.ActualWidth, 1, names.Popup + " must not clip horizontally.");
                Assert.AreEqual(root.DesiredSize.Height, root.ActualHeight, 1, names.Popup + " must not clip vertically.");
                var scroll = Descendants<ScrollViewer>(root).FirstOrDefault();
                if (names.Popup == "SubtitlePopup")
                {
                    Assert.IsNotNull(scroll);
                    Assert.IsTrue(scroll.ScrollableHeight > 0, "Many subtitle tracks must scroll instead of pushing adjustments outside the popup.");
                    var reset = Descendants<Button>(root).Single(button => Equals(button.CommandParameter, "reset"));
                    reset.BringIntoView();
                    Pump();
                    var bounds = reset.TransformToAncestor(scroll).TransformBounds(new Rect(reset.RenderSize));
                    Assert.IsTrue(bounds.Top >= -1 && bounds.Bottom <= scroll.ViewportHeight + 1,
                        $"Subtitle reset must be reachable in the viewport: {bounds}, height={scroll.ViewportHeight}.");
                    Assert.IsTrue(reset.IsVisible && reset.IsEnabled);
                }
                else
                {
                    foreach (var button in Descendants<Button>(root))
                    {
                        var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                        Assert.IsTrue(bounds.Left >= -1 && bounds.Right <= root.ActualWidth + 1
                            && bounds.Top >= -1 && bounds.Bottom <= root.ActualHeight + 1,
                            $"{names.Popup} button is clipped: {bounds} in {root.RenderSize}.");
                    }
                }
            }
        }));
    }

    [TestMethod]
    public void QueuePopup_At650By510BindsScrollsAndProtectsFocusedPositionKeys()
    {
        RunOnSta(() =>
        {
            var queue = CreateQueue(12);
            WithPage(2, (page, viewModel, player, preparation, window) =>
            {
                var popup = Popup(page, "QueuePopup");
                Open(page, "QueueMenuButton", popup);
                var root = (FrameworkElement)popup.Child;
                var embedded = Descendants<PlaybackQueuePage>(root).Single();
                Assert.AreSame(viewModel, root.DataContext);
                Assert.AreSame(viewModel.QueueViewModel, embedded.DataContext);
                Assert.IsTrue(embedded.IsEmbedded);
                Assert.AreEqual(Visibility.Collapsed, ((Button)embedded.FindName("QueueBackButton")).Visibility);
                Assert.AreEqual(12, ((ItemsControl)embedded.FindName("QueueItems")).Items.Count);
                Assert.IsTrue(root.ActualWidth > 0 && root.ActualWidth <= 650);
                Assert.IsTrue(root.ActualHeight > 0 && root.ActualHeight <= 510);
                Assert.AreEqual(root.DesiredSize.Width, root.ActualWidth, 1, "Queue popup must not clip horizontally.");
                Assert.AreEqual(root.DesiredSize.Height, root.ActualHeight, 1, "Queue popup must not clip vertically.");
                var scroll = (ScrollViewer)embedded.FindName("QueueScrollViewer");
                Assert.IsTrue(scroll.ViewportHeight > 0 && scroll.ScrollableHeight > 0);
                var position = Descendants<ComboBox>(embedded).First();
                position.BringIntoView();
                Pump();
                Keyboard.Focus(position);
                Pump();
                Assert.IsTrue(position.IsKeyboardFocusWithin, "The real position ComboBox must own keyboard focus.");
                Assert.IsTrue(viewModel.CanSeek, "Seeking must be available so the focus guard is exercised.");
                var seekCount = player.SeekCallCount;
                foreach (var key in new[] { Key.Left, Key.Right, Key.Up, Key.Down })
                {
                    var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(page), 0, key)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    page.RaiseEvent(args);
                    Pump();
                    Assert.IsFalse(args.Handled, "Player shortcuts must leave position-combo arrow keys available.");
                }
                Assert.AreEqual(seekCount, player.SeekCallCount);
                scroll.ScrollToBottom();
                Pump();
                var remove = Descendants<Button>(embedded).Last(button => button.Command == viewModel.QueueViewModel!.RemoveCommand);
                var bounds = remove.TransformToAncestor(scroll).TransformBounds(new Rect(remove.RenderSize));
                Assert.IsTrue(bounds.Top >= -1 && bounds.Bottom <= scroll.ViewportHeight + 1
                    && bounds.Left >= -1 && bounds.Right <= scroll.ViewportWidth + 1,
                    $"Last queue row action must be reachable: {bounds}, viewport {scroll.ViewportWidth} × {scroll.ViewportHeight}.");
                Assert.IsTrue(remove.IsEnabled);
                Open(page, "QualityMenuButton", Popup(page, "QualityPopup"));
                Assert.IsFalse(popup.IsOpen);
                Open(page, "QueueMenuButton", popup);
                Assert.IsFalse(Popup(page, "QualityPopup").IsOpen);
            }, queue);
        });
    }

    [TestMethod]
    public void QueuePopup_ClosingAfterPlayKeepsPendingPreparationAndLoadsChosenItem()
    {
        RunOnSta(() =>
        {
            var queue = CreateQueue(2);
            WithPage(2, (page, viewModel, player, preparation, window) =>
            {
                var prepared = new TaskCompletionSource<PlaybackLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                preparation.PreparePlaybackAsyncHandler = (_, _, _) => prepared.Task;
                var popup = Popup(page, "QueuePopup");
                Open(page, "QueueMenuButton", popup);
                var queueViewModel = viewModel.QueueViewModel!;
                var play = Descendants<Button>(popup.Child).First(button => button.Command == queueViewModel.PlayCommand);
                Assert.IsTrue(play.IsEnabled);
                ActivateWithEnter(play);
                Pump();
                Assert.AreEqual("queued-1", preparation.LastRequest!.ItemId);
                Assert.IsTrue(queueViewModel.IsBusy && viewModel.IsAdvancingQueue);
                popup.IsOpen = false;
                Pump();
                Assert.IsFalse(preparation.LastCancellationToken.IsCancellationRequested,
                    "Closing the queue popup must keep the explicitly requested playback preparation alive.");
                Assert.IsTrue(queueViewModel.IsBusy);
                prepared.SetResult(PlaybackLoadResult.Success(viewModel.PlaybackInfo! with
                { ItemId = "queued-1", Title = "Queued movie", MediaType = "Movie", PlaySessionId = "queued-session" }));
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while ((queueViewModel.IsBusy || queue.Snapshot.Current?.ItemId != "queued-1") && DateTime.UtcNow < deadline)
                {
                    Pump();
                    Thread.Sleep(1);
                }
                Assert.IsFalse(queueViewModel.IsBusy || viewModel.IsAdvancingQueue);
                Assert.IsFalse(queueViewModel.HasError);
                Assert.AreEqual("queued-1", viewModel.PlaybackInfo!.ItemId);
                Assert.AreEqual("queued-1", queue.Snapshot.Current!.ItemId);
                Assert.AreEqual("queued-2", queue.Snapshot.Pending.Single().ItemId);
                Assert.AreEqual(2, player.LoadCallCount);
                Assert.IsTrue(viewModel.IsPlaying);
                Assert.IsFalse(popup.IsOpen);
            }, queue);
        });
    }

    private static InMemoryPlaybackQueueService CreateQueue(int count)
    {
        var queue = new InMemoryPlaybackQueueService();
        queue.SetSession("http://media.local", "user-1");
        for (var index = 1; index <= count; index++)
            queue.Add(new($"queued-{index}", $"第 {index} 项：用于检查紧凑队列弹窗和末行滚动的长影片标题", "Movie", 2026));
        return queue;
    }

    private static void WithPage(int subtitleCount, Action<PlayerPage, PlayerViewModel, TestPlayerService, TestPlaybackService, Window> action,
        IPlaybackQueueService? queue = null)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var geometries = new ResourceDictionary { Source = new Uri("/EmbyPlayer.UI;component/Resources/Icons.xaml", UriKind.Relative) };
        var icons = new ResourceDictionary { Source = new Uri("/EmbyPlayer.UI;component/Styles/IconButtons.xaml", UriKind.Relative) };
        app.Resources.MergedDictionaries.Add(geometries);
        app.Resources.MergedDictionaries.Add(icons);
        var resources = LoadResources();
        app.Resources.MergedDictionaries.Add(resources);
        var navigation = new NavigationService();
        navigation.NavigateTo(AppPage.Player);
        var sessions = new CurrentSessionService();
        sessions.SetSession(new("http://media.local", "test-token", "user-1", "Test User", "server-1"));
        var player = new TestPlayerService
        {
            TechnicalInfoHandler = (_, _) => Task.FromResult<PlayerTechnicalInfo?>(new(1280, 720, "h264"))
        };
        player.LoadAsyncHandler = (request, _) =>
        {
            player.RaiseStatusChanged(new(request.PlaybackInstanceId, PlayerPlaybackState.Playing));
            return Task.FromResult(PlayerOperationResult.Success());
        };
        var preparation = new TestPlaybackService
        {
            PreparePlaybackAsyncHandler = (_, _, _) => Task.FromResult(PlaybackLoadResult.Failure(PlaybackLoadError.Forbidden))
        };
        var viewModel = new PlayerViewModel(navigation, player, new TestPlaybackReportService(), sessions,
            new TestAuthSessionStore(), _ => { }, new TestPlaybackReportScheduler(), new TestAppSettingsService(),
            playbackService: preparation, playbackQueue: queue);
        var playback = new PlaybackInfo("movie-1", "Test movie", "play-session",
            new("source-1", "mkv", true, true, true, new Dictionary<string, string>()), "test-media.mkv", true, false,
            TimeSpan.FromMinutes(90).Ticks, 0, Array.Empty<PlaybackTrack>(), Enumerable.Range(0, subtitleCount)
                .Select(i => new PlaybackSubtitle(i, "chi", "srt", $"字幕 {i + 1}", false, false, null)).ToArray(),
            MediaType: "Movie", MediaInfo: new(PlaybackMethod.Transcode, 3840, 2160, "hevc", 25_000_000));
        viewModel.Load(new(playback, new("movie-1", AppPage.Library)));
        var page = new PlayerPage { DataContext = viewModel };
        foreach (var popup in AllPopups(page)) popup.Child.Opacity = 0;
        var window = new Window
        {
            Width = 1100, Height = 700, Left = -10000, Top = -10000, WindowStyle = WindowStyle.None,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Content = page
        };
        try
        {
            window.Show();
            Pump();
            Assert.IsTrue(viewModel.IsPlaying && viewModel.CanSwitchQuality && viewModel.CanAdjustSubtitles);
            action(page, viewModel, player, preparation, window);
        }
        finally
        {
            foreach (var popup in AllPopups(page)) popup.IsOpen = false;
            window.Close();
            Pump();
            app.Resources.MergedDictionaries.Remove(resources);
            app.Resources.MergedDictionaries.Remove(icons);
            app.Resources.MergedDictionaries.Remove(geometries);
        }
    }

    private static Popup Popup(PlayerPage page, string name) => (Popup)page.FindName(name);
    private static Popup[] AllPopups(PlayerPage page) => new[] { "VolumePopup", "SubtitlePopup", "AudioPopup", "PlaybackInfoPopup", "QualityPopup", "QueuePopup", "SpeedPopup", "MorePopup" }
        .Select(name => Popup(page, name)).ToArray();
    private static void Open(PlayerPage page, string buttonName, Popup expected)
    {
        var button = (Button)page.FindName(buttonName);
        Assert.IsTrue(button.IsEnabled);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.IsTrue(expected.IsOpen, buttonName);
        Assert.AreEqual(1, AllPopups(page).Count(popup => popup.IsOpen));
    }
    private static void ActivateWithEnter(Button button) => button.RaiseEvent(new KeyEventArgs(
        Keyboard.PrimaryDevice, PresentationSource.FromVisual(button), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); } catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "Player popup interaction exceeded its test deadline.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static ResourceDictionary LoadResources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbyPlayer.sln"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        var appXaml = File.ReadAllText(Path.Combine(directory.FullName, "src", "EmbyPlayer.App", "App.xaml"));
        const string startMarker = "<ResourceDictionary>";
        const string endMarker = "</ResourceDictionary>";
        var start = appXaml.IndexOf(startMarker, StringComparison.Ordinal);
        var end = appXaml.LastIndexOf(endMarker, StringComparison.Ordinal);
        var dictionary = appXaml[start..(end + endMarker.Length)].Replace(startMarker,
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
            + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" "
            + "xmlns:controls=\"clr-namespace:EmbyPlayer.UI.Controls;assembly=EmbyPlayer.UI\" "
            + "xmlns:primitives=\"clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework\">",
            StringComparison.Ordinal);
        return (ResourceDictionary)XamlReader.Parse(dictionary);
    }
}
