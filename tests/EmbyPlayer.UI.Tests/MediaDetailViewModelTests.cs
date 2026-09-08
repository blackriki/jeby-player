using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Details;
using EmbyPlayer.Core.Playback;
using EmbyPlayer.Core.Series;
using EmbyPlayer.Core.UserData;
using EmbyPlayer.UI.Controls;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.Pages;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed partial class MediaDetailViewModelTests
{
    [TestMethod]
    public async Task OpenPersonCommandPreservesDetailContextAndWorkBackReturnsToPerson()
    {
        var context = CreateContext();
        var person = new MediaPerson("person-1", "Actor", "Lead", "Actor", null);
        context.MediaDetailService.LoadDetailAsyncHandler = (_, id, _) => Task.FromResult(
            MediaDetailLoadResult.Success(CreateDetail(id, "Movie", people: new[] { person })));
        await context.ViewModel.LoadAsync(new DetailNavigationParameter("movie-1", AppPage.HomeSection));
        object? parameter = null;
        context.NavigationService.CurrentPageChanged += (_, e) => parameter = e.Parameter;
        context.ViewModel.OpenPersonCommand.Execute(person);
        Assert.AreEqual(AppPage.Person, context.NavigationService.CurrentPage);
        var target = (PersonNavigationParameter)parameter!;
        Assert.AreEqual("person-1", target.PersonId);
        Assert.AreEqual("movie-1", target.ReturnDetail!.ItemId);
        Assert.AreEqual(AppPage.HomeSection, target.ReturnDetail.ReturnPage);
        await context.ViewModel.LoadAsync(new DetailNavigationParameter("movie-2", AppPage.Person, PersonReturnTarget: target));
        context.ViewModel.BackCommand.Execute(null);
        Assert.AreEqual(AppPage.Person, context.NavigationService.CurrentPage);
        Assert.AreSame(target, parameter);
    }

    [TestMethod]
    public async Task LoadAsync_ShowsLoadingWhileDetailIsPending()
    {
        var context = CreateContext();
        var pendingLoad = new TaskCompletionSource<MediaDetailLoadResult>();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, _, _) => pendingLoad.Task;

        var loadTask = context.ViewModel.LoadAsync("item-1", AppPage.Home);

        Assert.IsTrue(context.ViewModel.IsLoading);
        pendingLoad.SetResult(MediaDetailLoadResult.Success(CreateDetail("item-1")));
        await loadTask;
        Assert.IsFalse(context.ViewModel.IsLoading);
    }

    [TestMethod]
    public async Task LoadAsync_NewerItemCancelsAndSupersedesBusyRequest()
    {
        var context = CreateContext();
        var firstPending = new TaskCompletionSource<MediaDetailLoadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPending = new TaskCompletionSource<MediaDetailLoadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstToken = CancellationToken.None;
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, cancellationToken) =>
        {
            if (itemId == "item-1")
            {
                firstToken = cancellationToken;
                return firstPending.Task;
            }

            return secondPending.Task;
        };

        var firstLoad = context.ViewModel.LoadAsync("item-1", AppPage.Home);
        var secondLoad = context.ViewModel.LoadAsync("item-2", AppPage.Home);

        Assert.IsTrue(firstToken.IsCancellationRequested);
        Assert.AreEqual(2, context.MediaDetailService.LoadCallCount);
        secondPending.SetResult(MediaDetailLoadResult.Success(CreateDetail("item-2")));
        await secondLoad;
        firstPending.SetResult(MediaDetailLoadResult.Success(CreateDetail("item-1")));
        await firstLoad;

        Assert.AreEqual("item-2", context.ViewModel.Detail?.Id);
        Assert.IsFalse(context.ViewModel.IsLoading);
    }

    [TestMethod]
    public async Task PlaybackReturn_MovieUpdatesImmediatelyAndStaleDetailCannotRemoveResume()
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync("movie-1", AppPage.Home);
        var pendingRefresh = new TaskCompletionSource<MediaDetailLoadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.MediaDetailService.LoadDetailAsyncHandler = (_, _, _) => pendingRefresh.Task;
        var positionTicks = TimeSpan.FromMinutes(22.5).Ticks;
        var loadTask = context.ViewModel.LoadAsync(new DetailNavigationParameter(
            "movie-1",
            AppPage.Home,
            PlaybackState: new PlaybackReturnState(
                "movie-1",
                positionTicks,
                TimeSpan.FromMinutes(90).Ticks,
                25d,
                IsPlayed: false,
                IsSynchronized: true)));

        Assert.AreEqual(positionTicks, context.ViewModel.Detail?.ResumePositionTicks);
        Assert.AreEqual(25d, context.ViewModel.ProgressValue);
        pendingRefresh.SetResult(MediaDetailLoadResult.Success(CreateDetail("movie-1") with
        {
            PlayedPercentage = 80d,
            ResumePositionTicks = TimeSpan.FromMinutes(72).Ticks
        }));
        await loadTask;

        Assert.AreEqual(positionTicks, context.ViewModel.Detail?.ResumePositionTicks);
        Assert.AreEqual(25d, context.ViewModel.ProgressValue);
        await ((AsyncRelayCommand)context.ViewModel.ContinuePlayCommand).ExecuteAsync();
        Assert.AreEqual(positionTicks, context.PlaybackService.LastRequest?.StartPositionTicks);
    }

    [TestMethod]
    public async Task SameItemNavigationWithoutPlaybackStateClearsLocalSnapshotAndRejectsOldGeneration()
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync("movie-1", AppPage.Home);
        var firstPending = new TaskCompletionSource<MediaDetailLoadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPending = new TaskCompletionSource<MediaDetailLoadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCall = 0;
        context.MediaDetailService.LoadDetailAsyncHandler = (_, _, _) =>
            ++refreshCall == 1 ? firstPending.Task : secondPending.Task;
        var oldLoad = context.ViewModel.LoadAsync(new DetailNavigationParameter(
            "movie-1",
            AppPage.Home,
            PlaybackState: new PlaybackReturnState(
                "movie-1",
                TimeSpan.FromMinutes(45).Ticks,
                TimeSpan.FromMinutes(90).Ticks,
                50d,
                IsPlayed: false,
                IsSynchronized: false)));

        Assert.IsTrue(context.ViewModel.IsPlaybackSyncWarningVisible);
        Assert.AreEqual(50d, context.ViewModel.ProgressValue);
        var reopenLoad = context.ViewModel.LoadAsync("movie-1", AppPage.Home);

        Assert.IsFalse(context.ViewModel.IsPlaybackSyncWarningVisible);
        secondPending.SetResult(MediaDetailLoadResult.Success(CreateDetail("movie-1") with
        {
            PlayedPercentage = 10d,
            ResumePositionTicks = TimeSpan.FromMinutes(9).Ticks
        }));
        await reopenLoad;
        firstPending.SetResult(MediaDetailLoadResult.Success(CreateDetail("movie-1") with
        {
            PlayedPercentage = 80d,
            ResumePositionTicks = TimeSpan.FromMinutes(72).Ticks
        }));
        await oldLoad;

        Assert.IsFalse(context.ViewModel.IsPlaybackSyncWarningVisible);
        Assert.AreEqual(10d, context.ViewModel.ProgressValue);
        Assert.AreEqual(TimeSpan.FromMinutes(9).Ticks, context.ViewModel.Detail?.ResumePositionTicks);
    }

    [TestMethod]
    public async Task PlaybackReturn_EpisodeUpdatesExistingCardAndStaleEpisodeResponseCannotRollBack()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, false)
            }));
        var initialEpisodes = new[]
        {
            new EpisodeInfo(
                "episode-1",
                "第 1 集",
                1,
                1,
                TimeSpan.FromMinutes(45).Ticks,
                "Overview",
                null,
                null,
                null)
        };
        var pendingEpisodes = new TaskCompletionSource<SeriesEpisodesLoadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var episodeCallCount = 0;
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, _, _) =>
            ++episodeCallCount == 1
                ? Task.FromResult(SeriesEpisodesLoadResult.Success(initialEpisodes))
                : pendingEpisodes.Task;
        await context.ViewModel.LoadAsync(new DetailNavigationParameter(
            "series-1",
            AppPage.Library,
            SelectedSeasonId: "season-1",
            FocusedEpisodeId: "episode-1"));
        var positionTicks = TimeSpan.FromMinutes(22.5).Ticks;
        var changedProperties = new HashSet<string?>();
        context.ViewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        var returnTask = context.ViewModel.LoadAsync(new DetailNavigationParameter(
            "series-1",
            AppPage.Library,
            SelectedSeasonId: "season-1",
            FocusedEpisodeId: "episode-1",
            PlaybackState: new PlaybackReturnState(
                "episode-1",
                positionTicks,
                TimeSpan.FromMinutes(45).Ticks,
                50d,
                IsPlayed: false,
                IsSynchronized: true)));

        Assert.AreEqual("episode-1", context.ViewModel.SelectedEpisode?.Id);
        Assert.AreEqual(positionTicks, context.ViewModel.Episodes.Single().ResumePositionTicks);
        Assert.AreEqual(50d, context.ViewModel.Episodes.Single().ProgressValue);
        Assert.AreEqual("继续播放", context.ViewModel.SeriesPrimaryButtonText);
        StringAssert.Contains(context.ViewModel.SeriesPlaybackTargetText, "S1:E1");
        Assert.AreEqual("已观看 50%", context.ViewModel.SeriesResumeProgressText);
        Assert.AreEqual("继续位置 22:30", context.ViewModel.SeriesResumePositionText);
        Assert.IsTrue(changedProperties.Contains(nameof(MediaDetailViewModel.SeriesPrimaryButtonText)));
        Assert.IsTrue(changedProperties.Contains(nameof(MediaDetailViewModel.SeriesPlaybackTargetText)));
        Assert.IsTrue(changedProperties.Contains(nameof(MediaDetailViewModel.SeriesResumeProgressText)));
        Assert.IsTrue(changedProperties.Contains(nameof(MediaDetailViewModel.SeriesResumePositionText)));
        pendingEpisodes.SetResult(SeriesEpisodesLoadResult.Success(initialEpisodes));
        await returnTask;

        var episode = context.ViewModel.Episodes.Single();
        Assert.AreEqual("episode-1", context.ViewModel.SelectedEpisode?.Id);
        Assert.AreEqual(positionTicks, episode.ResumePositionTicks);
        Assert.AreEqual(50d, episode.ProgressValue);
        await ((AsyncRelayCommand)context.ViewModel.PlayEpisodeCommand).ExecuteAsync(episode);
        Assert.AreEqual(positionTicks, context.PlaybackService.LastRequest?.StartPositionTicks);
    }

    [TestMethod]
    public async Task PlaybackReturn_UnsynchronizedProgressIsVisibleAndPlayedEpisodeDoesNotResume()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Episode")));

        await context.ViewModel.LoadAsync(new DetailNavigationParameter(
            "episode-1",
            AppPage.Home,
            PlaybackState: new PlaybackReturnState(
                "episode-1",
                TimeSpan.FromMinutes(42).Ticks,
                TimeSpan.FromMinutes(45).Ticks,
                100d,
                IsPlayed: true,
                IsSynchronized: false)));

        Assert.IsTrue(context.ViewModel.IsPlayed);
        Assert.AreEqual(0, context.ViewModel.Detail?.ResumePositionTicks);
        Assert.IsFalse(context.ViewModel.IsContinuePlaybackVisible);
        Assert.IsTrue(context.ViewModel.IsPlaybackSyncWarningVisible);
        Assert.AreEqual("播放进度已保存在当前页面，但暂未同步到服务器", context.ViewModel.PlaybackSyncWarningMessage);
    }

    [TestMethod]
    public async Task CancelPendingLoad_CancelsEpisodeRequestAndIgnoresLateUnauthorized()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, true)
            }));
        var pendingEpisodes = new TaskCompletionSource<SeriesEpisodesLoadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var episodeToken = CancellationToken.None;
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, _, cancellationToken) =>
        {
            episodeToken = cancellationToken;
            return pendingEpisodes.Task;
        };

        var loadTask = context.ViewModel.LoadAsync("series-1", AppPage.Home);
        context.ViewModel.CancelPendingLoad();

        Assert.IsTrue(episodeToken.IsCancellationRequested);
        pendingEpisodes.SetResult(SeriesEpisodesLoadResult.Failure(SeriesLoadError.Unauthorized));
        await loadTask;

        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
        Assert.AreNotEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.IsFalse(context.ViewModel.IsLoading);
        Assert.IsFalse(context.ViewModel.IsEpisodesLoading);
    }

    [TestMethod]
    public async Task LoadAsync_WithoutItemIdShowsChineseError()
    {
        var context = CreateContext();

        await context.ViewModel.LoadAsync(null, AppPage.Home);

        Assert.IsTrue(context.ViewModel.HasError);
        Assert.AreEqual("未选择媒体", context.ViewModel.ErrorMessage);
        Assert.AreEqual(0, context.MediaDetailService.LoadCallCount);
    }

    [TestMethod]
    public async Task LoadAsync_SuccessDisplaysMediaDetail()
    {
        var context = CreateContext();

        await context.ViewModel.LoadAsync("item-1", AppPage.Home);

        Assert.IsTrue(context.ViewModel.IsContentVisible);
        Assert.AreEqual("Detail Item", context.ViewModel.Title);
        Assert.AreEqual("2024", context.ViewModel.YearText);
        Assert.AreEqual("电影", context.ViewModel.TypeText);
        Assert.AreEqual("1 小时 30 分钟", context.ViewModel.DurationText);
        Assert.AreEqual("Drama", context.ViewModel.GenresText);
        Assert.AreEqual("8.1", context.ViewModel.RatingText);
        Assert.AreEqual(40, context.ViewModel.ProgressValue);
    }

    [DataTestMethod]
    [DataRow("Series")]
    [DataRow("Movie")]
    public async Task LoadAsync_MovieAndSeriesExposePeopleAndArtworkWhileEpisodesDoNot(string itemType)
    {
        var context = CreateContext();
        var people = new[]
        {
            new MediaPerson("person-1", "Actor One", "Lead", "Actor", "http://media.local/people/1")
        };
        var artworkUrls = new[] { "http://media.local/art/1" };
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(
                itemId,
                itemId == "item-1" ? itemType : "Episode",
                people: people,
                artworkUrls: artworkUrls)));

        await context.ViewModel.LoadAsync("item-1", AppPage.Home);

        Assert.AreEqual(1, context.ViewModel.People.Count);
        Assert.AreEqual("person-1", context.ViewModel.People[0].Id);
        Assert.AreEqual(1, context.ViewModel.ArtworkUrls.Count);
        Assert.IsTrue(context.ViewModel.IsPeopleSectionVisible);
        Assert.IsTrue(context.ViewModel.IsArtworkSectionVisible);

        await context.ViewModel.LoadAsync("episode-1", AppPage.Home);

        Assert.AreEqual(0, context.ViewModel.People.Count);
        Assert.AreEqual(0, context.ViewModel.ArtworkUrls.Count);
        Assert.IsFalse(context.ViewModel.IsPeopleSectionVisible);
        Assert.IsFalse(context.ViewModel.IsArtworkSectionVisible);
    }

    [DataTestMethod]
    [DataRow("Series")]
    [DataRow("Movie")]
    public async Task LoadAsync_EmptyPeopleAndArtworkHidesBothSections(string itemType)
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, itemType)));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);

        Assert.IsFalse(context.ViewModel.IsPeopleSectionVisible);
        Assert.IsFalse(context.ViewModel.IsArtworkSectionVisible);
        Assert.IsFalse(context.ViewModel.ShouldBringEpisodeSectionIntoView);
    }

    [TestMethod]
    public async Task SelectingEpisodeKeepsSeriesPeopleAndArtworkWithoutReloadingDetail()
    {
        var context = CreateContext();
        var people = new[]
        {
            new MediaPerson("person-1", "Actor One", "Lead", "Actor", null)
        };
        var artworkUrls = new[] { "http://media.local/art/1" };
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(
                itemId,
                "Series",
                people: people,
                artworkUrls: artworkUrls)));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, false)
            }));
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, _, _) =>
            Task.FromResult(SeriesEpisodesLoadResult.Success(new[]
            {
                new EpisodeInfo("episode-1", "第 1 集", 1, 1, null, null, null, null, null),
                new EpisodeInfo("episode-2", "第 2 集", 1, 2, null, null, null, null, null)
            }));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);
        context.ViewModel.SelectedEpisode = context.ViewModel.Episodes[1];

        Assert.AreEqual(1, context.MediaDetailService.LoadCallCount);
        Assert.AreEqual("person-1", context.ViewModel.People[0].Id);
        Assert.AreEqual("http://media.local/art/1", context.ViewModel.ArtworkUrls[0]);
    }

    [DataTestMethod]
    [DataRow("Series")]
    [DataRow("Movie")]
    public async Task LoadAsync_MovieAndSeriesLoadSimilarItemsIntoIndependentSuccessState(string itemType)
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, itemType)));
        context.SimilarMediaService.LoadSimilarAsyncHandler = (_, _, _) =>
            Task.FromResult(SimilarMediaLoadResult.Success(new[]
            {
                CreateSimilarItem("item-2", "Similar Two", itemType, playedPercentage: 35),
                CreateSimilarItem("item-3", "Similar Three", itemType, isPlayed: true)
            }));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);

        Assert.AreEqual(1, context.SimilarMediaService.LoadCallCount);
        Assert.AreEqual("series-1", context.SimilarMediaService.LastItemId);
        Assert.AreEqual(itemType, context.SimilarMediaService.LastItemType);
        Assert.AreEqual(2, context.ViewModel.SimilarItems.Count);
        Assert.AreEqual("Similar Two", context.ViewModel.SimilarItems[0].Title);
        Assert.IsTrue(context.ViewModel.SimilarItems[0].HasProgress);
        Assert.IsTrue(context.ViewModel.SimilarItems[1].IsPlayed);
        Assert.IsTrue(context.ViewModel.IsSimilarItemsVisible);
        Assert.IsTrue(context.ViewModel.IsSimilarSectionVisible);
        Assert.IsFalse(context.ViewModel.IsSimilarEmpty);
        Assert.IsFalse(context.ViewModel.HasSimilarError);
    }

    [TestMethod]
    public async Task LoadAsync_EpisodeDoesNotRequestOrShowSimilarItems()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Episode")));

        await context.ViewModel.LoadAsync("episode-1", AppPage.Home);

        Assert.AreEqual(0, context.SimilarMediaService.LoadCallCount);
        Assert.IsFalse(context.ViewModel.IsSimilarSectionVisible);
    }

    [DataTestMethod]
    [DataRow("Series")]
    [DataRow("Movie")]
    public async Task LoadAsync_EmptySimilarResultHidesWholeSection(string itemType)
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, itemType)));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);

        Assert.IsTrue(context.ViewModel.IsSimilarEmpty);
        Assert.IsFalse(context.ViewModel.IsSimilarSectionVisible);
    }

    [DataTestMethod]
    [DataRow("Series")]
    [DataRow("Movie")]
    public async Task LoadAsync_SimilarForbiddenShowsLocalRetryWithoutClearingDetailOrSession(string itemType)
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(
                itemId,
                itemType,
                people: new[] { new MediaPerson("person-1", "Actor", "Lead", "Actor", null) },
                artworkUrls: new[] { "http://media.local/art/1" })));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, false)
            }));
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, _, _) =>
            Task.FromResult(SeriesEpisodesLoadResult.Success(new[]
            {
                new EpisodeInfo("episode-1", "第 1 集", 1, 1, null, null, null, null, null)
            }));
        context.SimilarMediaService.LoadSimilarAsyncHandler = (_, _, _) =>
            Task.FromResult(SimilarMediaLoadResult.Failure(SimilarMediaLoadError.Forbidden));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);

        Assert.IsTrue(context.ViewModel.IsContentVisible);
        Assert.AreEqual(itemType == "Series" ? 1 : 0, context.ViewModel.Episodes.Count);
        Assert.AreEqual(1, context.ViewModel.People.Count);
        Assert.AreEqual(1, context.ViewModel.ArtworkUrls.Count);
        Assert.AreEqual("没有权限加载类似作品", context.ViewModel.SimilarErrorMessage);
        Assert.IsTrue(context.ViewModel.IsSimilarErrorVisible);
        Assert.IsTrue(context.ViewModel.RetrySimilarCommand.CanExecute(null));
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
    }

    [DataTestMethod]
    [DataRow("Series")]
    [DataRow("Movie")]
    public async Task LoadAsync_SimilarUnauthorizedClearsSessionAndNavigatesToLogin(string itemType)
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, itemType)));
        context.SimilarMediaService.LoadSimilarAsyncHandler = (_, _, _) =>
            Task.FromResult(SimilarMediaLoadResult.Failure(SimilarMediaLoadError.Unauthorized));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);

        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.IsNull(context.CurrentSessionService.CurrentSession);
        Assert.AreEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual("登录状态已失效，请重新登录", context.LoginErrorMessage);
    }

    [DataTestMethod]
    [DataRow("Series")]
    [DataRow("Movie")]
    public async Task RetrySimilarCommand_ReplacesLocalErrorWithItems(string itemType)
    {
        var context = CreateContext();
        var callCount = 0;
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, itemType)));
        context.SimilarMediaService.LoadSimilarAsyncHandler = (_, _, _) => Task.FromResult(
            ++callCount == 1
                ? SimilarMediaLoadResult.Failure(SimilarMediaLoadError.ServerUnreachable)
                : SimilarMediaLoadResult.Success(new[] { CreateSimilarItem("item-2", "Recovered", itemType) }));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);
        await ((AsyncRelayCommand)context.ViewModel.RetrySimilarCommand).ExecuteAsync();

        Assert.AreEqual(2, context.SimilarMediaService.LoadCallCount);
        Assert.AreEqual(itemType, context.SimilarMediaService.LastItemType);
        Assert.AreEqual(1, context.ViewModel.SimilarItems.Count);
        Assert.IsFalse(context.ViewModel.HasSimilarError);
        Assert.IsTrue(context.ViewModel.IsSimilarItemsVisible);
    }

    [DataTestMethod]
    [DataRow("Series", "Series")]
    [DataRow("Movie", "Series")]
    [DataRow("Series", "Movie")]
    public async Task LoadAsync_NewerItemCancelsAndRejectsLateSimilarResult(string firstType, string nextType)
    {
        var context = CreateContext();
        var firstPending = new TaskCompletionSource<SimilarMediaLoadResult>();
        CancellationToken firstCancellationToken = default;
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(
                itemId,
                itemId == "item-1" ? firstType : nextType)));
        context.SimilarMediaService.LoadSimilarAsyncHandler = (_, itemId, cancellationToken) =>
        {
            if (itemId == "item-1")
            {
                firstCancellationToken = cancellationToken;
                return firstPending.Task;
            }

            return Task.FromResult(SimilarMediaLoadResult.Success(new[]
            {
                CreateSimilarItem("item-new", "New Similar", nextType)
            }));
        };

        await context.ViewModel.LoadAsync("item-1", AppPage.Home);
        await context.ViewModel.LoadAsync("item-2", AppPage.Home);
        firstPending.SetResult(SimilarMediaLoadResult.Success(new[]
        {
            CreateSimilarItem("item-old", "Old Similar", firstType)
        }));

        Assert.IsTrue(firstCancellationToken.IsCancellationRequested);
        Assert.AreEqual(nextType, context.SimilarMediaService.LastItemType);
        Assert.AreEqual("item-new", context.ViewModel.SimilarItems.Single().Id);
    }

    [DataTestMethod]
    [DataRow("Series")]
    [DataRow("Movie")]
    public async Task CancelPendingLoad_PreventsLateSimilarUnauthorizedFromClearingSession(string itemType)
    {
        var context = CreateContext();
        var pending = new TaskCompletionSource<SimilarMediaLoadResult>();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, itemType)));
        context.SimilarMediaService.LoadSimilarAsyncHandler = (_, _, _) => pending.Task;

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);
        context.ViewModel.CancelPendingLoad();
        pending.SetResult(SimilarMediaLoadResult.Failure(SimilarMediaLoadError.Unauthorized));

        Assert.IsTrue(context.SimilarMediaService.LastCancellationToken.IsCancellationRequested);
        Assert.IsFalse(context.ViewModel.IsSimilarLoading);
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
        Assert.AreNotEqual(AppPage.Login, context.NavigationService.CurrentPage);
    }

    [TestMethod]
    public async Task LoadAsync_MovieKeepsDetailAvailableWhileSimilarItemsLoad()
    {
        var context = CreateContext();
        var pending = new TaskCompletionSource<SimilarMediaLoadResult>();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(
                itemId,
                "Movie",
                people: new[] { new MediaPerson("person-1", "Actor", "Lead", "Actor", null) },
                artworkUrls: new[] { "http://media.local/art/1" })));
        context.SimilarMediaService.LoadSimilarAsyncHandler = (_, _, _) => pending.Task;

        await context.ViewModel.LoadAsync("movie-1", AppPage.Home);

        Assert.IsFalse(context.ViewModel.IsLoading);
        Assert.IsTrue(context.ViewModel.IsContentVisible);
        Assert.IsTrue(context.ViewModel.IsPeopleSectionVisible);
        Assert.IsTrue(context.ViewModel.IsArtworkSectionVisible);
        Assert.IsTrue(context.ViewModel.IsSimilarInitialLoading);
        Assert.IsTrue(context.ViewModel.IsSimilarSectionVisible);
        Assert.IsFalse(context.ViewModel.IsSeriesSectionVisible);
        Assert.AreEqual(0, context.SeriesService.LoadSeasonsCallCount);
        Assert.IsTrue(context.ViewModel.PlayCommand.CanExecute(null));

        pending.SetResult(SimilarMediaLoadResult.Success(new[]
        {
            CreateSimilarItem("movie-2", "Similar Movie", "Movie")
        }));
        await WaitForAsync(() => !context.ViewModel.IsSimilarLoading);

        Assert.IsTrue(context.ViewModel.IsSimilarItemsVisible);
        Assert.IsFalse(context.ViewModel.IsSimilarInitialLoading);
    }

    [TestMethod]
    public async Task OpenSimilarCommand_MovieBackRestoresDirectSourceThenOriginalBrowsePage()
    {
        var context = CreateContext();
        context.SimilarMediaService.LoadSimilarAsyncHandler = (_, _, _) =>
            Task.FromResult(SimilarMediaLoadResult.Success(new[]
            {
                CreateSimilarItem("movie-2", "Similar Movie", "Movie")
            }));
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;

        await context.ViewModel.LoadAsync("movie-1", AppPage.Library);
        context.ViewModel.OpenSimilarCommand.Execute(context.ViewModel.SimilarItems[0]);

        Assert.IsInstanceOfType<DetailNavigationParameter>(navigationParameter);
        var nextParameter = (DetailNavigationParameter)navigationParameter!;
        Assert.AreEqual("movie-2", nextParameter.ItemId);
        Assert.AreEqual("movie-1", nextParameter.BackTarget?.ItemId);
        Assert.IsNull(nextParameter.BackTarget?.SelectedSeasonId);
        Assert.IsNull(nextParameter.BackTarget?.FocusedEpisodeId);

        await context.ViewModel.LoadAsync(nextParameter);
        context.ViewModel.BackCommand.Execute(null);

        Assert.IsInstanceOfType<DetailNavigationParameter>(navigationParameter);
        var backParameter = (DetailNavigationParameter)navigationParameter!;
        Assert.AreEqual("movie-1", backParameter.ItemId);
        Assert.AreEqual(AppPage.Library, backParameter.ReturnPage);
        Assert.IsNull(backParameter.SelectedSeasonId);
        Assert.IsNull(backParameter.FocusedEpisodeId);

        await context.ViewModel.LoadAsync(backParameter);
        context.ViewModel.BackCommand.Execute(null);
        Assert.AreEqual(AppPage.Library, context.NavigationService.CurrentPage);
    }

    [TestMethod]
    public async Task OpenSimilarCommand_UsesDirectSeriesBackTargetWithSeasonAndEpisode()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, false)
            }));
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, _, _) =>
            Task.FromResult(SeriesEpisodesLoadResult.Success(new[]
            {
                new EpisodeInfo("episode-1", "第 1 集", 1, 1, null, null, null, null, null)
            }));
        context.SimilarMediaService.LoadSimilarAsyncHandler = (_, _, _) =>
            Task.FromResult(SimilarMediaLoadResult.Success(new[]
            {
                CreateSimilarItem("series-2", "Similar Series")
            }));
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;

        await context.ViewModel.LoadAsync("series-1", AppPage.Library);
        context.ViewModel.OpenSimilarCommand.Execute(context.ViewModel.SimilarItems[0]);

        Assert.IsInstanceOfType<DetailNavigationParameter>(navigationParameter);
        var detailParameter = (DetailNavigationParameter)navigationParameter!;
        Assert.AreEqual("series-2", detailParameter.ItemId);
        Assert.AreEqual(AppPage.Library, detailParameter.ReturnPage);
        Assert.AreEqual(AppPage.Detail, detailParameter.BackTarget?.Page);
        Assert.AreEqual("series-1", detailParameter.BackTarget?.ItemId);
        Assert.AreEqual("season-1", detailParameter.BackTarget?.SelectedSeasonId);
        Assert.AreEqual("episode-1", detailParameter.BackTarget?.FocusedEpisodeId);
    }

    [TestMethod]
    public async Task LoadAsync_UsesFavoriteStateFromDetail()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, isFavorite: true)));

        await context.ViewModel.LoadAsync("item-1", AppPage.Home);

        Assert.IsTrue(context.ViewModel.IsFavorite);
        Assert.AreEqual("已收藏", context.ViewModel.FavoriteButtonText);
    }

    [TestMethod]
    public async Task ToggleFavoriteCommand_FavoritesAfterServerSuccess()
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync("item-1", AppPage.Home);

        await ((AsyncRelayCommand)context.ViewModel.ToggleFavoriteCommand).ExecuteAsync();

        Assert.AreEqual(1, context.ItemUserDataService.SetFavoriteCallCount);
        Assert.AreEqual("item-1", context.ItemUserDataService.LastItemId);
        Assert.AreEqual(true, context.ItemUserDataService.LastIsFavorite);
        Assert.IsTrue(context.ViewModel.IsFavorite);
        Assert.AreEqual("已收藏", context.ViewModel.FavoriteButtonText);
    }

    [TestMethod]
    public async Task ToggleFavoriteCommand_UnfavoritesAfterServerSuccess()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, isFavorite: true)));
        await context.ViewModel.LoadAsync("item-1", AppPage.Home);

        await ((AsyncRelayCommand)context.ViewModel.ToggleFavoriteCommand).ExecuteAsync();

        Assert.AreEqual(false, context.ItemUserDataService.LastIsFavorite);
        Assert.IsFalse(context.ViewModel.IsFavorite);
        Assert.AreEqual("收藏", context.ViewModel.FavoriteButtonText);
    }

    [TestMethod]
    public async Task ToggleFavoriteCommand_FailureDoesNotChangeFavoriteState()
    {
        var context = CreateContext();
        context.ItemUserDataService.SetFavoriteAsyncHandler = (_, _, _, _) =>
            Task.FromResult(ItemUserDataResult.Failure(ItemUserDataError.ServerUnreachable));
        await context.ViewModel.LoadAsync("item-1", AppPage.Home);

        await ((AsyncRelayCommand)context.ViewModel.ToggleFavoriteCommand).ExecuteAsync();

        Assert.IsFalse(context.ViewModel.IsFavorite);
        Assert.IsTrue(context.ViewModel.IsFavoriteErrorVisible);
        Assert.AreEqual("无法更新收藏，请检查网络或服务器", context.ViewModel.FavoriteErrorMessage);
    }

    [TestMethod]
    public async Task ToggleFavoriteCommand_KeepsCurrentButtonTextWhilePending()
    {
        var context = CreateContext();
        var pendingFavorite = new TaskCompletionSource<ItemUserDataResult>();
        context.ItemUserDataService.SetFavoriteAsyncHandler = (_, _, _, _) => pendingFavorite.Task;
        await context.ViewModel.LoadAsync("item-1", AppPage.Home);

        var favoriteTask = ((AsyncRelayCommand)context.ViewModel.ToggleFavoriteCommand).ExecuteAsync();

        Assert.IsTrue(context.ViewModel.IsFavoriteChanging);
        Assert.AreEqual("\u6536\u85cf", context.ViewModel.FavoriteButtonText);
        Assert.IsFalse(context.ViewModel.ToggleFavoriteCommand.CanExecute(null));

        pendingFavorite.SetResult(ItemUserDataResult.Success());
        await favoriteTask;

        Assert.IsFalse(context.ViewModel.IsFavoriteChanging);
        Assert.IsTrue(context.ViewModel.IsFavorite);
        Assert.AreEqual("\u5df2\u6536\u85cf", context.ViewModel.FavoriteButtonText);
    }

    [TestMethod]
    public async Task ToggleFavoriteCommand_UnauthorizedClearsSessionAndNavigatesLogin()
    {
        var context = CreateContext();
        context.ItemUserDataService.SetFavoriteAsyncHandler = (_, _, _, _) =>
            Task.FromResult(ItemUserDataResult.Failure(ItemUserDataError.Unauthorized));
        await context.ViewModel.LoadAsync("item-1", AppPage.Home);

        await ((AsyncRelayCommand)context.ViewModel.ToggleFavoriteCommand).ExecuteAsync();

        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.IsNull(context.CurrentSessionService.CurrentSession);
        Assert.AreEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual("登录状态已失效，请重新登录", context.LoginErrorMessage);
    }

    [TestMethod]
    public async Task ToggleFavoriteCommand_ForbiddenKeepsSessionAndShowsPermissionError()
    {
        var context = CreateContext();
        context.ItemUserDataService.SetFavoriteAsyncHandler = (_, _, _, _) =>
            Task.FromResult(ItemUserDataResult.Failure(ItemUserDataError.Forbidden));
        await context.ViewModel.LoadAsync("item-1", AppPage.Home);

        await ((AsyncRelayCommand)context.ViewModel.ToggleFavoriteCommand).ExecuteAsync();

        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
        Assert.AreNotEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual("没有权限更新收藏", context.ViewModel.FavoriteErrorMessage);
    }

    [TestMethod]
    public async Task LoadAsync_UsesPlayedStateFromDetail()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, isPlayed: true)));

        await context.ViewModel.LoadAsync("item-1", AppPage.Home);

        Assert.IsTrue(context.ViewModel.IsPlayed);
        Assert.AreEqual("标记未观看", context.ViewModel.PlayedButtonText);
    }

    [TestMethod]
    public async Task TogglePlayedCommand_MarksPlayedAfterServerSuccess()
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync("item-1", AppPage.Home);

        await ((AsyncRelayCommand)context.ViewModel.TogglePlayedCommand).ExecuteAsync();

        Assert.AreEqual(1, context.ItemUserDataService.SetPlayedCallCount);
        Assert.AreEqual("item-1", context.ItemUserDataService.LastPlayedItemId);
        Assert.AreEqual(true, context.ItemUserDataService.LastIsPlayed);
        Assert.IsTrue(context.ViewModel.IsPlayed);
        Assert.AreEqual("标记未观看", context.ViewModel.PlayedButtonText);
    }

    [TestMethod]
    public async Task TogglePlayedCommand_MarksUnplayedAfterServerSuccess()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, isPlayed: true)));
        await context.ViewModel.LoadAsync("item-1", AppPage.Home);

        await ((AsyncRelayCommand)context.ViewModel.TogglePlayedCommand).ExecuteAsync();

        Assert.AreEqual(false, context.ItemUserDataService.LastIsPlayed);
        Assert.IsFalse(context.ViewModel.IsPlayed);
        Assert.AreEqual("标记已观看", context.ViewModel.PlayedButtonText);
    }

    [TestMethod]
    public async Task TogglePlayedCommand_FailureDoesNotChangePlayedState()
    {
        var context = CreateContext();
        context.ItemUserDataService.SetPlayedAsyncHandler = (_, _, _, _) =>
            Task.FromResult(ItemUserDataResult.Failure(ItemUserDataError.ServerUnreachable));
        await context.ViewModel.LoadAsync("item-1", AppPage.Home);

        await ((AsyncRelayCommand)context.ViewModel.TogglePlayedCommand).ExecuteAsync();

        Assert.IsFalse(context.ViewModel.IsPlayed);
        Assert.IsTrue(context.ViewModel.IsPlayedErrorVisible);
        Assert.AreEqual("无法更新观看状态，请检查网络或服务器", context.ViewModel.PlayedErrorMessage);
    }

    [TestMethod]
    public async Task TogglePlayedCommand_UnauthorizedClearsSessionAndNavigatesLogin()
    {
        var context = CreateContext();
        context.ItemUserDataService.SetPlayedAsyncHandler = (_, _, _, _) =>
            Task.FromResult(ItemUserDataResult.Failure(ItemUserDataError.Unauthorized));
        await context.ViewModel.LoadAsync("item-1", AppPage.Home);

        await ((AsyncRelayCommand)context.ViewModel.TogglePlayedCommand).ExecuteAsync();

        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.IsNull(context.CurrentSessionService.CurrentSession);
        Assert.AreEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual("登录状态已失效，请重新登录", context.LoginErrorMessage);
    }

    [TestMethod]
    public async Task TogglePlayedCommand_ForbiddenKeepsSessionAndShowsPermissionError()
    {
        var context = CreateContext();
        context.ItemUserDataService.SetPlayedAsyncHandler = (_, _, _, _) =>
            Task.FromResult(ItemUserDataResult.Failure(ItemUserDataError.Forbidden));
        await context.ViewModel.LoadAsync("item-1", AppPage.Home);

        await ((AsyncRelayCommand)context.ViewModel.TogglePlayedCommand).ExecuteAsync();

        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
        Assert.AreNotEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual("没有权限更新观看状态", context.ViewModel.PlayedErrorMessage);
    }

    [TestMethod]
    public async Task LoadAsync_MissingOptionalFieldsUsesDefaultText()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(new MediaDetail(
                itemId,
                string.Empty,
                string.Empty,
                null,
                null,
                null,
                Array.Empty<string>(),
                null,
                null,
                null,
                null,
                null)));

        await context.ViewModel.LoadAsync("item-1", AppPage.Home);

        Assert.AreEqual("未命名媒体", context.ViewModel.Title);
        Assert.AreEqual("未知年份", context.ViewModel.YearText);
        Assert.AreEqual("未知类型", context.ViewModel.TypeText);
        Assert.AreEqual("未知时长", context.ViewModel.DurationText);
        Assert.AreEqual("暂无简介", context.ViewModel.OverviewText);
        Assert.AreEqual("暂无类型标签", context.ViewModel.GenresText);
        Assert.AreEqual("暂无评分", context.ViewModel.RatingText);
        Assert.IsFalse(context.ViewModel.HasProgress);
    }

    [TestMethod]
    public async Task LoadAsync_SeriesDetailLoadsSeasons()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, false)
            }));

        await context.ViewModel.LoadAsync("series-1", AppPage.Library);

        Assert.IsTrue(context.ViewModel.IsSeriesSectionVisible);
        Assert.AreEqual(1, context.SeriesService.LoadSeasonsCallCount);
        Assert.AreEqual("series-1", context.SeriesService.LastSeriesId);
        Assert.AreEqual("season-1", context.ViewModel.SelectedSeason?.Id);
    }

    [TestMethod]
    public async Task LoadAsync_NonSeriesDetailDoesNotLoadSeasons()
    {
        var context = CreateContext();
        var changedProperties = new List<string?>();
        context.ViewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        await context.ViewModel.LoadAsync("movie-1", AppPage.Home);

        Assert.IsFalse(context.ViewModel.IsSeriesSectionVisible);
        Assert.AreEqual(0, context.SeriesService.LoadSeasonsCallCount);
        Assert.IsNull(context.ViewModel.SelectedEpisodeHeroUrl);
        Assert.AreEqual(context.ViewModel.BackdropUrl, context.ViewModel.HeroBackdropUrl);
        CollectionAssert.Contains(changedProperties, nameof(MediaDetailViewModel.SelectedEpisodeHeroUrl));
        CollectionAssert.Contains(changedProperties, nameof(MediaDetailViewModel.HeroBackdropUrl));
    }

    [TestMethod]
    public async Task LoadAsync_SeriesHeroBackdropTracksDefaultAndManualEpisodeSelection()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, false)
            }));
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, _, _) =>
            Task.FromResult(SeriesEpisodesLoadResult.Success(new[]
            {
                new EpisodeInfo("episode-1", "第 1 集", 1, 1, null, null, null, null, null, HeroImageUrl: "http://media.local/hero-1"),
                new EpisodeInfo("episode-2", "第 2 集", 1, 2, null, null, null, null, null, HeroImageUrl: "http://media.local/hero-2")
            }));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);

        Assert.AreEqual("episode-1", context.ViewModel.SelectedEpisode?.Id);
        Assert.AreEqual("http://media.local/hero-1", context.ViewModel.SelectedEpisodeHeroUrl);
        Assert.AreEqual("http://media.local/hero-1", context.ViewModel.HeroBackdropUrl);

        var changedProperties = new List<string?>();
        context.ViewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);
        context.ViewModel.SelectedEpisode = context.ViewModel.Episodes[1];

        Assert.AreEqual("http://media.local/hero-2", context.ViewModel.SelectedEpisodeHeroUrl);
        Assert.AreEqual("http://media.local/hero-2", context.ViewModel.HeroBackdropUrl);
        CollectionAssert.Contains(changedProperties, nameof(MediaDetailViewModel.SelectedEpisodeHeroUrl));
        CollectionAssert.Contains(changedProperties, nameof(MediaDetailViewModel.HeroBackdropUrl));
    }

    [TestMethod]
    public async Task LoadAsync_RequestedHeroFallsBackToSeriesBackdropWhileChangingToSeasonWithoutHero()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, false),
                new SeasonInfo("season-2", "第 2 季", 2, false)
            }));
        var seasonTwoEpisodes = new TaskCompletionSource<SeriesEpisodesLoadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, seasonId, _) =>
            seasonId == "season-1"
                ? Task.FromResult(SeriesEpisodesLoadResult.Success(new[]
                {
                    new EpisodeInfo("episode-1", "第 1 集", 1, 1, null, null, null, null, null, HeroImageUrl: "http://media.local/hero-1"),
                    new EpisodeInfo("episode-2", "第 2 集", 1, 2, null, null, null, null, null, HeroImageUrl: "http://media.local/hero-2")
                }))
                : seasonTwoEpisodes.Task;

        await context.ViewModel.LoadAsync(new DetailNavigationParameter(
            "series-1",
            AppPage.Home,
            SelectedSeasonId: "season-1",
            FocusedEpisodeId: "episode-2"));

        Assert.AreEqual("episode-2", context.ViewModel.SelectedEpisode?.Id);
        Assert.AreEqual("http://media.local/hero-2", context.ViewModel.HeroBackdropUrl);

        var changedProperties = new List<string?>();
        context.ViewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);
        var changeSeasonTask = context.ViewModel.SelectSeasonAsync(context.ViewModel.Seasons[1]);
        Assert.IsNull(context.ViewModel.SelectedEpisode);
        Assert.IsNull(context.ViewModel.SelectedEpisodeHeroUrl);
        Assert.AreEqual(context.ViewModel.BackdropUrl, context.ViewModel.HeroBackdropUrl);
        CollectionAssert.Contains(changedProperties, nameof(MediaDetailViewModel.SelectedEpisodeHeroUrl));
        CollectionAssert.Contains(changedProperties, nameof(MediaDetailViewModel.HeroBackdropUrl));

        seasonTwoEpisodes.SetResult(SeriesEpisodesLoadResult.Success(new[]
        {
            new EpisodeInfo("episode-3", "第 1 集", 2, 1, null, null, null, null, null)
        }));
        await changeSeasonTask;

        Assert.AreEqual("episode-3", context.ViewModel.SelectedEpisode?.Id);
        Assert.IsNull(context.ViewModel.SelectedEpisodeHeroUrl);
        Assert.AreEqual(context.ViewModel.BackdropUrl, context.ViewModel.HeroBackdropUrl);
    }

    [TestMethod]
    public async Task LoadAsync_EpisodeDetailDoesNotLoadSeasons()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Episode")));

        await context.ViewModel.LoadAsync("episode-1", AppPage.Home);

        Assert.IsFalse(context.ViewModel.IsSeriesSectionVisible);
        Assert.AreEqual(0, context.SeriesService.LoadSeasonsCallCount);
    }

    [TestMethod]
    public async Task LoadAsync_DefaultSelectsResumeSeasonBeforeFirstSeason()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, false),
                new SeasonInfo("season-2", "第 2 季", 2, true)
            }));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);

        Assert.AreEqual("season-2", context.ViewModel.SelectedSeason?.Id);
    }

    [TestMethod]
    public async Task LoadAsync_DefaultSelectsSmallestIndexSeasonWhenNoResumeSeasonExists()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-3", "第 3 季", 3, false),
                new SeasonInfo("season-1", "第 1 季", 1, false)
            }));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);

        Assert.AreEqual("season-1", context.ViewModel.SelectedSeason?.Id);
    }

    [TestMethod]
    public async Task SelectSeasonAsync_LoadsEpisodesSortedBySeasonAndEpisodeIndex()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, false),
                new SeasonInfo("season-2", "第 2 季", 2, false)
            }));
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, seasonId, _) =>
            Task.FromResult(SeriesEpisodesLoadResult.Success(new[]
            {
                new EpisodeInfo($"episode-{seasonId}-2", "第 2 集：第二集", 1, 2, null, null, null, null, null),
                new EpisodeInfo($"episode-{seasonId}-1", "第 1 集：第一集", 1, 1, null, null, null, null, null)
            }));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);
        await context.ViewModel.SelectSeasonAsync(context.ViewModel.Seasons[1]);

        Assert.AreEqual("season-2", context.SeriesService.LastSeasonId);
        Assert.AreEqual("episode-season-2-1", context.ViewModel.Episodes[0].Id);
        Assert.AreEqual("episode-season-2-2", context.ViewModel.Episodes[1].Id);
    }

    [TestMethod]
    public async Task SelectSeasonAsync_NewerSeasonCancelsAndSupersedesBusyEpisodeRequest()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, false),
                new SeasonInfo("season-2", "第 2 季", 2, false),
                new SeasonInfo("season-3", "第 3 季", 3, false)
            }));
        var seasonTwoPending = new TaskCompletionSource<SeriesEpisodesLoadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var seasonThreePending = new TaskCompletionSource<SeriesEpisodesLoadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var seasonTwoToken = CancellationToken.None;
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, seasonId, cancellationToken) =>
        {
            if (seasonId == "season-1")
            {
                return Task.FromResult(SeriesEpisodesLoadResult.Success(new[]
                {
                    new EpisodeInfo("episode-1", "第 1 集", 1, 1, null, null, null, null, null)
                }));
            }

            if (seasonId == "season-2")
            {
                seasonTwoToken = cancellationToken;
                return seasonTwoPending.Task;
            }

            return seasonThreePending.Task;
        };
        await context.ViewModel.LoadAsync("series-1", AppPage.Home);

        var seasonTwoLoad = context.ViewModel.SelectSeasonAsync(context.ViewModel.Seasons[1]);
        var seasonThreeLoad = context.ViewModel.SelectSeasonAsync(context.ViewModel.Seasons[2]);

        Assert.IsTrue(seasonTwoToken.IsCancellationRequested);
        Assert.AreEqual(3, context.SeriesService.LoadEpisodesCallCount);
        seasonThreePending.SetResult(SeriesEpisodesLoadResult.Success(new[]
        {
            new EpisodeInfo("episode-3", "第 1 集", 3, 1, null, null, null, null, null)
        }));
        await seasonThreeLoad;
        seasonTwoPending.SetResult(SeriesEpisodesLoadResult.Success(new[]
        {
            new EpisodeInfo("episode-2", "第 1 集", 2, 1, null, null, null, null, null)
        }));
        await seasonTwoLoad;

        Assert.AreEqual("season-3", context.ViewModel.SelectedSeason?.Id);
        Assert.AreEqual("episode-3", context.ViewModel.Episodes.Single().Id);
        Assert.AreEqual("episode-3", context.ViewModel.SelectedEpisode?.Id);
        Assert.IsFalse(context.ViewModel.IsEpisodesLoading);
    }

    [TestMethod]
    public async Task LoadAsync_ContinueContextSelectsSeasonAndFocusesResumeEpisode()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, false),
                new SeasonInfo("season-2", "第 2 季", 2, true)
            }));
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, seasonId, _) =>
            Task.FromResult(SeriesEpisodesLoadResult.Success(seasonId == "season-2"
                ? new[]
                {
                    new EpisodeInfo("episode-2-1", "第 1 集", 2, 1, TimeSpan.FromMinutes(45).Ticks, null, 25, TimeSpan.FromMinutes(10).Ticks, null),
                    new EpisodeInfo("episode-2-4", "第 4 集", 2, 4, TimeSpan.FromMinutes(45).Ticks, null, 42, TimeSpan.FromMinutes(18).Ticks, null)
                }
                : new[]
                {
                    new EpisodeInfo("episode-1-1", "第 1 集", 1, 1, TimeSpan.FromMinutes(45).Ticks, null, null, null, null)
                }));

        await context.ViewModel.LoadAsync(new DetailNavigationParameter(
            "series-1",
            AppPage.Home,
            SelectedSeasonId: "season-2",
            FocusedEpisodeId: "episode-2-4",
            FallbackItemId: "episode-2-4"));

        Assert.AreEqual("season-2", context.ViewModel.SelectedSeason?.Id);
        Assert.AreEqual("episode-2-4", context.ViewModel.SelectedEpisode?.Id);
        Assert.AreEqual("episode-2-4", context.ViewModel.FocusedEpisode?.Id);
        Assert.IsTrue(context.ViewModel.FocusedEpisode!.IsNavigationTarget);
        Assert.IsTrue(context.ViewModel.ShouldBringEpisodeSectionIntoView);
        Assert.AreEqual(1, context.SeriesService.LoadSeasonsCallCount);
        Assert.AreEqual(1, context.SeriesService.LoadEpisodesCallCount);

        await ((AsyncRelayCommand)context.ViewModel.SeriesPlayCommand).ExecuteAsync();

        Assert.AreEqual("episode-2-4", context.PlaybackService.LastRequest?.ItemId);
        Assert.AreEqual(TimeSpan.FromMinutes(18).Ticks, context.PlaybackService.LastRequest?.StartPositionTicks);

        await context.ViewModel.SelectSeasonAsync(context.ViewModel.Seasons[0]);

        Assert.AreEqual("season-1", context.ViewModel.SelectedSeason?.Id);
        Assert.AreEqual("episode-1-1", context.ViewModel.SelectedEpisode?.Id);
        Assert.AreEqual("episode-1-1", context.ViewModel.FocusedEpisode?.Id);
    }

    [TestMethod]
    public async Task SelectedEpisode_DoesNotOverrideRecommendedSeriesPlaybackTarget()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, true)
            }));
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, _, _) =>
            Task.FromResult(SeriesEpisodesLoadResult.Success(new[]
            {
                new EpisodeInfo("episode-1", "第 1 集", 1, 1, TimeSpan.FromMinutes(45).Ticks, null, 40, TimeSpan.FromMinutes(18).Ticks, null),
                new EpisodeInfo("episode-2", "第 2 集", 1, 2, TimeSpan.FromMinutes(45).Ticks, null, null, null, null)
            }));
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;
        await context.ViewModel.LoadAsync("series-1", AppPage.Home);

        Assert.AreEqual("episode-1", context.ViewModel.SelectedEpisode?.Id);
        context.ViewModel.SelectedEpisode = context.ViewModel.Episodes[1];
        Assert.AreEqual("继续播放", context.ViewModel.SeriesPrimaryButtonText);
        StringAssert.Contains(context.ViewModel.SeriesPlaybackTargetText, "第 1 集");
        await ((AsyncRelayCommand)context.ViewModel.SeriesPlayCommand).ExecuteAsync();

        Assert.AreEqual("episode-1", context.PlaybackService.LastRequest?.ItemId);
        Assert.AreEqual(TimeSpan.FromMinutes(18).Ticks, context.PlaybackService.LastRequest?.StartPositionTicks);
        Assert.AreEqual("episode-1", context.ViewModel.SelectedEpisode?.Id);
        Assert.IsInstanceOfType<PlayerNavigationParameter>(navigationParameter);
        var backParameter = ((PlayerNavigationParameter)navigationParameter!).BackToDetailParameter;
        Assert.AreEqual("season-1", backParameter.SelectedSeasonId);
        Assert.AreEqual("episode-1", backParameter.FocusedEpisodeId);
    }

    [TestMethod]
    public async Task PlayEpisodeCommand_SelectsParameterAndPlaysOnlyThatEpisode()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, true)
            }));
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, _, _) =>
            Task.FromResult(SeriesEpisodesLoadResult.Success(new[]
            {
                new EpisodeInfo("episode-1", "第 1 集", 1, 1, TimeSpan.FromMinutes(45).Ticks, null, 40, TimeSpan.FromMinutes(18).Ticks, null),
                new EpisodeInfo("episode-2", "第 2 集", 1, 2, TimeSpan.FromMinutes(45).Ticks, null, null, null, null)
            }));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);
        var requestedEpisode = context.ViewModel.Episodes[1];

        await ((AsyncRelayCommand)context.ViewModel.PlayEpisodeCommand).ExecuteAsync(requestedEpisode);

        Assert.AreSame(requestedEpisode, context.ViewModel.SelectedEpisode);
        Assert.AreEqual("episode-2", context.PlaybackService.LastRequest?.ItemId);
        Assert.AreEqual(0, context.PlaybackService.LastRequest?.StartPositionTicks);
    }

    [TestMethod]
    public async Task LoadAsync_DefaultSelectionPrefersPartialProgressWithoutRuntimeMetadata()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, true)
            }));
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, _, _) =>
            Task.FromResult(SeriesEpisodesLoadResult.Success(new[]
            {
                new EpisodeInfo("episode-1", "第 1 集", 1, 1, null, null, null, null, null),
                new EpisodeInfo("episode-2", "第 2 集", 1, 2, null, null, 35, null, null)
            }));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);

        Assert.AreEqual("episode-2", context.ViewModel.SelectedEpisode?.Id);
    }

    [TestMethod]
    public async Task LoadAsync_PlayedFlagCountsAsWatchHistoryAndSelectsFirstUnplayedEpisode()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, true)
            }));
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, _, _) =>
            Task.FromResult(SeriesEpisodesLoadResult.Success(new[]
            {
                new EpisodeInfo("episode-1", "第 1 集", 1, 1, null, null, null, null, null, IsPlayed: true),
                new EpisodeInfo("episode-2", "第 2 集", 1, 2, null, null, null, null, null)
            }));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);

        Assert.IsTrue(context.ViewModel.Episodes[0].IsPlayed);
        Assert.AreEqual("episode-2", context.ViewModel.SelectedEpisode?.Id);
        Assert.AreEqual("播放下一集", context.ViewModel.SeriesPrimaryButtonText);
    }

    [TestMethod]
    public async Task LoadAsync_MissingParentSeriesNavigatesToFallbackEpisodeDetail()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, _, _) =>
            Task.FromResult(MediaDetailLoadResult.Failure(MediaDetailLoadError.NotFound));
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;

        await context.ViewModel.LoadAsync(new DetailNavigationParameter(
            "missing-series",
            AppPage.Home,
            SelectedSeasonId: "season-2",
            FocusedEpisodeId: "episode-2-4",
            FallbackItemId: "episode-2-4"));

        Assert.AreEqual(AppPage.Detail, context.NavigationService.CurrentPage);
        Assert.IsInstanceOfType<DetailNavigationParameter>(navigationParameter);
        var fallbackParameter = (DetailNavigationParameter)navigationParameter!;
        Assert.AreEqual("episode-2-4", fallbackParameter.ItemId);
        Assert.AreEqual(AppPage.Home, fallbackParameter.ReturnPage);
        Assert.IsNull(fallbackParameter.FallbackItemId);
        Assert.IsNull(context.ViewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task SeriesPlayCommand_ContinuesResumeEpisodeWithRealProgress()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-2", "第 2 季", 2, true)
            }));
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, _, _) =>
            Task.FromResult(SeriesEpisodesLoadResult.Success(new[]
            {
                new EpisodeInfo(
                    "episode-2-1",
                    "第 1 集：开始与结束",
                    2,
                    1,
                    TimeSpan.FromMinutes(50).Ticks,
                    "Overview",
                    48,
                    new TimeSpan(0, 23, 18).Ticks,
                    null)
            }));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);

        Assert.IsTrue(context.ViewModel.IsSeriesResumeProgressVisible);
        Assert.AreEqual("继续播放", context.ViewModel.SeriesPrimaryButtonText);
        Assert.AreEqual("已观看 48%", context.ViewModel.SeriesResumeProgressText);
        Assert.AreEqual("继续位置 23:18", context.ViewModel.SeriesResumePositionText);

        await ((AsyncRelayCommand)context.ViewModel.SeriesPlayCommand).ExecuteAsync();

        Assert.AreEqual("episode-2-1", context.PlaybackService.LastRequest?.ItemId);
        Assert.AreEqual(new TimeSpan(0, 23, 18).Ticks, context.PlaybackService.LastRequest?.StartPositionTicks);
    }

    [TestMethod]
    public async Task SeriesPlayCommand_PlaysNextUnfinishedEpisodeAfterWatchHistory()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, true)
            }));
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, _, _) =>
            Task.FromResult(SeriesEpisodesLoadResult.Success(new[]
            {
                new EpisodeInfo("episode-1-1", "第 1 集", 1, 1, TimeSpan.FromMinutes(45).Ticks, null, 95, null, null),
                new EpisodeInfo("episode-1-2", "第 2 集", 1, 2, TimeSpan.FromMinutes(45).Ticks, null, null, null, null)
            }));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);
        await ((AsyncRelayCommand)context.ViewModel.SeriesPlayCommand).ExecuteAsync();

        Assert.AreEqual("播放下一集", context.ViewModel.SeriesPrimaryButtonText);
        Assert.IsFalse(context.ViewModel.IsSeriesResumeProgressVisible);
        Assert.AreEqual("episode-1-2", context.PlaybackService.LastRequest?.ItemId);
        Assert.AreEqual(0, context.PlaybackService.LastRequest?.StartPositionTicks);
    }

    [TestMethod]
    public async Task LoadAsync_DefaultSeasonSkipsSeasonZeroWhenNormalSeasonExists()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-0", "特别篇", 0, false),
                new SeasonInfo("season-1", "第 1 季", 1, false)
            }));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);

        Assert.AreEqual("season-1", context.ViewModel.SelectedSeason?.Id);
    }

    [TestMethod]
    public async Task SeriesPlayCommand_ReplaysFirstEpisodeWhenSeasonIsComplete()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, false)
            }));
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, _, _) =>
            Task.FromResult(SeriesEpisodesLoadResult.Success(new[]
            {
                new EpisodeInfo("episode-1-2", "第 2 集", 1, 2, TimeSpan.FromMinutes(45).Ticks, null, 100, null, null),
                new EpisodeInfo("episode-1-1", "第 1 集", 1, 1, TimeSpan.FromMinutes(45).Ticks, null, 100, null, null)
            }));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);
        await ((AsyncRelayCommand)context.ViewModel.SeriesPlayCommand).ExecuteAsync();

        Assert.AreEqual("从第 1 集重播", context.ViewModel.SeriesPrimaryButtonText);
        Assert.AreEqual("episode-1-1", context.PlaybackService.LastRequest?.ItemId);
    }

    [TestMethod]
    public async Task LoadAsync_EmptySeasonListShowsEmptySeasonState()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(Array.Empty<SeasonInfo>()));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);

        Assert.IsTrue(context.ViewModel.IsSeasonsEmpty);
        Assert.AreEqual(0, context.SeriesService.LoadEpisodesCallCount);
    }

    [TestMethod]
    public async Task LoadAsync_SeasonFailureKeepsDetailContentVisible()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Failure(SeriesLoadError.ServerUnreachable));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);

        Assert.IsTrue(context.ViewModel.IsContentVisible);
        Assert.IsTrue(context.ViewModel.IsSeasonErrorVisible);
        Assert.AreEqual("无法加载剧集，请检查网络或服务器", context.ViewModel.SeasonErrorMessage);
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
    }

    [TestMethod]
    public async Task SelectSeasonAsync_EpisodeFailureDoesNotClearSeasonList()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, false)
            }));
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, _, _) =>
            Task.FromResult(SeriesEpisodesLoadResult.Failure(SeriesLoadError.ServerTimeout));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);

        Assert.AreEqual(1, context.ViewModel.Seasons.Count);
        Assert.IsTrue(context.ViewModel.IsEpisodeErrorVisible);
        Assert.AreEqual("单集加载超时，请稍后重试", context.ViewModel.EpisodeErrorMessage);
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
    }

    [TestMethod]
    public async Task LoadAsync_SeasonUnauthorizedClearsSessionAndNavigatesToLogin()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Failure(SeriesLoadError.Unauthorized));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);

        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.IsNull(context.CurrentSessionService.CurrentSession);
        Assert.AreEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual("登录状态已失效，请重新登录", context.LoginErrorMessage);
    }

    [TestMethod]
    public async Task LoadAsync_SeasonForbiddenKeepsSessionAndShowsPermissionError()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Failure(SeriesLoadError.Forbidden));

        await context.ViewModel.LoadAsync("series-1", AppPage.Home);

        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
        Assert.AreNotEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual("没有权限加载剧集", context.ViewModel.SeasonErrorMessage);
    }

    [TestMethod]
    public void OpenEpisodeCommand_NavigatesToDetailWithEpisodeId()
    {
        var context = CreateContext();
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, e) => navigationParameter = e.Parameter;
        var episode = new EpisodeInfoViewModel(new EpisodeInfo(
            "episode-1",
            "第 1 集：第一集",
            1,
            1,
            null,
            null,
            null,
            null,
            null));

        context.ViewModel.OpenEpisodeCommand.Execute(episode);

        Assert.AreEqual(AppPage.Detail, context.NavigationService.CurrentPage);
        Assert.IsInstanceOfType<DetailNavigationParameter>(navigationParameter);
        var detailParameter = (DetailNavigationParameter)navigationParameter!;
        Assert.AreEqual("episode-1", detailParameter.ItemId);
        Assert.AreEqual("episode-1", detailParameter.BackTarget?.FocusedEpisodeId);
    }

    [TestMethod]
    public async Task BackCommand_FromEpisodeDetailReturnsSeriesDetailAndRestoresSeason()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(
                itemId,
                itemId == "episode-1" ? "Episode" : "Series")));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, false),
                new SeasonInfo("season-2", "第 2 季", 2, false)
            }));
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, seasonId, _) =>
            Task.FromResult(SeriesEpisodesLoadResult.Success(new[]
            {
                new EpisodeInfo("episode-1", "第 1 集：第一集", 2, 1, null, null, null, null, null)
            }));

        await context.ViewModel.LoadAsync("series-1", AppPage.Library);
        await context.ViewModel.SelectSeasonAsync(context.ViewModel.Seasons[1]);

        object? episodeNavigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, e) => episodeNavigationParameter = e.Parameter;
        context.ViewModel.OpenEpisodeCommand.Execute(context.ViewModel.Episodes[0]);

        Assert.IsInstanceOfType<DetailNavigationParameter>(episodeNavigationParameter);
        await context.ViewModel.LoadAsync((DetailNavigationParameter)episodeNavigationParameter!);

        Assert.AreEqual("Episode", context.ViewModel.Detail?.Type);
        Assert.IsFalse(context.ViewModel.IsSeriesSectionVisible);

        object? backNavigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, e) => backNavigationParameter = e.Parameter;
        context.ViewModel.BackCommand.Execute(null);

        Assert.IsInstanceOfType<DetailNavigationParameter>(backNavigationParameter);
        var detailParameter = (DetailNavigationParameter)backNavigationParameter!;
        Assert.AreEqual("series-1", detailParameter.ItemId);
        Assert.AreEqual(AppPage.Library, detailParameter.ReturnPage);
        Assert.AreEqual("season-2", detailParameter.SelectedSeasonId);
        Assert.AreEqual("episode-1", detailParameter.FocusedEpisodeId);

        await context.ViewModel.LoadAsync(detailParameter);

        Assert.AreEqual("Series", context.ViewModel.Detail?.Type);
        Assert.AreEqual("season-2", context.ViewModel.SelectedSeason?.Id);
        Assert.AreEqual("episode-1", context.ViewModel.SelectedEpisode?.Id);
    }

    [TestMethod]
    public void EpisodeInfoViewModel_UsesFriendlyTitleWithoutRepeatingSeriesName()
    {
        var episode = new EpisodeInfoViewModel(new EpisodeInfo(
            "episode-1",
            "第 6 集：单集标题",
            1,
            6,
            null,
            null,
            null,
            null,
            null));

        Assert.AreEqual("第 6 集：单集标题", episode.Title);
    }

    [TestMethod]
    public void EpisodeInfoViewModel_ExposesHeroImageUrl()
    {
        var episode = new EpisodeInfoViewModel(new EpisodeInfo(
            "episode-1",
            "第 1 集",
            1,
            1,
            null,
            null,
            null,
            null,
            "http://media.local/thumb",
            HeroImageUrl: "http://media.local/hero"));

        Assert.AreEqual("http://media.local/hero", episode.HeroImageUrl);
    }

    [TestMethod]
    public async Task RetryCommand_ReloadsDetail()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, _, _) =>
            Task.FromResult(MediaDetailLoadResult.Failure(MediaDetailLoadError.ServerUnreachable));
        await context.ViewModel.LoadAsync("item-1", AppPage.Home);
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId)));

        await ((AsyncRelayCommand)context.ViewModel.RetryCommand).ExecuteAsync();

        Assert.AreEqual(2, context.MediaDetailService.LoadCallCount);
        Assert.IsFalse(context.ViewModel.HasError);
        Assert.AreEqual("Detail Item", context.ViewModel.Title);
    }

    [TestMethod]
    public async Task LoadAsync_UnauthorizedClearsSessionAndNavigatesToLogin()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, _, _) =>
            Task.FromResult(MediaDetailLoadResult.Failure(MediaDetailLoadError.Unauthorized));

        await context.ViewModel.LoadAsync("item-1", AppPage.Home);

        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.IsNull(context.CurrentSessionService.CurrentSession);
        Assert.AreEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual("登录状态已失效，请重新登录", context.LoginErrorMessage);
    }

    [TestMethod]
    public async Task LoadAsync_ForbiddenKeepsSessionAndShowsPermissionError()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, _, _) =>
            Task.FromResult(MediaDetailLoadResult.Failure(MediaDetailLoadError.Forbidden));

        await context.ViewModel.LoadAsync("item-1", AppPage.Home);

        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
        Assert.AreNotEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual("没有权限查看此媒体", context.ViewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task LoadAsync_NetworkFailureDoesNotClearSession()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, _, _) =>
            Task.FromResult(MediaDetailLoadResult.Failure(MediaDetailLoadError.ServerUnreachable));

        await context.ViewModel.LoadAsync("item-1", AppPage.Library);

        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
        Assert.IsTrue(context.ViewModel.HasError);
        Assert.AreEqual("无法加载详情，请检查网络或服务器", context.ViewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task LoadAsync_NotFoundRequestsLocalSearchIndexRefresh()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, _, _) =>
            Task.FromResult(MediaDetailLoadResult.Failure(MediaDetailLoadError.NotFound));

        await context.ViewModel.LoadAsync("stale-item", AppPage.Search);

        Assert.AreSame(context.Session, context.LocalMediaSearchIndex.LastSession);
        Assert.AreEqual("媒体不存在或已不可用", context.ViewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task BackCommand_FromHomeReturnsHome()
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync("item-1", AppPage.Home);

        context.ViewModel.BackCommand.Execute(null);

        Assert.AreEqual(AppPage.Home, context.NavigationService.CurrentPage);
    }

    [TestMethod]
    public async Task BackCommand_FromLibraryReturnsLibrary()
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync("item-1", AppPage.Library);

        context.ViewModel.BackCommand.Execute(null);

        Assert.AreEqual(AppPage.Library, context.NavigationService.CurrentPage);
    }

    [TestMethod]
    public async Task BackCommand_FromSearchReturnsSearch()
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync("item-1", AppPage.Search);

        context.ViewModel.BackCommand.Execute(null);

        Assert.AreEqual(AppPage.Search, context.NavigationService.CurrentPage);
    }

    [TestMethod]
    public async Task PlayCommand_PreparesPlaybackFromStartAndNavigatesPlayer()
    {
        var context = CreateContext();
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, e) => navigationParameter = e.Parameter;

        await context.ViewModel.LoadAsync("item-1", AppPage.Home);
        await ((AsyncRelayCommand)context.ViewModel.PlayCommand).ExecuteAsync();

        Assert.AreEqual(1, context.PlaybackService.PrepareCallCount);
        Assert.AreEqual("item-1", context.PlaybackService.LastRequest!.ItemId);
        Assert.AreEqual(0, context.PlaybackService.LastRequest.StartPositionTicks);
        Assert.AreEqual(AppPage.Player, context.NavigationService.CurrentPage);
        Assert.IsInstanceOfType<PlayerNavigationParameter>(navigationParameter);
        var playerParameter = (PlayerNavigationParameter)navigationParameter!;
        Assert.AreEqual("item-1", playerParameter.BackToDetailParameter.ItemId);
        Assert.AreEqual(AppPage.Home, playerParameter.BackToDetailParameter.ReturnPage);
        Assert.AreEqual("http://media.local:8096/Items/item-1/Images/Logo", playerParameter.LogoUrl);
    }

    [TestMethod]
    public async Task PlayCommand_SeriesDetailDoesNotPreparePlayback()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Series")));

        await context.ViewModel.LoadAsync("series-1", AppPage.Library);

        Assert.IsFalse(context.ViewModel.IsPlayableMedia);
        Assert.IsFalse(context.ViewModel.IsPlaybackControlsVisible);
        Assert.IsTrue(context.ViewModel.IsPlaybackUnavailableVisible);
        Assert.AreEqual("请选择具体单集播放", context.ViewModel.PlaybackUnavailableText);
        Assert.IsFalse(context.ViewModel.PlayCommand.CanExecute(null));
        await ((AsyncRelayCommand)context.ViewModel.PlayCommand).ExecuteAsync();

        Assert.AreEqual(0, context.PlaybackService.PrepareCallCount);
    }

    [TestMethod]
    public async Task PlayCommand_EpisodeDetailCanPreparePlayback()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId, "Episode")));

        await context.ViewModel.LoadAsync("episode-1", AppPage.Home);
        await ((AsyncRelayCommand)context.ViewModel.PlayCommand).ExecuteAsync();

        Assert.IsTrue(context.ViewModel.IsPlayableMedia);
        Assert.AreEqual(1, context.PlaybackService.PrepareCallCount);
        Assert.AreEqual("episode-1", context.PlaybackService.LastRequest!.ItemId);
    }

    [TestMethod]
    public async Task RestartPlayCommand_PreparesPlaybackFromStart()
    {
        var context = CreateContext();

        await context.ViewModel.LoadAsync("item-1", AppPage.Home);
        await ((AsyncRelayCommand)context.ViewModel.RestartPlayCommand).ExecuteAsync();

        Assert.AreEqual(1, context.PlaybackService.PrepareCallCount);
        Assert.AreEqual(0, context.PlaybackService.LastRequest!.StartPositionTicks);
    }

    [TestMethod]
    public async Task ContinuePlayCommand_UsesResumePositionTicks()
    {
        var context = CreateContext();

        await context.ViewModel.LoadAsync("item-1", AppPage.Home);
        await ((AsyncRelayCommand)context.ViewModel.ContinuePlayCommand).ExecuteAsync();

        Assert.AreEqual(1, context.PlaybackService.PrepareCallCount);
        Assert.AreEqual(TimeSpan.FromMinutes(12).Ticks, context.PlaybackService.LastRequest!.StartPositionTicks);
    }

    [TestMethod]
    public async Task ContinuePlayCommand_IsUnavailableWithoutResumePosition()
    {
        var context = CreateContext();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(new MediaDetail(
                itemId,
                "Detail Item",
                "Movie",
                2024,
                TimeSpan.FromMinutes(90).Ticks,
                "Overview",
                Array.Empty<string>(),
                null,
                null,
                null,
                null,
                null)));

        await context.ViewModel.LoadAsync("item-1", AppPage.Home);

        Assert.IsFalse(context.ViewModel.IsContinuePlaybackVisible);
        Assert.IsFalse(context.ViewModel.ContinuePlayCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task PlayCommand_DisablesPlaybackCommandsWhilePreparing()
    {
        var context = CreateContext();
        var pendingPlayback = new TaskCompletionSource<PlaybackLoadResult>();
        context.PlaybackService.PreparePlaybackAsyncHandler = (_, request, _) => pendingPlayback.Task;
        await context.ViewModel.LoadAsync("item-1", AppPage.Home);

        var playbackTask = ((AsyncRelayCommand)context.ViewModel.PlayCommand).ExecuteAsync();

        Assert.IsTrue(context.ViewModel.IsPreparingPlayback);
        Assert.IsFalse(context.ViewModel.PlayCommand.CanExecute(null));
        Assert.IsFalse(context.ViewModel.ContinuePlayCommand.CanExecute(null));
        Assert.IsFalse(context.ViewModel.RestartPlayCommand.CanExecute(null));

        pendingPlayback.SetResult(PlaybackLoadResult.Success(CreatePlaybackInfo(new PlaybackStartRequest("item-1", "Detail Item", 0))));
        await playbackTask;

        Assert.IsFalse(context.ViewModel.IsPreparingPlayback);
    }

    [TestMethod]
    public async Task PlayCommand_FailureShowsChineseErrorAndStaysOnDetail()
    {
        var context = CreateContext();
        context.PlaybackService.PreparePlaybackAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackLoadResult.Failure(PlaybackLoadError.ServerUnreachable));
        context.NavigationService.NavigateTo(AppPage.Detail);

        await context.ViewModel.LoadAsync("item-1", AppPage.Home);
        await ((AsyncRelayCommand)context.ViewModel.PlayCommand).ExecuteAsync();

        Assert.AreEqual(AppPage.Detail, context.NavigationService.CurrentPage);
        Assert.IsTrue(context.ViewModel.IsPlaybackErrorVisible);
        Assert.AreEqual("无法准备播放，请检查网络或服务器", context.ViewModel.PlaybackErrorMessage);
    }

    [TestMethod]
    public async Task RetryPlaybackCommand_ReusesLastPlaybackRequest()
    {
        var context = CreateContext();
        context.PlaybackService.PreparePlaybackAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackLoadResult.Failure(PlaybackLoadError.ServerTimeout));
        await context.ViewModel.LoadAsync("item-1", AppPage.Home);
        await ((AsyncRelayCommand)context.ViewModel.ContinuePlayCommand).ExecuteAsync();

        context.PlaybackService.PreparePlaybackAsyncHandler = (_, request, _) =>
            Task.FromResult(PlaybackLoadResult.Success(CreatePlaybackInfo(request)));
        await ((AsyncRelayCommand)context.ViewModel.RetryPlaybackCommand).ExecuteAsync();

        Assert.AreEqual(2, context.PlaybackService.PrepareCallCount);
        Assert.AreEqual(TimeSpan.FromMinutes(12).Ticks, context.PlaybackService.LastRequest!.StartPositionTicks);
        Assert.AreEqual(AppPage.Player, context.NavigationService.CurrentPage);
    }

    [TestMethod]
    public async Task PlayCommand_UnauthorizedClearsSessionAndNavigatesLogin()
    {
        var context = CreateContext();
        context.PlaybackService.PreparePlaybackAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackLoadResult.Failure(PlaybackLoadError.Unauthorized));

        await context.ViewModel.LoadAsync("item-1", AppPage.Home);
        await ((AsyncRelayCommand)context.ViewModel.PlayCommand).ExecuteAsync();

        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.IsNull(context.CurrentSessionService.CurrentSession);
        Assert.AreEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual("登录状态已失效，请重新登录", context.LoginErrorMessage);
    }

    [TestMethod]
    public async Task PlayCommand_ForbiddenKeepsSessionAndShowsPermissionError()
    {
        var context = CreateContext();
        context.PlaybackService.PreparePlaybackAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackLoadResult.Failure(PlaybackLoadError.Forbidden));

        await context.ViewModel.LoadAsync("item-1", AppPage.Home);
        await ((AsyncRelayCommand)context.ViewModel.PlayCommand).ExecuteAsync();

        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
        Assert.AreNotEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual("没有权限播放此媒体", context.ViewModel.PlaybackErrorMessage);
    }

    [TestMethod]
    public async Task MediaDetailPage_UsesSafeOneWayBindingsAndPlaybackPlaceholderText()
    {
        var repositoryRoot = FindRepositoryRoot();
        var detailPagePath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "MediaDetailPage.xaml");
        var xaml = await File.ReadAllTextAsync(detailPagePath);

        StringAssert.Contains(xaml, "Value=\"{Binding ProgressValue, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "ImageUrl=\"{Binding PosterUrl, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "ImageUrl=\"{Binding BackdropUrl, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "ImageUrl=\"{Binding SelectedEpisodeHeroUrl, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "ShowPlaceholder=\"False\"");
        StringAssert.Contains(xaml, "IsHitTestVisible=\"False\"");
        StringAssert.Contains(xaml, "Focusable=\"False\"");
        StringAssert.Contains(xaml, "<GradientStop Color=\"#FF080C12\" Offset=\"0\" />");
        StringAssert.Contains(xaml, "<GradientStop Color=\"#D9080C12\" Offset=\"0.76\" />");
        StringAssert.Contains(xaml, "ImageUrl=\"{Binding ThumbnailUrl, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "Visibility=\"{Binding IsContinuePlaybackVisible, Mode=OneWay");
        StringAssert.Contains(xaml, "Visibility=\"{Binding IsStandardPlaybackPanelVisible, Mode=OneWay");
        StringAssert.Contains(xaml, "Visibility=\"{Binding IsNonSeriesPlaybackUnavailableVisible, Mode=OneWay");
        StringAssert.Contains(xaml, "Command=\"{Binding SeriesPlayCommand}\"");
        StringAssert.Contains(xaml, "Value=\"{Binding SeriesResumeProgressValue, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "Text=\"{Binding PlaybackUnavailableText, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "Text=\"正在准备播放...\"");
        Assert.AreEqual(1, CountOccurrences(xaml, "Visibility=\"{Binding IsStandardPlaybackPanelVisible, Mode=OneWay"));
        StringAssert.Contains(
            xaml.Replace("\r\n", "\n", StringComparison.Ordinal),
            "Visibility=\"{Binding IsStandardProgressVisible, Mode=OneWay, Converter={StaticResource BooleanToVisibilityConverter}}\"\n                        Margin=\"0,30,0,0\"\n                        MaxWidth=\"860\"");
    }

    [TestMethod]
    public async Task MediaDetailPage_HeroLayersFillOneGridWithoutAFixedImageEdge()
    {
        var repositoryRoot = FindRepositoryRoot();
        var detailPagePath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "MediaDetailPage.xaml");
        var xaml = await File.ReadAllTextAsync(detailPagePath);
        var heroStart = xaml.IndexOf(
            "<Grid Grid.Row=\"0\" Background=\"#070A0F\" ClipToBounds=\"True\">",
            StringComparison.Ordinal);
        var heroEnd = xaml.IndexOf("<Grid Grid.Row=\"0\"", heroStart + 1, StringComparison.Ordinal);

        Assert.IsTrue(heroStart >= 0);
        Assert.IsTrue(heroEnd > heroStart);
        var heroXaml = xaml[heroStart..heroEnd];
        Assert.AreEqual(2, CountOccurrences(heroXaml, "<controls:AuthenticatedImage"));
        Assert.AreEqual(1, CountOccurrences(heroXaml, "Stretch=\"UniformToFill\""));
        Assert.AreEqual(1, CountOccurrences(heroXaml, "Stretch=\"Uniform\""));
        Assert.AreEqual(2, CountOccurrences(heroXaml, "HorizontalAlignment=\"Stretch\""));
        Assert.IsFalse(heroXaml.Contains("Width=\"1320\"", StringComparison.Ordinal));
        Assert.IsFalse(heroXaml.Contains("Margin=\"", StringComparison.Ordinal));
        Assert.IsFalse(heroXaml.Contains("OpacityMask", StringComparison.Ordinal));
        Assert.IsFalse(heroXaml.Contains("<Grid.ColumnDefinitions>", StringComparison.Ordinal));
        Assert.IsFalse(heroXaml.Contains("Grid.Column=", StringComparison.Ordinal));
        StringAssert.Contains(heroXaml, "ImageUrl=\"{Binding BackdropUrl, Mode=OneWay}\"");
        StringAssert.Contains(heroXaml, "x:Name=\"EpisodeHeroLayer\"");
        StringAssert.Contains(heroXaml, "ImageUrl=\"{Binding SelectedEpisodeHeroUrl, Mode=OneWay}\"");
        StringAssert.Contains(heroXaml, "ImageHorizontalAlignment=\"Right\"");
        StringAssert.Contains(heroXaml, "VerticalAlignment=\"Stretch\"");
        StringAssert.Contains(heroXaml, "ShowPlaceholder=\"False\"");
        StringAssert.Contains(heroXaml, "IsHitTestVisible=\"False\"");
        StringAssert.Contains(heroXaml, "Focusable=\"False\"");
        StringAssert.Contains(heroXaml, "<GradientStop Color=\"#FA080C12\" Offset=\"0.45\" />");
        StringAssert.Contains(heroXaml, "<GradientStop Color=\"#C8080C12\" Offset=\"0.58\" />");
        StringAssert.Contains(heroXaml, "<GradientStop Color=\"#70080C12\" Offset=\"0.74\" />");
        StringAssert.Contains(heroXaml, "<GradientStop Color=\"#18080C12\" Offset=\"0.90\" />");
        StringAssert.Contains(heroXaml, "<GradientStop Color=\"#00080C12\" Offset=\"1\" />");
        StringAssert.Contains(heroXaml, "<GradientStop Color=\"#D9080C12\" Offset=\"0.76\" />");

        var episodeLayerStart = heroXaml.IndexOf(
            "<controls:AuthenticatedImage x:Name=\"EpisodeHeroLayer\"",
            StringComparison.Ordinal);
        var episodeLayerEnd = heroXaml.IndexOf("/>", episodeLayerStart, StringComparison.Ordinal);
        Assert.IsTrue(episodeLayerStart >= 0);
        Assert.IsTrue(episodeLayerEnd > episodeLayerStart);
        var episodeLayerXaml = heroXaml[episodeLayerStart..episodeLayerEnd];
        Assert.IsFalse(episodeLayerXaml.Contains("Width=\"", StringComparison.Ordinal));
        Assert.IsFalse(episodeLayerXaml.Contains("Height=\"", StringComparison.Ordinal));
        Assert.IsFalse(episodeLayerXaml.Contains("MaxHeight=\"", StringComparison.Ordinal));
        Assert.IsFalse(episodeLayerXaml.Contains("Grid.Row=", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MediaDetailPage_HorizontalEpisodeTracksVirtualizeAndCenterWithoutDelay()
    {
        var repositoryRoot = FindRepositoryRoot();
        var pagePath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "MediaDetailPage.xaml");
        var codePath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "MediaDetailPage.xaml.cs");
        var xaml = await File.ReadAllTextAsync(pagePath);
        var code = await File.ReadAllTextAsync(codePath);

        StringAssert.Contains(xaml, "x:Name=\"EpisodeNumberListBox\"");
        StringAssert.Contains(xaml, "x:Name=\"EpisodeCardListBox\"");
        Assert.AreEqual(2, CountOccurrences(xaml, "SelectedItem=\"{Binding SelectedEpisode, Mode=TwoWay}\""));
        Assert.AreEqual(2, CountOccurrences(xaml, "VirtualizingPanel.VirtualizationMode=\"Recycling\""));
        Assert.AreEqual(2, CountOccurrences(xaml, "<VirtualizingStackPanel Orientation=\"Horizontal\""));
        Assert.AreEqual(2, CountOccurrences(xaml, "ScrollViewer.CanContentScroll=\"True\""));
        StringAssert.Contains(xaml, "Command=\"{Binding DataContext.PlayEpisodeCommand, RelativeSource={RelativeSource AncestorType=UserControl}}\"");
        StringAssert.Contains(xaml, "CommandParameter=\"{Binding}\"");
        StringAssert.Contains(xaml, "AutomationProperties.Name=\"{Binding AutomationName, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "<Setter Property=\"Width\" Value=\"292\" />");
        StringAssert.Contains(xaml, "Height=\"164\"");
        StringAssert.Contains(xaml, "<Setter Property=\"Height\" Value=\"334\" />");
        StringAssert.Contains(xaml, "MaxHeight=\"44\"");
        StringAssert.Contains(xaml, "MaxHeight=\"60\"");
        StringAssert.Contains(xaml, "Style=\"{StaticResource MediaPlayedBadgeStyle}\"");
        StringAssert.Contains(xaml, "Style=\"{StaticResource MediaCardProgressBarStyle}\"");
        StringAssert.Contains(xaml, "PlaceholderText=\"暂无缩略图\"");
        Assert.AreEqual(6, CountOccurrences(xaml, "Click=\"OnHorizontalRowLeftClick\""));
        Assert.AreEqual(6, CountOccurrences(xaml, "Click=\"OnHorizontalRowRightClick\""));
        Assert.AreEqual(6, CountOccurrences(xaml, "HorizontalScrollBarVisibility=\"Hidden\""));
        Assert.AreEqual(2, CountOccurrences(xaml, "RequestBringIntoView=\"OnEpisodeTrackRequestBringIntoView\""));
        StringAssert.Contains(xaml, "CommandParameter=\"{Binding ElementName=EpisodeNumberListBox}\"");
        StringAssert.Contains(xaml, "CommandParameter=\"{Binding ElementName=EpisodeCardListBox}\"");
        Assert.IsFalse(xaml.Contains("Command=\"{Binding DataContext.OpenEpisodeCommand", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("x:Name=\"EpisodesItemsControl\"", StringComparison.Ordinal));
        Assert.AreEqual(0, CountOccurrences(xaml, "SelectionChanged=\"OnEpisodeTrackSelectionChanged\""));
        StringAssert.Contains(code, "ItemContainerGenerator.StatusChanged");
        StringAssert.Contains(code, "episodeWaitingForContainers");
        StringAssert.Contains(code, "generator.Status != GeneratorStatus.ContainersGenerated");
        StringAssert.Contains(code, "listBox.ScrollIntoView(episode)");
        StringAssert.Contains(code, "episodePositionGate.TryQueue");
        StringAssert.Contains(code, "episodeCenteringOperation.Abort()");
        StringAssert.Contains(code, "ShouldWriteScrollOffset");
        StringAssert.Contains(code, "GetEpisodeTrackStableWidth");
        StringAssert.Contains(code, "scrollViewer?.ActualWidth > 0");
        StringAssert.Contains(code, "ReferenceEquals(e.OriginalSource, MediaDetailScrollViewer)");
        var queuedCenteringStart = code.IndexOf(
            "private void QueueEpisodeCenteringOperation",
            StringComparison.Ordinal);
        var centeredItemStart = code.IndexOf(
            "private bool CenterSelectedItem",
            StringComparison.Ordinal);
        Assert.IsTrue(queuedCenteringStart >= 0 && centeredItemStart > queuedCenteringStart);
        var queuedCenteringBody = code[queuedCenteringStart..centeredItemStart];
        Assert.IsFalse(queuedCenteringBody.Contains(
            "panel.Margin",
            StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("listBox.Padding =", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("panel.Margin =", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("UpdateLayout()", StringComparison.Ordinal));
        StringAssert.Contains(code, "Dispatcher.BeginInvoke");
        StringAssert.Contains(code, "CalculateCenteredHorizontalOffset");
        StringAssert.Contains(code, "FindVisualChild<ScrollViewer>(listBox)");
        StringAssert.Contains(code, "attachedViewModel?.ShouldBringEpisodeSectionIntoView != true");
        StringAssert.Contains(code, "MediaDetailScrollViewer.ScrollToTop()");
        StringAssert.Contains(code, "CalculateEpisodeSectionVerticalOffset");
        StringAssert.Contains(code, "MediaDetailScrollViewer.ScrollToVerticalOffset");
        StringAssert.Contains(code, "|| episodeSectionPositionApplied");
        StringAssert.Contains(code, "episodeSectionPositionApplied = true;");
        StringAssert.Contains(code, "episodeSectionPositionOperation is { Status: DispatcherOperationStatus.Pending }");
        StringAssert.Contains(code, "private void OnEpisodeTrackRequestBringIntoView");
        StringAssert.Contains(code, "e.Handled = true;");
        StringAssert.Contains(code, "CancelHorizontalScrollAnimation(scrollViewer);");
        StringAssert.Contains(code, "HorizontalScrollPageRatio = 0.95d");
        StringAssert.Contains(code, "TimeSpan.FromMilliseconds(220)");
        Assert.IsFalse(code.Contains("Task.Delay", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("DispatcherTimer", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EpisodeInfoViewModel_ProvidesStableCardTextProgressAndAccessibleState()
    {
        var episode = new EpisodeInfoViewModel(new EpisodeInfo(
            "episode-4",
            "",
            2,
            4,
            TimeSpan.FromMinutes(40).Ticks,
            null,
            null,
            TimeSpan.FromMinutes(10).Ticks,
            null));

        Assert.AreEqual("4", episode.QuickNumberText);
        Assert.AreEqual("第 4 集", episode.Title);
        Assert.AreEqual("暂无简介", episode.OverviewText);
        Assert.IsTrue(episode.HasProgress);
        Assert.AreEqual(25, episode.ProgressValue);
        Assert.AreEqual("继续", episode.PlayActionText);
        StringAssert.Contains(episode.AutomationName, "S2:E4");
        StringAssert.Contains(episode.AutomationName, "已观看 25%");
    }

    [TestMethod]
    public async Task IconSystemPhaseOne_DefinesSharedSemanticKeysAndVisualOnlyAppIcon()
    {
        var repositoryRoot = FindRepositoryRoot();
        var iconsPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Resources", "Icons.xaml");
        var appIconPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Controls", "AppIcon.cs");
        var iconButtonsPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Styles", "IconButtons.xaml");

        var icons = await File.ReadAllTextAsync(iconsPath);
        var appIcon = await File.ReadAllTextAsync(appIconPath);
        var iconButtons = await File.ReadAllTextAsync(iconButtonsPath);

        foreach (var key in new[]
                 {
                     "Icon.Back",
                     "Icon.Play",
                     "Icon.Restart",
                     "Icon.FavoriteOutline",
                     "Icon.FavoriteFilled",
                     "Icon.Watched",
                     "Icon.Unwatched",
                     "Icon.ChevronRight",
                     "Icon.Retry",
                 })
        {
            StringAssert.Contains(icons, $"x:Key=\"{key}\"");
        }

        StringAssert.Contains(appIcon, "FocusableProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(false))");
        StringAssert.Contains(appIcon, "IsHitTestVisibleProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(false))");
        StringAssert.Contains(appIcon, "public enum AppIconRenderMode");
        StringAssert.Contains(appIcon, "StrokeThicknessProperty");
        Assert.IsFalse(appIcon.Contains("Geometry.Bounds", StringComparison.Ordinal));
        Assert.IsFalse(appIcon.Contains("DrawGeometry", StringComparison.Ordinal));
        StringAssert.Contains(appIcon, "FrameworkPropertyMetadataOptions.AffectsMeasure");
        StringAssert.Contains(appIcon, "FrameworkPropertyMetadataOptions.AffectsArrange");
        StringAssert.Contains(appIcon, "FrameworkPropertyMetadataOptions.AffectsRender");
        StringAssert.Contains(iconButtons, "<Style TargetType=\"{x:Type controls:AppIcon}\">");
        StringAssert.Contains(iconButtons, "BasedOn=\"{StaticResource {x:Type controls:AppIcon}}\"");
        StringAssert.Contains(iconButtons, "<Viewbox Width=\"{TemplateBinding Size}\"");
        StringAssert.Contains(iconButtons, "Height=\"{TemplateBinding Size}\"");
        StringAssert.Contains(iconButtons, "Width=\"24\"");
        StringAssert.Contains(iconButtons, "Height=\"24\"");
        StringAssert.Contains(iconButtons, "x:Name=\"FillPath\"");
        StringAssert.Contains(iconButtons, "x:Name=\"StrokePath\"");
        StringAssert.Contains(iconButtons, "StrokeThickness=\"{TemplateBinding StrokeThickness}\"");
        StringAssert.Contains(iconButtons, "StrokeStartLineCap=\"Round\"");
        StringAssert.Contains(iconButtons, "StrokeLineJoin=\"Round\"");
        StringAssert.Contains(iconButtons, "<Trigger Property=\"RenderMode\" Value=\"Fill\">");
        StringAssert.Contains(iconButtons, "<Setter Property=\"StrokeThickness\" Value=\"1.9\" />");
    }

    [TestMethod]
    public void AppIconTemplate_RendersFillAndStrokeModesWithFixedCanvas()
    {
        RunOnStaThread(() =>
        {
        var resources = LoadIconSystemResources();
        Assert.IsNotNull(resources["Icon.Play"]);
        Assert.IsNotNull(resources[typeof(AppIcon)]);
        Assert.IsNotNull(resources["PrimaryActionButtonIcon"]);
        Assert.IsNotNull(resources["SecondaryActionButtonIcon"]);
        Assert.IsNotNull(resources["EpisodePlayButtonIcon"]);
        Assert.IsNotNull(resources["CompactIconButtonIcon"]);

        var primaryStyle = (Style)resources["PrimaryActionButtonIcon"];
        var secondaryStyle = (Style)resources["SecondaryActionButtonIcon"];
        Assert.AreSame(resources[typeof(AppIcon)], primaryStyle.BasedOn);
        Assert.AreSame(resources[typeof(AppIcon)], secondaryStyle.BasedOn);

        var fillIcon = new AppIcon
        {
            Data = (Geometry)resources["Icon.Play"],
            Style = primaryStyle,
        };

        var strokeIcon = new AppIcon
        {
            Data = (Geometry)resources["Icon.FavoriteOutline"],
            Style = secondaryStyle,
        };

        var panel = new StackPanel
        {
            Resources = resources,
        };
        var button = new Button
        {
            Foreground = Brushes.White,
            Content = panel,
        };
        panel.Children.Add(fillIcon);
        panel.Children.Add(strokeIcon);

        button.Measure(new Size(200, 100));
        button.Arrange(new Rect(0, 0, 200, 100));
        fillIcon.ApplyTemplate();
        strokeIcon.ApplyTemplate();
        fillIcon.Measure(new Size(100, 100));
        strokeIcon.Measure(new Size(100, 100));

        var fillPath = (ShapePath?)fillIcon.Template.FindName("FillPath", fillIcon);
        var fillStrokePath = (ShapePath?)fillIcon.Template.FindName("StrokePath", fillIcon);
        var strokeFillPath = (ShapePath?)strokeIcon.Template.FindName("FillPath", strokeIcon);
        var strokePath = (ShapePath?)strokeIcon.Template.FindName("StrokePath", strokeIcon);
        var fillViewbox = (Viewbox?)fillIcon.Template.FindName("IconViewbox", fillIcon);
        var fillCanvas = (FrameworkElement?)fillIcon.Template.FindName("IconCanvas", fillIcon);

        Assert.IsNotNull(fillIcon.Template);
        Assert.IsNotNull(fillPath);
        Assert.IsNotNull(fillStrokePath);
        Assert.IsNotNull(strokeFillPath);
        Assert.IsNotNull(strokePath);
        Assert.IsNotNull(fillViewbox);
        Assert.IsNotNull(fillCanvas);
        Assert.AreEqual(18d, fillIcon.Size);
        Assert.AreNotEqual(0d, fillIcon.DesiredSize.Width);
        Assert.AreEqual(Visibility.Visible, fillPath.Visibility);
        Assert.AreEqual(Visibility.Collapsed, fillStrokePath.Visibility);
        Assert.IsNotNull(fillPath.Data);
        Assert.IsNotNull(fillPath.Fill);
        Assert.AreEqual(AppIconRenderMode.Fill, fillIcon.RenderMode);
        Assert.AreEqual(24d, fillCanvas.Width);
        Assert.AreEqual(24d, fillCanvas.Height);

        Assert.AreEqual(Visibility.Collapsed, strokeFillPath.Visibility);
        Assert.AreEqual(Visibility.Visible, strokePath.Visibility);
        Assert.IsNotNull(strokePath.Data);
        Assert.IsNotNull(strokePath.Stroke);
        Assert.AreEqual(1.9d, strokeIcon.StrokeThickness);
        Assert.AreEqual(AppIconRenderMode.Stroke, strokeIcon.RenderMode);
        });
    }

    [TestMethod]
    public async Task MediaDetailPage_UsesSharedIconsAndKeepsExistingCommands()
    {
        var repositoryRoot = FindRepositoryRoot();
        var detailPagePath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "MediaDetailPage.xaml");
        var xaml = await File.ReadAllTextAsync(detailPagePath);

        Assert.IsFalse(xaml.Contains("Segoe MDL2 Assets", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("Tag=\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("E768", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(xaml.Contains("EB51", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(xaml.Contains("E73E", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(xaml.Contains("▶", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("♡", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("♥", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains(".png", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(xaml.Contains(".svg", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(xaml.Contains("<Path", StringComparison.Ordinal));

        StringAssert.Contains(xaml, "Data=\"{StaticResource Icon.Play}\"");
        StringAssert.Contains(xaml, "Data=\"{StaticResource Icon.Restart}\"");
        StringAssert.Contains(xaml, "Value=\"{StaticResource Icon.WatchLater}\"");
        StringAssert.Contains(xaml, "Value=\"{StaticResource Icon.FavoriteFilled}\"");
        StringAssert.Contains(xaml, "Value=\"{StaticResource Icon.Watched}\"");
        StringAssert.Contains(xaml, "Value=\"{StaticResource Icon.Unwatched}\"");
        StringAssert.Contains(xaml, "Command=\"{Binding DataContext.PlayEpisodeCommand, RelativeSource={RelativeSource AncestorType=UserControl}}\"");
        StringAssert.Contains(xaml, "Style=\"{StaticResource CardOverlayPlayButton}\"");
        Assert.IsFalse(xaml.Contains("Text=\"{Binding PlayActionText, Mode=OneWay}\"", StringComparison.Ordinal));
        Assert.IsTrue(CountOccurrences(xaml, "Data=\"{StaticResource Icon.Play}\"") >= 3);
        Assert.AreEqual(1, CountOccurrences(await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Resources", "Icons.xaml")), "x:Key=\"Icon.Play\""));

        StringAssert.Contains(xaml, "Command=\"{Binding PlayCommand}\"");
        StringAssert.Contains(xaml, "Command=\"{Binding ContinuePlayCommand}\"");
        StringAssert.Contains(xaml, "Command=\"{Binding RestartPlayCommand}\"");
        StringAssert.Contains(xaml, "Command=\"{Binding SeriesPlayCommand}\"");
        StringAssert.Contains(xaml, "PlayEpisodeCommand");
        StringAssert.Contains(xaml, "Command=\"{Binding ToggleFavoriteCommand}\"");
        StringAssert.Contains(xaml, "Command=\"{Binding TogglePlayedCommand}\"");
        StringAssert.Contains(xaml, "ToolTip=\"播放当前媒体\"");
        StringAssert.Contains(xaml, "AutomationProperties.Name=\"播放\"");
        StringAssert.Contains(xaml, "AutomationProperties.HelpText=\"从默认位置开始播放当前媒体。\"");
    }

    [TestMethod]
    public void MediaDetailPage_MovieSectionsRemainVisibleOutsideCollapsedEpisodeSection()
    {
        RunOnStaThread(() =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var resources = LoadApplicationResources();
            app.Resources.MergedDictionaries.Add(resources);
            var context = CreateContext();
            context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
                Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(
                    itemId,
                    itemId == "series-1" ? "Series" : "Movie",
                    people: new[] { new MediaPerson("person-1", "Actor", "Lead", "Actor", null) },
                    artworkUrls: new[] { "http://media.local/art/1" })));
            context.SimilarMediaService.LoadSimilarAsyncHandler = (_, _, _) =>
                Task.FromResult(SimilarMediaLoadResult.Success(new[]
                {
                    CreateSimilarItem("movie-2", "Similar Movie", "Movie")
                }));
            context.ViewModel.LoadAsync("movie-1", AppPage.Library).GetAwaiter().GetResult();
            var page = new MediaDetailPage { DataContext = context.ViewModel };
            var window = new Window
            {
                Width = 1200,
                Height = 900,
                Left = -10000,
                Top = -10000,
                ShowActivated = false,
                ShowInTaskbar = false,
                Opacity = 0,
                Content = page
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                var supplementary = (StackPanel)page.FindName("SupplementarySectionRoot");
                var episodeSection = (StackPanel)page.FindName("EpisodeSectionRoot");
                var peopleSection = (StackPanel)page.FindName("PeopleSection");
                var artworkSection = (StackPanel)page.FindName("ArtworkSection");
                var similarSection = (StackPanel)page.FindName("SimilarSection");

                Assert.AreEqual(Visibility.Collapsed, episodeSection.Visibility);
                Assert.AreEqual(0d, episodeSection.DesiredSize.Height);
                foreach (var section in new[] { peopleSection, artworkSection, similarSection })
                {
                    Assert.IsTrue(section.IsVisible, $"{section.Name} must have visible ancestors for movies.");
                    Assert.IsTrue(section.ActualHeight > 0, $"{section.Name} must participate in layout.");
                    Assert.AreSame(supplementary, section.Parent);
                }

                Assert.AreEqual(peopleSection.Margin.Top,
                    peopleSection.TransformToAncestor(supplementary).Transform(new Point()).Y,
                    0.1,
                    "The hidden season and episode block must not leave a gap before cast and crew.");
                Assert.IsTrue(artworkSection.TransformToAncestor(supplementary).Transform(new Point()).Y
                    >= peopleSection.ActualHeight + peopleSection.Margin.Top);
                Assert.IsTrue(similarSection.TransformToAncestor(supplementary).Transform(new Point()).Y
                    > artworkSection.TransformToAncestor(supplementary).Transform(new Point()).Y);

                context.ViewModel.LoadAsync("series-1", AppPage.Library).GetAwaiter().GetResult();
                window.UpdateLayout();
                Assert.IsTrue(episodeSection.IsVisible);
                Assert.IsTrue(episodeSection.ActualHeight > 0);

                context.ViewModel.LoadAsync("movie-1", AppPage.Library).GetAwaiter().GetResult();
                window.UpdateLayout();
                Assert.AreEqual(Visibility.Collapsed, episodeSection.Visibility);
                Assert.IsTrue(peopleSection.IsVisible);
                Assert.IsTrue(artworkSection.IsVisible);
                Assert.IsTrue(similarSection.IsVisible);
            }
            finally
            {
                window.Close();
                app.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }

    [TestMethod]
    public async Task MediaDetailPage_PlacesPeopleArtworkAndSimilarInOrderAndUsesAuthenticatedImages()
    {
        var repositoryRoot = FindRepositoryRoot();
        var detailPagePath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "MediaDetailPage.xaml");
        var xaml = await File.ReadAllTextAsync(detailPagePath);

        var episodesIndex = xaml.IndexOf("x:Name=\"EpisodeCardListBox\"", StringComparison.Ordinal);
        var peopleIndex = xaml.IndexOf("x:Name=\"PeopleSection\"", StringComparison.Ordinal);
        var artworkIndex = xaml.IndexOf("x:Name=\"ArtworkSection\"", StringComparison.Ordinal);
        var similarIndex = xaml.IndexOf("x:Name=\"SimilarSection\"", StringComparison.Ordinal);

        Assert.IsTrue(episodesIndex >= 0);
        Assert.IsTrue(peopleIndex > episodesIndex);
        Assert.IsTrue(artworkIndex > peopleIndex);
        Assert.IsTrue(similarIndex > artworkIndex);
        StringAssert.Contains(xaml, "Visibility=\"{Binding IsPeopleSectionVisible, Mode=OneWay, Converter={StaticResource BooleanToVisibilityConverter}}\"");
        StringAssert.Contains(xaml, "Visibility=\"{Binding IsArtworkSectionVisible, Mode=OneWay, Converter={StaticResource BooleanToVisibilityConverter}}\"");
        StringAssert.Contains(xaml, "ItemsSource=\"{Binding People, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "ItemsSource=\"{Binding ArtworkUrls, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "ImageUrl=\"{Binding ImageUrl, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "ImageUrl=\"{Binding Mode=OneWay}\"");
        StringAssert.Contains(xaml, "PlaceholderText=\"暂无人物图片\"");
        StringAssert.Contains(xaml, "PlaceholderText=\"暂无艺术图\"");
        StringAssert.Contains(xaml, "Visibility=\"{Binding IsSimilarSectionVisible, Mode=OneWay, Converter={StaticResource BooleanToVisibilityConverter}}\"");
        StringAssert.Contains(xaml, "ItemsSource=\"{Binding SimilarItems, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "Command=\"{Binding DataContext.OpenSimilarCommand, RelativeSource={RelativeSource AncestorType=UserControl}}\"");
        StringAssert.Contains(xaml, "RetryCommand=\"{Binding RetrySimilarCommand}\"");
        StringAssert.Contains(xaml, "x:Name=\"SimilarPreviousButton\"");
        StringAssert.Contains(xaml, "x:Name=\"SimilarNextButton\"");
        StringAssert.Contains(xaml, "x:Name=\"PeoplePreviousButton\"");
        StringAssert.Contains(xaml, "x:Name=\"PeopleNextButton\"");
        StringAssert.Contains(xaml, "x:Name=\"ArtworkPreviousButton\"");
        StringAssert.Contains(xaml, "x:Name=\"ArtworkNextButton\"");
        StringAssert.Contains(xaml, "Click=\"OnHorizontalRowLeftClick\"");
        StringAssert.Contains(xaml, "Click=\"OnHorizontalRowRightClick\"");
        StringAssert.Contains(xaml, "AutomationProperties.Name=\"{Binding AutomationName, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "Path=(ItemsControl.AlternationIndex)");
        StringAssert.Contains(xaml, "Visibility=\"{Binding IsPlayed, Mode=OneWay, Converter={StaticResource BooleanToVisibilityConverter}}\"");
        StringAssert.Contains(xaml, "Value=\"{Binding ProgressValue, Mode=OneWay}\"");

        var codeBehind = await File.ReadAllTextAsync(Path.ChangeExtension(detailPagePath, ".xaml.cs"));
        StringAssert.Contains(codeBehind, "ResolveHorizontalScrollViewer");
        StringAssert.Contains(codeBehind, "AnimateHorizontalOffset(scrollViewer, targetOffset)");
        StringAssert.Contains(codeBehind, "SimilarScrollViewer.ScrollToLeftEnd()");
        Assert.IsFalse(codeBehind.Contains("Task.Delay", StringComparison.Ordinal));
        Assert.IsFalse(codeBehind.Contains("DispatcherTimer", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MediaPages_UseAuthenticatedImageForEmbyImageUrls()
    {
        var repositoryRoot = FindRepositoryRoot();
        foreach (var page in new[] { "HomePage.xaml", "LibraryPage.xaml", "SearchPage.xaml", "MediaDetailPage.xaml" })
        {
            var pagePath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", page);
            var xaml = await File.ReadAllTextAsync(pagePath);

            StringAssert.Contains(xaml, "controls:AuthenticatedImage");
            Assert.IsFalse(xaml.Contains("Image Source=\"{Binding PosterUrl", StringComparison.Ordinal));
            Assert.IsFalse(xaml.Contains("Image Source=\"{Binding BackdropUrl", StringComparison.Ordinal));
            Assert.IsFalse(xaml.Contains("Image Source=\"{Binding ThumbnailUrl", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task PlayerPage_UsesOneWayBindingsAndDoesNotBindFullPlaybackUrl()
    {
        var repositoryRoot = FindRepositoryRoot();
        var playerPagePath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "PlayerPage.xaml");
        var xaml = await File.ReadAllTextAsync(playerPagePath);

        StringAssert.Contains(xaml, "Text=\"{Binding Title, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "x:Name=\"PlayerSurface\"");
        StringAssert.Contains(xaml, "x:Name=\"VideoArea\"");
        StringAssert.Contains(xaml, "x:Name=\"ControlsLayer\"");
        StringAssert.Contains(xaml, "<Grid x:Name=\"TopChrome\"");
        StringAssert.Contains(xaml, "<Grid x:Name=\"BottomChrome\"");
        StringAssert.Contains(xaml, "<controls:SettingsSlider x:Name=\"SeekSlider\"");
        StringAssert.Contains(xaml, "Value=\"{Binding SeekPercent, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"");
        StringAssert.Contains(xaml, "IsEnabled=\"{Binding CanSeek, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "ValueChanged=\"OnSeekSliderValueChanged\"");
        Assert.IsFalse(xaml.Contains("OnSeekHitAreaMouseLeftButtonUp", StringComparison.Ordinal));
        StringAssert.Contains(xaml, "Command=\"{Binding TogglePlayPauseCommand}\"");
        StringAssert.Contains(xaml, "<controls:SettingsSlider x:Name=\"VolumeSlider\"");
        StringAssert.Contains(xaml, "Value=\"{Binding Volume, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "Command=\"{Binding ToggleMuteCommand}\"");
        StringAssert.Contains(xaml, "MouseLeftButtonDown=\"OnVideoAreaMouseLeftButtonDown\"");
        Assert.IsFalse(xaml.Contains("PlaybackPath", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(xaml.Contains("SeekCommand", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(xaml.Contains("Command=\"{Binding PlayCommand}\"", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(xaml.Contains("Command=\"{Binding PauseCommand}\"", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task PlayerPage_CodeBehindHandlesBasicFullscreenShortcuts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var playerPageCodeBehindPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "PlayerPage.xaml.cs");
        var code = await File.ReadAllTextAsync(playerPageCodeBehindPath);

        StringAssert.Contains(code, "viewModel.ResolveShortcut(key, modifiers)");
        StringAssert.Contains(code, "PlayerShortcutAction.ToggleFullscreen");
        StringAssert.Contains(code, "fullscreenController?.IsFullscreen == true");
        StringAssert.Contains(code, "ToggleFullscreen();");
        StringAssert.Contains(code, "ExitFullscreen();");
        StringAssert.Contains(code, "OnFullscreenButtonClick");
        StringAssert.Contains(code, "SyncFullscreenState(controller.IsFullscreen)");
        StringAssert.Contains(code, "viewModel.SetFullscreen(isFullscreen)");
        StringAssert.Contains(code, "Mouse.OverrideCursor = Cursors.None");
        StringAssert.Contains(code, "Mouse.OverrideCursor = null");
        StringAssert.Contains(code, "ShowMouseCursor();");
        StringAssert.Contains(code, "cursorActivityTimer.Tick += OnCursorActivityTimerTick");
        StringAssert.Contains(code, "StartCursorActivityMonitor();");
        StringAssert.Contains(code, "StopCursorActivityMonitor();");
        StringAssert.Contains(code, "GetPhysicalCursorPos(out var nativePoint)");
        StringAssert.Contains(code, "ShowControlsFromPointerActivity(force: true)");
        StringAssert.Contains(code, "ApplyFullscreenChromeVisibility();");
        StringAssert.Contains(code, "ControlsLayer.Opacity");
        StringAssert.Contains(code, "ControlsLayer.IsHitTestVisible = shouldShow;");
        Assert.IsFalse(code.Contains("new GridLength(0)", StringComparison.Ordinal));
        StringAssert.Contains(code, "OnVideoAreaMouseLeftButtonDown");
        StringAssert.Contains(code, "VideoHost.HostMouseMoved");
        StringAssert.Contains(code, "ShowControlsFromPointerActivity");
    }

    private static MediaDetailViewModelTestContext CreateContext(EmbyPlayer.Core.WatchLater.IWatchLaterStore? watchLater = null,
        EmbyPlayer.Core.PlaybackQueue.IPlaybackQueueService? queue = null)
    {
        return new MediaDetailViewModelTestContext(watchLater, queue);
    }

    private static MediaDetail CreateDetail(string itemId, bool isFavorite = false, bool isPlayed = false)
    {
        return CreateDetail(itemId, "Movie", isFavorite, isPlayed);
    }

    private static MediaDetail CreateDetail(
        string itemId,
        string type,
        bool isFavorite = false,
        bool isPlayed = false,
        IReadOnlyList<MediaPerson>? people = null,
        IReadOnlyList<string>? artworkUrls = null)
    {
        return new MediaDetail(
            itemId,
            "Detail Item",
            type,
            2024,
            TimeSpan.FromMinutes(90).Ticks,
            "Overview",
            new[] { "Drama" },
            8.1,
            40,
            TimeSpan.FromMinutes(12).Ticks,
            "http://media.local:8096/Items/item-1/Images/Primary",
            "http://media.local:8096/Items/item-1/Images/Backdrop/0",
            isFavorite,
            isPlayed,
            "http://media.local:8096/Items/item-1/Images/Logo",
            people,
            artworkUrls);
    }

    private static SimilarMediaItem CreateSimilarItem(
        string id,
        string title,
        string type = "Series",
        double? playedPercentage = null,
        bool isPlayed = false)
    {
        return new SimilarMediaItem(
            id,
            title,
            type,
            2024,
            $"http://media.local/Items/{id}/Images/Primary",
            playedPercentage,
            null,
            isPlayed);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("Timed out waiting for the expected asynchronous state.");
            }

            await Task.Delay(10);
        }
    }

    private static PlaybackInfo CreatePlaybackInfo(PlaybackStartRequest request)
    {
        return new PlaybackInfo(
            request.ItemId,
            request.Title,
            "play-session-1",
            new PlaybackMediaSource(
                "media-source-1",
                "mkv",
                SupportsDirectPlay: true,
                SupportsDirectStream: true,
                SupportsTranscoding: false,
                new Dictionary<string, string>()),
            "http://media.local:8096/Videos/item-1/stream.mkv",
            RequiresTranscoding: false,
            RequiresTokenInUrl: false,
            TimeSpan.FromMinutes(90).Ticks,
            request.StartPositionTicks,
            Array.Empty<PlaybackTrack>(),
            Array.Empty<PlaybackSubtitle>());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EmbyPlayer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static ResourceDictionary LoadApplicationResources()
    {
        var appXaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "EmbyPlayer.App", "App.xaml"));
        const string startMarker = "<ResourceDictionary>";
        const string endMarker = "</ResourceDictionary>";
        var start = appXaml.IndexOf(startMarker, StringComparison.Ordinal);
        var end = appXaml.LastIndexOf(endMarker, StringComparison.Ordinal);
        var dictionaryXaml = appXaml[start..(end + endMarker.Length)].Replace(
            startMarker,
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
            + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" "
            + "xmlns:controls=\"clr-namespace:EmbyPlayer.UI.Controls;assembly=EmbyPlayer.UI\" "
            + "xmlns:primitives=\"clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework\">",
            StringComparison.Ordinal);
        return (ResourceDictionary)XamlReader.Parse(dictionaryXaml);
    }

    private static ResourceDictionary LoadIconSystemResources()
    {
        var repositoryRoot = FindRepositoryRoot();
        var resources = new ResourceDictionary();
        using (var iconsStream = File.OpenRead(Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Resources", "Icons.xaml")))
        {
            resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(iconsStream));
        }

        var iconButtonsXaml = File.ReadAllText(Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Styles", "IconButtons.xaml"))
            .Replace(
                "clr-namespace:EmbyPlayer.UI.Controls\"",
                "clr-namespace:EmbyPlayer.UI.Controls;assembly=EmbyPlayer.UI\"",
                StringComparison.Ordinal);
        resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(iconButtonsXaml));

        return resources;
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            throw exception;
        }
    }

    private sealed class MediaDetailViewModelTestContext
    {
        public MediaDetailViewModelTestContext(EmbyPlayer.Core.WatchLater.IWatchLaterStore? watchLater = null,
            EmbyPlayer.Core.PlaybackQueue.IPlaybackQueueService? queue = null)
        {
            NavigationService = new NavigationService();
            MediaDetailService = new TestMediaDetailService();
            SimilarMediaService = new TestSimilarMediaService();
            ItemUserDataService = new TestItemUserDataService();
            SeriesService = new TestSeriesService();
            PlaybackService = new TestPlaybackService();
            CurrentSessionService = new CurrentSessionService();
            AuthSessionStore = new TestAuthSessionStore();
            LocalMediaSearchIndex = new TestLocalMediaSearchIndex();
            Session = new AuthSession(
                "http://media.local:8096",
                "test-access-token",
                "user-1",
                "Test User",
                "server-1");
            CurrentSessionService.SetSession(Session);
            MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
                Task.FromResult(MediaDetailLoadResult.Success(CreateDetail(itemId)));
            ViewModel = new MediaDetailViewModel(
                NavigationService,
                MediaDetailService,
                SimilarMediaService,
                ItemUserDataService,
                SeriesService,
                PlaybackService,
                CurrentSessionService,
                AuthSessionStore,
                message => LoginErrorMessage = message,
                LocalMediaSearchIndex,
                watchLater,
                queue);
        }

        public NavigationService NavigationService { get; }

        public TestMediaDetailService MediaDetailService { get; }

        public TestSimilarMediaService SimilarMediaService { get; }

        public TestItemUserDataService ItemUserDataService { get; }

        public TestSeriesService SeriesService { get; }

        public TestPlaybackService PlaybackService { get; }

        public CurrentSessionService CurrentSessionService { get; }

        public TestAuthSessionStore AuthSessionStore { get; }

        public TestLocalMediaSearchIndex LocalMediaSearchIndex { get; }

        public AuthSession Session { get; }

        public string? LoginErrorMessage { get; private set; }

        public MediaDetailViewModel ViewModel { get; }
    }
}
