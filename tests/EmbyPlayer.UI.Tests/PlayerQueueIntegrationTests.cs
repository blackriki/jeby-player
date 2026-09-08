using EmbyPlayer.Core.Playback;
using EmbyPlayer.Core.PlaybackQueue;
using EmbyPlayer.Player;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class PlayerViewModelTests
{
    [TestMethod]
    public async Task Queue_NaturalCompletionUsesReorderedPendingAndContinuesAcrossMovies()
    {
        var queue = new InMemoryPlaybackQueueService();
        var context = await StartQueueContextAsync(queue);
        queue.Add(new("a", "A", "Movie"));
        queue.Add(new("b", "B", "Movie"));
        queue.Move("b", 0);
        ConfigureQueuePlayback(context);
        var old = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseStatusChanged(new(old, PlayerPlaybackState.Completed));
        await UntilQueueAsync(() => queue.Snapshot.Current?.ItemId == "b" && !context.ViewModel.IsAdvancingQueue);
        Assert.AreEqual("a", queue.Snapshot.Pending.Single().ItemId);
        context.PlayerService.RaiseStatusChanged(new(old, PlayerPlaybackState.Completed));
        Assert.AreEqual(1, context.PlaybackPreparationService.PrepareCallCount);
        context.PlayerService.RaiseStatusChanged(new(context.PlayerService.LastLoadRequest!.PlaybackInstanceId, PlayerPlaybackState.Completed));
        await UntilQueueAsync(() => queue.Snapshot.Current?.ItemId == "a" && !context.ViewModel.IsAdvancingQueue);
        Assert.AreEqual(0, queue.Snapshot.Pending.Count);
        Assert.AreEqual(2, context.PlaybackReportService.StoppedCallCount);
    }

    [TestMethod]
    public async Task Queue_AutoPlayOffLeavesPendingAndReportsCompletion()
    {
        var queue = new InMemoryPlaybackQueueService();
        var context = await StartQueueContextAsync(queue);
        queue.Add(new("a", "A", "Movie"));
        queue.SetAutoPlayEnabled(false);
        context.PlayerService.RaiseStatusChanged(new(context.PlayerService.LastLoadRequest!.PlaybackInstanceId, PlayerPlaybackState.Completed));
        await UntilQueueAsync(() => context.PlaybackReportService.StoppedCallCount == 1);
        Assert.AreEqual(0, context.PlaybackPreparationService.PrepareCallCount);
        Assert.AreEqual("a", queue.Snapshot.Pending.Single().ItemId);
    }

    [TestMethod]
    public async Task Queue_PreparationFailureKeepsCurrentAndPending()
    {
        var queue = new InMemoryPlaybackQueueService();
        var context = await StartQueueContextAsync(queue);
        var next = new PlaybackQueueItem("a", "A", "Movie"); queue.Add(next);
        var stops = context.PlayerService.StopCallCount;
        context.PlaybackPreparationService.PreparePlaybackAsyncHandler = (_, _, _) => Task.FromResult(PlaybackLoadResult.Failure(PlaybackLoadError.Forbidden));
        var error = await context.ViewModel.PlayQueueItemAsync(next, CancellationToken.None);
        StringAssert.Contains(error!, "没有权限");
        Assert.AreEqual("item-1", queue.Snapshot.Current!.ItemId);
        Assert.AreEqual("a", queue.Snapshot.Pending.Single().ItemId);
        Assert.AreEqual(stops, context.PlayerService.StopCallCount);
        Assert.IsTrue(context.ViewModel.IsPlaying);
    }

    [TestMethod]
    public async Task Queue_NativeFailureKeepsCandidateForRetryAndStopsItsSession()
    {
        var queue = new InMemoryPlaybackQueueService();
        var context = await StartQueueContextAsync(queue);
        var next = new PlaybackQueueItem("a", "A", "Movie"); queue.Add(next);
        ConfigureQueuePlayback(context);
        context.PlayerService.LoadAsyncHandler = (_, _) => Task.FromResult(PlayerOperationResult.Success());
        var changing = context.ViewModel.PlayQueueItemAsync(next, CancellationToken.None);
        Assert.AreEqual("item-1", queue.Snapshot.Current!.ItemId);
        context.PlayerService.RaiseStatusChanged(new(context.PlayerService.LastLoadRequest!.PlaybackInstanceId, PlayerPlaybackState.Failed, PlayerError.LoadFailed));
        var error = await changing.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsNotNull(error);
        Assert.AreEqual("a", queue.Snapshot.Pending.Single().ItemId);
        Assert.AreEqual(2, context.PlaybackReportService.StoppedCallCount);
        Assert.IsFalse(context.ViewModel.IsAdvancingQueue);
    }

    [TestMethod]
    public async Task Queue_BackCancelsLatePreparationWithoutConsumingPending()
    {
        var queue = new InMemoryPlaybackQueueService();
        var context = await StartQueueContextAsync(queue);
        var next = new PlaybackQueueItem("a", "A", "Movie"); queue.Add(next);
        var pending = new TaskCompletionSource<PlaybackLoadResult>();
        context.PlaybackPreparationService.PreparePlaybackAsyncHandler = (_, _, _) => pending.Task;
        var changing = context.ViewModel.PlayQueueItemAsync(next, CancellationToken.None);
        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();
        var loads = context.PlayerService.LoadCallCount;
        pending.SetResult(PlaybackLoadResult.Success(CreatePlaybackInfo() with { ItemId = "a", MediaType = "Movie" }));
        await changing;
        Assert.AreEqual(loads, context.PlayerService.LoadCallCount);
        Assert.AreEqual("a", queue.Snapshot.Pending.Single().ItemId);
        Assert.IsNull(queue.Snapshot.Current);
    }

    [TestMethod]
    public async Task Queue_AccountChangeRejectsPreparedOldAccountItem()
    {
        var queue = new InMemoryPlaybackQueueService();
        var context = await StartQueueContextAsync(queue);
        var next = new PlaybackQueueItem("a", "A", "Movie"); queue.Add(next);
        var pending = new TaskCompletionSource<PlaybackLoadResult>();
        context.PlaybackPreparationService.PreparePlaybackAsyncHandler = (_, _, _) => pending.Task;
        var changing = context.ViewModel.PlayQueueItemAsync(next, CancellationToken.None);
        queue.SetSession(context.Session.ServerBase, "other-user");
        var loads = context.PlayerService.LoadCallCount;
        pending.SetResult(PlaybackLoadResult.Success(CreatePlaybackInfo() with { ItemId = "a", MediaType = "Movie" }));
        await changing;
        Assert.AreEqual(loads, context.PlayerService.LoadCallCount);
        Assert.IsNull(queue.Snapshot.Current);
        Assert.AreEqual(0, queue.Snapshot.Pending.Count);
    }

    [TestMethod]
    public async Task Queue_NextItemPreservesVolumeMuteFullscreenButResetsSubtitleAdjustments()
    {
        var queue = new InMemoryPlaybackQueueService();
        var context = await StartQueueContextAsync(queue);
        await context.ViewModel.SetVolumeAsync(37);
        await context.ViewModel.ToggleMuteAsync();
        context.ViewModel.SetFullscreen(true);
        await context.ViewModel.AdjustSubtitleAsync("later");
        var next = new PlaybackQueueItem("a", "A", "Movie"); queue.Add(next);
        ConfigureQueuePlayback(context);
        Assert.IsNull(await context.ViewModel.PlayQueueItemAsync(next, CancellationToken.None));
        Assert.AreEqual(37, context.ViewModel.Volume);
        Assert.IsTrue(context.ViewModel.IsMuted);
        Assert.IsTrue(context.ViewModel.IsFullscreen);
        Assert.AreEqual(0d, context.ViewModel.SubtitleDelaySeconds);
        Assert.AreEqual("a", queue.Snapshot.Current!.ItemId);
    }

    [TestMethod]
    public async Task Queue_EofPreparationFailureDoesNotRetryAutomatically()
    {
        var queue = new InMemoryPlaybackQueueService();
        var context = await StartQueueContextAsync(queue);
        queue.Add(new("a", "A", "Movie"));
        context.PlaybackPreparationService.PreparePlaybackAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackLoadResult.Failure(PlaybackLoadError.Forbidden));
        context.PlayerService.RaiseStatusChanged(new(context.PlayerService.LastLoadRequest!.PlaybackInstanceId, PlayerPlaybackState.Completed));
        await UntilQueueAsync(() => context.PlaybackReportService.StoppedCallCount == 1 && !context.ViewModel.IsAdvancingQueue);
        Assert.AreEqual(1, context.PlaybackPreparationService.PrepareCallCount);
        Assert.AreEqual("a", queue.Snapshot.Pending.Single().ItemId);
    }

    [TestMethod]
    public async Task Queue_ShortItemCompletingDuringLoadStillAdvancesOnce()
    {
        var queue = new InMemoryPlaybackQueueService();
        var context = await StartQueueContextAsync(queue);
        var next = new PlaybackQueueItem("a", "A", "Movie"); queue.Add(next);
        queue.Add(new("b", "B", "Movie"));
        ConfigureQueuePlayback(context);
        context.PlayerService.LoadAsyncHandler = (request, _) =>
        {
            context.PlayerService.RaiseStatusChanged(new(request.PlaybackInstanceId, PlayerPlaybackState.Playing));
            if (context.ViewModel.PlaybackInfo!.ItemId == "a")
                context.PlayerService.RaiseStatusChanged(new(request.PlaybackInstanceId, PlayerPlaybackState.Completed));
            return Task.FromResult(PlayerOperationResult.Success());
        };
        Assert.IsNull(await context.ViewModel.PlayQueueItemAsync(next, CancellationToken.None));
        await UntilQueueAsync(() => queue.Snapshot.Current?.ItemId == "b" && !context.ViewModel.IsAdvancingQueue);
        Assert.AreEqual(2, context.PlaybackPreparationService.PrepareCallCount);
        Assert.AreEqual(0, queue.Snapshot.Pending.Count);
    }

    [TestMethod]
    public async Task Queue_CancelAcceptedLoadStopsAndIgnoresLateReady()
    {
        var queue = new InMemoryPlaybackQueueService();
        var context = await StartQueueContextAsync(queue);
        var next = new PlaybackQueueItem("a", "A", "Movie"); queue.Add(next);
        ConfigureQueuePlayback(context);
        context.PlayerService.LoadAsyncHandler = (_, _) => Task.FromResult(PlayerOperationResult.Success());
        using var cancellation = new CancellationTokenSource();
        var changing = context.ViewModel.PlayQueueItemAsync(next, cancellation.Token);
        var instance = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        cancellation.Cancel();
        await changing.WaitAsync(TimeSpan.FromSeconds(3));
        context.PlayerService.RaiseStatusChanged(new(instance, PlayerPlaybackState.Playing));
        Assert.IsFalse(context.ViewModel.IsPlaying);
        Assert.IsTrue(context.ViewModel.HasError);
        Assert.AreEqual("a", queue.Snapshot.Pending.Single().ItemId);
        Assert.AreEqual(2, context.PlaybackReportService.StoppedCallCount);
        ConfigureQueuePlayback(context);
        Assert.IsNull(await context.ViewModel.PlayQueueItemAsync(next, CancellationToken.None));
        Assert.AreEqual("a", queue.Snapshot.Current!.ItemId);
        Assert.IsTrue(context.ViewModel.IsPlaying);
    }

    [TestMethod]
    public async Task Queue_BrowseUnauthorizedCleanupCannotClearNewSession()
    {
        var queue = new InMemoryPlaybackQueueService();
        var context = CreateContext(queue);
        queue.SetSession(context.Session.ServerBase, context.Session.UserId);
        context.NavigationService.NavigateTo(AppPage.PlaybackQueue);
        var next = new PlaybackQueueItem("a", "A", "Movie"); queue.Add(next);
        context.PlaybackPreparationService.PreparePlaybackAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackLoadResult.Failure(PlaybackLoadError.Unauthorized));
        var cleanup = new TaskCompletionSource();
        context.AuthSessionStore.ClearAsyncHandler = _ => cleanup.Task;
        var changing = context.ViewModel.PlayQueueItemAsync(next, CancellationToken.None);
        var newSession = new EmbyPlayer.Core.Authentication.AuthSession(context.Session.ServerBase, "new-token", "other-user", "Other", "server-1");
        context.CurrentSessionService.SetSession(newSession);
        queue.SetSession(newSession.ServerBase, newSession.UserId);
        queue.Add(new("new", "New", "Movie"));
        cleanup.SetResult();
        await changing;
        Assert.AreSame(newSession, context.CurrentSessionService.CurrentSession);
        Assert.AreEqual(AppPage.PlaybackQueue, context.NavigationService.CurrentPage);
        Assert.AreEqual("new", queue.Snapshot.Pending.Single().ItemId);
    }

    [TestMethod]
    public async Task Queue_BrowseNativeFailureReportsStoppedAndKeepsPending()
    {
        var queue = new InMemoryPlaybackQueueService();
        var context = CreateContext(queue);
        queue.SetSession(context.Session.ServerBase, context.Session.UserId);
        var next = new PlaybackQueueItem("a", "A", "Movie"); queue.Add(next);
        context.NavigationService.NavigateTo(AppPage.Player);
        context.ViewModel.Load(new PlayerNavigationParameter(CreatePlaybackInfo() with { ItemId = "a", MediaType = "Movie" },
            new DetailNavigationParameter("a", AppPage.PlaybackQueue), IsQueuedPlayback: true));
        context.PlayerService.LoadAsyncHandler = (_, _) => Task.FromResult(PlayerOperationResult.Success());
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(123));
        context.PlayerService.RaiseStatusChanged(new(context.PlayerService.LastLoadRequest!.PlaybackInstanceId, PlayerPlaybackState.Failed, PlayerError.LoadFailed));
        await UntilQueueAsync(() => context.PlaybackReportService.StoppedCallCount == 1);
        Assert.AreEqual("a", queue.Snapshot.Pending.Single().ItemId);
        Assert.IsNull(queue.Snapshot.Current);
    }

    [TestMethod]
    public async Task Queue_BrowseRejectedLoadReportsStoppedAndKeepsPending()
    {
        var queue = new InMemoryPlaybackQueueService();
        var context = CreateContext(queue);
        queue.SetSession(context.Session.ServerBase, context.Session.UserId);
        queue.Add(new("a", "A", "Movie"));
        context.NavigationService.NavigateTo(AppPage.Player);
        context.ViewModel.Load(new PlayerNavigationParameter(CreatePlaybackInfo() with { ItemId = "a", MediaType = "Movie" },
            new DetailNavigationParameter("a", AppPage.PlaybackQueue), IsQueuedPlayback: true));
        context.PlayerService.LoadAsyncHandler = (_, _) => Task.FromResult(PlayerOperationResult.Failure(PlayerError.LoadFailed));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(123));
        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
        Assert.AreEqual("a", queue.Snapshot.Pending.Single().ItemId);
        Assert.IsTrue(context.ViewModel.HasError);
    }

    private static async Task<PlayerViewModelTestContext> StartQueueContextAsync(InMemoryPlaybackQueueService queue)
    {
        var context = CreateContext(queue);
        queue.SetSession(context.Session.ServerBase, context.Session.UserId);
        context.NavigationService.NavigateTo(AppPage.Player);
        await StartLoadedPlayerAsync(context, CreatePlaybackInfo() with { MediaType = "Movie" });
        return context;
    }

    private static void ConfigureQueuePlayback(PlayerViewModelTestContext context)
    {
        context.PlaybackPreparationService.PreparePlaybackAsyncHandler = (_, request, _) => Task.FromResult(
            PlaybackLoadResult.Success(CreatePlaybackInfoForRequest(request)));
        context.PlayerService.LoadAsyncHandler = (request, _) =>
        {
            context.PlayerService.RaiseStatusChanged(new(request.PlaybackInstanceId, PlayerPlaybackState.Playing));
            return Task.FromResult(PlayerOperationResult.Success());
        };
    }

    private static async Task UntilQueueAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
