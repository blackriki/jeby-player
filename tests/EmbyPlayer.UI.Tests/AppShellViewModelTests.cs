using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.ViewModels;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Details;
using EmbyPlayer.Core.Diagnostics;
using EmbyPlayer.Core.Home;
using EmbyPlayer.Core.Library;
using EmbyPlayer.Core.Playback;
using EmbyPlayer.Core.Series;
using EmbyPlayer.Core.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class AppShellViewModelTests
{
    [TestMethod]
    public void NavigateCommand_ChangesCurrentPage()
    {
        var navigationService = new NavigationService();
        var context = CreateContext(navigationService);
        context.CurrentSessionService.SetSession(CreateSession());
        var viewModel = context.ViewModel;

        viewModel.NavigateCommand.Execute(AppPage.Library);

        Assert.AreEqual(AppPage.Library, viewModel.CurrentPage);
        Assert.IsInstanceOfType<LibraryViewModel>(viewModel.CurrentPageViewModel);
    }

    [TestMethod]
    public void NavigationItems_AreEmptyAfterShellNavigationSimplification()
    {
        var viewModel = CreateViewModel();

        Assert.AreEqual(0, viewModel.NavigationItems.Count);
    }

    [TestMethod]
    public void NavigationItems_DoNotRenderDuplicateTopLevelLabels()
    {
        var viewModel = CreateViewModel();

        Assert.AreEqual(0, viewModel.NavigationItems.Select(item => item.Label).ToArray().Length);
    }

    [TestMethod]
    public void NavigationItems_ExcludeNonPrimaryPages()
    {
        var viewModel = CreateViewModel();
        var pages = viewModel.NavigationItems.Select(item => item.Page).ToArray();

        CollectionAssert.DoesNotContain(pages, AppPage.ServerConnection);
        CollectionAssert.DoesNotContain(pages, AppPage.Login);
        CollectionAssert.DoesNotContain(pages, AppPage.Search);
        CollectionAssert.DoesNotContain(pages, AppPage.Detail);
        CollectionAssert.DoesNotContain(pages, AppPage.Player);
    }

    [TestMethod]
    public void NavigateToLogin_UsesLoginViewModel()
    {
        var viewModel = CreateViewModel();

        viewModel.NavigateTo(AppPage.Login);

        Assert.IsInstanceOfType<LoginViewModel>(viewModel.CurrentPageViewModel);
    }

    [TestMethod]
    public void NavigateToHome_UsesHomeViewModel()
    {
        var context = CreateContext();
        context.CurrentSessionService.SetSession(CreateSession());

        context.ViewModel.NavigateTo(AppPage.Home);

        Assert.IsInstanceOfType<HomeViewModel>(context.ViewModel.CurrentPageViewModel);
    }

    [DataTestMethod]
    [DataRow(HomeSectionKind.ContinueWatching)]
    [DataRow(HomeSectionKind.RecentlyAdded)]
    [DataRow(HomeSectionKind.Movies)]
    [DataRow(HomeSectionKind.Series)]
    [DataRow(HomeSectionKind.Animation)]
    [DataRow(HomeSectionKind.BoxSets)]
    public void HomeViewAll_OpensAndLoadsTheRequestedSection(HomeSectionKind section)
    {
        var context = CreateContext();
        context.CurrentSessionService.SetSession(CreateSession());
        context.NavigationService.NavigateTo(AppPage.Home);

        context.HomeViewModel.OpenSectionCommand.Execute(section);

        Assert.AreEqual(AppPage.HomeSection, context.ViewModel.CurrentPage);
        Assert.AreSame(context.HomeViewModel.SectionViewModel, context.ViewModel.CurrentPageViewModel);
        Assert.AreEqual(section, context.HomeService.LastSection);
        Assert.AreEqual(0, context.HomeService.LastSectionStartIndex);
        Assert.AreEqual(1, context.HomeService.LoadSectionCallCount);
    }

    [TestMethod]
    public async Task HomeSection_DetailBackPreservesAllLoadedPages()
    {
        var context = CreateContext();
        context.CurrentSessionService.SetSession(CreateSession());
        context.HomeService.LoadSectionAsyncHandler = (_, _, start, limit, _) =>
            Task.FromResult(HomeSectionItemsLoadResult.Success(
                Enumerable.Range(start, limit)
                    .Select(index => new MediaCard($"movie-{index}", $"Movie {index}", "Movie", 2024, null, null))
                    .ToArray(),
                start + limit,
                start == 0));
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(new MediaDetail(
                itemId, "Movie", "Movie", 2024, null, null, null, null, null, null, null, null)));
        context.HomeViewModel.OpenSectionCommand.Execute(HomeSectionKind.Movies);
        var sectionViewModel = context.HomeViewModel.SectionViewModel;
        await sectionViewModel.LoadMoreAsync();
        var loadedItems = sectionViewModel.Items;
        Assert.IsTrue(loadedItems.Count > 24);
        Assert.AreEqual(2, context.HomeService.LoadSectionCallCount);

        sectionViewModel.OpenMediaCommand.Execute(loadedItems[^1]);
        Assert.AreEqual(AppPage.Detail, context.ViewModel.CurrentPage);
        Assert.AreEqual(loadedItems[^1].Id, context.MediaDetailService.LastItemId);
        context.MediaDetailViewModel.BackCommand.Execute(null);

        Assert.AreEqual(AppPage.HomeSection, context.ViewModel.CurrentPage);
        Assert.AreSame(sectionViewModel, context.ViewModel.CurrentPageViewModel);
        Assert.AreSame(loadedItems, sectionViewModel.Items);
        Assert.AreEqual(2, context.HomeService.LoadSectionCallCount);
    }

    [TestMethod]
    public async Task LeavingHomeSection_CancelsRefreshAndIgnoresLateUnauthorized()
    {
        var context = CreateContext();
        var session = CreateSession();
        context.CurrentSessionService.SetSession(session);
        context.HomeViewModel.OpenSectionCommand.Execute(HomeSectionKind.ContinueWatching);
        var pending = new TaskCompletionSource<HomeSectionItemsLoadResult>();
        context.HomeService.LoadSectionAsyncHandler = (_, _, _, _, _) => pending.Task;
        var refresh = context.HomeViewModel.SectionViewModel.RefreshAsync();
        var token = context.HomeService.LastSectionCancellationToken;

        context.NavigationService.NavigateTo(AppPage.Home);
        pending.SetResult(HomeSectionItemsLoadResult.Failure(HomeLoadError.Unauthorized));
        await refresh;

        Assert.IsTrue(token.IsCancellationRequested);
        Assert.AreEqual(AppPage.Home, context.ViewModel.CurrentPage);
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(session, context.CurrentSessionService.CurrentSession);
    }

    [DataTestMethod]
    [DataRow(AppPage.Login)]
    [DataRow(AppPage.ServerConnection)]
    public void AuthenticationNavigation_ClearsHomeSectionItems(AppPage destination)
    {
        var context = CreateContext();
        context.CurrentSessionService.SetSession(CreateSession());
        context.HomeService.LoadSectionAsyncHandler = (_, _, _, _, _) =>
            Task.FromResult(HomeSectionItemsLoadResult.Success(
                [new MediaCard("movie-1", "Movie", "Movie", 2024, null, null)], 1, false));
        context.HomeViewModel.OpenSectionCommand.Execute(HomeSectionKind.Movies);
        Assert.AreEqual(1, context.HomeViewModel.SectionViewModel.Items.Count);

        context.NavigationService.NavigateTo(destination);

        Assert.AreEqual(0, context.HomeViewModel.SectionViewModel.Items.Count);
    }

    [TestMethod]
    public void NavigateToLibrary_UsesLibraryViewModel()
    {
        var context = CreateContext();
        context.CurrentSessionService.SetSession(CreateSession());

        context.ViewModel.NavigateTo(AppPage.Library);

        Assert.IsInstanceOfType<LibraryViewModel>(context.ViewModel.CurrentPageViewModel);
    }

    [TestMethod]
    public void NavigateToSettings_UsesDedicatedSettingsViewModel()
    {
        var context = CreateContext();
        context.CurrentSessionService.SetSession(CreateSession());

        context.ViewModel.NavigateTo(AppPage.Settings);

        Assert.AreSame(context.ViewModel.SettingsViewModel, context.ViewModel.CurrentPageViewModel);
    }

    [TestMethod]
    public void SubmitSearchCommand_NavigatesToSearchPage()
    {
        var context = CreateContext();
        context.CurrentSessionService.SetSession(CreateSession());
        context.ViewModel.ShellSearchText = "one piece";

        context.ViewModel.SubmitSearchCommand.Execute(null);

        Assert.AreEqual(AppPage.Search, context.ViewModel.CurrentPage);
        Assert.IsInstanceOfType<SearchViewModel>(context.ViewModel.CurrentPageViewModel);
        Assert.AreEqual("one piece", context.SearchViewModel.Keyword);
        Assert.AreEqual("one piece", context.SearchService.LastKeyword);
    }

    [TestMethod]
    public void ActivateSearch_FromBrowsePageNavigatesToSearch()
    {
        var context = CreateContext();
        context.CurrentSessionService.SetSession(CreateSession());
        context.ViewModel.NavigateTo(AppPage.Library);

        var handled = context.ViewModel.ActivateSearch();

        Assert.IsTrue(handled);
        Assert.AreEqual(AppPage.Search, context.ViewModel.CurrentPage);
    }

    [TestMethod]
    public void ActivateSearch_OnPlayerPageDoesNotInterceptPlayerShortcut()
    {
        var context = CreateContext();
        context.ViewModel.NavigateTo(AppPage.Player);

        var handled = context.ViewModel.ActivateSearch();

        Assert.IsFalse(handled);
        Assert.AreEqual(AppPage.Player, context.ViewModel.CurrentPage);
    }

    [TestMethod]
    public void NavigateToDetail_UsesMediaDetailViewModel()
    {
        var context = CreateContext();
        context.CurrentSessionService.SetSession(CreateSession());

        context.ViewModel.NavigateTo(AppPage.Detail);

        Assert.IsInstanceOfType<MediaDetailViewModel>(context.ViewModel.CurrentPageViewModel);
    }

    [TestMethod]
    public void NavigateToPlayer_UsesPlayerViewModel()
    {
        var context = CreateContext();

        context.NavigationService.NavigateTo(
            AppPage.Player,
            new PlayerNavigationParameter(
                CreatePlaybackInfo(),
                new DetailNavigationParameter("item-1", AppPage.Home)));

        Assert.IsInstanceOfType<PlayerViewModel>(context.ViewModel.CurrentPageViewModel);
        Assert.AreEqual("Playback Item", context.PlayerViewModel.Title);
    }

    [TestMethod]
    public async Task NavigateToLibrary_WithLibraryId_SelectsRequestedLibrary()
    {
        var context = CreateContext();
        context.CurrentSessionService.SetSession(CreateSession());
        context.LibraryService.LoadLibrariesAsyncHandler = (_, _) =>
            Task.FromResult(LibraryLoadResult.Success(new[]
            {
                new LibraryItem("library-1", "Movies", "movies"),
                new LibraryItem("library-2", "TV", "tvshows")
            }));
        context.LibraryService.LoadLibraryItemsAsyncHandler = (_, library, _, _, _) =>
            Task.FromResult(LibraryItemsLoadResult.Success(new[]
            {
                new LibraryMediaItem($"{library.Id}-item", "Item", "Movie", 2024, null, null)
            }));

        context.NavigationService.NavigateTo(AppPage.Library, "library-2");
        await WaitForAsync(() => context.LibraryService.LoadLibraryItemsCallCount > 0);

        Assert.AreEqual("library-2", context.LibraryViewModel.SelectedLibrary!.Id);
        Assert.AreEqual("library-2", context.LibraryService.LastLibrary!.Id);
    }

    [TestMethod]
    public async Task NavigateToDetail_WithItemId_LoadsRequestedDetail()
    {
        var context = CreateContext();
        context.CurrentSessionService.SetSession(CreateSession());

        context.NavigationService.NavigateTo(AppPage.Detail, "item-1");
        await WaitForAsync(() => context.MediaDetailService.LoadCallCount > 0);

        Assert.AreEqual("item-1", context.MediaDetailService.LastItemId);
        Assert.IsInstanceOfType<MediaDetailViewModel>(context.ViewModel.CurrentPageViewModel);
    }

    [TestMethod]
    public async Task LeavingDetail_CancelsPendingLoadAndIgnoresLateUnauthorized()
    {
        var context = CreateContext();
        context.CurrentSessionService.SetSession(CreateSession());
        var pendingDetail = new TaskCompletionSource<MediaDetailLoadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var detailToken = CancellationToken.None;
        context.MediaDetailService.LoadDetailAsyncHandler = (_, _, cancellationToken) =>
        {
            detailToken = cancellationToken;
            return pendingDetail.Task;
        };

        context.NavigationService.NavigateTo(AppPage.Detail, "item-1");
        Assert.AreEqual(1, context.MediaDetailService.LoadCallCount);

        context.NavigationService.NavigateTo(AppPage.Home);

        Assert.IsTrue(detailToken.IsCancellationRequested);
        pendingDetail.SetResult(MediaDetailLoadResult.Failure(MediaDetailLoadError.Unauthorized));
        await Task.Yield();
        Assert.AreEqual(AppPage.Home, context.ViewModel.CurrentPage);
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.IsNotNull(context.CurrentSessionService.CurrentSession);
    }

    [TestMethod]
    public async Task LeavingLibrary_CancelsPendingItemsAndIgnoresLateUnauthorized()
    {
        var context = CreateContext();
        context.CurrentSessionService.SetSession(CreateSession());
        context.NavigationService.NavigateTo(AppPage.Library);
        context.LibraryService.LoadLibrariesAsyncHandler = (_, _) => Task.FromResult(
            LibraryLoadResult.Success([new LibraryItem("library-1", "Movies", "movies")]));
        var pending = new TaskCompletionSource<LibraryItemsLoadResult>();
        var token = CancellationToken.None;
        context.LibraryService.LoadLibraryQueryAsyncHandler = (_, _, _, _, _, cancellationToken) =>
        {
            token = cancellationToken;
            return pending.Task;
        };
        var load = context.LibraryViewModel.LoadAsync();

        context.NavigationService.NavigateTo(AppPage.Home);
        pending.SetResult(LibraryItemsLoadResult.Failure(LibraryLoadError.Unauthorized));
        await load;

        Assert.IsTrue(token.IsCancellationRequested);
        Assert.IsFalse(context.LibraryViewModel.IsLoading);
        Assert.AreEqual(AppPage.Home, context.ViewModel.CurrentPage);
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.IsNotNull(context.CurrentSessionService.CurrentSession);
    }

    [DataTestMethod]
    [DataRow(AppPage.Login)]
    [DataRow(AppPage.ServerConnection)]
    public async Task AuthenticationNavigation_ClearsRetainedLibraryOptionsAndContent(AppPage destination)
    {
        var context = CreateContext();
        context.CurrentSessionService.SetSession(CreateSession());
        context.LibraryService.LoadLibrariesAsyncHandler = (_, _) => Task.FromResult(
            LibraryLoadResult.Success([new LibraryItem("library-1", "Movies", "movies")]));
        context.LibraryService.LoadLibraryQueryAsyncHandler = (_, _, _, _, _, _) => Task.FromResult(
            LibraryItemsLoadResult.Success([new LibraryMediaItem("movie-1", "Movie", "Movie", 2024, null, null)]));
        context.NavigationService.NavigateTo(AppPage.Library);
        await context.LibraryViewModel.UpdateQueryAsync(new LibraryQuery(FavoritesOnly: true));
        context.NavigationService.NavigateTo(AppPage.Settings);
        Assert.IsTrue(context.LibraryViewModel.FavoritesOnly);

        context.NavigationService.NavigateTo(destination);

        Assert.AreEqual(new LibraryQuery(), context.LibraryViewModel.Query);
        Assert.AreEqual(0, context.LibraryViewModel.Items.Count);
        Assert.AreEqual(0, context.LibraryViewModel.Libraries.Count);
    }

    [TestMethod]
    public async Task NavigateToDetail_FromDetailWithEpisodeId_ReloadsRequestedEpisode()
    {
        var context = CreateContext();
        context.CurrentSessionService.SetSession(CreateSession());

        context.NavigationService.NavigateTo(AppPage.Detail, "series-1");
        await WaitForAsync(() => context.MediaDetailService.LoadCallCount == 1);
        context.NavigationService.NavigateTo(AppPage.Detail, "episode-1");
        await WaitForAsync(() => context.MediaDetailService.LoadCallCount == 2);

        Assert.AreEqual("episode-1", context.MediaDetailService.LastItemId);
        Assert.IsInstanceOfType<MediaDetailViewModel>(context.ViewModel.CurrentPageViewModel);
    }

    [TestMethod]
    public async Task NavigateToDetail_ParentSeriesNotFoundLoadsEpisodeFallbackOnce()
    {
        var context = CreateContext();
        context.CurrentSessionService.SetSession(CreateSession());
        var requestedItemIds = new List<string>();
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
        {
            requestedItemIds.Add(itemId);
            return Task.FromResult(itemId == "series-missing"
                ? MediaDetailLoadResult.Failure(MediaDetailLoadError.NotFound)
                : MediaDetailLoadResult.Success(new MediaDetail(
                    itemId,
                    "Resume Episode",
                    "Episode",
                    2024,
                    TimeSpan.FromMinutes(45).Ticks,
                    "Episode overview",
                    Array.Empty<string>(),
                    null,
                    35,
                    TimeSpan.FromMinutes(15).Ticks,
                    null,
                    null)));
        };

        context.NavigationService.NavigateTo(
            AppPage.Detail,
            new DetailNavigationParameter(
                "series-missing",
                AppPage.Home,
                SelectedSeasonId: "season-1",
                FocusedEpisodeId: "episode-1",
                FallbackItemId: "episode-1"));
        await WaitForAsync(() => context.MediaDetailViewModel.Detail?.Id == "episode-1");

        CollectionAssert.AreEqual(
            new[] { "series-missing", "episode-1" },
            requestedItemIds);
        Assert.AreEqual(2, context.MediaDetailService.LoadCallCount);
        Assert.IsFalse(context.MediaDetailViewModel.IsLoading);
        Assert.IsFalse(context.MediaDetailViewModel.HasError);
        Assert.AreEqual("Resume Episode", context.MediaDetailViewModel.Detail?.Title);
        Assert.AreEqual("Episode", context.MediaDetailViewModel.Detail?.Type);
    }

    [TestMethod]
    public async Task NavigateToDetail_ParentSeriesNonNotFoundFailureDoesNotFallback()
    {
        var context = CreateContext();
        context.CurrentSessionService.SetSession(CreateSession());
        context.MediaDetailService.LoadDetailAsyncHandler = (_, _, _) =>
            Task.FromResult(MediaDetailLoadResult.Failure(MediaDetailLoadError.ServerUnreachable));

        context.NavigationService.NavigateTo(
            AppPage.Detail,
            new DetailNavigationParameter(
                "series-unreachable",
                AppPage.Home,
                SelectedSeasonId: "season-1",
                FocusedEpisodeId: "episode-1",
                FallbackItemId: "episode-1"));
        await WaitForAsync(() => context.MediaDetailViewModel.HasError);

        Assert.AreEqual(1, context.MediaDetailService.LoadCallCount);
        Assert.AreEqual("series-unreachable", context.MediaDetailService.LastItemId);
        Assert.IsNull(context.MediaDetailViewModel.Detail);
        Assert.AreEqual("无法加载详情，请检查网络或服务器", context.MediaDetailViewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task DetailBack_FromEpisodeReturnsSeriesDetailAndRestoresSelectedSeason()
    {
        var context = CreateContext();
        context.CurrentSessionService.SetSession(CreateSession());
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(new MediaDetail(
                itemId,
                "Detail Item",
                itemId == "episode-1" ? "Episode" : "Series",
                2024,
                null,
                null,
                Array.Empty<string>(),
                null,
                null,
                null,
                null,
                null)));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, false),
                new SeasonInfo("season-2", "第 2 季", 2, false)
            }));
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, _, _) =>
            Task.FromResult(SeriesEpisodesLoadResult.Success(new[]
            {
                new EpisodeInfo("episode-1", "第 1 集：第一集", 2, 1, null, null, null, null, null)
            }));

        context.NavigationService.NavigateTo(
            AppPage.Detail,
            new DetailNavigationParameter("series-1", AppPage.Library));
        await WaitForAsync(() => context.MediaDetailService.LoadCallCount == 1
            && context.MediaDetailViewModel.Episodes.Count > 0);
        await context.MediaDetailViewModel.SelectSeasonAsync(context.MediaDetailViewModel.Seasons[1]);

        context.MediaDetailViewModel.OpenEpisodeCommand.Execute(context.MediaDetailViewModel.Episodes[0]);
        await WaitForAsync(() => context.MediaDetailService.LoadCallCount == 2);
        Assert.AreEqual("episode-1", context.MediaDetailService.LastItemId);
        Assert.IsFalse(context.MediaDetailViewModel.IsSeriesSectionVisible);

        context.MediaDetailViewModel.BackCommand.Execute(null);
        await WaitForAsync(() => context.MediaDetailService.LoadCallCount == 3);

        Assert.AreEqual(AppPage.Detail, context.NavigationService.CurrentPage);
        Assert.AreEqual("series-1", context.MediaDetailService.LastItemId);
        Assert.AreEqual("season-2", context.MediaDetailViewModel.SelectedSeason?.Id);
    }

    [TestMethod]
    public async Task OpenSimilar_AndBackRestoresDirectSourceSeriesSeasonAndEpisode()
    {
        var context = CreateContext();
        context.CurrentSessionService.SetSession(CreateSession());
        context.MediaDetailService.LoadDetailAsyncHandler = (_, itemId, _) =>
            Task.FromResult(MediaDetailLoadResult.Success(new MediaDetail(
                itemId,
                itemId == "series-1" ? "Source Series" : "Similar Series",
                "Series",
                2025,
                null,
                null,
                Array.Empty<string>(),
                null,
                null,
                null,
                null,
                null)));
        context.SeriesService.LoadSeasonsAsyncHandler = (_, _, _) =>
            Task.FromResult(SeriesSeasonsLoadResult.Success(new[]
            {
                new SeasonInfo("season-1", "第 1 季", 1, false),
                new SeasonInfo("season-2", "第 2 季", 2, false)
            }));
        context.SeriesService.LoadEpisodesAsyncHandler = (_, _, seasonId, _) =>
            Task.FromResult(SeriesEpisodesLoadResult.Success(new[]
            {
                new EpisodeInfo(
                    $"{seasonId}-episode",
                    "本集",
                    seasonId == "season-2" ? 2 : 1,
                    1,
                    null,
                    null,
                    null,
                    null,
                    null)
            }));
        context.SimilarMediaService.LoadSimilarAsyncHandler = (_, itemId, _) =>
            Task.FromResult(SimilarMediaLoadResult.Success(itemId == "series-1"
                ? new[]
                {
                    new SimilarMediaItem("series-2", "Similar Series", "Series", 2025, null, 0, 0, false)
                }
                : Array.Empty<SimilarMediaItem>()));

        context.NavigationService.NavigateTo(
            AppPage.Detail,
            new DetailNavigationParameter("series-1", AppPage.Library));
        await WaitForAsync(() => context.MediaDetailViewModel.SimilarItems.Count == 1
            && context.MediaDetailViewModel.Episodes.Count == 1);
        await context.MediaDetailViewModel.SelectSeasonAsync(context.MediaDetailViewModel.Seasons[1]);

        context.MediaDetailViewModel.OpenSimilarCommand.Execute(context.MediaDetailViewModel.SimilarItems[0]);
        await WaitForAsync(() => context.MediaDetailViewModel.Detail?.Id == "series-2");

        context.MediaDetailViewModel.BackCommand.Execute(null);
        await WaitForAsync(() => context.MediaDetailService.LoadCallCount == 3
            && context.MediaDetailViewModel.Detail?.Id == "series-1"
            && context.MediaDetailViewModel.SelectedSeason?.Id == "season-2"
            && context.MediaDetailViewModel.SelectedEpisode?.Id == "season-2-episode");

        Assert.AreEqual(AppPage.Detail, context.NavigationService.CurrentPage);
        Assert.AreEqual("series-1", context.MediaDetailService.LastItemId);
        Assert.AreEqual("season-2", context.MediaDetailViewModel.SelectedSeason?.Id);
        Assert.AreEqual("season-2-episode", context.MediaDetailViewModel.SelectedEpisode?.Id);
    }

    [DataTestMethod]
    [DataRow(AppPage.ServerConnection)]
    [DataRow(AppPage.Login)]
    [DataRow(AppPage.Home)]
    [DataRow(AppPage.Library)]
    [DataRow(AppPage.Search)]
    [DataRow(AppPage.Detail)]
    [DataRow(AppPage.Settings)]
    [DataRow(AppPage.Player)]
    public void IsTopBarVisible_ReturnsFalseForAllPages(AppPage page)
    {
        var viewModel = CreateViewModelOnPage(page);

        Assert.IsFalse(viewModel.IsTopBarVisible);
    }

    [TestMethod]
    public void PlayerPage_HidesPlaceholderNavigation()
    {
        var viewModel = CreateViewModelOnPage(AppPage.Player);

        Assert.IsFalse(viewModel.IsPlaceholderNavigationVisible);
    }

    [TestMethod]
    public void NonPlayerPage_HidesPlaceholderNavigation()
    {
        var viewModel = CreateViewModelOnPage(AppPage.Home);

        Assert.IsFalse(viewModel.IsPlaceholderNavigationVisible);
    }

    [TestMethod]
    public async Task AppShell_IsOnlyPageHostWithoutPersistentNavigation()
    {
        var repositoryRoot = FindRepositoryRoot();
        var shellPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Controls", "AppShell.xaml");
        var xaml = await File.ReadAllTextAsync(shellPath);

        Assert.IsFalse(xaml.Contains("ItemsSource=\"{Binding NavigationItems}\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("UserDisplayName", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("LogoutCommand", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("CommandParameter=\"{x:Static navigation:AppPage.Home}\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("CommandParameter=\"{x:Static navigation:AppPage.Settings}\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("ShellButtonStyle", StringComparison.Ordinal));
        StringAssert.Contains(xaml, "Content=\"{Binding CurrentPageViewModel}\"");
        StringAssert.Contains(xaml, "IsStartupLoading");
        StringAssert.Contains(xaml, "controls:PageStateView");
    }

    [TestMethod]
    public async Task SettingsPage_BindsAccountActionsWithoutDependingOnShellCommands()
    {
        var repositoryRoot = FindRepositoryRoot();
        var settingsPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "SettingsPage.xaml");
        var xaml = await File.ReadAllTextAsync(settingsPath);

        StringAssert.Contains(xaml, "UserDisplayName");
        StringAssert.Contains(xaml, "CurrentServerText");
        StringAssert.Contains(xaml, "Text=\"退出登录\"");
        StringAssert.Contains(xaml, "Command=\"{Binding RequestLogoutCommand}\"");
        StringAssert.Contains(xaml, "Command=\"{Binding RequestSwitchServerCommand}\"");
        Assert.IsFalse(xaml.Contains("Command=\"{Binding LogoutCommand}\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SettingsPage_ContainsFiveFrozenDesignCategories()
    {
        var repositoryRoot = FindRepositoryRoot();
        var settingsPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "SettingsPage.xaml");
        var xaml = await File.ReadAllTextAsync(settingsPath);

        foreach (var panelName in new[]
                 {
                     "PlaybackPanel",
                     "TracksPanel",
                     "AccountPanel",
                     "LocalDataPanel",
                     "AboutPanel"
                 })
        {
            StringAssert.Contains(xaml, $"x:Name=\"{panelName}\"");
        }

        Assert.IsFalse(xaml.Contains("默认全屏播放", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SettingsPage_UsesScrollableLayoutForSmallWindows()
    {
        var repositoryRoot = FindRepositoryRoot();
        var settingsPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "SettingsPage.xaml");
        var xaml = await File.ReadAllTextAsync(settingsPath);

        StringAssert.Contains(xaml, "x:Name=\"ContentScroller\"");
        StringAssert.Contains(xaml, "VerticalScrollBarVisibility=\"Auto\"");
        StringAssert.Contains(xaml, "HorizontalScrollBarVisibility=\"Disabled\"");
    }

    [TestMethod]
    public async Task SettingsPage_BindsRememberLastVolumeToSettingsViewModel()
    {
        var repositoryRoot = FindRepositoryRoot();
        var settingsPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "SettingsPage.xaml");
        var xaml = await File.ReadAllTextAsync(settingsPath);

        StringAssert.Contains(xaml, "x:Name=\"RememberVolumeToggle\"");
        StringAssert.Contains(xaml, "Text=\"记住上次音量\"");
        StringAssert.Contains(xaml, "Style=\"{StaticResource SettingsToggle}\"");
        StringAssert.Contains(xaml, "IsChecked=\"{Binding RememberLastVolume, Mode=TwoWay}\"");
    }

    [TestMethod]
    public async Task SettingsPage_UsesFrozenSliderAndSelectControls()
    {
        var repositoryRoot = FindRepositoryRoot();
        var settingsPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "SettingsPage.xaml");
        var xaml = await File.ReadAllTextAsync(settingsPath);

        StringAssert.Contains(xaml, "x:Name=\"DefaultVolumeSlider\"");
        StringAssert.Contains(xaml, "Style=\"{StaticResource SettingsSlider}\"");
        StringAssert.Contains(xaml, "x:Name=\"SeekSecondsComboBox\"");
        StringAssert.Contains(xaml, "x:Name=\"ControlsHideComboBox\"");
        Assert.IsFalse(xaml.Contains("UpdateSourceTrigger=LostFocus", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SettingsPage_BindsLanguageComboBoxesToSettingsViewModel()
    {
        var repositoryRoot = FindRepositoryRoot();
        var settingsPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "SettingsPage.xaml");
        var xaml = await File.ReadAllTextAsync(settingsPath);

        StringAssert.Contains(xaml, "x:Name=\"SubtitleLanguageComboBox\"");
        StringAssert.Contains(xaml, "x:Name=\"AudioLanguageComboBox\"");
        StringAssert.Contains(xaml, "Style=\"{StaticResource SettingsComboBox}\"");
        StringAssert.Contains(xaml, "ItemsSource=\"{Binding SubtitleLanguageOptions, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "SelectedValue=\"{Binding DefaultSubtitleLanguage, Mode=TwoWay}\"");
        StringAssert.Contains(xaml, "ItemsSource=\"{Binding AudioLanguageOptions, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "SelectedValue=\"{Binding DefaultAudioLanguage, Mode=TwoWay}\"");
    }

    [TestMethod]
    public async Task SettingsPage_BindsAutoPlayNextEpisodeWithAccessibleName()
    {
        var settingsPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "EmbyPlayer.UI",
            "Pages",
            "SettingsPage.xaml");
        var xaml = await File.ReadAllTextAsync(settingsPath);

        StringAssert.Contains(xaml, "x:Name=\"AutoNextToggle\"");
        StringAssert.Contains(xaml, "AutoPlayNextEpisodeCheckBox");
        StringAssert.Contains(xaml, "AutomationProperties.Name=\"自动播放下一集\"");
        StringAssert.Contains(xaml, "IsChecked=\"{Binding AutoPlayNextEpisode, Mode=TwoWay}\"");
    }

    [TestMethod]
    public async Task SettingsPage_BindsImplementedIndexRebuildWithoutPreviewHandlers()
    {
        var settingsPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "EmbyPlayer.UI",
            "Pages",
            "SettingsPage.xaml");
        var xaml = await File.ReadAllTextAsync(settingsPath);

        Assert.IsFalse(xaml.Contains("OnPreviewActionClick", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("OnRebuildIndexClick", StringComparison.Ordinal));

        const string rebuildIndexAutomationName = "AutomationProperties.Name=\"重建搜索索引\"";
        var rebuildIndexAutomationNameStart = xaml.IndexOf(
            rebuildIndexAutomationName,
            StringComparison.Ordinal);
        Assert.IsTrue(rebuildIndexAutomationNameStart >= 0);

        var rebuildIndexButtonStart = xaml.LastIndexOf(
            "<Button",
            rebuildIndexAutomationNameStart,
            StringComparison.Ordinal);
        var rebuildIndexButtonEnd = xaml.IndexOf(">", rebuildIndexAutomationNameStart, StringComparison.Ordinal);
        Assert.IsTrue(rebuildIndexButtonStart >= 0 && rebuildIndexButtonEnd > rebuildIndexAutomationNameStart);

        var rebuildIndexButton = xaml[rebuildIndexButtonStart..(rebuildIndexButtonEnd + 1)];
        StringAssert.Contains(rebuildIndexButton, "Command=\"{Binding RebuildSearchIndexCommand}\"");
        Assert.IsFalse(rebuildIndexButton.Contains("IsEnabled=\"False\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task HomePage_ContainsSearchAndSettingsEntries()
    {
        var repositoryRoot = FindRepositoryRoot();
        var homePath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "HomePage.xaml");
        var xaml = await File.ReadAllTextAsync(homePath);

        StringAssert.Contains(xaml, "SubmitSearchCommand");
        StringAssert.Contains(xaml, "OpenSettingsCommand");
    }

    [TestMethod]
    public async Task HomePage_HeaderActionsCanWrapInNarrowWindows()
    {
        var repositoryRoot = FindRepositoryRoot();
        var homePath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "HomePage.xaml");
        var xaml = await File.ReadAllTextAsync(homePath);

        StringAssert.Contains(xaml, "x:Name=\"HomeHeaderActionsPanel\"");
        StringAssert.Contains(xaml, "x:Name=\"HomeSearchTextBox\"");
        StringAssert.Contains(xaml, "TextTrimming=\"CharacterEllipsis\"");
    }

    [TestMethod]
    public async Task PrimaryPages_DefineTooltipsForKeyboardReachableActions()
    {
        var repositoryRoot = FindRepositoryRoot();
        var pagePaths = new[]
        {
            Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "HomePage.xaml"),
            Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "LibraryPage.xaml"),
            Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "SearchPage.xaml"),
            Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "SettingsPage.xaml")
        };

        foreach (var pagePath in pagePaths)
        {
            var xaml = await File.ReadAllTextAsync(pagePath);
            StringAssert.Contains(xaml, "ToolTip=");
        }
    }

    [TestMethod]
    public async Task SearchFilterButtons_HaveKeyboardFocusState()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.App", "App.xaml");
        var searchPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "SearchPage.xaml");
        var appXaml = await File.ReadAllTextAsync(appPath);
        var xaml = await File.ReadAllTextAsync(searchPath);

        StringAssert.Contains(appXaml, "IsKeyboardFocused");
        StringAssert.Contains(appXaml, "FilterChipButtonStyle");
        StringAssert.Contains(xaml, "SearchFilterButtonStyle");
    }

    [TestMethod]
    public async Task OrdinaryChildPages_UseUnifiedPageBackButtonStyle()
    {
        var repositoryRoot = FindRepositoryRoot();
        var pagePaths = new[]
        {
            Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "LibraryPage.xaml"),
            Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "SearchPage.xaml")
        };

        foreach (var pagePath in pagePaths)
        {
            var xaml = await File.ReadAllTextAsync(pagePath);
            StringAssert.Contains(xaml, "{StaticResource Icon.Back}");
            StringAssert.Contains(xaml, "Text=\"返回首页\"");
            StringAssert.Contains(xaml, "Style=\"{StaticResource PageBackButton}\"");
        }

        var settingsXaml = await File.ReadAllTextAsync(Path.Combine(
            repositoryRoot,
            "src",
            "EmbyPlayer.UI",
            "Pages",
            "SettingsPage.xaml"));
        Assert.IsFalse(settingsXaml.Contains("PageBackButton", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PlayerLoginAndServerConnectionPages_DoNotShowOrdinaryPageBackButton()
    {
        var repositoryRoot = FindRepositoryRoot();
        var pagePaths = new[]
        {
            Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "PlayerPage.xaml"),
            Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "LoginPage.xaml"),
            Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "ServerConnectionPage.xaml")
        };

        foreach (var pagePath in pagePaths)
        {
            var xaml = await File.ReadAllTextAsync(pagePath);
            Assert.IsFalse(xaml.Contains("PageBackButton", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task SearchFilterButtons_UseDarkChipStyle()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.App", "App.xaml");
        var searchPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "SearchPage.xaml");
        var appXaml = await File.ReadAllTextAsync(appPath);
        var searchXaml = await File.ReadAllTextAsync(searchPath);

        StringAssert.Contains(appXaml, "x:Key=\"FilterChipButtonStyle\"");
        StringAssert.Contains(appXaml, "IsPressed");
        StringAssert.Contains(appXaml, "IsEnabled\" Value=\"False\"");
        StringAssert.Contains(searchXaml, "BasedOn=\"{StaticResource FilterChipButtonStyle}\"");
    }


    [TestMethod]
    public async Task InitializeAsync_WithoutLastServerBase_NavigatesToServerConnection()
    {
        var context = CreateContext();

        Assert.IsTrue(context.ViewModel.IsStartupLoading);

        await context.ViewModel.InitializeAsync(CancellationToken.None);

        Assert.IsFalse(context.ViewModel.IsStartupLoading);
        Assert.AreEqual(AppPage.ServerConnection, context.ViewModel.CurrentPage);
        Assert.AreEqual(0, context.AuthSessionStore.LoadCallCount);
        Assert.AreEqual(0, context.AuthSessionValidator.ValidateCallCount);
        Assert.AreEqual(1, context.AppSettingsService.GetRecentServerBasesCallCount);
        CollectionAssert.AreEqual(
            new[]
            {
                "category=startup event=start ",
                "category=startup event=session-state hasServer=false hasSession=false",
                "category=startup event=route targetPageCategory=serverconnection"
            },
            context.Diagnostics.Entries);
    }

    [TestMethod]
    public async Task InitializeAsync_WithLastServerBaseAndNoAuthSession_NavigatesToLogin()
    {
        var context = CreateContext();
        context.AppSettingsService.LastServerBase = "http://media.local:8096";

        await context.ViewModel.InitializeAsync(CancellationToken.None);

        Assert.AreEqual(AppPage.Login, context.ViewModel.CurrentPage);
        Assert.AreEqual(1, context.AuthSessionStore.LoadCallCount);
        Assert.AreEqual(0, context.AuthSessionValidator.ValidateCallCount);
        Assert.IsTrue(context.Diagnostics.Entries.Contains(
            "category=startup event=session-state hasServer=true hasSession=false"));
        Assert.IsTrue(context.Diagnostics.Entries.Contains(
            "category=startup event=route targetPageCategory=login"));
    }

    [TestMethod]
    public async Task InitializeAsync_ValidSessionDoesNotWaitForHiddenServerHistory()
    {
        var context = CreateContext();
        var session = CreateSession();
        var recentServers = new TaskCompletionSource<IReadOnlyList<string>>();
        var addressReads = 0;
        context.AppSettingsService.GetLastServerBaseAsyncHandler = _ =>
        {
            addressReads++;
            return Task.FromResult<string?>(session.ServerBase);
        };
        context.AppSettingsService.GetRecentServerBasesAsyncHandler = _ => recentServers.Task;
        await context.AuthSessionStore.SaveAsync(session, CancellationToken.None);

        var startup = context.ViewModel.InitializeAsync(CancellationToken.None);
        try
        {
            await startup.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(AppPage.Home, context.ViewModel.CurrentPage);
            Assert.AreEqual(1, addressReads);
            Assert.AreEqual(0, context.AppSettingsService.GetRecentServerBasesCallCount);

            context.ViewModel.NavigateTo(AppPage.ServerConnection);
            Assert.AreEqual(1, context.AppSettingsService.GetRecentServerBasesCallCount);
            recentServers.SetResult(new[] { session.ServerBase });
            await WaitForAsync(() => !context.ServerConnectionViewModel.IsLoadingRecentServers);
            Assert.AreEqual(session.ServerBase, context.ServerConnectionViewModel.ServerUrl);
            CollectionAssert.AreEqual(new[] { session.ServerBase }, context.ServerConnectionViewModel.RecentServerBases.ToArray());
        }
        finally
        {
            recentServers.TrySetResult(Array.Empty<string>());
            await startup;
        }
    }

    [TestMethod]
    public async Task InitializeAsync_WithValidAuthSession_NavigatesToHome()
    {
        var context = CreateContext();
        var session = CreateSession();
        context.AppSettingsService.LastServerBase = session.ServerBase;
        await context.AuthSessionStore.SaveAsync(session, CancellationToken.None);

        await context.ViewModel.InitializeAsync(CancellationToken.None);

        Assert.AreEqual(AppPage.Home, context.ViewModel.CurrentPage);
        Assert.AreSame(session, context.CurrentSessionService.CurrentSession);
        Assert.AreEqual(1, context.AuthSessionValidator.ValidateCallCount);
        Assert.IsTrue(context.Diagnostics.Entries.Contains(
            "category=startup event=session-validation result=Valid"));
        Assert.IsTrue(context.Diagnostics.Entries.Contains(
            "category=startup event=route targetPageCategory=home"));
        await WaitForAsync(() => context.LocalMediaSearchIndex.PrepareCallCount == 1);
        Assert.AreSame(session, context.LocalMediaSearchIndex.LastSession);
    }

    [TestMethod]
    public async Task InitializeAsync_InvalidToken_ClearsAuthSessionAndNavigatesToLogin()
    {
        var context = CreateContext();
        var session = CreateSession();
        context.AppSettingsService.LastServerBase = session.ServerBase;
        await context.AuthSessionStore.SaveAsync(session, CancellationToken.None);
        context.AuthSessionValidator.ValidateAsyncHandler = (_, _) =>
            Task.FromResult(AuthSessionValidationResult.InvalidToken());

        await context.ViewModel.InitializeAsync(CancellationToken.None);

        Assert.AreEqual(AppPage.Login, context.ViewModel.CurrentPage);
        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.IsNull(context.CurrentSessionService.CurrentSession);
        Assert.IsTrue(context.Diagnostics.Entries.Contains(
            "category=startup event=session-validation result=InvalidToken"));
        Assert.IsTrue(context.Diagnostics.Entries.Contains(
            "category=startup event=route targetPageCategory=login"));
    }

    [TestMethod]
    public async Task InitializeAsync_InvalidTokenPersistentClearFailureStillRoutesSafelyAndCompletesOnce()
    {
        var context = CreateContext();
        var session = CreateSession();
        context.AppSettingsService.LastServerBase = session.ServerBase;
        await context.AuthSessionStore.SaveAsync(session, CancellationToken.None);
        context.AuthSessionValidator.ValidateAsyncHandler = (_, _) =>
            Task.FromResult(AuthSessionValidationResult.InvalidToken());
        context.AuthSessionStore.ClearAsyncHandler = _ => throw new IOException("credential failure");

        await context.ViewModel.InitializeAsync(CancellationToken.None);
        await context.ViewModel.InitializeAsync(CancellationToken.None);

        Assert.AreEqual(AppPage.Login, context.ViewModel.CurrentPage);
        Assert.IsNull(context.CurrentSessionService.CurrentSession);
        Assert.IsFalse(context.ViewModel.IsStartupLoading);
        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.AreEqual(1, context.AuthSessionValidator.ValidateCallCount);
        Assert.AreEqual(
            "登录状态已失效，但本地登录信息清理失败，请稍后重试。",
            context.ViewModel.ShellErrorMessage);
        Assert.AreEqual(context.ViewModel.ShellErrorMessage, context.LoginViewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task InitializeAsync_ForbiddenPreservesSessionAndShowsPermissionError()
    {
        var context = CreateContext();
        var session = CreateSession();
        context.AppSettingsService.LastServerBase = session.ServerBase;
        await context.AuthSessionStore.SaveAsync(session, CancellationToken.None);
        context.AuthSessionValidator.ValidateAsyncHandler = (_, _) =>
            Task.FromResult(AuthSessionValidationResult.Forbidden());

        await context.ViewModel.InitializeAsync(CancellationToken.None);

        Assert.AreEqual(AppPage.Login, context.ViewModel.CurrentPage);
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(session, context.CurrentSessionService.CurrentSession);
        Assert.AreEqual(
            "当前账号没有权限访问服务器，请联系服务器管理员。",
            context.LoginViewModel.ErrorMessage);
        Assert.IsTrue(context.Diagnostics.Entries.Contains(
            "category=startup event=session-validation result=Forbidden"));
    }

    [TestMethod]
    public async Task InitializeAsync_MissingTokenMetadataCleanupFailureRoutesSafelyAndCompletesOnce()
    {
        var context = CreateContext();
        context.AppSettingsService.LastServerBase = "http://media.local:8096";
        context.AuthSessionStore.LoadAsyncHandler = _ =>
            throw new AuthSessionClearPartialFailureException(new IOException("metadata failure"));

        await context.ViewModel.InitializeAsync(CancellationToken.None);
        await context.ViewModel.InitializeAsync(CancellationToken.None);

        Assert.AreEqual(AppPage.Login, context.ViewModel.CurrentPage);
        Assert.IsNull(context.CurrentSessionService.CurrentSession);
        Assert.IsFalse(context.ViewModel.IsStartupLoading);
        Assert.AreEqual(1, context.AuthSessionStore.LoadCallCount);
        Assert.AreEqual(
            "安全令牌已清除，但本地账户信息未能完全清理，请稍后重试。",
            context.ViewModel.ShellErrorMessage);
        Assert.AreEqual(context.ViewModel.ShellErrorMessage, context.LoginViewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task InitializeAsync_UnexpectedSessionLoadFailureRoutesToLoginWithoutEscaping()
    {
        var context = CreateContext();
        context.AppSettingsService.LastServerBase = "http://media.local:8096";
        context.AuthSessionStore.LoadAsyncHandler = _ => throw new UnauthorizedAccessException("credential failure");

        await context.ViewModel.InitializeAsync(CancellationToken.None);

        Assert.AreEqual(AppPage.Login, context.ViewModel.CurrentPage);
        Assert.IsFalse(context.ViewModel.IsStartupLoading);
        Assert.AreEqual(
            "无法读取本地登录状态，请重新登录或切换服务器。",
            context.LoginViewModel.ErrorMessage);
        Assert.IsTrue(context.Diagnostics.Entries.Contains(
            "category=startup event=failure exceptionType=UnauthorizedAccessException"));
        Assert.IsTrue(context.Diagnostics.Entries.Contains(
            "category=startup event=route targetPageCategory=login"));
    }

    [TestMethod]
    public async Task InitializeAsync_NetworkValidationFailure_DoesNotClearAuthSessionAndShowsLoginError()
    {
        var context = CreateContext();
        var session = CreateSession();
        context.AppSettingsService.LastServerBase = session.ServerBase;
        await context.AuthSessionStore.SaveAsync(session, CancellationToken.None);
        context.AuthSessionValidator.ValidateAsyncHandler = (_, _) =>
            Task.FromResult(AuthSessionValidationResult.NetworkUnavailable());

        await context.ViewModel.InitializeAsync(CancellationToken.None);

        Assert.AreEqual(AppPage.Login, context.ViewModel.CurrentPage);
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(session, context.CurrentSessionService.CurrentSession);
        Assert.IsTrue(context.LoginViewModel.HasError);
        StringAssert.Contains(context.LoginViewModel.ErrorMessage!, "暂时无法验证登录状态");
    }

    [TestMethod]
    public async Task InitializeAsync_WhenCalledTwice_DoesNotRepeatStartupRouting()
    {
        var context = CreateContext();
        var session = CreateSession();
        context.AppSettingsService.LastServerBase = session.ServerBase;
        await context.AuthSessionStore.SaveAsync(session, CancellationToken.None);

        await context.ViewModel.InitializeAsync(CancellationToken.None);
        await context.ViewModel.InitializeAsync(CancellationToken.None);

        Assert.AreEqual(AppPage.Home, context.ViewModel.CurrentPage);
        Assert.AreEqual(1, context.AuthSessionStore.LoadCallCount);
        Assert.AreEqual(1, context.AuthSessionValidator.ValidateCallCount);
    }

    [TestMethod]
    public async Task LogoutCommand_ClearsAuthSessionButKeepsLastServerBaseAndDeviceId()
    {
        var context = CreateContext();
        const string lastServerBase = "http://media.local:8096";
        const string deviceId = "stable-device-id";
        context.AppSettingsService.LastServerBase = lastServerBase;
        context.AppSettingsService.DeviceId = deviceId;
        context.CurrentSessionService.SetSession(CreateSession());

        await ((AsyncRelayCommand)context.ViewModel.LogoutCommand).ExecuteAsync();

        Assert.AreEqual(AppPage.Login, context.ViewModel.CurrentPage);
        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.IsNull(context.CurrentSessionService.CurrentSession);
        Assert.AreEqual(lastServerBase, context.AppSettingsService.LastServerBase);
        Assert.AreEqual(deviceId, context.AppSettingsService.DeviceId);
    }

    [TestMethod]
    public async Task LogoutCommand_FailureKeepsRuntimeSessionAndCurrentPage()
    {
        var context = CreateContext();
        var session = CreateSession();
        context.CurrentSessionService.SetSession(session);
        context.AuthSessionStore.ClearAsyncHandler = _ => throw new IOException("credential failure");
        context.ViewModel.NavigateTo(AppPage.Home);

        await ((AsyncRelayCommand)context.ViewModel.LogoutCommand).ExecuteAsync();

        Assert.AreEqual(AppPage.Home, context.ViewModel.CurrentPage);
        Assert.AreSame(session, context.CurrentSessionService.CurrentSession);
        Assert.AreEqual("退出登录失败，请稍后重试。", context.ViewModel.ShellErrorMessage);
    }

    [TestMethod]
    public async Task LogoutCommand_PartialAuthenticationCleanupClearsRuntimeAndNavigatesWithWarning()
    {
        var context = CreateContext();
        context.CurrentSessionService.SetSession(CreateSession());
        context.AuthSessionStore.ClearAsyncHandler = _ =>
            throw new AuthSessionClearPartialFailureException(new IOException("metadata failure"));
        context.ViewModel.NavigateTo(AppPage.Home);

        await ((AsyncRelayCommand)context.ViewModel.LogoutCommand).ExecuteAsync();

        Assert.AreEqual(AppPage.Login, context.ViewModel.CurrentPage);
        Assert.IsNull(context.CurrentSessionService.CurrentSession);
        Assert.AreEqual(
            "安全令牌已清除，但本地账户信息未能完全清理，请稍后重试。",
            context.ViewModel.ShellErrorMessage);
        Assert.AreEqual(context.ViewModel.ShellErrorMessage, context.LoginViewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task MainWindow_LoadedHandlerAwaitsShellInitialization()
    {
        var mainWindowPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "EmbyPlayer.App",
            "MainWindow.xaml.cs");
        var source = await File.ReadAllTextAsync(mainWindowPath);

        StringAssert.Contains(source, "private async void OnLoaded(object sender, RoutedEventArgs e)");
        StringAssert.Contains(source, "await appShellViewModel.InitializeAsync(CancellationToken.None);");
        StringAssert.Contains(source, "catch (Exception exception)");
        StringAssert.Contains(source, "appShellViewModel.RecoverFromStartupFailure(exception);");
        Assert.IsFalse(source.Contains(
            "_ = appShellViewModel.InitializeAsync",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ServerConnectionNavigation_ReinitializesAndClearsStaleAddressAndError()
    {
        var context = CreateContext();
        context.ServerConnectionViewModel.ServerUrl = "unreachable.local";
        await context.ServerConnectionViewModel.ConnectAsync();
        Assert.IsTrue(context.ServerConnectionViewModel.HasError);
        context.AppSettingsService.LastServerBase = null;

        context.ViewModel.NavigateTo(AppPage.Login);
        context.ViewModel.NavigateTo(AppPage.ServerConnection);
        await WaitForAsync(() => string.IsNullOrEmpty(context.ServerConnectionViewModel.ServerUrl)
            && !context.ServerConnectionViewModel.HasError);

        Assert.AreEqual(AppPage.ServerConnection, context.ViewModel.CurrentPage);
    }

    [TestMethod]
    public async Task ServerConnectionNavigation_PartialSwitchFailureShowsWarningAndRetainedAddress()
    {
        var context = CreateContext();
        context.AppSettingsService.LastServerBase = "http://media.local:8096";
        var parameter = new ServerConnectionNavigationParameter(
            ServerConnectionNavigationParameter.SwitchServerPartialFailureMessage);

        context.NavigationService.NavigateTo(AppPage.Login);
        context.NavigationService.NavigateTo(AppPage.ServerConnection, parameter);
        await WaitForAsync(() =>
            context.ServerConnectionViewModel.ServerUrl == "http://media.local:8096"
            && context.ServerConnectionViewModel.ErrorMessage == parameter.Message);

        Assert.AreEqual(AppPage.ServerConnection, context.ViewModel.CurrentPage);
        Assert.IsTrue(context.ServerConnectionViewModel.CanConnect);
    }

    private static AppShellViewModel CreateViewModelOnPage(AppPage page)
    {
        var context = CreateContext();
        if (page is AppPage.Home or AppPage.Library or AppPage.Search or AppPage.Detail)
        {
            context.CurrentSessionService.SetSession(CreateSession());
        }

        context.ViewModel.NavigateTo(page);

        return context.ViewModel;
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

    private static AppShellViewModel CreateViewModel(NavigationService? navigationService = null)
    {
        return CreateContext(navigationService).ViewModel;
    }

    private static AppShellTestContext CreateContext(NavigationService? navigationService = null)
    {
        return new AppShellTestContext(navigationService);
    }

    private static AuthSession CreateSession()
    {
        return new AuthSession(
            "http://media.local:8096",
            "test-access-token",
            "user-1",
            "Test User",
            "server-1");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            if (timeout.IsCancellationRequested)
            {
                Assert.Fail("Timed out waiting for asynchronous navigation work.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class AppShellTestContext
    {
        public AppShellTestContext(NavigationService? navigationService = null)
        {
            NavigationService = navigationService ?? new NavigationService();
            AppSettingsService = new TestAppSettingsService();
            AuthSessionStore = new TestAuthSessionStore();
            AuthSessionValidator = new TestAuthSessionValidator();
            Diagnostics = new RecordingApplicationDiagnostics();
            CurrentSessionService = new CurrentSessionService();
            AccountSessionService = new AccountSessionService(
                AuthSessionStore,
                CurrentSessionService,
                AppSettingsService);
            MediaLibraryScanService = new TestMediaLibraryScanService();
            ServerConnectionViewModel = new ServerConnectionViewModel(
                NavigationService,
                new TestServerConnectionService(),
                AppSettingsService);
            LoginViewModel = new LoginViewModel(
                NavigationService,
                new TestAuthenticationService(),
                CurrentSessionService,
                AuthSessionStore,
                AppSettingsService,
                AccountSessionService);
            HomeService = new TestHomeService();
            HomeViewModel = new HomeViewModel(
                NavigationService,
                HomeService,
                new TestPlaybackService(),
                CurrentSessionService,
                AuthSessionStore,
                LoginViewModel.ShowError);
            LibraryService = new TestLibraryService();
            LibraryViewModel = new LibraryViewModel(
                NavigationService,
                LibraryService,
                CurrentSessionService,
                AuthSessionStore,
                LoginViewModel.ShowError);
            SearchService = new TestSearchService();
            LocalMediaSearchIndex = new TestLocalMediaSearchIndex();
            SearchViewModel = new SearchViewModel(
                NavigationService,
                SearchService,
                CurrentSessionService,
                AuthSessionStore,
                AppSettingsService,
                LoginViewModel.ShowError);
            MediaDetailService = new TestMediaDetailService();
            SimilarMediaService = new TestSimilarMediaService();
            ItemUserDataService = new TestItemUserDataService();
            SeriesService = new TestSeriesService();
            PlaybackService = new TestPlaybackService();
            MediaDetailViewModel = new MediaDetailViewModel(
                NavigationService,
                MediaDetailService,
                SimilarMediaService,
                ItemUserDataService,
                SeriesService,
                PlaybackService,
                CurrentSessionService,
                AuthSessionStore,
                LoginViewModel.ShowError);
            PlayerService = new TestPlayerService();
            PlayerViewModel = new PlayerViewModel(NavigationService, PlayerService);
            ViewModel = new AppShellViewModel(
                NavigationService,
                ServerConnectionViewModel,
                LoginViewModel,
                HomeViewModel,
                LibraryViewModel,
                SearchViewModel,
                MediaDetailViewModel,
                PlayerViewModel,
                AppSettingsService,
                AuthSessionStore,
                AuthSessionValidator,
                CurrentSessionService,
                AccountSessionService,
                MediaLibraryScanService,
                LocalMediaSearchIndex,
                Diagnostics);
        }

        public NavigationService NavigationService { get; }

        public TestAppSettingsService AppSettingsService { get; }

        public TestAuthSessionStore AuthSessionStore { get; }

        public TestAuthSessionValidator AuthSessionValidator { get; }

        public RecordingApplicationDiagnostics Diagnostics { get; }

        public CurrentSessionService CurrentSessionService { get; }

        public AccountSessionService AccountSessionService { get; }

        public TestMediaLibraryScanService MediaLibraryScanService { get; }

        public ServerConnectionViewModel ServerConnectionViewModel { get; }

        public LoginViewModel LoginViewModel { get; }

        public HomeViewModel HomeViewModel { get; }

        public TestHomeService HomeService { get; }

        public TestLibraryService LibraryService { get; }

        public LibraryViewModel LibraryViewModel { get; }

        public TestSearchService SearchService { get; }

        public TestLocalMediaSearchIndex LocalMediaSearchIndex { get; }

        public SearchViewModel SearchViewModel { get; }

        public TestMediaDetailService MediaDetailService { get; }

        public TestSimilarMediaService SimilarMediaService { get; }

        public TestItemUserDataService ItemUserDataService { get; }

        public TestSeriesService SeriesService { get; }

        public TestPlaybackService PlaybackService { get; }

        public TestPlayerService PlayerService { get; }

        public MediaDetailViewModel MediaDetailViewModel { get; }

        public PlayerViewModel PlayerViewModel { get; }

        public AppShellViewModel ViewModel { get; }
    }

    private sealed class RecordingApplicationDiagnostics : IApplicationDiagnostics
    {
        public List<string> Entries { get; } = new();

        public void Write(string category, string eventName, string? details = null)
        {
            Entries.Add($"category={category} event={eventName} {details}");
        }

        public Task<bool> FlushAsync(TimeSpan timeout) => Task.FromResult(true);
    }

    private static PlaybackInfo CreatePlaybackInfo()
    {
        return new PlaybackInfo(
            "item-1",
            "Playback Item",
            "play-session-1",
            new PlaybackMediaSource(
                "source-1",
                "mkv",
                SupportsDirectPlay: true,
                SupportsDirectStream: true,
                SupportsTranscoding: false,
                new Dictionary<string, string>()),
            "http://media.local:8096/Videos/item-1/stream.mkv",
            RequiresTranscoding: false,
            RequiresTokenInUrl: false,
            TimeSpan.FromMinutes(90).Ticks,
            0,
            Array.Empty<PlaybackTrack>(),
            Array.Empty<PlaybackSubtitle>());
    }
}
