using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Diagnostics;
using EmbyPlayer.Core.Search;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class SearchViewModelTests
{
    [TestMethod]
    public async Task SearchAsync_EmptyKeyword_DoesNotCallApiAndShowsPrompt()
    {
        var context = CreateContext();

        await context.ViewModel.SearchAsync("   ");

        Assert.AreEqual(0, context.SearchService.SearchCallCount);
        Assert.IsTrue(context.ViewModel.IsEmptyKeywordVisible);
        Assert.IsFalse(context.ViewModel.IsNoResultsVisible);
    }

    [TestMethod]
    public async Task SearchAsync_SuccessDisplaysResults()
    {
        var context = CreateContext();
        var changedProperties = new List<string?>();
        context.ViewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        await context.ViewModel.SearchAsync("one piece");

        Assert.AreEqual(1, context.SearchService.SearchCallCount);
        Assert.AreEqual("one piece", context.SearchService.LastKeyword);
        Assert.AreEqual(1, context.ViewModel.Results.Count);
        Assert.AreEqual("One Piece", context.ViewModel.Results[0].Title);
        Assert.AreEqual("电影", context.ViewModel.Results[0].Type);
        Assert.IsTrue(context.ViewModel.IsResultsVisible);
        CollectionAssert.Contains(changedProperties, nameof(SearchViewModel.Results));
    }

    [TestMethod]
    public async Task SearchAsync_ClientFilterLimitsDisplayedResults()
    {
        var context = CreateContext();
        context.SearchService.SearchAsyncHandler = (_, _, _) =>
            Task.FromResult(SearchLoadResult.Success(new[]
            {
                new SearchResultItem("movie-1", "Movie", "Movie", 2024, null, null),
                new SearchResultItem("series-1", "Series", "Series", 2024, null, null),
                new SearchResultItem("episode-1", "Episode", "Episode", 2024, null, null),
                new SearchResultItem("playlist-1", "Playlist", "Playlist", null, null, null)
            }));

        await context.ViewModel.SearchAsync("test");
        context.ViewModel.SelectFilterCommand.Execute(context.ViewModel.Filters.Single(filter => filter.Key == "series"));

        Assert.AreEqual(1, context.ViewModel.Results.Count);
        Assert.AreEqual("Series", context.ViewModel.Results[0].Title);
        Assert.AreEqual("1 个结果", context.ViewModel.ResultCountText);
    }

    [TestMethod]
    public async Task SearchAsync_VisibleFiltersOnlyContainTypesPresentInResults()
    {
        var context = CreateContext();
        context.SearchService.SearchAsyncHandler = (_, _, _) =>
            Task.FromResult(SearchLoadResult.Success(new[]
            {
                new SearchResultItem("movie-1", "Movie", "Movie", 2024, null, null),
                new SearchResultItem("series-1", "Series", "Series", 2024, null, null)
            }));

        await context.ViewModel.SearchAsync("test");

        CollectionAssert.AreEqual(
            new[] { "all", "movie", "series" },
            context.ViewModel.VisibleFilters.Select(filter => filter.Key).ToArray());
    }

    [TestMethod]
    public async Task SearchAsync_SelectingAllRestoresEveryResult()
    {
        var context = CreateContext();
        context.SearchService.SearchAsyncHandler = (_, _, _) =>
            Task.FromResult(SearchLoadResult.Success(new[]
            {
                new SearchResultItem("movie-1", "Movie", "Movie", 2024, null, null),
                new SearchResultItem("series-1", "Series", "Series", 2024, null, null)
            }));

        await context.ViewModel.SearchAsync("test");
        context.ViewModel.SelectFilterCommand.Execute(context.ViewModel.Filters.Single(filter => filter.Key == "movie"));
        context.ViewModel.SelectFilterCommand.Execute(context.ViewModel.Filters.Single(filter => filter.Key == "all"));

        Assert.AreEqual(2, context.ViewModel.Results.Count);
        Assert.AreEqual("2 个结果", context.ViewModel.ResultCountText);
    }

    [TestMethod]
    public async Task SearchAsync_AllCollapsesSeriesNameOnlyEpisodesButEpisodeFilterKeepsThem()
    {
        var context = CreateContext();
        context.SearchService.SearchAsyncHandler = (_, _, _) =>
            Task.FromResult(SearchLoadResult.Success(new[]
            {
                CreateSearchResult("series-1", "Agent Series", "Series"),
                CreateSearchResult(
                    "episode-1",
                    "Agent Series S1:E1",
                    "Episode",
                    SearchMatchSource.SeriesName,
                    "Agent Series"),
                CreateSearchResult(
                    "episode-2",
                    "Agent Series S1:E2",
                    "Episode",
                    SearchMatchSource.SeriesName,
                    "Agent Series")
            }));

        await context.ViewModel.SearchAsync("agent");

        Assert.AreEqual(1, context.ViewModel.Results.Count);
        Assert.AreEqual("series-1", context.ViewModel.Results.Single().Id);
        Assert.AreEqual(1, context.ViewModel.Filters.Single(filter => filter.Key == "all").Count);
        Assert.AreEqual(1, context.ViewModel.Filters.Single(filter => filter.Key == "series").Count);
        Assert.AreEqual(2, context.ViewModel.Filters.Single(filter => filter.Key == "episode").Count);

        context.ViewModel.SelectFilterCommand.Execute(
            context.ViewModel.Filters.Single(filter => filter.Key == "episode"));

        CollectionAssert.AreEqual(
            new[] { "episode-1", "episode-2" },
            context.ViewModel.Results.Select(item => item.Id).ToArray());
        Assert.AreEqual("2 个结果", context.ViewModel.ResultCountText);
        Assert.AreEqual(1, context.SearchService.SearchCallCount);
    }

    [DataTestMethod]
    [DataRow(SearchMatchSource.Name)]
    [DataRow(SearchMatchSource.OriginalTitle)]
    [DataRow(SearchMatchSource.SortName)]
    public async Task SearchAsync_AllKeepsEpisodeWithDirectTitleMatch(SearchMatchSource matchSource)
    {
        var context = CreateContext();
        context.SearchService.SearchAsyncHandler = (_, _, _) =>
            Task.FromResult(SearchLoadResult.Success(new[]
            {
                CreateSearchResult("series-1", "Agent Series", "Series"),
                CreateSearchResult(
                    "episode-1",
                    "Red Haired Woman",
                    "Episode",
                    matchSource,
                    "Agent Series")
            }));

        await context.ViewModel.SearchAsync("red hair");

        CollectionAssert.AreEqual(
            new[] { "series-1", "episode-1" },
            context.ViewModel.Results.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public async Task SearchAsync_AllKeepsSeriesNameOnlyEpisodeWhenParentSeriesIsMissing()
    {
        var context = CreateContext();
        context.SearchService.SearchAsyncHandler = (_, _, _) =>
            Task.FromResult(SearchLoadResult.Success(new[]
            {
                CreateSearchResult(
                    "episode-1",
                    "Agent Series S1:E1",
                    "Episode",
                    SearchMatchSource.SeriesName,
                    "Agent Series")
            }));

        await context.ViewModel.SearchAsync("agent");

        Assert.AreEqual("episode-1", context.ViewModel.Results.Single().Id);
    }

    [TestMethod]
    public async Task SearchAsync_AllKeepsEpisodeWhenSeriesRelationshipCannotBeConfirmed()
    {
        var context = CreateContext();
        context.SearchService.SearchAsyncHandler = (_, _, _) =>
            Task.FromResult(SearchLoadResult.Success(new[]
            {
                CreateSearchResult("series-1", "Different Series", "Series"),
                CreateSearchResult(
                    "episode-1",
                    "Agent Series S1:E1",
                    "Episode",
                    SearchMatchSource.SeriesName,
                    "Agent Series")
            }));

        await context.ViewModel.SearchAsync("agent");

        Assert.AreEqual(2, context.ViewModel.Results.Count);
        Assert.IsTrue(context.ViewModel.Results.Any(item => item.Id == "episode-1"));
    }

    [TestMethod]
    public async Task SearchAsync_AllKeepsServerEpisodeWithoutMatchSource()
    {
        var context = CreateContext();
        context.SearchService.SearchAsyncHandler = (_, _, _) =>
            Task.FromResult(SearchLoadResult.Success(new[]
            {
                CreateSearchResult("series-1", "Agent Series", "Series"),
                CreateSearchResult("episode-1", "Agent Series S1:E1", "Episode")
            }));

        await context.ViewModel.SearchAsync("agent");

        Assert.AreEqual(2, context.ViewModel.Results.Count);
        Assert.IsTrue(context.ViewModel.Results.Any(item => item.Id == "episode-1"));
    }

    [TestMethod]
    public async Task SearchAsync_MapsPlaylistTypeToChineseCopy()
    {
        var context = CreateContext();
        context.SearchService.SearchAsyncHandler = (_, _, _) =>
            Task.FromResult(SearchLoadResult.Success(new[]
            {
                new SearchResultItem("playlist-1", "Playlist", "Playlist", null, null, null)
            }));

        await context.ViewModel.SearchAsync("playlist");

        Assert.AreEqual(1, context.ViewModel.Results.Count);
        Assert.AreEqual("\u64ad\u653e\u5217\u8868", context.ViewModel.Results[0].Type);
        Assert.IsTrue(context.ViewModel.Filters.Any(filter => filter.Key == "playlist" && filter.Label == "\u64ad\u653e\u5217\u8868"));
    }

    [TestMethod]
    public async Task SearchAsync_PlaylistFilterMatchesSingularAndPluralTypes()
    {
        var context = CreateContext();
        context.SearchService.SearchAsyncHandler = (_, _, _) =>
            Task.FromResult(SearchLoadResult.Success(new[]
            {
                new SearchResultItem("playlist-1", "Playlist One", "Playlist", null, null, null),
                new SearchResultItem("playlist-2", "Playlist Two", "Playlists", null, null, null),
                new SearchResultItem("movie-1", "Movie", "Movie", 2024, null, null)
            }));

        await context.ViewModel.SearchAsync("playlist");
        context.ViewModel.SelectFilterCommand.Execute(context.ViewModel.Filters.Single(filter => filter.Key == "playlist"));

        Assert.AreEqual(2, context.ViewModel.Results.Count);
        Assert.IsTrue(context.ViewModel.Results.All(result => result.Type == "\u64ad\u653e\u5217\u8868"));
    }

    [TestMethod]
    public async Task SearchAsync_FilterWithNoMatchesShowsNoResultsState()
    {
        var context = CreateContext();
        context.SearchService.SearchAsyncHandler = (_, _, _) =>
            Task.FromResult(SearchLoadResult.Success(new[]
            {
                new SearchResultItem("movie-1", "Movie", "Movie", 2024, null, null)
            }));

        await context.ViewModel.SearchAsync("test");
        context.ViewModel.SelectFilterCommand.Execute(context.ViewModel.Filters.Single(filter => filter.Key == "series"));

        Assert.AreEqual(0, context.ViewModel.Results.Count);
        Assert.IsTrue(context.ViewModel.IsNoResultsVisible);
        Assert.IsFalse(context.ViewModel.IsResultsVisible);
    }

    [TestMethod]
    public async Task LoadFavoritesAsync_UsesFavoritesServiceAndShowsFavoriteTitle()
    {
        var context = CreateContext();

        await context.ViewModel.LoadFavoritesAsync();

        Assert.AreEqual(0, context.SearchService.SearchCallCount);
        Assert.AreEqual(1, context.SearchService.LoadFavoritesCallCount);
        Assert.IsTrue(context.ViewModel.IsFavoritesMode);
        Assert.AreEqual("\u6211\u7684\u6536\u85cf", context.ViewModel.ResultTitle);
    }

    [TestMethod]
    public async Task LoadFavoritesAsync_MapsFavoriteBadgeState()
    {
        var context = CreateContext();
        context.SearchService.LoadFavoritesAsyncHandler = (_, _) =>
            Task.FromResult(SearchLoadResult.Success(new[]
            {
                new SearchResultItem(
                    "item-1",
                    "Favorite Movie",
                    "Movie",
                    2024,
                    null,
                    null,
                    true)
            }));

        await context.ViewModel.LoadFavoritesAsync();

        Assert.AreEqual(1, context.ViewModel.Results.Count);
        Assert.IsTrue(context.ViewModel.Results[0].IsFavorite);
    }

    [TestMethod]
    public async Task SearchAsync_MapsPlayedBadgeState()
    {
        var context = CreateContext();
        context.SearchService.SearchAsyncHandler = (_, _, _) =>
            Task.FromResult(SearchLoadResult.Success(new[]
            {
                new SearchResultItem(
                    "item-1",
                    "Played Movie",
                    "Movie",
                    2024,
                    null,
                    null,
                    IsPlayed: true)
            }));

        await context.ViewModel.SearchAsync("played");

        Assert.AreEqual(1, context.ViewModel.Results.Count);
        Assert.IsTrue(context.ViewModel.Results[0].IsPlayed);
    }

    [TestMethod]
    public async Task SearchAsync_NoResultsShowsEmptyState()
    {
        var context = CreateContext();
        context.SearchService.SearchAsyncHandler = (_, _, _) =>
            Task.FromResult(SearchLoadResult.Success(Array.Empty<SearchResultItem>()));

        await context.ViewModel.SearchAsync("missing");

        Assert.IsTrue(context.ViewModel.IsNoResultsVisible);
        Assert.IsFalse(context.ViewModel.IsResultsVisible);
    }

    [TestMethod]
    public async Task SearchAsync_FailureShowsChineseError()
    {
        var context = CreateContext();
        context.SearchService.SearchAsyncHandler = (_, _, _) =>
            Task.FromResult(SearchLoadResult.Failure(SearchLoadError.ServerUnreachable));

        await context.ViewModel.SearchAsync("movie");

        Assert.IsTrue(context.ViewModel.HasError);
        StringAssert.Contains(context.ViewModel.ErrorMessage!, "无法搜索媒体");
        Assert.IsTrue(context.ViewModel.IsErrorVisible);
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
    }

    [TestMethod]
    public async Task LoadFavoritesAsync_FailureShowsErrorWithoutClearingSession()
    {
        var context = CreateContext();
        context.SearchService.LoadFavoritesAsyncHandler = (_, _) =>
            Task.FromResult(SearchLoadResult.Failure(SearchLoadError.ServerUnreachable));

        await context.ViewModel.LoadFavoritesAsync();

        Assert.IsTrue(context.ViewModel.HasError);
        StringAssert.Contains(context.ViewModel.ErrorMessage!, "\u65e0\u6cd5\u641c\u7d22\u5a92\u4f53");
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
    }

    [TestMethod]
    public async Task LoadFavoritesAsync_UnexpectedFailureShowsRetryableErrorAndWritesSafeDiagnostic()
    {
        var context = CreateContext();
        const string storageDetails = "settings path contains private data";
        context.SearchService.LoadFavoritesAsyncHandler = (_, _) =>
            Task.FromException<SearchLoadResult>(new IOException(storageDetails));

        await context.ViewModel.LoadFavoritesAsync();

        Assert.AreEqual("收藏加载失败，请稍后重试", context.ViewModel.ErrorMessage);
        Assert.IsTrue(context.ViewModel.IsErrorVisible);
        Assert.IsFalse(context.ViewModel.IsLoading);
        Assert.IsTrue(context.ViewModel.RetryCommand.CanExecute(null));
        Assert.IsTrue(context.Diagnostics.Events.Any(entry =>
            entry.Category == "search"
            && entry.EventName == "failed"
            && entry.Details == "operation=favorites exceptionType=IOException"));
        Assert.IsFalse(context.Diagnostics.Events.Any(entry =>
            entry.Details?.Contains(storageDetails, StringComparison.Ordinal) == true));
    }

    [TestMethod]
    public async Task RetryCommand_AfterUnexpectedFavoritesFailureLoadsFavoritesAgain()
    {
        var context = CreateContext();
        context.SearchService.LoadFavoritesAsyncHandler = (_, _) =>
            Task.FromException<SearchLoadResult>(new IOException("settings unavailable"));
        await context.ViewModel.LoadFavoritesAsync();
        context.SearchService.LoadFavoritesAsyncHandler = (_, _) =>
            Task.FromResult(CreateSuccessResult());

        await ((AsyncRelayCommand)context.ViewModel.RetryCommand).ExecuteAsync();

        Assert.AreEqual(2, context.SearchService.LoadFavoritesCallCount);
        Assert.IsTrue(context.ViewModel.IsFavoritesMode);
        Assert.IsFalse(context.ViewModel.HasError);
        Assert.AreEqual(1, context.ViewModel.Results.Count);
    }

    [TestMethod]
    public async Task LoadFavoritesAsync_CancellationRemainsSilent()
    {
        var context = CreateContext();
        context.SearchService.LoadFavoritesAsyncHandler = async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateSuccessResult();
        };

        var loadTask = context.ViewModel.LoadFavoritesAsync();
        await WaitForAsync(() => context.ViewModel.IsLoading);
        context.ViewModel.Deactivate();
        await loadTask;

        Assert.IsFalse(context.ViewModel.HasError);
        Assert.IsFalse(context.ViewModel.IsLoading);
        Assert.AreEqual(0, context.Diagnostics.Events.Count);
    }

    [TestMethod]
    public async Task RetryCommand_RunsSearchAgain()
    {
        var context = CreateContext();
        context.SearchService.SearchAsyncHandler = (_, _, _) =>
            Task.FromResult(SearchLoadResult.Failure(SearchLoadError.ServerUnreachable));
        await context.ViewModel.SearchAsync("movie");
        context.SearchService.SearchAsyncHandler = (_, _, _) =>
            Task.FromResult(CreateSuccessResult());

        await ((AsyncRelayCommand)context.ViewModel.RetryCommand).ExecuteAsync();

        Assert.AreEqual(2, context.SearchService.SearchCallCount);
        Assert.IsFalse(context.ViewModel.HasError);
        Assert.AreEqual(1, context.ViewModel.Results.Count);
    }

    [TestMethod]
    public async Task OpenResultCommand_NavigatesToDetail()
    {
        var context = CreateContext();
        await context.ViewModel.SearchAsync("one");
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;

        context.ViewModel.OpenResultCommand.Execute(context.ViewModel.Results[0]);

        Assert.AreEqual(AppPage.Detail, context.NavigationService.CurrentPage);
        Assert.AreEqual("item-1", navigationParameter);
    }

    [TestMethod]
    public void NavigateHomeCommand_NavigatesHome()
    {
        var context = CreateContext();

        context.ViewModel.NavigateHomeCommand.Execute(null);

        Assert.AreEqual(AppPage.Home, context.NavigationService.CurrentPage);
    }

    [TestMethod]
    public async Task SearchAsync_UnauthorizedClearsSessionAndNavigatesLogin()
    {
        var context = CreateContext();
        context.SearchService.SearchAsyncHandler = (_, _, _) =>
            Task.FromResult(SearchLoadResult.Failure(SearchLoadError.Unauthorized));

        await context.ViewModel.SearchAsync("movie");

        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.IsNull(context.CurrentSessionService.CurrentSession);
        Assert.AreEqual(AppPage.Login, context.NavigationService.CurrentPage);
        StringAssert.Contains(context.LoginErrorMessage!, "登录状态已失效");
    }

    [TestMethod]
    public async Task SearchAsync_ForbiddenKeepsSessionAndShowsPermissionError()
    {
        var context = CreateContext();
        context.SearchService.SearchAsyncHandler = (_, _, _) =>
            Task.FromResult(SearchLoadResult.Failure(SearchLoadError.Forbidden));

        await context.ViewModel.SearchAsync("movie");

        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
        Assert.AreNotEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual("没有权限搜索或查看此内容", context.ViewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task Keyword_WithFewerThanTwoCharacters_DoesNotSearch()
    {
        var context = CreateContext();

        context.ViewModel.Keyword = "a";
        await Task.Delay(450);

        Assert.AreEqual(0, context.SearchService.SearchCallCount);
        Assert.IsTrue(context.ViewModel.IsInitialState);
    }

    [TestMethod]
    public async Task Keyword_TwoChineseCharacters_AreAllowedToSearch()
    {
        var context = CreateContext();

        await context.ViewModel.SearchAsync("特务");

        Assert.AreEqual(1, context.SearchService.SearchCallCount);
        Assert.AreEqual("特务", context.SearchService.LastKeyword);
    }

    [TestMethod]
    public async Task Keyword_DebouncesRapidInput_AndSearchesStableValueOnce()
    {
        var context = CreateContext();

        context.ViewModel.Keyword = "海";
        await Task.Delay(100);
        context.ViewModel.Keyword = "海贼";
        await Task.Delay(100);
        context.ViewModel.Keyword = "海贼王";
        await WaitForAsync(() => context.SearchService.SearchCallCount == 1);

        Assert.AreEqual(1, context.SearchService.SearchCallCount);
        Assert.AreEqual("海贼王", context.SearchService.LastKeyword);
    }

    [TestMethod]
    public async Task SearchNowAsync_BypassesPendingDebounce()
    {
        var context = CreateContext();
        context.ViewModel.Keyword = "movie";

        await context.ViewModel.SearchNowAsync();
        await Task.Delay(450);

        Assert.AreEqual(1, context.SearchService.SearchCallCount);
    }

    [TestMethod]
    public async Task SearchAsync_OlderLateResponse_DoesNotReplaceNewerResults()
    {
        var context = CreateContext();
        var olderResult = new TaskCompletionSource<SearchLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newerResult = new TaskCompletionSource<SearchLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.SearchService.SearchAsyncHandler = (_, keyword, _) =>
            keyword == "older" ? olderResult.Task : newerResult.Task;

        var olderSearch = context.ViewModel.SearchAsync("older");
        await WaitForAsync(() => context.SearchService.SearchCallCount == 1);
        var newerSearch = context.ViewModel.SearchAsync("newer");
        await WaitForAsync(() => context.SearchService.SearchCallCount == 2);

        newerResult.SetResult(SearchLoadResult.Success(new[]
        {
            new SearchResultItem("new", "New Result", "Movie", 2025, null, null)
        }));
        await newerSearch;
        olderResult.SetResult(SearchLoadResult.Success(new[]
        {
            new SearchResultItem("old", "Old Result", "Movie", 2020, null, null)
        }));
        await olderSearch;

        Assert.AreEqual("New Result", context.ViewModel.Results.Single().Title);
    }

    [TestMethod]
    public async Task ClearSearch_CancelsActiveRequestAndReturnsToInitialState()
    {
        var context = CreateContext();
        var pendingResult = new TaskCompletionSource<SearchLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.SearchService.SearchAsyncHandler = (_, _, _) => pendingResult.Task;
        var search = context.ViewModel.SearchAsync("movie");
        await WaitForAsync(() => context.SearchService.SearchCallCount == 1);

        context.ViewModel.ClearSearch();

        Assert.IsTrue(context.SearchService.LastSearchCancellationToken.IsCancellationRequested);
        Assert.AreEqual(string.Empty, context.ViewModel.Keyword);
        Assert.IsTrue(context.ViewModel.IsInitialState);
        pendingResult.SetResult(CreateSuccessResult());
        await search;
        Assert.AreEqual(0, context.ViewModel.Results.Count);
    }

    [TestMethod]
    public async Task SearchAsync_SameKeyword_DoesNotRepeatRequest()
    {
        var context = CreateContext();

        await context.ViewModel.SearchAsync("movie");
        await context.ViewModel.SearchAsync(" MOVIE ");

        Assert.AreEqual(1, context.SearchService.SearchCallCount);
    }

    [TestMethod]
    public async Task SearchHistory_KeepsEightNewestEntries()
    {
        var context = CreateContext();

        for (var index = 0; index < 9; index++)
        {
            await context.ViewModel.SearchAsync($"query-{index}");
        }

        Assert.AreEqual(8, context.ViewModel.SearchHistory.Count);
        Assert.AreEqual("query-8", context.ViewModel.SearchHistory[0]);
        Assert.IsFalse(context.ViewModel.SearchHistory.Contains("query-0"));
    }

    [TestMethod]
    public async Task SearchHistory_DeduplicatesIgnoringCaseAndMovesEntryToFront()
    {
        var context = CreateContext();
        context.AppSettingsService.SearchHistory = new[] { "Other", "Movie" };
        await context.ViewModel.LoadAsync(null);

        await context.ViewModel.SearchAsync("movie");

        Assert.AreEqual(2, context.ViewModel.SearchHistory.Count);
        Assert.AreEqual("movie", context.ViewModel.SearchHistory[0]);
        Assert.AreEqual(1, context.ViewModel.SearchHistory.Count(value =>
            string.Equals(value, "movie", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task SearchHistory_DeleteOneAndClearAll_DoNotSearch()
    {
        var context = CreateContext();
        context.AppSettingsService.SearchHistory = new[] { "Movie", "Series" };
        await context.ViewModel.LoadAsync(null);

        context.ViewModel.RemoveHistoryCommand.Execute("Movie");
        await WaitForAsync(() => context.ViewModel.SearchHistory.Count == 1);
        Assert.AreEqual(0, context.SearchService.SearchCallCount);

        context.ViewModel.ClearHistoryCommand.Execute(null);
        await WaitForAsync(() => context.ViewModel.SearchHistory.Count == 0);
        Assert.AreEqual(0, context.SearchService.SearchCallCount);
    }

    [TestMethod]
    public async Task DeactivateAndReturn_PreserveKeywordResultsFilterAndScrollOffset()
    {
        var context = CreateContext();
        context.SearchService.SearchAsyncHandler = (_, _, _) =>
            Task.FromResult(SearchLoadResult.Success(new[]
            {
                CreateSearchResult("series-1", "Agent Series", "Series"),
                CreateSearchResult("episode-1", "Episode One", "Episode")
            }));
        await context.ViewModel.SearchAsync("agent");
        context.ViewModel.SelectFilterCommand.Execute(
            context.ViewModel.Filters.Single(filter => filter.Key == "episode"));
        context.ViewModel.ScrollOffset = 240;

        context.ViewModel.Deactivate();
        await context.ViewModel.LoadAsync(null);

        Assert.AreEqual("agent", context.ViewModel.Keyword);
        Assert.AreEqual("episode-1", context.ViewModel.Results.Single().Id);
        Assert.IsTrue(context.ViewModel.Filters.Single(filter => filter.Key == "episode").IsSelected);
        Assert.AreEqual(240, context.ViewModel.ScrollOffset);
        Assert.AreEqual(1, context.SearchService.SearchCallCount);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10);
        }
    }

    private static SearchViewModelTestContext CreateContext()
    {
        return new SearchViewModelTestContext();
    }

    private static SearchLoadResult CreateSuccessResult()
    {
        return SearchLoadResult.Success(new[]
        {
            new SearchResultItem(
                "item-1",
                "One Piece",
                "Movie",
                2024,
                "http://media.local:8096/Items/item-1/Images/Primary",
                25,
                true)
        });
    }

    private static SearchResultItem CreateSearchResult(
        string id,
        string title,
        string type,
        SearchMatchSource source = SearchMatchSource.Unknown,
        string? seriesName = null)
    {
        return new SearchResultItem(
            id,
            title,
            type,
            2025,
            null,
            null,
            MatchInfo: source == SearchMatchSource.Unknown
                ? null
                : new SearchMatchInfo(source, seriesName));
    }

    private sealed class SearchViewModelTestContext
    {
        public SearchViewModelTestContext()
        {
            NavigationService = new NavigationService();
            SearchService = new TestSearchService();
            CurrentSessionService = new CurrentSessionService();
            AuthSessionStore = new TestAuthSessionStore();
            AppSettingsService = new TestAppSettingsService();
            Diagnostics = new RecordingApplicationDiagnostics();
            Session = new AuthSession(
                "http://media.local:8096",
                "test-access-token",
                "user-1",
                "Test User",
                "server-1");
            CurrentSessionService.SetSession(Session);
            SearchService.SearchAsyncHandler = (_, _, _) =>
                Task.FromResult(CreateSuccessResult());
            ViewModel = new SearchViewModel(
                NavigationService,
                SearchService,
                CurrentSessionService,
                AuthSessionStore,
                AppSettingsService,
                message => LoginErrorMessage = message,
                Diagnostics);
        }

        public NavigationService NavigationService { get; }

        public TestSearchService SearchService { get; }

        public CurrentSessionService CurrentSessionService { get; }

        public TestAuthSessionStore AuthSessionStore { get; }

        public TestAppSettingsService AppSettingsService { get; }

        public RecordingApplicationDiagnostics Diagnostics { get; }

        public AuthSession Session { get; }

        public string? LoginErrorMessage { get; private set; }

        public SearchViewModel ViewModel { get; }
    }

    private sealed class RecordingApplicationDiagnostics : IApplicationDiagnostics
    {
        public List<DiagnosticEvent> Events { get; } = new();

        public void Write(string category, string eventName, string? details = null)
        {
            Events.Add(new DiagnosticEvent(category, eventName, details));
        }

        public Task<bool> FlushAsync(TimeSpan timeout) => Task.FromResult(true);

        public sealed record DiagnosticEvent(string Category, string EventName, string? Details);
    }
}
