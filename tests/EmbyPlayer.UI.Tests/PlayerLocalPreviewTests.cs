using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Playback;
using EmbyPlayer.Player;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class PlayerViewModelTests
{
    [TestMethod]
    public async Task LocalPreview_CacheOnlyBecomesEligibleAfterCurrentSourceBifUnavailable()
    {
        var context = new PreviewContext();
        await context.StartAsync();
        var image = new PlaybackPreviewResult([10], TimeSpan.FromSeconds(20), PlaybackPreviewError.None);
        context.Local.Cached = image;
        Assert.IsNull(context.ViewModel.TryGetSeekPreview(TimeSpan.FromSeconds(20)));
        Assert.AreEqual(0, context.Local.CacheCalls);
        Assert.AreEqual(0, context.Local.Prefetches.Count);
        await context.ViewModel.GetSeekPreviewAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
        var serverCalls = context.Server.Calls;
        Assert.AreSame(image, context.ViewModel.TryGetSeekPreview(TimeSpan.FromSeconds(21)));
        Assert.AreEqual(serverCalls, context.Server.Calls, "A cache hit must not wait for server I/O.");
        Assert.AreEqual(TimeSpan.FromSeconds(21), context.Local.Prefetches.Last().Position);
        Assert.AreSame(context.ViewModel.PlaybackInfo, context.Local.Prefetches.Last().Info);
        Assert.AreEqual(0, context.Player.SeekCallCount);
    }

    [DataTestMethod]
    [DataRow(PlaybackPreviewError.None)]
    [DataRow(PlaybackPreviewError.Forbidden)]
    public async Task LocalPreview_ServerAvailableOrForbiddenNeverEnablesLocalCache(PlaybackPreviewError error)
    {
        var context = new PreviewContext();
        await context.StartAsync();
        context.Local.Cached = new([11], TimeSpan.Zero, PlaybackPreviewError.None);
        context.Server.Handler = (_, _, _, _) => Task.FromResult(error == PlaybackPreviewError.None
            ? new PlaybackPreviewResult([12], TimeSpan.Zero, error) : PlaybackPreviewResult.Failure(error));
        await context.ViewModel.GetSeekPreviewAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.IsNull(context.ViewModel.TryGetSeekPreview(TimeSpan.Zero));
        Assert.AreEqual(0, context.Local.CacheCalls);
        Assert.AreEqual(0, context.Local.Prefetches.Count);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalPreview_CacheEligibilityDoesNotCrossSourceOrSession(bool sessionChanged)
    {
        var context = new PreviewContext();
        await context.StartAsync();
        await context.ViewModel.GetSeekPreviewAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
        context.Local.Cached = new([13], TimeSpan.FromSeconds(20), PlaybackPreviewError.None);
        if (sessionChanged)
            context.Sessions.SetSession(new("http://other.invalid", "other-token", "other-user", "Other", "other-server"));
        else await context.StartAsync(CreatePlaybackInfo() with { ItemId = "replacement-media" });
        var warms = context.Local.Prefetches.Count;
        Assert.IsNull(context.ViewModel.TryGetSeekPreview(TimeSpan.FromSeconds(20)));
        Assert.AreEqual(0, context.Local.CacheCalls);
        Assert.AreEqual(warms, context.Local.Prefetches.Count);
    }

    [TestMethod]
    public async Task LocalPreview_WarmupUsesPlaybackLifetimeAndLatestHoverWhileForegroundCanCancel()
    {
        var context = new PreviewContext();
        await context.StartAsync();
        var pending = new TaskCompletionSource<PlaybackPreviewResult>();
        context.Local.Handler = (_, _, _, _) => pending.Task;
        using var hover = new CancellationTokenSource();
        context.ViewModel.TryGetSeekPreview(TimeSpan.FromSeconds(50));
        var preview = context.ViewModel.GetSeekPreviewAsync(TimeSpan.FromSeconds(20), hover.Token);
        var warm = context.Local.Prefetches.Single();
        Assert.AreEqual(TimeSpan.FromSeconds(50), warm.Position);
        Assert.AreEqual(context.Player.LastLoadRequest!.PlaybackInstanceId, warm.Instance);
        Assert.AreSame(context.ViewModel.PlaybackInfo, warm.Info);
        hover.Cancel();
        Assert.IsFalse(warm.Token.IsCancellationRequested, "Leaving hover must not throw away warm cache generation.");
        pending.SetResult(PlaybackPreviewResult.Failure(PlaybackPreviewError.Cancelled));
        await preview;
        await context.ViewModel.ReleasePlayerAsync();
        Assert.IsTrue(warm.Token.IsCancellationRequested);
        Assert.IsNull(context.ViewModel.TryGetSeekPreview(TimeSpan.FromSeconds(50)));
    }

    [TestMethod]
    public async Task LocalPreview_ServerImageWinsWithoutLocalDecodeOrMainSeek()
    {
        var context = new PreviewContext();
        await context.StartAsync();
        var image = new PlaybackPreviewResult([1, 2, 3], TimeSpan.FromSeconds(30), PlaybackPreviewError.None);
        context.Server.Handler = (_, _, _, _) => Task.FromResult(image);
        Assert.AreSame(image, await context.ViewModel.GetSeekPreviewAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
        Assert.AreEqual(0, context.Local.Calls);
        Assert.AreEqual(0, context.Player.SeekCallCount);
        Assert.IsTrue(context.ViewModel.IsPlaying);
    }

    [TestMethod]
    public async Task LocalPreview_UnavailableDirectSourceUsesExactPlaybackInfoAndRequestedTime()
    {
        var context = new PreviewContext();
        await context.StartAsync();
        var requested = TimeSpan.FromSeconds(42);
        var image = new PlaybackPreviewResult([4, 5], requested, PlaybackPreviewError.None);
        context.Local.Handler = (_, _, _, _) => Task.FromResult(image);
        var result = await context.ViewModel.GetSeekPreviewAsync(requested, CancellationToken.None);
        Assert.AreSame(image, result);
        Assert.AreEqual(1, context.Server.Calls);
        Assert.AreEqual(1, context.Local.Calls);
        Assert.AreSame(context.ViewModel.PlaybackInfo, context.Local.LastInfo);
        Assert.AreEqual(context.Player.LastLoadRequest!.PlaybackInstanceId, context.Local.LastInstance);
        Assert.AreEqual(requested, context.Local.LastPosition);
        Assert.AreEqual(0, context.Player.SeekCallCount);
    }

    [DataTestMethod]
    [DataRow(PlaybackPreviewError.Unauthorized)]
    [DataRow(PlaybackPreviewError.Forbidden)]
    [DataRow(PlaybackPreviewError.ServerTimeout)]
    [DataRow(PlaybackPreviewError.ServerUnreachable)]
    [DataRow(PlaybackPreviewError.InvalidResponse)]
    public async Task LocalPreview_ServerFailureDoesNotBypassWithLocalDecode(PlaybackPreviewError error)
    {
        var context = new PreviewContext();
        await context.StartAsync();
        context.Server.Handler = (_, _, _, _) => Task.FromResult(PlaybackPreviewResult.Failure(error));
        var result = await context.ViewModel.GetSeekPreviewAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.IsNull(result.ImageBytes);
        Assert.AreEqual(0, context.Local.Calls);
        Assert.AreEqual(0, context.Player.SeekCallCount);
        if (error == PlaybackPreviewError.Unauthorized)
        {
            Assert.IsNull(context.Sessions.CurrentSession);
            Assert.AreEqual(AppPage.Login, context.Navigation.CurrentPage);
        }
        else Assert.AreEqual(error, result.Error);
    }

    [TestMethod]
    public async Task LocalPreview_TranscodeUnavailableRemainsTimeOnly()
    {
        var context = new PreviewContext();
        await context.StartAsync(CreatePlaybackInfo() with { RequiresTranscoding = true });
        var result = await context.ViewModel.GetSeekPreviewAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.AreEqual(PlaybackPreviewError.Unavailable, result.Error);
        Assert.AreEqual(0, context.Local.Calls);
        Assert.IsNull(result.ImageBytes);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalPreview_HoverCancellationDiscardsLateImage(bool localStage)
    {
        var context = new PreviewContext();
        await context.StartAsync();
        var pending = new TaskCompletionSource<PlaybackPreviewResult>();
        CancellationToken received = default;
        if (localStage) context.Local.Handler = (_, _, _, token) => { received = token; return pending.Task; };
        else context.Server.Handler = (_, _, _, token) => { received = token; return pending.Task; };
        using var hover = new CancellationTokenSource();
        var preview = context.ViewModel.GetSeekPreviewAsync(TimeSpan.FromSeconds(25), hover.Token);
        hover.Cancel();
        Assert.IsTrue(received.IsCancellationRequested);
        pending.SetResult(new([6], TimeSpan.FromSeconds(25), PlaybackPreviewError.None));
        var result = await preview;
        Assert.AreEqual(PlaybackPreviewError.Cancelled, result.Error);
        Assert.IsNull(result.ImageBytes);
        Assert.AreEqual(0, context.Player.SeekCallCount);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalPreview_DecodeCancellationReachesOnlyLocalOperation(bool ignoresCancellation)
    {
        var context = new PreviewContext();
        await context.StartAsync();
        CancellationToken serverToken = default;
        CancellationToken decoderToken = default;
        context.Server.Handler = (_, _, _, token) =>
        {
            serverToken = token;
            return Task.FromResult(PlaybackPreviewResult.Failure(PlaybackPreviewError.Unavailable));
        };
        var pending = new TaskCompletionSource<PlaybackPreviewResult>();
        context.Local.Handler = (_, _, _, token) => { decoderToken = token; return pending.Task; };
        using var decode = new CancellationTokenSource();
        var preview = context.ViewModel.GetSeekPreviewAsync(TimeSpan.FromSeconds(10), CancellationToken.None, decode.Token);
        decode.Cancel();
        Assert.IsTrue(decoderToken.IsCancellationRequested);
        Assert.IsFalse(serverToken.IsCancellationRequested);
        pending.SetResult(ignoresCancellation ? new([9], TimeSpan.FromSeconds(10), PlaybackPreviewError.None)
            : PlaybackPreviewResult.Failure(PlaybackPreviewError.Cancelled));
        Assert.AreEqual(PlaybackPreviewError.Cancelled, (await preview).Error);
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task LocalPreview_ObsoleteMediaOrAccountCannotDeliverImage(bool localStage, bool sessionChanged)
    {
        var context = new PreviewContext();
        await context.StartAsync();
        var pending = new TaskCompletionSource<PlaybackPreviewResult>();
        if (localStage) context.Local.Handler = (_, _, _, _) => pending.Task;
        else context.Server.Handler = (_, _, _, _) => pending.Task;
        var preview = context.ViewModel.GetSeekPreviewAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
        if (sessionChanged)
            context.Sessions.SetSession(new AuthSession("http://other.invalid", "other-token", "other-user", "Other", "other-server"));
        else await context.StartAsync(CreatePlaybackInfo() with { ItemId = "replacement-media" });
        pending.SetResult(new([7], TimeSpan.FromSeconds(15), PlaybackPreviewError.None));
        var result = await preview;
        Assert.AreEqual(PlaybackPreviewError.Cancelled, result.Error);
        Assert.IsNull(result.ImageBytes);
        if (!localStage) Assert.AreEqual(0, context.Local.Calls);
    }

    [TestMethod]
    public async Task LocalPreview_ReleaseCancelsPendingDecodeAndReleasesMatchingInstance()
    {
        var context = new PreviewContext();
        await context.StartAsync();
        var instance = context.Player.LastLoadRequest!.PlaybackInstanceId;
        var pending = new TaskCompletionSource<PlaybackPreviewResult>();
        CancellationToken received = default;
        context.Local.Handler = (_, _, _, token) => { received = token; return pending.Task; };
        var preview = context.ViewModel.GetSeekPreviewAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
        await context.ViewModel.ReleasePlayerAsync();
        Assert.IsTrue(received.IsCancellationRequested);
        CollectionAssert.Contains(context.Local.Released, instance);
        pending.SetResult(new([8], TimeSpan.FromSeconds(15), PlaybackPreviewError.None));
        Assert.AreEqual(PlaybackPreviewError.Cancelled, (await preview).Error);
    }

    private sealed class PreviewContext
    {
        public TestPlayerService Player { get; } = new();
        public CurrentSessionService Sessions { get; } = new();
        public NavigationService Navigation { get; } = new();
        public PreviewServerStub Server { get; } = new();
        public PreviewLocalStub Local { get; } = new();
        public PlayerViewModel ViewModel { get; }
        public PreviewContext()
        {
            Sessions.SetSession(CreateSession());
            ViewModel = new(Navigation, Player, new TestPlaybackReportService(), Sessions, new TestAuthSessionStore(),
                _ => { }, new TestPlaybackReportScheduler(), playbackPreviewService: Server, localPlaybackPreviewService: Local);
        }
        public async Task StartAsync(PlaybackInfo? info = null)
        {
            ViewModel.Load(CreateNavigationParameter(info ?? CreatePlaybackInfo(runTimeTicks: TimeSpan.FromMinutes(10).Ticks)));
            await ViewModel.AttachVideoHostAsync(new IntPtr(1234));
            Player.RaiseStatusChanged(new(Player.LastLoadRequest!.PlaybackInstanceId, PlayerPlaybackState.Playing));
            Player.RaiseProgressChanged(new(Player.LastLoadRequest.PlaybackInstanceId,
                TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10), 1d / 6d, false));
            Assert.IsTrue(ViewModel.CanSeek);
        }
    }

    private sealed class PreviewServerStub : IPlaybackPreviewService
    {
        public int Calls;
        public Func<AuthSession, string, TimeSpan, CancellationToken, Task<PlaybackPreviewResult>> Handler =
            (_, _, _, _) => Task.FromResult(PlaybackPreviewResult.Failure(PlaybackPreviewError.Unavailable));
        public Task<PlaybackPreviewResult> GetPreviewAsync(AuthSession session, string id, TimeSpan position, CancellationToken token)
        { Calls++; return Handler(session, id, position, token); }
    }

    private sealed class PreviewLocalStub : ILocalPlaybackPreviewService
    {
        public int Calls;
        public long LastInstance;
        public PlaybackInfo? LastInfo;
        public TimeSpan LastPosition;
        public List<long?> Released { get; } = [];
        public List<(long Instance, PlaybackInfo Info, TimeSpan Position, CancellationToken Token)> Prefetches { get; } = [];
        public int CacheCalls;
        public PlaybackPreviewResult? Cached;
        public Func<long, PlaybackInfo, TimeSpan, CancellationToken, Task<PlaybackPreviewResult>> Handler =
            (_, _, _, _) => Task.FromResult(PlaybackPreviewResult.Failure(PlaybackPreviewError.Unavailable));
        public Task<PlaybackPreviewResult> GetPreviewAsync(long instance, PlaybackInfo info, TimeSpan position, CancellationToken token)
        { Calls++; LastInstance = instance; LastInfo = info; LastPosition = position; return Handler(instance, info, position, token); }
        public Task ReleaseAsync(long? playbackInstanceId = null) { Released.Add(playbackInstanceId); return Task.CompletedTask; }
        public PlaybackPreviewResult? TryGetCachedPreview(long instance, PlaybackInfo info, TimeSpan position)
        { CacheCalls++; return Cached; }
        public void Prefetch(long instance, PlaybackInfo info, TimeSpan position, CancellationToken token)
            => Prefetches.Add((instance, info, position, token));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
