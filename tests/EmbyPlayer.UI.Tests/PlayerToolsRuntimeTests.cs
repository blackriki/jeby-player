using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Playback;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.Pages;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class PlayerEnhancementRuntimeTests
{
    [TestMethod]
    public void SeekPreviewEdgesStayWithinMiniCompactAndWideWindowsWithoutSeeking()
    {
        RunOnSta(() => WithPage(0, (page, vm, player, _, window) =>
        {
            var seekCount = player.SeekCallCount;
            foreach (var size in new[] { new Size(480, 270), new Size(640, 360), new Size(1100, 700) })
            {
                if (size.Width == 480) ((Button)page.FindName("MiniPlayerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else if (size.Width == 640) ((Button)page.FindName("MiniPlayerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Width = size.Width; window.Height = size.Height;
                vm.ShowControlsOverlay(); Pump();
                var overlay = (FrameworkElement)page.FindName("OverlayRoot");
                foreach (var leftEdge in new[] { true, false })
                {
                    // Move the transparent test HWND relative to the existing cursor; never move the user's cursor.
                    var cursor = window.PointToScreen(Mouse.GetPosition(window));
                    var transform = PresentationSource.FromVisual(window)!.CompositionTarget!.TransformFromDevice;
                    var cursorDip = transform.Transform(cursor);
                    window.Left = cursorDip.X - (leftEdge ? -50 : size.Width + 50);
                    window.Top = cursorDip.Y - size.Height / 2;
                    Pump();
                    InvokeTools(page, "OnSeekThumbnailMove", page.FindName("SeekSlider"), new MouseEventArgs(Mouse.PrimaryDevice, 0));
                    Pump();
                    var card = ToolsField<Border>(page, "seekThumbnailCard");
                    Assert.AreEqual(Visibility.Visible, card.Visibility);
                    var bounds = card.TransformToAncestor(overlay).TransformBounds(new Rect(card.RenderSize));
                    Assert.IsTrue(bounds.Left >= 7 && bounds.Right <= overlay.ActualWidth - 7, $"{size}, left={leftEdge}: {bounds}");
                    Assert.IsTrue(bounds.Top >= 7 && bounds.Bottom <= overlay.ActualHeight, $"{size}: {bounds}");
                    Assert.AreEqual(leftEdge ? "00:00" : "1:30:00", ToolsField<TextBlock>(page, "seekThumbnailTime").Text);
                    ToolsField<Image>(page, "seekThumbnailImage").Visibility = Visibility.Visible;
                    InvokeTools(page, "PositionSeekThumbnail");
                    Pump();
                    bounds = card.TransformToAncestor(overlay).TransformBounds(new Rect(card.RenderSize));
                    Assert.IsTrue(bounds.Left >= 7 && bounds.Right <= overlay.ActualWidth - 7 && bounds.Top >= 7
                        && bounds.Bottom <= overlay.ActualHeight, $"Image preview at {size}: {bounds}");
                    InvokeTools(page, "HideSeekThumbnail");
                }
            }
            Assert.AreEqual(seekCount, player.SeekCallCount, "Hovering must never move playback.");
        }));
    }

    [TestMethod]
    public void SeekPreviewLateImageAfterLeaveStaysHidden()
    {
        RunOnSta(() => WithPage(0, (page, oldVm, player, _, _) =>
        {
            var preview = new DeferredPreview();
            var sessions = new CurrentSessionService();
            sessions.SetSession(new("http://media.local", "test-token", "user", "User", "server"));
            var navigation = new NavigationService(); navigation.NavigateTo(AppPage.Player);
            var vm = new PlayerViewModel(navigation, player, new TestPlaybackReportService(), sessions,
                new TestAuthSessionStore(), _ => { }, new TestPlaybackReportScheduler(), new TestAppSettingsService(),
                playbackPreviewService: preview);
            vm.Load(new(oldVm.PlaybackInfo!, new("movie-1", AppPage.Library)));
            page.DataContext = vm; Pump();
            var seekCount = player.SeekCallCount;
            InvokeTools(page, "OnSeekThumbnailMove", page.FindName("SeekSlider"), new MouseEventArgs(Mouse.PrimaryDevice, 0));
            PumpUntil(() => preview.Calls == 1);
            InvokeTools(page, "HideSeekThumbnail");
            preview.Completion.SetResult(new(CreatePreviewPng(), TimeSpan.Zero, PlaybackPreviewError.None));
            PumpUntil(() => preview.Returned);
            Pump();
            Assert.AreEqual(Visibility.Collapsed, ToolsField<Border>(page, "seekThumbnailCard").Visibility);
            Assert.AreEqual(Visibility.Collapsed, ToolsField<Image>(page, "seekThumbnailImage").Visibility);
            Assert.IsNull(ToolsField<Image>(page, "seekThumbnailImage").Source);
            Assert.AreEqual(seekCount, player.SeekCallCount);
        }));
    }

    [TestMethod]
    public void SubtitleImportRemainsReachableThroughCompactMenuAndValidatesDropShape()
    {
        RunOnSta(() => WithPage(24, (page, _, _, _, window) =>
        {
            foreach (var size in new[] { new Size(480, 270), new Size(640, 360), new Size(1100, 700) })
            {
                if (size.Width == 480) ((Button)page.FindName("MiniPlayerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else if (size.Width == 640) ((Button)page.FindName("MiniPlayerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Width = size.Width; window.Height = size.Height; Pump();
                if (size.Width < 900)
                {
                    Open(page, "MoreMenuButton", Popup(page, "MorePopup"));
                    var subtitles = Descendants<Button>(Popup(page, "MorePopup").Child).Single(b => Equals(b.Tag, "Subtitle"));
                    subtitles.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                }
                else Open(page, "SubtitleMenuButton", Popup(page, "SubtitlePopup"));
                var popup = Popup(page, "SubtitlePopup");
                Assert.IsTrue(popup.IsOpen);
                var import = (Button)page.FindName("ImportSubtitleButton");
                import.BringIntoView(); Pump();
                Assert.IsTrue(import.IsVisible && import.IsEnabled && import.IsTabStop);
                var root = (FrameworkElement)popup.Child;
                var bounds = import.TransformToAncestor(root).TransformBounds(new Rect(import.RenderSize));
                Assert.IsTrue(bounds.Top >= -1 && bounds.Bottom <= root.ActualHeight + 1, $"{size}: {bounds}/{root.RenderSize}");
                popup.IsOpen = false; Pump();
            }
            foreach (var file in new[] { "one.SRT", "one.ass", "one.ssa", "one.vtt" })
                Assert.AreEqual(file, Dropped(new[] { file }));
            Assert.IsNull(Dropped(new[] { "one.exe" }));
            Assert.IsNull(Dropped(new[] { "one.srt", "two.srt" }));
            Assert.IsNull(Dropped(Array.Empty<string>()));
            Assert.IsNull(typeof(PlayerPage).GetMethod("GetDroppedSubtitle", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { new DataObject(DataFormats.Text, "one.srt") }));
        }));
    }

    private static object? Dropped(string[] paths) => typeof(PlayerPage).GetMethod("GetDroppedSubtitle", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, new object[] { new DataObject(DataFormats.FileDrop, paths) });
    private static T ToolsField<T>(PlayerPage page, string name) => (T)typeof(PlayerPage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
    private static object? InvokeTools(PlayerPage page, string name, params object[] args) => typeof(PlayerPage).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args);
    private static void PumpUntil(Func<bool> done)
    {
        var limit = DateTime.UtcNow.AddSeconds(3);
        while (!done() && DateTime.UtcNow < limit)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        Assert.IsTrue(done(), "Preview operation exceeded its deadline.");
    }
    private static byte[] CreatePreviewPng()
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 255, 0, 0, 255 }, 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private sealed class DeferredPreview : IPlaybackPreviewService
    {
        public TaskCompletionSource<PlaybackPreviewResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public bool Returned { get; private set; }
        public async Task<PlaybackPreviewResult> GetPreviewAsync(AuthSession session, string itemId, TimeSpan position, CancellationToken cancellationToken)
        {
            Calls++;
            var result = await Completion.Task;
            Returned = true;
            return result;
        }
    }
}
