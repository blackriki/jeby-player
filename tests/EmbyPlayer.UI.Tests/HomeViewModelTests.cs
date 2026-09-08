using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Home;
using EmbyPlayer.Core.Playback;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.Pages;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed partial class HomeViewModelTests
{
    [DataTestMethod]
    [DataRow("movies", HomeSectionKind.Movies)]
    [DataRow("series", HomeSectionKind.Series)]
    [DataRow("animation", HomeSectionKind.Animation)]
    [DataRow("boxsets", HomeSectionKind.BoxSets)]
    public void OpenSectionCommand_CategoryIdentifiersNavigateToFullSection(string id, HomeSectionKind expected)
    {
        var context = CreateContext();
        object? parameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => parameter = args.Parameter;
        context.ViewModel.OpenSectionCommand.Execute(id);
        Assert.AreEqual(AppPage.HomeSection, context.NavigationService.CurrentPage);
        Assert.AreEqual(expected, parameter);
        Assert.IsNotNull(context.ViewModel.SectionViewModel);
    }

    [TestMethod]
    public async Task NavigateAsync_FirstVisitStartsExactlyOneAwaitedLoad()
    {
        var context = CreateContext();
        var pendingLoad = new TaskCompletionSource<HomeLoadResult>();
        context.HomeService.LoadHomeAsyncHandler = (_, _) => pendingLoad.Task;

        var navigationTask = context.ViewModel.NavigateAsync();

        Assert.AreEqual(1, context.HomeService.LoadCallCount);
        Assert.IsFalse(navigationTask.IsCompleted);
        Assert.IsTrue(context.ViewModel.IsInitialLoading);

        pendingLoad.SetResult(CreateSuccessResult(context.Session.UserName));
        await navigationTask;

        Assert.IsTrue(context.ViewModel.HasLoadedSuccessfully);
    }

    [TestMethod]
    public async Task NavigateAsync_FreshRevisitWithinThirtySecondsMakesNoNewRequest()
    {
        var timeProvider = new FakeTimeProvider();
        var context = CreateContext(timeProvider);
        await context.ViewModel.NavigateAsync();
        timeProvider.Advance(TimeSpan.FromSeconds(29));

        await context.ViewModel.NavigateAsync();

        Assert.AreEqual(1, context.HomeService.LoadCallCount);
        Assert.IsFalse(context.ViewModel.IsRefreshing);
    }

    [TestMethod]
    public async Task NavigateAsync_EquivalentNormalizedServerAndUserReuseSameScope()
    {
        var timeProvider = new FakeTimeProvider();
        var context = CreateContext(timeProvider);
        await context.ViewModel.NavigateAsync();
        context.CurrentSessionService.SetSession(new AuthSession(
            "HTTP://MEDIA.LOCAL:8096/",
            "replacement-token",
            context.Session.UserId,
            context.Session.UserName,
            context.Session.ServerId));

        await context.ViewModel.NavigateAsync();

        Assert.AreEqual(1, context.HomeService.LoadCallCount);
        Assert.IsTrue(context.ViewModel.HasLoadedSuccessfully);
    }

    [TestMethod]
    public async Task NavigateAsync_StaleRevisitReturnsImmediatelyAndRefreshesInBackground()
    {
        var timeProvider = new FakeTimeProvider();
        var context = CreateContext(timeProvider);
        await context.ViewModel.NavigateAsync();
        var existingItem = context.ViewModel.ContinueWatching[0];
        var pendingRefresh = new TaskCompletionSource<HomeLoadResult>();
        context.HomeService.LoadHomeAsyncHandler = (_, _) => pendingRefresh.Task;
        timeProvider.Advance(TimeSpan.FromSeconds(30));

        var navigationTask = context.ViewModel.NavigateAsync();

        Assert.IsTrue(navigationTask.IsCompletedSuccessfully);
        Assert.AreEqual(2, context.HomeService.LoadCallCount);
        Assert.IsTrue(context.ViewModel.IsRefreshing);
        Assert.AreSame(existingItem, context.ViewModel.ContinueWatching[0]);

        var inflightRefresh = context.ViewModel.LoadAsync();
        pendingRefresh.SetResult(CreateSuccessResult(context.Session.UserName, "Refreshed Movie"));
        await inflightRefresh;

        Assert.AreEqual("Refreshed Movie", context.ViewModel.ContinueWatching[0].Title);
    }

    [TestMethod]
    public async Task NavigateAsync_RepeatedStaleRevisitsReuseSingleFlight()
    {
        var timeProvider = new FakeTimeProvider();
        var context = CreateContext(timeProvider);
        await context.ViewModel.NavigateAsync();
        var pendingRefresh = new TaskCompletionSource<HomeLoadResult>();
        context.HomeService.LoadHomeAsyncHandler = (_, _) => pendingRefresh.Task;
        timeProvider.Advance(TimeSpan.FromSeconds(30));

        var firstRevisit = context.ViewModel.NavigateAsync();
        var secondRevisit = context.ViewModel.NavigateAsync();
        var thirdRevisit = context.ViewModel.NavigateAsync();

        Assert.IsTrue(firstRevisit.IsCompletedSuccessfully);
        Assert.IsTrue(secondRevisit.IsCompletedSuccessfully);
        Assert.IsTrue(thirdRevisit.IsCompletedSuccessfully);
        Assert.AreEqual(2, context.HomeService.LoadCallCount);

        var inflightRefresh = context.ViewModel.LoadAsync();
        pendingRefresh.SetResult(CreateSuccessResult(context.Session.UserName));
        await inflightRefresh;
    }

    [TestMethod]
    public async Task LoadAsync_ForceRefreshBypassesFreshSnapshot()
    {
        var timeProvider = new FakeTimeProvider();
        var context = CreateContext(timeProvider);
        await context.ViewModel.NavigateAsync();

        await context.ViewModel.LoadAsync();

        Assert.AreEqual(2, context.HomeService.LoadCallCount);
    }

    [TestMethod]
    public async Task NavigateAsync_StaleRefreshFailureKeepsSnapshotAndShowsNonBlockingError()
    {
        var timeProvider = new FakeTimeProvider();
        var context = CreateContext(timeProvider);
        await context.ViewModel.NavigateAsync();
        var existingItem = context.ViewModel.ContinueWatching[0];
        var pendingRefresh = new TaskCompletionSource<HomeLoadResult>();
        context.HomeService.LoadHomeAsyncHandler = (_, _) => pendingRefresh.Task;
        timeProvider.Advance(TimeSpan.FromSeconds(30));

        _ = context.ViewModel.NavigateAsync();
        var inflightRefresh = context.ViewModel.LoadAsync();
        pendingRefresh.SetResult(HomeLoadResult.Failure(HomeLoadError.ServerUnreachable));
        await inflightRefresh;

        Assert.AreSame(existingItem, context.ViewModel.ContinueWatching[0]);
        Assert.IsTrue(context.ViewModel.IsRefreshErrorVisible);
        Assert.IsFalse(context.ViewModel.IsErrorVisible);
    }

    [TestMethod]
    public async Task NavigateAsync_ScopeChangeClearsOldSnapshotAndOldCompletionCannotOverwrite()
    {
        var timeProvider = new FakeTimeProvider();
        var context = CreateContext(timeProvider);
        var oldRefresh = new TaskCompletionSource<HomeLoadResult>();
        var newLoad = new TaskCompletionSource<HomeLoadResult>();
        var oldScopeCallCount = 0;
        context.HomeService.LoadHomeAsyncHandler = (session, _) =>
        {
            if (session.UserId == context.Session.UserId)
            {
                oldScopeCallCount++;
                return oldScopeCallCount == 1
                    ? Task.FromResult(CreateSuccessResult(session.UserName, "Old Scope"))
                    : oldRefresh.Task;
            }

            return newLoad.Task;
        };
        await context.ViewModel.NavigateAsync();
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        _ = context.ViewModel.NavigateAsync();
        var oldInflight = context.ViewModel.LoadAsync();
        var newSession = new AuthSession(
            "HTTP://MEDIA.LOCAL:8096/",
            "new-access-token",
            "user-2",
            "New User",
            "server-1");
        context.CurrentSessionService.SetSession(newSession);

        var newNavigation = context.ViewModel.NavigateAsync();

        Assert.IsFalse(context.ViewModel.HasLoadedSuccessfully);
        Assert.AreEqual(0, context.ViewModel.ContinueWatching.Count);
        Assert.IsTrue(context.ViewModel.IsInitialLoading);

        newLoad.SetResult(CreateSuccessResult(newSession.UserName, "New Scope"));
        await newNavigation;
        oldRefresh.SetResult(CreateSuccessResult(context.Session.UserName, "Late Old Scope"));
        await oldInflight;

        Assert.AreEqual("New Scope", context.ViewModel.ContinueWatching[0].Title);
        Assert.AreEqual("欢迎，New User", context.ViewModel.WelcomeText);
    }

    [TestMethod]
    public async Task RetryCommand_ForcesRefreshOfFreshSnapshot()
    {
        var context = CreateContext(new FakeTimeProvider());
        await context.ViewModel.NavigateAsync();

        await ((AsyncRelayCommand)context.ViewModel.RetryCommand).ExecuteAsync();

        Assert.AreEqual(2, context.HomeService.LoadCallCount);
    }

    [TestMethod]
    public async Task LoadAsync_ShowsLoadingWhileHomeDataIsPending()
    {
        var context = CreateContext();
        var pendingLoad = new TaskCompletionSource<HomeLoadResult>();
        context.HomeService.LoadHomeAsyncHandler = (_, _) => pendingLoad.Task;

        Assert.IsFalse(context.ViewModel.IsContentVisible);
        var loadTask = context.ViewModel.LoadAsync();

        Assert.IsTrue(context.ViewModel.IsLoading);
        Assert.IsTrue(context.ViewModel.IsInitialLoading);
        Assert.IsFalse(context.ViewModel.IsContinueWatchingEmpty && context.ViewModel.IsContentVisible);
        pendingLoad.SetResult(CreateSuccessResult(context.Session.UserName));
        await loadTask;
        Assert.IsFalse(context.ViewModel.IsLoading);
    }

    [TestMethod]
    public async Task LoadAsync_UsesCurrentSessionUserNameForWelcomeText()
    {
        var context = CreateContext();
        context.HomeService.LoadHomeAsyncHandler = (_, _) =>
            Task.FromResult(CreateSuccessResult("Different User"));

        await context.ViewModel.LoadAsync();

        Assert.AreEqual("欢迎，Test User", context.ViewModel.WelcomeText);
    }

    [TestMethod]
    public async Task LoadAsync_SuccessDisplaysHomeSections()
    {
        var context = CreateContext();

        await context.ViewModel.LoadAsync();

        Assert.AreEqual(1, context.ViewModel.ContinueWatching.Count);
        Assert.AreEqual("Continue Movie", context.ViewModel.ContinueWatching[0].Title);
        Assert.AreEqual(1, context.ViewModel.RecentlyAdded.Count);
        Assert.AreEqual("Recent Movie", context.ViewModel.RecentlyAdded[0].Title);
        Assert.AreEqual(1, context.ViewModel.Libraries.Count);
        Assert.AreEqual("Movies", context.ViewModel.Libraries[0].Name);
    }

    [TestMethod]
    public async Task LoadAsync_CategoryCollectionsMatchVisibleCardCounts()
    {
        var context = CreateContext();
        context.HomeService.LoadHomeAsyncHandler = (_, _) =>
            Task.FromResult(HomeLoadResult.Success(new HomeData(
                context.Session.UserName,
                CreateContinueWatchingItems(),
                CreateRecentItems(),
                CreateLibraries(),
                new[]
                {
                    HomeMediaSection.Success("movies", "电影", new[] { CreateMediaCard("movie-1", "Movie") }),
                    HomeMediaSection.Success("series", "电视节目", new[] { CreateMediaCard("series-1", "Series") }),
                    HomeMediaSection.Success("animation", "动画", Array.Empty<MediaCard>()),
                    HomeMediaSection.Success("boxsets", "合集", new[] { CreateMediaCard("boxset-1", "Box Set") })
                })));

        await context.ViewModel.LoadAsync();

        Assert.AreEqual(4, context.ViewModel.MediaSections.Count);
        Assert.AreEqual("movies", context.ViewModel.MediaSections[0].Id);
        Assert.AreEqual("series", context.ViewModel.MediaSections[1].Id);
        Assert.AreEqual("animation", context.ViewModel.MediaSections[2].Id);
        Assert.AreEqual("boxsets", context.ViewModel.MediaSections[3].Id);
        Assert.IsTrue(context.ViewModel.MediaSections[2].IsEmpty);
    }

    [TestMethod]
    public async Task Refresh_CategoryFailureKeepsExistingCards()
    {
        var context = CreateContext();
        context.HomeService.LoadHomeAsyncHandler = (_, _) =>
            Task.FromResult(HomeLoadResult.Success(new HomeData(
                context.Session.UserName,
                CreateContinueWatchingItems(),
                CreateRecentItems(),
                CreateLibraries(),
                new[] { HomeMediaSection.Success("movies", "电影", new[] { CreateMediaCard("movie-1", "Movie") }) })));
        await context.ViewModel.LoadAsync();
        var existingMovie = context.ViewModel.MediaSections[0].Items[0];
        context.HomeService.LoadHomeAsyncHandler = (_, _) =>
            Task.FromResult(HomeLoadResult.Success(new HomeData(
                context.Session.UserName,
                CreateContinueWatchingItems(),
                CreateRecentItems(),
                CreateLibraries(),
                new[] { HomeMediaSection.Failure("movies", "电影") })));

        await context.ViewModel.LoadAsync();

        Assert.AreSame(existingMovie, context.ViewModel.MediaSections[0].Items[0]);
        Assert.IsTrue(context.ViewModel.MediaSections[0].IsLoadFailed);
    }

    [TestMethod]
    public async Task LoadAsync_SortsLibrariesWithPlaylistsAfterCollections()
    {
        var context = CreateContext();
        context.HomeService.LoadHomeAsyncHandler = (_, _) =>
            Task.FromResult(HomeLoadResult.Success(new HomeData(
                context.Session.UserName,
                CreateContinueWatchingItems(),
                CreateRecentItems(),
                new[]
                {
                    new MediaLibrary("library-playlists", "Playlists", "playlists"),
                    new MediaLibrary("library-other", "Other", "mixed"),
                    new MediaLibrary("library-tv", "TV Shows", "tvshows"),
                    new MediaLibrary("library-collections", "Collections", "collections"),
                    new MediaLibrary("library-movies", "Movies", "movies")
                })));

        await context.ViewModel.LoadAsync();

        CollectionAssert.AreEqual(
            new[] { "library-movies", "library-tv", "library-collections", "library-playlists", "library-other" },
            context.ViewModel.Libraries.Select(library => library.Id).ToArray());
    }

    [TestMethod]
    public async Task LoadAsync_UsesContinueWatchingAsHeroWhenAvailable()
    {
        var context = CreateContext();

        await context.ViewModel.LoadAsync();

        Assert.IsTrue(context.ViewModel.HasHeroCard);
        Assert.AreEqual("continue-1", context.ViewModel.HeroCard!.Id);
    }

    [TestMethod]
    public async Task LoadAsync_UsesRecentlyAddedAsHeroWhenContinueWatchingIsEmpty()
    {
        var context = CreateContext();
        context.HomeService.LoadHomeAsyncHandler = (_, _) =>
            Task.FromResult(HomeLoadResult.Success(new HomeData(
                context.Session.UserName,
                Array.Empty<MediaCard>(),
                CreateRecentItems(),
                CreateLibraries())));

        await context.ViewModel.LoadAsync();

        Assert.IsTrue(context.ViewModel.HasHeroCard);
        Assert.AreEqual("recent-1", context.ViewModel.HeroCard!.Id);
    }

    [TestMethod]
    public async Task LoadAsync_BuildsDeduplicatedHeroCarouselWithFiveItemsAtMost()
    {
        var context = CreateContext();
        context.HomeService.LoadHomeAsyncHandler = (_, _) =>
            Task.FromResult(HomeLoadResult.Success(new HomeData(
                context.Session.UserName,
                new[]
                {
                    CreateMediaCard("shared", "Shared"),
                    CreateMediaCard("continue-2", "Continue 2"),
                    CreateMediaCard("continue-3", "Continue 3")
                },
                new[]
                {
                    CreateMediaCard("recent-backdrop", "Recent Backdrop", hasBackdrop: true),
                    CreateMediaCard("shared", "Shared Duplicate"),
                    CreateMediaCard("recent-fallback", "Recent Fallback"),
                    CreateMediaCard("recent-extra", "Recent Extra")
                },
                CreateLibraries())));

        await context.ViewModel.LoadAsync();

        Assert.AreEqual(5, context.ViewModel.HeroItems.Count);
        CollectionAssert.AreEqual(
            new[] { "recent-backdrop", "shared", "continue-2", "continue-3", "recent-fallback" },
            context.ViewModel.HeroItems.Select(item => item.Card.Id).ToArray());
        Assert.AreEqual(5, context.ViewModel.HeroItems.Select(item => item.Card.Id).Distinct().Count());
        Assert.IsTrue(context.ViewModel.HeroItems[0].IsSelected);
    }

    [TestMethod]
    public async Task HeroCommands_WrapAtBothEnds()
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync();

        context.ViewModel.PreviousHeroCommand.Execute(null);

        Assert.AreEqual(1, context.ViewModel.CurrentHeroIndex);
        Assert.AreEqual("recent-1", context.ViewModel.HeroCard!.Id);

        context.ViewModel.NextHeroCommand.Execute(null);

        Assert.AreEqual(0, context.ViewModel.CurrentHeroIndex);
        Assert.AreEqual("continue-1", context.ViewModel.HeroCard!.Id);
    }

    [TestMethod]
    public async Task SelectHeroCommand_SelectsOnlyRequestedHeroItem()
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync();
        var selectedItem = context.ViewModel.HeroItems[1];

        context.ViewModel.SelectHeroCommand.Execute(selectedItem);

        Assert.AreEqual(1, context.ViewModel.CurrentHeroIndex);
        Assert.AreSame(selectedItem.Card, context.ViewModel.HeroCard);
        Assert.AreEqual(1, context.ViewModel.HeroItems.Count(item => item.IsSelected));
        Assert.IsTrue(selectedItem.IsSelected);
    }

    [TestMethod]
    public async Task LoadAsync_SingleHeroItemDisablesCarouselNavigation()
    {
        var context = CreateContext();
        context.HomeService.LoadHomeAsyncHandler = (_, _) =>
            Task.FromResult(HomeLoadResult.Success(new HomeData(
                context.Session.UserName,
                CreateContinueWatchingItems(),
                Array.Empty<MediaCard>(),
                CreateLibraries())));

        await context.ViewModel.LoadAsync();

        Assert.IsFalse(context.ViewModel.HasMultipleHeroCards);
        Assert.IsFalse(context.ViewModel.NextHeroCommand.CanExecute(null));
        Assert.IsFalse(context.ViewModel.PreviousHeroCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task LoadAsync_EmptyContinueWatchingShowsEmptyState()
    {
        var context = CreateContext();
        context.HomeService.LoadHomeAsyncHandler = (_, _) =>
            Task.FromResult(HomeLoadResult.Success(new HomeData(
                context.Session.UserName,
                Array.Empty<MediaCard>(),
                CreateRecentItems(),
                CreateLibraries())));

        await context.ViewModel.LoadAsync();

        Assert.IsTrue(context.ViewModel.IsContinueWatchingEmpty);
    }

    [TestMethod]
    public async Task LoadAsync_EmptyRecentlyAddedShowsEmptyState()
    {
        var context = CreateContext();
        context.HomeService.LoadHomeAsyncHandler = (_, _) =>
            Task.FromResult(HomeLoadResult.Success(new HomeData(
                context.Session.UserName,
                CreateContinueWatchingItems(),
                Array.Empty<MediaCard>(),
                CreateLibraries())));

        await context.ViewModel.LoadAsync();

        Assert.IsTrue(context.ViewModel.IsRecentlyAddedEmpty);
    }

    [TestMethod]
    public async Task LoadAsync_EmptyLibrariesShowsEmptyState()
    {
        var context = CreateContext();
        context.HomeService.LoadHomeAsyncHandler = (_, _) =>
            Task.FromResult(HomeLoadResult.Success(new HomeData(
                context.Session.UserName,
                CreateContinueWatchingItems(),
                CreateRecentItems(),
                Array.Empty<MediaLibrary>())));

        await context.ViewModel.LoadAsync();

        Assert.IsTrue(context.ViewModel.IsLibrariesEmpty);
        Assert.IsTrue(context.ViewModel.IsContentVisible);
    }

    [TestMethod]
    public async Task LoadAsync_ServerFailureShowsChineseError()
    {
        var context = CreateContext();
        context.HomeService.LoadHomeAsyncHandler = (_, _) =>
            Task.FromResult(HomeLoadResult.Failure(HomeLoadError.ServerUnreachable));

        await context.ViewModel.LoadAsync();

        Assert.IsTrue(context.ViewModel.HasError);
        StringAssert.Contains(context.ViewModel.ErrorMessage!, "无法加载首页");
        Assert.IsTrue(context.ViewModel.IsErrorVisible);
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
    }

    [TestMethod]
    public async Task RetryCommand_ReloadsHomeData()
    {
        var context = CreateContext();
        context.HomeService.LoadHomeAsyncHandler = (_, _) =>
            Task.FromResult(HomeLoadResult.Failure(HomeLoadError.ServerUnreachable));
        await context.ViewModel.LoadAsync();
        context.HomeService.LoadHomeAsyncHandler = (_, _) =>
            Task.FromResult(CreateSuccessResult(context.Session.UserName));

        await ((AsyncRelayCommand)context.ViewModel.RetryCommand).ExecuteAsync();

        Assert.AreEqual(2, context.HomeService.LoadCallCount);
        Assert.IsFalse(context.ViewModel.HasError);
        Assert.AreEqual(1, context.ViewModel.Libraries.Count);
    }

    [TestMethod]
    public async Task Refresh_KeepsExistingContentAndUsesNonBlockingErrorState()
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync();
        var existingContinueItem = context.ViewModel.ContinueWatching[0];
        var pendingRefresh = new TaskCompletionSource<HomeLoadResult>();
        context.HomeService.LoadHomeAsyncHandler = (_, _) => pendingRefresh.Task;

        var refreshTask = context.ViewModel.LoadAsync();

        Assert.IsTrue(context.ViewModel.IsRefreshing);
        Assert.IsTrue(context.ViewModel.IsContentVisible);
        Assert.AreSame(existingContinueItem, context.ViewModel.ContinueWatching[0]);
        Assert.IsFalse(context.ViewModel.RetryCommand.CanExecute(null));

        pendingRefresh.SetResult(HomeLoadResult.Failure(HomeLoadError.ServerUnreachable));
        await refreshTask;

        Assert.AreSame(existingContinueItem, context.ViewModel.ContinueWatching[0]);
        Assert.IsTrue(context.ViewModel.IsRefreshErrorVisible);
        Assert.IsFalse(context.ViewModel.IsErrorVisible);
    }

    [TestMethod]
    public async Task LoadAsync_UnauthorizedClearsSessionAndNavigatesToLogin()
    {
        var context = CreateContext();
        const string lastServerBase = "http://media.local:8096";
        const string deviceId = "stable-device-id";
        context.AppSettingsService.LastServerBase = lastServerBase;
        context.AppSettingsService.DeviceId = deviceId;
        context.HomeService.LoadHomeAsyncHandler = (_, _) =>
            Task.FromResult(HomeLoadResult.Failure(HomeLoadError.Unauthorized));

        await context.ViewModel.LoadAsync();

        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.IsNull(context.CurrentSessionService.CurrentSession);
        Assert.AreEqual(AppPage.Login, context.NavigationService.CurrentPage);
        StringAssert.Contains(context.LoginErrorMessage!, "登录状态已失效");
        Assert.AreEqual(lastServerBase, context.AppSettingsService.LastServerBase);
        Assert.AreEqual(deviceId, context.AppSettingsService.DeviceId);
    }

    [TestMethod]
    public async Task LoadAsync_ForbiddenKeepsSessionAndShowsPermissionError()
    {
        var context = CreateContext();
        context.HomeService.LoadHomeAsyncHandler = (_, _) =>
            Task.FromResult(HomeLoadResult.Failure(HomeLoadError.Forbidden));

        await context.ViewModel.LoadAsync();

        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
        Assert.AreNotEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual("没有权限加载首页内容", context.ViewModel.ErrorMessage);
    }

    [TestMethod]
    public void OpenMediaCommand_NavigatesToDetailPlaceholder()
    {
        var context = CreateContext();
        var card = new HomeMediaCardViewModel(CreateContinueWatchingItems()[0]);
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;

        context.ViewModel.OpenMediaCommand.Execute(card);

        Assert.AreEqual(AppPage.Detail, context.NavigationService.CurrentPage);
        Assert.AreEqual("continue-1", navigationParameter);
    }

    [TestMethod]
    public void OpenContinueMediaCommand_EpisodeNavigatesToParentSeriesWithFocusContext()
    {
        var context = CreateContext();
        var card = new HomeMediaCardViewModel(new MediaCard(
            "episode-4",
            "第 4 集",
            "Episode",
            2026,
            null,
            42,
            resumePositionTicks: 123000000,
            seriesId: "series-1",
            seasonId: "season-2"));
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;

        context.ViewModel.OpenContinueMediaCommand.Execute(card);

        Assert.AreEqual(AppPage.Detail, context.NavigationService.CurrentPage);
        Assert.IsInstanceOfType<DetailNavigationParameter>(navigationParameter);
        var detailParameter = (DetailNavigationParameter)navigationParameter!;
        Assert.AreEqual("series-1", detailParameter.ItemId);
        Assert.AreEqual(AppPage.Home, detailParameter.ReturnPage);
        Assert.AreEqual("season-2", detailParameter.SelectedSeasonId);
        Assert.AreEqual("episode-4", detailParameter.FocusedEpisodeId);
        Assert.AreEqual("episode-4", detailParameter.FallbackItemId);
    }

    [TestMethod]
    public void OpenContinueMediaCommand_EpisodeWithoutSeriesFallsBackToEpisodeDetail()
    {
        var context = CreateContext();
        var card = new HomeMediaCardViewModel(new MediaCard(
            "episode-4",
            "第 4 集",
            "Episode",
            2026,
            null,
            42));
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;

        context.ViewModel.OpenContinueMediaCommand.Execute(card);

        Assert.AreEqual(AppPage.Detail, context.NavigationService.CurrentPage);
        Assert.IsInstanceOfType<DetailNavigationParameter>(navigationParameter);
        var detailParameter = (DetailNavigationParameter)navigationParameter!;
        Assert.AreEqual("episode-4", detailParameter.ItemId);
        Assert.AreEqual(AppPage.Home, detailParameter.ReturnPage);
        Assert.IsNull(detailParameter.SelectedSeasonId);
        Assert.IsNull(detailParameter.FocusedEpisodeId);
        Assert.IsNull(detailParameter.FallbackItemId);
    }

    [TestMethod]
    public void OpenContinueMediaCommand_MovieKeepsMovieDetailNavigation()
    {
        var context = CreateContext();
        var card = new HomeMediaCardViewModel(CreateContinueWatchingItems()[0]);
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;

        context.ViewModel.OpenContinueMediaCommand.Execute(card);

        Assert.AreEqual(AppPage.Detail, context.NavigationService.CurrentPage);
        Assert.IsInstanceOfType<DetailNavigationParameter>(navigationParameter);
        var detailParameter = (DetailNavigationParameter)navigationParameter!;
        Assert.AreEqual("continue-1", detailParameter.ItemId);
        Assert.AreEqual(AppPage.Home, detailParameter.ReturnPage);
        Assert.IsNull(detailParameter.SelectedSeasonId);
        Assert.IsNull(detailParameter.FocusedEpisodeId);
        Assert.IsNull(detailParameter.FallbackItemId);
    }

    [TestMethod]
    public async Task ContinueMediaCommand_PreparesPlaybackFromResumePosition()
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync();
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;

        context.ViewModel.ContinueMediaCommand.Execute(context.ViewModel.ContinueWatching[0]);
        await WaitUntilAsync(() => context.NavigationService.CurrentPage == AppPage.Player);

        Assert.AreEqual(123000000, context.PlaybackService.LastRequest!.StartPositionTicks);
        Assert.IsInstanceOfType<PlayerNavigationParameter>(navigationParameter);
        var playerParameter = (PlayerNavigationParameter)navigationParameter!;
        Assert.AreEqual("continue-1", playerParameter.BackToDetailParameter.ItemId);
        Assert.AreEqual(AppPage.Home, playerParameter.BackToDetailParameter.ReturnPage);
        Assert.AreEqual(
            "http://media.local:8096/Items/continue-1/Images/Logo",
            playerParameter.LogoUrl);
    }

    [TestMethod]
    public async Task ContinueMediaCommand_EpisodeStillPreparesDirectPlayback()
    {
        var context = CreateContext();
        var card = new HomeMediaCardViewModel(new MediaCard(
            "episode-4",
            "第 4 集",
            "Episode",
            2026,
            null,
            42,
            resumePositionTicks: 123000000,
            seriesId: "series-1",
            seasonId: "season-2"));
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;

        context.ViewModel.ContinueMediaCommand.Execute(card);
        await WaitUntilAsync(() => context.NavigationService.CurrentPage == AppPage.Player);

        Assert.AreEqual("episode-4", context.PlaybackService.LastRequest!.ItemId);
        Assert.AreEqual(123000000, context.PlaybackService.LastRequest.StartPositionTicks);
        Assert.AreEqual(1, context.PlaybackService.PrepareCallCount);
        Assert.IsInstanceOfType<PlayerNavigationParameter>(navigationParameter);
        var backParameter = ((PlayerNavigationParameter)navigationParameter!).BackToDetailParameter;
        Assert.AreEqual("series-1", backParameter.ItemId);
        Assert.AreEqual(AppPage.Home, backParameter.ReturnPage);
        Assert.AreEqual("season-2", backParameter.SelectedSeasonId);
        Assert.AreEqual("episode-4", backParameter.FocusedEpisodeId);
        Assert.AreEqual("episode-4", backParameter.FallbackItemId);
    }

    [TestMethod]
    public async Task PlayMediaCommand_PreparesPlaybackFromBeginning()
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync();

        context.ViewModel.PlayMediaCommand.Execute(context.ViewModel.RecentlyAdded[0]);
        await WaitUntilAsync(() => context.NavigationService.CurrentPage == AppPage.Player);

        Assert.AreEqual(0, context.PlaybackService.LastRequest!.StartPositionTicks);
    }

    [TestMethod]
    public async Task PlayMediaCommand_ShowsPerCardPreparingStateAndIgnoresDuplicateRequest()
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync();
        var card = context.ViewModel.RecentlyAdded[0];
        var pendingPlayback = new TaskCompletionSource<PlaybackLoadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.PlaybackService.PreparePlaybackAsyncHandler = (_, _, _) => pendingPlayback.Task;

        context.ViewModel.PlayMediaCommand.Execute(card);
        context.ViewModel.PlayMediaCommand.Execute(card);
        await WaitUntilAsync(() => context.PlaybackService.PrepareCallCount == 1);

        Assert.IsTrue(context.ViewModel.IsPreparingPlayback);
        Assert.IsTrue(card.IsPreparingPlayback);
        Assert.IsFalse(context.ViewModel.PlayMediaCommand.CanExecute(card));

        pendingPlayback.SetResult(PlaybackLoadResult.Failure(PlaybackLoadError.ServerError));
        await WaitUntilAsync(() => !context.ViewModel.IsPreparingPlayback);

        Assert.IsFalse(card.IsPreparingPlayback);
        Assert.AreEqual("播放准备失败，请稍后重试", context.ViewModel.PlaybackErrorMessage);
    }

    [TestMethod]
    public void OpenLibraryCommand_NavigatesToLibraryPlaceholder()
    {
        var context = CreateContext();
        var library = new HomeLibraryViewModel(CreateLibraries()[0]);
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;

        context.ViewModel.OpenLibraryCommand.Execute(library);

        Assert.AreEqual(AppPage.Library, context.NavigationService.CurrentPage);
        Assert.AreEqual("library-1", navigationParameter);
    }

    [TestMethod]
    public void SubmitSearchCommand_EmptyKeywordShowsPrompt()
    {
        var context = CreateContext();

        context.ViewModel.SearchKeyword = "   ";
        context.ViewModel.SubmitSearchCommand.Execute(null);

        Assert.IsTrue(context.ViewModel.IsSearchPlaceholderMessageVisible);
        Assert.AreEqual("\u8bf7\u8f93\u5165\u641c\u7d22\u5173\u952e\u8bcd", context.ViewModel.SearchPlaceholderMessage);
    }

    [TestMethod]
    public void SubmitSearchCommand_NavigatesToSearchWithKeyword()
    {
        var context = CreateContext();
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;

        context.ViewModel.SearchKeyword = "one piece";
        context.ViewModel.SubmitSearchCommand.Execute(null);

        Assert.AreEqual(AppPage.Search, context.NavigationService.CurrentPage);
        Assert.IsInstanceOfType<SearchNavigationParameter>(navigationParameter);
        var searchParameter = (SearchNavigationParameter)navigationParameter!;
        Assert.IsFalse(searchParameter.FavoritesOnly);
        Assert.AreEqual("one piece", searchParameter.Keyword);
    }

    [TestMethod]
    public void OpenFavoritesCommand_NavigatesToFavoritesSearchMode()
    {
        var context = CreateContext();
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;

        context.ViewModel.OpenFavoritesCommand.Execute(null);

        Assert.AreEqual(AppPage.Search, context.NavigationService.CurrentPage);
        Assert.IsInstanceOfType<SearchNavigationParameter>(navigationParameter);
        Assert.IsTrue(((SearchNavigationParameter)navigationParameter!).FavoritesOnly);
    }

    [TestMethod]
    public void HomeMediaCardViewModel_UsesSubtitleTextWhenProvided()
    {
        var card = new HomeMediaCardViewModel(new MediaCard(
            "episode-1",
            "Test Series",
            "Episode",
            2024,
            null,
            40,
            "S1:E6 \u00b7 Episode Name"));

        Assert.AreEqual("Test Series", card.Title);
        Assert.AreEqual("S1:E6 \u00b7 Episode Name", card.SubtitleText);
        Assert.IsTrue(card.HasSubtitle);
    }

    [TestMethod]
    public void HomeMediaCardViewModel_MapsPlayedBadgeState()
    {
        var card = new HomeMediaCardViewModel(new MediaCard(
            "movie-1",
            "Movie",
            "Movie",
            2024,
            null,
            null,
            isPlayed: true));

        Assert.IsTrue(card.IsPlayed);
    }

    [TestMethod]
    public void HomeMediaCardViewModel_ExposesHeroImageUrl()
    {
        var card = new HomeMediaCardViewModel(new MediaCard(
            "movie-1",
            "Movie",
            "Movie",
            2024,
            "http://media.local:8096/Items/movie-1/Images/Primary",
            null,
            heroImageUrl: "http://media.local:8096/Items/movie-1/Images/Backdrop/0"));

        Assert.AreEqual("http://media.local:8096/Items/movie-1/Images/Backdrop/0", card.HeroImageUrl);
        Assert.AreEqual("http://media.local:8096/Items/movie-1/Images/Primary", card.PosterUrl);
        Assert.IsTrue(card.HasDedicatedHeroImage);
    }

    [DataTestMethod]
    [DataRow("playlists", "播放列表")]
    [DataRow("movies", "电影")]
    [DataRow("tvshows", "电视剧")]
    [DataRow("collections", "合集")]
    [DataRow("boxsets", "合集")]
    public void HomeLibraryViewModel_UsesChineseTypeLabels(string sourceType, string expectedLabel)
    {
        var library = new HomeLibraryViewModel(new MediaLibrary("library-1", "Library", sourceType));

        Assert.AreEqual(expectedLabel, library.Type);
    }

    [TestMethod]
    public void HomePage_CalculatesArrowScrollDistanceFromVisibleWidth()
    {
        Assert.AreEqual(950, HomePage.CalculateHorizontalScrollDistance(1000, 1200), 0.001);
        Assert.AreEqual(760, HomePage.CalculateHorizontalScrollDistance(0, 800), 0.001);
    }

    [TestMethod]
    public void HomePage_CalculatesVerticalWheelTargetForPageScroll()
    {
        Assert.AreEqual(380, HomePage.CalculateVerticalWheelTarget(500, 120, 1000), 0.001);
        Assert.AreEqual(620, HomePage.CalculateVerticalWheelTarget(500, -120, 1000), 0.001);
        Assert.AreEqual(0, HomePage.CalculateVerticalWheelTarget(20, 120, 1000), 0.001);
        Assert.AreEqual(1000, HomePage.CalculateVerticalWheelTarget(980, -120, 1000), 0.001);
    }

    [TestMethod]
    public void HomePage_AccumulatesRapidVerticalWheelInputAgainstTheActiveTarget()
    {
        Assert.AreEqual(
            740,
            HomePage.CalculateAccumulatedVerticalWheelTarget(540, 620, true, -120, 1000),
            0.001);
        Assert.AreEqual(
            660,
            HomePage.CalculateAccumulatedVerticalWheelTarget(540, 620, false, -120, 1000),
            0.001);
        Assert.AreEqual(
            1000,
            HomePage.CalculateAccumulatedVerticalWheelTarget(980, 980, true, -240, 1000),
            0.001);
    }

    [TestMethod]
    public void HomePage_RoutesOnlyOrdinaryWheelToVerticalAnimation()
    {
        Assert.IsTrue(HomePage.ShouldRouteWheelVertically(false, 120));
        Assert.IsFalse(HomePage.ShouldRouteWheelVertically(true, 120));
        Assert.IsFalse(HomePage.ShouldRouteWheelVertically(false, 0));
    }

    [TestMethod]
    public void HomePage_CalculatesResponsiveHeaderLayout()
    {
        Assert.IsTrue(HomePage.UsesCompactHeaderLayout(700));
        Assert.IsFalse(HomePage.UsesCompactHeaderLayout(900));
        Assert.IsFalse(HomePage.UsesCompactHeaderLayout(1120));
        Assert.IsFalse(HomePage.UsesCompactHeaderLayout(0));
        Assert.AreEqual(260, HomePage.CalculateCompactSearchWidth(500), 0.001);
        Assert.AreEqual(280, HomePage.CalculateCompactSearchWidth(600), 0.001);
        Assert.AreEqual(360, HomePage.CalculateCompactSearchWidth(900), 0.001);
    }

    [TestMethod]
    public void HomePage_FiveLibrariesFillNormalWidthWithoutOverflow()
    {
        const double availableWidth = 1000;
        var capacity = HomePage.CalculateLibraryCapacity(availableWidth);

        Assert.AreEqual(5, capacity);
        Assert.AreEqual(5, HomePage.CalculateLibraryVisibleCount(5, capacity));
        Assert.IsFalse(HomePage.HasLibraryOverflow(5, capacity));
        Assert.AreEqual(190.4, HomePage.CalculateLibraryCardWidth(availableWidth, 5, capacity), 0.001);
    }

    [TestMethod]
    public void HomePage_SixLibrariesUseFiveCardCapacityAndFourCardStep()
    {
        var capacity = HomePage.CalculateLibraryCapacity(1200);

        Assert.AreEqual(5, capacity);
        Assert.IsTrue(HomePage.HasLibraryOverflow(6, capacity));
        Assert.AreEqual(4, HomePage.CalculateLibraryScrollStep(capacity));
    }

    [DataTestMethod]
    [DataRow(800d, 4, 3)]
    [DataRow(600d, 3, 2)]
    [DataRow(400d, 2, 1)]
    public void HomePage_LibraryCapacityControlsScrollStep(
        double availableWidth,
        int expectedCapacity,
        int expectedStep)
    {
        var capacity = HomePage.CalculateLibraryCapacity(availableWidth);

        Assert.AreEqual(expectedCapacity, capacity);
        Assert.AreEqual(expectedStep, HomePage.CalculateLibraryScrollStep(capacity));
    }

    [TestMethod]
    public void HomePage_LibraryArrowStateTracksFirstAndLastVisibleIndexes()
    {
        Assert.IsFalse(HomePage.CanScrollLibraryLeft(0));
        Assert.IsTrue(HomePage.CanScrollLibraryRight(10, 5, 0));
        Assert.IsTrue(HomePage.CanScrollLibraryLeft(4));
        Assert.IsTrue(HomePage.CanScrollLibraryRight(10, 5, 4));
        Assert.IsTrue(HomePage.CanScrollLibraryLeft(5));
        Assert.IsFalse(HomePage.CanScrollLibraryRight(10, 5, 5));
    }

    [TestMethod]
    public void HomePage_LibraryResizeRestoresByCardIndexAndClampsToNewCapacity()
    {
        var oldFirstVisibleIndex = HomePage.CalculateLibraryFirstVisibleIndex(424, 200);
        var resizedFirstVisibleIndex = HomePage.ClampLibraryFirstVisibleIndex(
            oldFirstVisibleIndex,
            totalItemCount: 6,
            capacity: 5);

        Assert.AreEqual(2, oldFirstVisibleIndex);
        Assert.AreEqual(1, resizedFirstVisibleIndex);
    }

    [TestMethod]
    public void HomePage_FewerThanFiveLibrariesStayLeftAlignedWithBoundedWidth()
    {
        Assert.AreEqual(300, HomePage.CalculateLibraryCardWidth(1000, 2, 5), 0.001);
    }

    [TestMethod]
    public void HeroRotationInteractionState_UsesExpectedPointerDelays()
    {
        var state = new HeroRotationInteractionState();

        Assert.AreEqual(HeroRotationDirectiveKind.Stop, state.OnPointerEntered().Kind);

        var ordinaryLeave = state.OnPointerExited();
        Assert.AreEqual(HeroRotationDirectiveKind.Schedule, ordinaryLeave.Kind);
        Assert.AreEqual(TimeSpan.FromSeconds(4), ordinaryLeave.Delay);

        state.OnPointerManualInteraction();
        var manualLeave = state.OnPointerExited();
        Assert.AreEqual(HeroRotationDirectiveKind.Schedule, manualLeave.Kind);
        Assert.AreEqual(TimeSpan.FromSeconds(6), manualLeave.Delay);
        Assert.IsFalse(state.HasKeyboardInteraction);
        Assert.IsTrue(HomePage.CanScheduleHeroRotation(
            true,
            true,
            true,
            true,
            false,
            state.HasKeyboardInteraction,
            true));
    }

    [TestMethod]
    public void HeroRotationInteractionState_KeyboardInteractionPausesUntilFocusLeaves()
    {
        var state = new HeroRotationInteractionState();

        Assert.AreEqual(HeroRotationDirectiveKind.Stop, state.OnKeyboardInteraction().Kind);
        Assert.IsTrue(state.HasKeyboardInteraction);

        var focusLeft = state.OnKeyboardFocusLeft();
        Assert.AreEqual(HeroRotationDirectiveKind.Schedule, focusLeft.Kind);
        Assert.AreEqual(TimeSpan.FromSeconds(6), focusLeft.Delay);
        Assert.IsFalse(state.HasKeyboardInteraction);
    }

    [TestMethod]
    public void HeroRotationInteractionState_WindowLifecycleUsesNormalDelayAndResets()
    {
        var state = new HeroRotationInteractionState();
        state.OnKeyboardInteraction();

        Assert.AreEqual(
            HeroRotationDirectiveKind.Stop,
            state.OnWindowDeactivated().Kind);

        var activated = state.OnWindowActivated();
        Assert.AreEqual(HeroRotationDirectiveKind.Schedule, activated.Kind);
        Assert.AreEqual(TimeSpan.FromSeconds(7), activated.Delay);

        Assert.AreEqual(HeroRotationDirectiveKind.Stop, state.OnUnloaded().Kind);
        Assert.IsFalse(state.HasKeyboardInteraction);
        Assert.IsFalse(state.HasManualPointerInteraction);
    }

    [TestMethod]
    public void HomePage_HeroRotationRequiresMultipleLoadedItemsAndNoActivePause()
    {
        Assert.IsTrue(HomePage.CanScheduleHeroRotation(
            isLoaded: true,
            isVisible: true,
            isContentVisible: true,
            hasMultipleItems: true,
            isPointerOver: false,
            hasKeyboardInteraction: false,
            isWindowActive: true));
        Assert.IsFalse(HomePage.CanScheduleHeroRotation(
            true, true, true, false, false, false, true));
        Assert.IsFalse(HomePage.CanScheduleHeroRotation(
            true, true, false, true, false, false, true));
        Assert.IsFalse(HomePage.CanScheduleHeroRotation(
            true, true, true, true, true, false, true));
        Assert.IsFalse(HomePage.CanScheduleHeroRotation(
            true, true, true, true, false, true, true));
        Assert.IsFalse(HomePage.CanScheduleHeroRotation(
            true, true, true, true, false, false, false));
    }

    [TestMethod]
    public async Task HomePage_ProgressValueBinding_IsOneWay()
    {
        var repositoryRoot = FindRepositoryRoot();
        var homePagePath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "HomePage.xaml");
        var xaml = await File.ReadAllTextAsync(homePagePath);

        StringAssert.Contains(xaml, "Value=\"{Binding ProgressValue, Mode=OneWay}\"");
    }

    [TestMethod]
    public async Task HomePage_UsesHeroAndPolishedHomeCardStyles()
    {
        var repositoryRoot = FindRepositoryRoot();
        var homePagePath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "HomePage.xaml");
        var xaml = await File.ReadAllTextAsync(homePagePath);
        var appXaml = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "src", "EmbyPlayer.App", "App.xaml"));

        StringAssert.Contains(xaml, "HeroButtonStyle");
        StringAssert.Contains(xaml, "CommandParameter=\"{Binding HeroCard, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "ImageUrl=\"{Binding HeroCard.HeroImageUrl, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "ItemsSource=\"{Binding HeroItems}\"");
        StringAssert.Contains(xaml, "Command=\"{Binding PreviousHeroCommand}\"");
        StringAssert.Contains(xaml, "Command=\"{Binding NextHeroCommand}\"");
        StringAssert.Contains(xaml, "SelectHeroCommand");
        StringAssert.Contains(xaml, "OnHeroPointerEntered");
        StringAssert.Contains(xaml, "ContinueCardButtonStyle");
        StringAssert.Contains(xaml, "LinearGradientBrush");
        StringAssert.Contains(xaml, "HomeCardSurfaceBrush");
        StringAssert.Contains(xaml, "CornerRadius=\"10\"");
        StringAssert.Contains(xaml, "CornerRadius=\"16\"");
        StringAssert.Contains(xaml, "BorderBrush\" Value=\"Transparent\"");
        StringAssert.Contains(xaml, "CardOverlayPlayButton");
        StringAssert.Contains(xaml, "ContinueMediaCommand");
        StringAssert.Contains(xaml, "PlayMediaCommand");
        StringAssert.Contains(xaml, "Background=\"#52000000\"");
        Assert.AreEqual(0, CountOccurrences(xaml, "CornerRadius=\"8\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Margin=\"0,0,24,0\""));
        StringAssert.Contains(xaml, "Margin=\"-6,0,-6,0\"");
        StringAssert.Contains(xaml, "Duration=\"0:0:0.14\"");
        Assert.IsFalse(xaml.Contains("<UniformGrid Columns=\"5\" />", StringComparison.Ordinal));
        StringAssert.Contains(xaml, "x:Name=\"HomeHeaderActionsPanel\"");
        StringAssert.Contains(xaml, "KeyboardNavigation.TabNavigation=\"Local\"");
        StringAssert.Contains(xaml, "controls:RoundedClipBorder");
        StringAssert.Contains(xaml, "Grid.ColumnSpan=\"2\"");
        StringAssert.Contains(xaml, "FocusVisualStyle\" Value=\"{x:Null}\"");
        StringAssert.Contains(appXaml, "TargetName=\"SearchPlaceholder\" Property=\"Visibility\" Value=\"Collapsed\"");
        StringAssert.Contains(xaml, "正在准备播放...");
        StringAssert.Contains(xaml, "ToolTipService.ShowOnDisabled=\"True\"");
        StringAssert.Contains(xaml, "AutomationProperties.Name=\"向右滚动继续观看\"");
        Assert.IsTrue(CountOccurrences(xaml, "Duration=\"0:0:0.14\"") >= 8);
        Assert.IsFalse(xaml.Contains("HomeSearchGlyphButtonStyle", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("AppIconButtonStyle", StringComparison.Ordinal));
        Assert.AreEqual(1, CountOccurrences(xaml, "Text=\"继续观看\""));
    }

    [TestMethod]
    public void HomePage_CalculatesHorizontalWheelTargetForHorizontalRows()
    {
        Assert.AreEqual(380, HomePage.CalculateHorizontalWheelTarget(500, 120, 1000), 0.001);
        Assert.AreEqual(620, HomePage.CalculateHorizontalWheelTarget(500, -120, 1000), 0.001);
        Assert.AreEqual(0, HomePage.CalculateHorizontalWheelTarget(20, 120, 1000), 0.001);
        Assert.AreEqual(1000, HomePage.CalculateHorizontalWheelTarget(980, -120, 1000), 0.001);
    }

    [TestMethod]
    public void HomePage_RoutesOnlyShiftWheelToHorizontalRowsWhenTheyCanMove()
    {
        Assert.IsFalse(HomePage.ShouldRouteWheelHorizontally(false, 500, 120, 1000));
        Assert.IsTrue(HomePage.ShouldRouteWheelHorizontally(true, 500, 120, 1000));
        Assert.IsFalse(HomePage.ShouldRouteWheelHorizontally(true, 0, 120, 1000));
        Assert.IsFalse(HomePage.ShouldRouteWheelHorizontally(true, 1000, -120, 1000));
        Assert.IsFalse(HomePage.ShouldRouteWheelHorizontally(true, 0, -120, 0));
    }

    [TestMethod]
    public async Task HomePage_AllHorizontalRowsShareWheelRoutingWithoutReplacingPageScroll()
    {
        var repositoryRoot = FindRepositoryRoot();
        var homePagePath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "HomePage.xaml");
        var xaml = await File.ReadAllTextAsync(homePagePath);

        StringAssert.Contains(xaml, "x:Name=\"HomePageScrollViewer\"");
        StringAssert.Contains(xaml, "PreviewMouseWheel=\"OnHomePagePreviewMouseWheel\"");
        Assert.AreEqual(5, CountOccurrences(xaml, "PreviewMouseWheel=\"OnHorizontalRowPreviewMouseWheel\""));
        StringAssert.Contains(xaml, "x:Name=\"LibraryScrollViewer\"");
        StringAssert.Contains(xaml, "x:Name=\"ContinueWatchingScrollViewer\"");
        StringAssert.Contains(xaml, "x:Name=\"RecentlyAddedScrollViewer\"");
    }

    [TestMethod]
    public async Task HomePage_CategoryRowsWireTheSharedHorizontalInteractionHandlers()
    {
        var repositoryRoot = FindRepositoryRoot();
        var homePagePath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "HomePage.xaml");
        var xaml = await File.ReadAllTextAsync(homePagePath);
        var categoryTemplateStart = xaml.IndexOf("<DataTemplate x:Key=\"HomeMediaSectionTemplate\">", StringComparison.Ordinal);
        var categoryTemplateEnd = xaml.IndexOf("</DataTemplate>", categoryTemplateStart, StringComparison.Ordinal);

        Assert.IsTrue(categoryTemplateStart >= 0 && categoryTemplateEnd > categoryTemplateStart);
        var categoryTemplate = xaml[categoryTemplateStart..(categoryTemplateEnd + "</DataTemplate>".Length)];
        StringAssert.Contains(categoryTemplate, "PreviewMouseWheel=\"OnHorizontalRowPreviewMouseWheel\"");
        StringAssert.Contains(categoryTemplate, "ScrollChanged=\"OnHorizontalRowScrollChanged\"");
        StringAssert.Contains(categoryTemplate, "Loaded=\"OnHorizontalRowLoaded\"");
    }

    [TestMethod]
    public async Task HomePage_CategoryRetryUsesApplicationButtonStyle()
    {
        var repositoryRoot = FindRepositoryRoot();
        var homePageXaml = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "HomePage.xaml"));
        var appXaml = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "src", "EmbyPlayer.App", "App.xaml"));

        StringAssert.Contains(homePageXaml, "Style=\"{StaticResource AppPrimaryButtonStyle}\"");
        StringAssert.Contains(appXaml, "x:Key=\"AppPrimaryButtonStyle\"");
        Assert.IsFalse(homePageXaml.Contains("StaticResource RetryButtonStyle", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task HomePage_UsesSharedIconSystemWithoutLegacyGlyphs()
    {
        var repositoryRoot = FindRepositoryRoot();
        var homePagePath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "HomePage.xaml");
        var iconsPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Resources", "Icons.xaml");
        var iconButtonsPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Styles", "IconButtons.xaml");
        var uiProjectPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "EmbyPlayer.UI.csproj");
        var xaml = await File.ReadAllTextAsync(homePagePath);
        var icons = await File.ReadAllTextAsync(iconsPath);
        var iconButtons = await File.ReadAllTextAsync(iconButtonsPath);
        var appXaml = await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "src", "EmbyPlayer.App", "App.xaml"));
        var uiProject = await File.ReadAllTextAsync(uiProjectPath);

        foreach (var legacyGlyph in new[] { "Segoe MDL2 Assets", "▶", "♡", "♥", "‹", "›", "&#xE895;" })
        {
            Assert.IsFalse(xaml.Contains(legacyGlyph, StringComparison.Ordinal), $"Legacy glyph remains: {legacyGlyph}");
        }

        foreach (var iconKey in new[]
                 {
                     "Icon.Play",
                     "Icon.ChevronLeft",
                     "Icon.ChevronRight",
                     "Icon.Search",
                     "Icon.Settings",
                     "Icon.WatchLater",
                     "Icon.FavoriteFilled",
                     "Icon.Watched"
                 })
        {
            StringAssert.Contains(icons, $"x:Key=\"{iconKey}\"");
            StringAssert.Contains(xaml + iconButtons + appXaml, $"{{StaticResource {iconKey}}}");
        }

        foreach (var iconName in new[] { "Movie", "Series", "Animation", "BoxSet", "Playlist" })
        {
            var fileName = $"Icon.Library{iconName}.png";
            var iconPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Assets", "HomeIcons", fileName);

            Assert.IsTrue(File.Exists(iconPath), $"Missing Home shortcut icon: {fileName}");
            StringAssert.Contains(xaml, $"/EmbyPlayer.UI;component/Assets/HomeIcons/{fileName}");
            Assert.IsFalse(xaml.Contains($"{{StaticResource Icon.Library{iconName}}}", StringComparison.Ordinal));
        }

        StringAssert.Contains(uiProject, "<Resource Include=\"Assets\\HomeIcons\\*.png\" />");
        StringAssert.Contains(xaml, "<Style x:Key=\"LibraryIconStyle\" TargetType=\"Image\">");
        StringAssert.Contains(xaml, "<Setter Property=\"Width\" Value=\"32\" />");
        StringAssert.Contains(xaml, "<Setter Property=\"Height\" Value=\"32\" />");
        StringAssert.Contains(xaml, "<Setter Property=\"Stretch\" Value=\"Uniform\" />");
        StringAssert.Contains(xaml, "<Setter Property=\"SnapsToDevicePixels\" Value=\"True\" />");
        StringAssert.Contains(xaml, "<Setter Property=\"RenderOptions.BitmapScalingMode\" Value=\"HighQuality\" />");
        StringAssert.Contains(xaml, "<DataTrigger Binding=\"{Binding Name}\" Value=\"动画\">");
        Assert.IsFalse(xaml.Contains("LibraryIconBorderStyle", StringComparison.Ordinal));

        StringAssert.Contains(iconButtons, "x:Key=\"CardOverlayPlayButton\"");
        Assert.AreEqual(2, CountOccurrences(xaml, "BasedOn=\"{StaticResource CardOverlayPlayButton}\""));
        StringAssert.Contains(xaml, "Command=\"{Binding OpenFavoritesCommand}\"");
        StringAssert.Contains(xaml, "Command=\"{Binding OpenSettingsCommand}\"");
        StringAssert.Contains(xaml, "Command=\"{Binding PreviousHeroCommand}\"");
        StringAssert.Contains(xaml, "Command=\"{Binding NextHeroCommand}\"");
        StringAssert.Contains(xaml, "ContinueMediaCommand");
        StringAssert.Contains(xaml, "PlayMediaCommand");
    }

    [TestMethod]
    public async Task RoundedClipBorder_ClipsContentWithoutClippingItsOwnBorder()
    {
        var repositoryRoot = FindRepositoryRoot();
        var controlPath = Path.Combine(
            repositoryRoot,
            "src",
            "EmbyPlayer.UI",
            "Controls",
            "RoundedClipBorder.cs");
        var source = await File.ReadAllTextAsync(controlPath);

        StringAssert.Contains(source, "Clip = null;");
        StringAssert.Contains(source, "Child.Clip = new RectangleGeometry");
        Assert.IsFalse(source.Contains("        Clip = new RectangleGeometry(", StringComparison.Ordinal));
    }

    private static HomeViewModelTestContext CreateContext(TimeProvider? timeProvider = null)
    {
        return new HomeViewModelTestContext(timeProvider);
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

    private static int CountOccurrences(string value, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }

        return count;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Condition was not met before timeout.");
    }

    private static HomeLoadResult CreateSuccessResult(string userName, string continueWatchingTitle = "Continue Movie")
    {
        return HomeLoadResult.Success(new HomeData(
            userName,
            new[]
            {
                new MediaCard(
                    "continue-1",
                    continueWatchingTitle,
                    "Movie",
                    2020,
                    "http://media.local:8096/Items/continue-1/Images/Primary",
                    42,
                    resumePositionTicks: 123000000,
                    logoUrl: "http://media.local:8096/Items/continue-1/Images/Logo")
            },
            CreateRecentItems(),
            CreateLibraries()));
    }

    private static IReadOnlyList<MediaCard> CreateContinueWatchingItems()
    {
        return new[]
        {
            new MediaCard(
                "continue-1",
                "Continue Movie",
                "Movie",
                2020,
                "http://media.local:8096/Items/continue-1/Images/Primary",
                42,
                resumePositionTicks: 123000000,
                logoUrl: "http://media.local:8096/Items/continue-1/Images/Logo")
        };
    }

    private static IReadOnlyList<MediaCard> CreateRecentItems()
    {
        return new[]
        {
            new MediaCard(
                "recent-1",
                "Recent Movie",
                "Movie",
                2024,
                null,
                null)
        };
    }

    private static IReadOnlyList<MediaLibrary> CreateLibraries()
    {
        return new[]
        {
            new MediaLibrary("library-1", "Movies", "movies")
        };
    }

    private static MediaCard CreateMediaCard(string id, string title, bool hasBackdrop = false)
    {
        var posterUrl = $"http://media.local:8096/Items/{id}/Images/Primary";
        return new MediaCard(
            id,
            title,
            "Movie",
            2024,
            posterUrl,
            null,
            heroImageUrl: hasBackdrop
                ? $"http://media.local:8096/Items/{id}/Images/Backdrop/0"
                : null);
    }

    private sealed class HomeViewModelTestContext
    {
        public HomeViewModelTestContext(TimeProvider? timeProvider)
        {
            NavigationService = new NavigationService();
            HomeService = new TestHomeService();
            PlaybackService = new TestPlaybackService();
            CurrentSessionService = new CurrentSessionService();
            AuthSessionStore = new TestAuthSessionStore();
            AppSettingsService = new TestAppSettingsService();
            Session = new AuthSession(
                "http://media.local:8096",
                "test-access-token",
                "user-1",
                "Test User",
                "server-1");
            CurrentSessionService.SetSession(Session);
            HomeService.LoadHomeAsyncHandler = (_, _) =>
                Task.FromResult(CreateSuccessResult(Session.UserName));
            ViewModel = new HomeViewModel(
                NavigationService,
                HomeService,
                PlaybackService,
                CurrentSessionService,
                AuthSessionStore,
                message => LoginErrorMessage = message,
                timeProvider,
                WatchLaterStore,
                Queue);
        }

        public NavigationService NavigationService { get; }

        public TestWatchLaterStore WatchLaterStore { get; } = new();

        public EmbyPlayer.Core.PlaybackQueue.InMemoryPlaybackQueueService Queue { get; } = new();

        public TestHomeService HomeService { get; }

        public TestPlaybackService PlaybackService { get; }

        public CurrentSessionService CurrentSessionService { get; }

        public TestAuthSessionStore AuthSessionStore { get; }

        public TestAppSettingsService AppSettingsService { get; }

        public AuthSession Session { get; }

        public string? LoginErrorMessage { get; private set; }

        public HomeViewModel ViewModel { get; }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow = new(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }

        public void Advance(TimeSpan elapsed)
        {
            utcNow += elapsed;
        }
    }
}
