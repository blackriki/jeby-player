using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Playback;
using EmbyPlayer.Player;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.Pages;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class PlayerEnhancementRuntimeTests
{
    [TestMethod]
    public void LocalPreviewHover_MovementKeepsDecodeAliveAndSamplesLatestTargetWithoutOldImage()
    {
        RunOnSta(() => WithPage(0, (page, oldVm, player, _, window) =>
        {
            var server = new RuntimeUnavailablePreview();
            var local = new RuntimeDeferredLocalPreview();
            var sessions = new CurrentSessionService();
            sessions.SetSession(new("http://media.local", "test-token", "user", "User", "server"));
            var navigation = new NavigationService(); navigation.NavigateTo(AppPage.Player);
            var vm = new PlayerViewModel(navigation, player, new TestPlaybackReportService(), sessions,
                new TestAuthSessionStore(), _ => { }, new TestPlaybackReportScheduler(), new TestAppSettingsService(),
                playbackPreviewService: server, localPlaybackPreviewService: local);
            vm.Load(new(oldVm.PlaybackInfo! with { RequiresTranscoding = false }, new("movie-1", AppPage.Library)));
            page.DataContext = vm; Pump();
            var seeks = player.SeekCallCount;

            MovePreviewHoverToEdge(page, window, left: true);
            PumpUntil(() => local.Requests.Count == 1);
            Assert.AreEqual(TimeSpan.Zero, local.Requests[0].Position);
            Assert.AreEqual(1, server.Calls);
            var hoverToken = server.LastToken;
            MovePreviewHoverToEdge(page, window, left: false);
            MovePreviewHoverToEdge(page, window, left: true);
            MovePreviewHoverToEdge(page, window, left: false);
            Assert.AreEqual(1, local.Requests.Count, "Movement must not queue competing foreground decodes.");
            Assert.IsFalse(local.Requests[0].Token.IsCancellationRequested);
            Assert.IsFalse(hoverToken.IsCancellationRequested, "Changing targets must not cancel a reusable server download.");
            local.Requests[0].Completion.SetResult(new(CreatePreviewPng(), TimeSpan.Zero, PlaybackPreviewError.None));
            PumpUntil(() => local.Requests.Count == 2);
            Assert.AreEqual(Visibility.Collapsed, ToolsField<Image>(page, "seekThumbnailImage").Visibility);
            Assert.AreEqual(TimeSpan.FromMinutes(90), local.Requests[1].Position);
            local.Requests[1].Completion.SetResult(new(CreatePreviewPng(), TimeSpan.FromMinutes(90), PlaybackPreviewError.None));
            PumpUntil(() => ToolsField<Image>(page, "seekThumbnailImage").Visibility == Visibility.Visible);
            Assert.AreEqual("1:30:00", ToolsField<TextBlock>(page, "seekThumbnailTime").Text);
            Assert.AreEqual(seeks, player.SeekCallCount);
            InvokeTools(page, "HideSeekThumbnail");
            Assert.IsNull(ToolsField<Image>(page, "seekThumbnailImage").Source);
        }));
    }

    [TestMethod]
    public void LocalPreviewHover_KeepsFrameOnColdTargetAndCachedFrameWinsOverLateOldTarget()
    {
        RunOnSta(() => WithPage(0, (page, oldVm, player, _, window) =>
        {
            var server = new RuntimeUnavailablePreview();
            var local = new RuntimeDeferredLocalPreview();
            var sessions = new CurrentSessionService();
            sessions.SetSession(new("http://media.local", "test-token", "user", "User", "server"));
            var vm = new PlayerViewModel(new NavigationService(), player, new TestPlaybackReportService(), sessions,
                new TestAuthSessionStore(), _ => { }, new TestPlaybackReportScheduler(), new TestAppSettingsService(),
                playbackPreviewService: server, localPlaybackPreviewService: local);
            vm.Load(new(oldVm.PlaybackInfo! with { RequiresTranscoding = false }, new("movie-1", AppPage.Library)));
            page.DataContext = vm; Pump();
            var seekCalls = player.SeekCallCount;
            MovePreviewHoverToEdge(page, window, left: true);
            PumpUntil(() => local.Requests.Count == 1);
            local.Requests[0].Completion.SetResult(new(CreatePreviewPng(), TimeSpan.Zero, PlaybackPreviewError.None));
            PumpUntil(() => ToolsField<Image>(page, "seekThumbnailImage").Visibility == Visibility.Visible);
            var firstFrame = ToolsField<Image>(page, "seekThumbnailImage").Source;

            MovePreviewHoverToEdge(page, window, left: false);
            Assert.AreEqual(Visibility.Visible, ToolsField<Image>(page, "seekThumbnailImage").Visibility);
            Assert.AreSame(firstFrame, ToolsField<Image>(page, "seekThumbnailImage").Source);
            var previewContent = (StackPanel)ToolsField<Border>(page, "seekThumbnailCard").Child;
            Assert.AreEqual(1, previewContent.Children.OfType<TextBlock>().Count(), "The preview shows only the pointer target time.");
            Assert.AreEqual("1:30:00", ToolsField<TextBlock>(page, "seekThumbnailTime").Text);
            PumpUntil(() => local.Requests.Count == 2);

            local.Cache[TimeSpan.Zero] = new(CreatePreviewPng(), TimeSpan.Zero, PlaybackPreviewError.None);
            MovePreviewHoverToEdge(page, window, left: true);
            var cachedFrame = ToolsField<Image>(page, "seekThumbnailImage").Source;
            Assert.IsNotNull(cachedFrame);
            Assert.AreNotSame(firstFrame, cachedFrame, "The cached frame must be installed synchronously on movement.");
            Assert.IsFalse(local.Requests[1].Token.IsCancellationRequested);
            local.Requests[1].Completion.SetResult(new(CreatePreviewPng(), TimeSpan.FromMinutes(90), PlaybackPreviewError.None));
            PumpUntil(() => local.Returned == 2); Pump();
            Assert.AreSame(cachedFrame, ToolsField<Image>(page, "seekThumbnailImage").Source,
                "A late frame for the old target must not overwrite the current cached frame.");
            Assert.AreEqual("00:00", ToolsField<TextBlock>(page, "seekThumbnailTime").Text);
            Assert.AreEqual(seekCalls, player.SeekCallCount);
            InvokeTools(page, "HideSeekThumbnail");
            Assert.IsTrue(local.PrefetchTokens.Count > 0);
            Assert.IsTrue(local.PrefetchTokens.All(token => !token.IsCancellationRequested));
        }));
    }

    [TestMethod]
    public void LocalPreviewHover_LeavingDuringLocalDecodeKeepsLateResultHidden()
    {
        RunOnSta(() => WithPage(0, (page, oldVm, player, _, window) =>
        {
            var server = new RuntimeUnavailablePreview();
            var local = new RuntimeDeferredLocalPreview();
            var sessions = new CurrentSessionService();
            sessions.SetSession(new("http://media.local", "test-token", "user", "User", "server"));
            var vm = new PlayerViewModel(new NavigationService(), player, new TestPlaybackReportService(), sessions,
                new TestAuthSessionStore(), _ => { }, new TestPlaybackReportScheduler(), new TestAppSettingsService(),
                playbackPreviewService: server, localPlaybackPreviewService: local);
            vm.Load(new(oldVm.PlaybackInfo! with { RequiresTranscoding = false }, new("movie-1", AppPage.Library)));
            page.DataContext = vm; Pump();
            MovePreviewHoverToEdge(page, window, left: true);
            PumpUntil(() => local.Requests.Count == 1);
            InvokeTools(page, "HideSeekThumbnail");
            Assert.IsTrue(local.Requests[0].Token.IsCancellationRequested);
            local.Requests[0].Completion.SetResult(new(CreatePreviewPng(), TimeSpan.Zero, PlaybackPreviewError.None));
            PumpUntil(() => local.Returned == 1); Pump();
            Assert.AreEqual(Visibility.Collapsed, ToolsField<Border>(page, "seekThumbnailCard").Visibility);
            Assert.AreEqual(Visibility.Collapsed, ToolsField<Image>(page, "seekThumbnailImage").Visibility);
            Assert.IsNull(ToolsField<Image>(page, "seekThumbnailImage").Source);
        }));
    }

    private static void MovePreviewHoverToEdge(PlayerPage page, Window window, bool left)
    {
        // Relocate only the invisible fixture HWND; never move the user's cursor.
        var cursor = window.PointToScreen(Mouse.GetPosition(window));
        var cursorDip = PresentationSource.FromVisual(window)!.CompositionTarget!.TransformFromDevice.Transform(cursor);
        window.Left = cursorDip.X - (left ? -50 : window.Width + 50);
        window.Top = cursorDip.Y - window.Height / 2;
        Pump();
        InvokeTools(page, "OnSeekThumbnailMove", page.FindName("SeekSlider"), new MouseEventArgs(Mouse.PrimaryDevice, 0));
    }

    private sealed class RuntimeUnavailablePreview : IPlaybackPreviewService
    {
        public int Calls;
        public CancellationToken LastToken;
        public Task<PlaybackPreviewResult> GetPreviewAsync(AuthSession session, string id, TimeSpan position, CancellationToken token)
        { Calls++; LastToken = token; return Task.FromResult(PlaybackPreviewResult.Failure(PlaybackPreviewError.Unavailable)); }
    }

    private sealed class RuntimeDeferredLocalPreview : ILocalPlaybackPreviewService
    {
        public List<RuntimeLocalRequest> Requests { get; } = [];
        public Dictionary<TimeSpan, PlaybackPreviewResult> Cache { get; } = [];
        public List<CancellationToken> PrefetchTokens { get; } = [];
        public int Returned;
        public async Task<PlaybackPreviewResult> GetPreviewAsync(long instance, PlaybackInfo info, TimeSpan position, CancellationToken token)
        {
            if (Cache.TryGetValue(position, out var cached)) return cached;
            var request = new RuntimeLocalRequest(position, token);
            Requests.Add(request);
            var result = await request.Completion.Task;
            Returned++;
            return result;
        }
        public Task ReleaseAsync(long? playbackInstanceId = null) => Task.CompletedTask;
        public PlaybackPreviewResult? TryGetCachedPreview(long instance, PlaybackInfo info, TimeSpan position)
            => Cache.GetValueOrDefault(position);
        public void Prefetch(long instance, PlaybackInfo info, TimeSpan position, CancellationToken token)
            => PrefetchTokens.Add(token);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record RuntimeLocalRequest(TimeSpan Position, CancellationToken Token)
    {
        public TaskCompletionSource<PlaybackPreviewResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
