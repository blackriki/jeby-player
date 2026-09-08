using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Details;
using EmbyPlayer.Core.PlaybackQueue;
using EmbyPlayer.Core.Series;
using EmbyPlayer.Core.WatchLater;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class MediaDetailViewModelTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PersonalMedia_WatchLaterAddAndRemoveRemainIndependentOfFavoriteAndQueue(bool favorite)
    {
        var store = new TestWatchLaterStore();
        var queue = new InMemoryPlaybackQueueService();
        var context = CreateContext(store, queue);
        queue.SetSession(context.Session.ServerBase, context.Session.UserId);
        queue.Add(new("queued", "Queued movie", "Movie"));
        context.MediaDetailService.LoadDetailAsyncHandler = (_, id, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(id, isFavorite: favorite, isPlayed: true)));
        await context.ViewModel.LoadAsync("movie-1", AppPage.Home);

        await ((AsyncRelayCommand)context.ViewModel.ToggleWatchLaterCommand).ExecuteAsync();

        Assert.IsTrue(context.ViewModel.IsSavedForLater);
        Assert.AreEqual("movie-1", store.Items.Single().ItemId);
        Assert.AreEqual("Movie", store.Items.Single().MediaType);
        Assert.AreEqual(favorite, context.ViewModel.IsFavorite);
        Assert.IsTrue(context.ViewModel.IsPlayed);
        Assert.AreEqual("queued", queue.Snapshot.Pending.Single().ItemId);
        Assert.IsNull(queue.Snapshot.Current);

        await ((AsyncRelayCommand)context.ViewModel.ToggleWatchLaterCommand).ExecuteAsync();

        Assert.IsFalse(context.ViewModel.IsSavedForLater);
        Assert.AreEqual(0, store.Items.Count);
        Assert.AreEqual("movie-1", store.LastRemovedId);
        Assert.AreEqual(favorite, context.ViewModel.IsFavorite);
        Assert.IsTrue(context.ViewModel.IsPlayed);
        Assert.AreEqual(0, context.ItemUserDataService.SetFavoriteCallCount);
        Assert.AreEqual(0, context.ItemUserDataService.SetPlayedCallCount);
        Assert.AreEqual(0, context.PlaybackService.PrepareCallCount);
        Assert.AreEqual("queued", queue.Snapshot.Pending.Single().ItemId);
    }

    [TestMethod]
    public async Task PersonalMedia_FailedWatchLaterRemovalKeepsSavedStateAndFavorite()
    {
        var store = new TestWatchLaterStore();
        store.Items.Add(new("movie-1", "Saved movie", null, "Movie", 2026));
        store.Remove = (_, _, _) => Task.FromException(new IOException("Fixture write failure"));
        var context = CreateContext(store);
        context.MediaDetailService.LoadDetailAsyncHandler = (_, id, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(id, isFavorite: true)));
        await context.ViewModel.LoadAsync("movie-1", AppPage.Home);

        await ((AsyncRelayCommand)context.ViewModel.ToggleWatchLaterCommand).ExecuteAsync();

        Assert.IsTrue(context.ViewModel.IsSavedForLater);
        Assert.IsTrue(context.ViewModel.IsFavorite);
        Assert.AreEqual(1, store.Items.Count);
        Assert.AreEqual(0, context.ItemUserDataService.SetFavoriteCallCount);
        StringAssert.Contains(context.ViewModel.PersonalMediaMessage!, "保存失败");
        Assert.IsTrue(context.ViewModel.ToggleWatchLaterCommand.CanExecute(null));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PersonalMedia_LateWatchLaterReadCannotChangeNewDetailOrItsPendingState(bool oldReadFails)
    {
        var oldRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken = default;
        var store = new SavedStateWatchLaterStore((_, id, token) =>
        {
            if (id == "old-movie") { oldToken = token; return oldRead.Task; }
            return newRead.Task;
        });
        var context = CreateContext(store);
        var oldLoad = context.ViewModel.LoadAsync("old-movie", AppPage.Home);
        var newLoad = context.ViewModel.LoadAsync("new-movie", AppPage.WatchLater);
        Assert.IsTrue(oldToken.IsCancellationRequested);
        Assert.AreEqual("new-movie", context.ViewModel.Detail!.Id);

        if (oldReadFails) oldRead.SetException(new IOException("Fixture old read failure"));
        else oldRead.SetResult(true);
        await oldLoad;

        Assert.AreEqual("new-movie", context.ViewModel.Detail!.Id);
        Assert.IsTrue(context.ViewModel.IsLoading);
        Assert.IsFalse(context.ViewModel.IsSavedForLater);
        Assert.IsFalse(context.ViewModel.ToggleWatchLaterCommand.CanExecute(null));
        Assert.IsNull(context.ViewModel.PersonalMediaMessage);
        newRead.SetResult(false);
        await newLoad;
        Assert.IsFalse(context.ViewModel.IsSavedForLater);
        Assert.IsFalse(context.ViewModel.IsLoading);
        Assert.IsTrue(context.ViewModel.ToggleWatchLaterCommand.CanExecute(null));
        Assert.IsNull(context.ViewModel.PersonalMediaMessage);
    }

    [TestMethod]
    public async Task PersonalMedia_AddMovieToQueuePreservesMetadataAndDeduplicatesWithoutPlaying()
    {
        var queue = new InMemoryPlaybackQueueService();
        var context = CreateContext(queue: queue);
        queue.SetSession(context.Session.ServerBase, context.Session.UserId);
        await context.ViewModel.LoadAsync("movie-1", AppPage.Home);

        context.ViewModel.AddToQueueCommand.Execute(null);
        var item = queue.Snapshot.Pending.Single();
        Assert.AreEqual("movie-1", item.ItemId);
        Assert.AreEqual(context.ViewModel.Detail!.Title, item.Title);
        Assert.AreEqual("Movie", item.MediaType);
        Assert.AreEqual(context.ViewModel.Detail.Year, item.ProductionYear);
        Assert.AreEqual(context.ViewModel.Detail.PosterUrl, item.ImageUrl);
        context.ViewModel.AddToQueueCommand.Execute(null);

        Assert.AreEqual(1, queue.Snapshot.Pending.Count);
        Assert.IsNull(queue.Snapshot.Current);
        Assert.AreEqual(0, context.PlaybackService.PrepareCallCount);
        StringAssert.Contains(context.ViewModel.PersonalMediaMessage!, "已在播放队列");
    }

    [TestMethod]
    public async Task PersonalMedia_SeriesSavesSeriesButQueuesSelectedEpisodeInsteadOfResumeTarget()
    {
        var store = new TestWatchLaterStore();
        var queue = new InMemoryPlaybackQueueService();
        var context = CreateContext(store, queue);
        queue.SetSession(context.Session.ServerBase, context.Session.UserId);
        context.MediaDetailService.LoadDetailAsyncHandler = (_, id, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(id, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) => Task.FromResult(
            SeriesSeasonsLoadResult.Success(new[] { new SeasonInfo("season-1", "第 1 季", 1, true) }));
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, _, _) => Task.FromResult(
            SeriesEpisodesLoadResult.Success(new[]
            {
                new EpisodeInfo("resume-episode", "第 1 集", 1, 1, TimeSpan.FromMinutes(45).Ticks,
                    null, 40, TimeSpan.FromMinutes(18).Ticks, null),
                new EpisodeInfo("selected-episode", "第 2 集", 1, 2, TimeSpan.FromMinutes(45).Ticks,
                    null, null, null, null)
            }));
        await context.ViewModel.LoadAsync("series-1", AppPage.Home);
        Assert.AreEqual("resume-episode", context.ViewModel.SelectedEpisode!.Id);
        context.ViewModel.SelectedEpisode = context.ViewModel.Episodes[1];
        StringAssert.Contains(context.ViewModel.SeriesPlaybackTargetText, "第 1 集");

        context.ViewModel.AddToQueueCommand.Execute(null);
        await ((AsyncRelayCommand)context.ViewModel.ToggleWatchLaterCommand).ExecuteAsync();

        var queued = queue.Snapshot.Pending.Single();
        Assert.AreEqual("selected-episode", queued.ItemId);
        Assert.AreEqual("Episode", queued.MediaType);
        StringAssert.Contains(queued.Title, "第 2 集");
        Assert.AreEqual("series-1", store.Items.Single().ItemId);
        Assert.AreEqual("Series", store.Items.Single().MediaType);
        Assert.AreEqual(0, context.PlaybackService.PrepareCallCount);
        Assert.AreEqual(0, context.ItemUserDataService.SetFavoriteCallCount);
    }

    [TestMethod]
    public async Task PersonalMedia_UnresolvedSeriesCannotEnterQueue()
    {
        var queue = new InMemoryPlaybackQueueService();
        var context = CreateContext(queue: queue);
        queue.SetSession(context.Session.ServerBase, context.Session.UserId);
        context.MediaDetailService.LoadDetailAsyncHandler = (_, id, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(id, "Series")));
        await context.ViewModel.LoadAsync("series-without-episodes", AppPage.Home);

        Assert.IsFalse(context.ViewModel.AddToQueueCommand.CanExecute(null));
        context.ViewModel.AddToQueueCommand.Execute(null);

        Assert.AreEqual(0, queue.Snapshot.Pending.Count);
        Assert.AreEqual(0, context.PlaybackService.PrepareCallCount);
    }

    [DataTestMethod]
    [DataRow("Movie")]
    [DataRow("Episode")]
    public async Task PersonalMedia_PlaybackAndDetailBackPreserveWatchLaterRoute(string mediaType)
    {
        var context = CreateContext(new TestWatchLaterStore());
        context.MediaDetailService.LoadDetailAsyncHandler = (_, id, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(id, mediaType)));
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;
        await context.ViewModel.LoadAsync(new DetailNavigationParameter("saved-item", AppPage.WatchLater));
        await ((AsyncRelayCommand)context.ViewModel.PlayCommand).ExecuteAsync();

        Assert.AreEqual(AppPage.Player, context.NavigationService.CurrentPage);
        Assert.IsInstanceOfType<PlayerNavigationParameter>(navigationParameter);
        var backToDetail = ((PlayerNavigationParameter)navigationParameter!).BackToDetailParameter;
        Assert.AreEqual(AppPage.WatchLater, backToDetail.ReturnPage);
        Assert.AreEqual("saved-item", backToDetail.ItemId);
        await context.ViewModel.LoadAsync(backToDetail);
        context.ViewModel.BackCommand.Execute(null);
        Assert.AreEqual(AppPage.WatchLater, context.NavigationService.CurrentPage);
    }

    private sealed class SavedStateWatchLaterStore(
        Func<AuthSession, string, CancellationToken, Task<bool>> readSaved) : IWatchLaterStore
    {
        private readonly TestWatchLaterStore inner = new();
        public Task<bool> IsSavedAsync(AuthSession session, string itemId, CancellationToken token) => readSaved(session, itemId, token);
        public Task<IReadOnlyList<WatchLaterItem>> LoadAsync(AuthSession session, CancellationToken token) => inner.LoadAsync(session, token);
        public Task AddAsync(AuthSession session, WatchLaterItem item, CancellationToken token) => inner.AddAsync(session, item, token);
        public Task RemoveAsync(AuthSession session, string itemId, CancellationToken token) => inner.RemoveAsync(session, itemId, token);
        public Task ResetCorruptedAsync(AuthSession session, CancellationToken token) => inner.ResetCorruptedAsync(session, token);
    }
}
