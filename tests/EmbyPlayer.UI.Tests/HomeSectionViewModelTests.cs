using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Home;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class HomeSectionViewModelTests
{
    [DataTestMethod]
    [DataRow(HomeSectionKind.ContinueWatching, "继续观看")]
    [DataRow(HomeSectionKind.RecentlyAdded, "最近添加")]
    [DataRow(HomeSectionKind.Movies, "电影")]
    [DataRow(HomeSectionKind.Series, "电视节目")]
    [DataRow(HomeSectionKind.Animation, "动画")]
    [DataRow(HomeSectionKind.BoxSets, "合集")]
    public async Task LoadAsync_UsesRequestedSectionAndShowsItsTitle(HomeSectionKind section, string title)
    {
        var context = new Context();
        await context.ViewModel.LoadAsync(section);

        Assert.AreEqual(section, context.Service.LastSection);
        Assert.AreEqual(title, context.ViewModel.Title);
        Assert.AreEqual(0, context.Service.LastSectionStartIndex);
        Assert.AreEqual(48, context.Service.LastSectionLimit);
        Assert.IsTrue(context.ViewModel.IsEmpty);
        Assert.IsFalse(context.ViewModel.IsInitialLoading);
    }

    [TestMethod]
    public async Task LoadMoreAsync_BrowsesBeyondTheHomePreviewAndUsesServerCursor()
    {
        var context = new Context();
        context.Service.LoadSectionAsyncHandler = (_, _, start, _, _) => Task.FromResult(start == 0
            ? HomeSectionItemsLoadResult.Success(Cards(0, 48), 60, true)
            : HomeSectionItemsLoadResult.Success(Cards(47, 14), 74, false));

        await context.ViewModel.LoadAsync(HomeSectionKind.Movies);
        Assert.AreEqual(48, context.ViewModel.Items.Count);
        var firstItem = context.ViewModel.Items[0];
        await context.ViewModel.LoadMoreAsync();

        Assert.AreEqual(60, context.Service.LastSectionStartIndex);
        Assert.AreEqual(61, context.ViewModel.Items.Count);
        Assert.AreSame(firstItem, context.ViewModel.Items[0]);
        Assert.AreEqual("item-60", context.ViewModel.Items[^1].Id);
        Assert.IsFalse(context.ViewModel.HasMore);
        Assert.IsFalse(context.ViewModel.LoadMoreCommand.CanExecute(null));
        await context.ViewModel.LoadMoreAsync();
        Assert.AreEqual(2, context.Service.LoadSectionCallCount);
    }

    [TestMethod]
    public async Task LoadMoreAsync_PendingRequestDisablesDuplicatesAndRetainsCurrentCards()
    {
        var context = new Context();
        var pending = new TaskCompletionSource<HomeSectionItemsLoadResult>();
        context.Service.LoadSectionAsyncHandler = (_, _, start, _, _) => start == 0
            ? Task.FromResult(HomeSectionItemsLoadResult.Success(Cards(0, 48), 48, true))
            : pending.Task;
        await context.ViewModel.LoadAsync(HomeSectionKind.Movies);

        var loading = context.ViewModel.LoadMoreAsync();
        await context.ViewModel.LoadMoreAsync();
        Assert.IsTrue(context.ViewModel.IsLoadingMore);
        Assert.IsFalse(context.ViewModel.RefreshCommand.CanExecute(null));
        Assert.AreEqual(48, context.ViewModel.Items.Count);
        Assert.AreEqual(2, context.Service.LoadSectionCallCount);
        pending.SetResult(HomeSectionItemsLoadResult.Success(Cards(48, 1), 49, false));
        await loading;
        Assert.IsFalse(context.ViewModel.IsLoadingMore);
        Assert.AreEqual(49, context.ViewModel.Items.Count);
    }

    [TestMethod]
    public async Task LoadMoreAsync_FailureRetriesSamePageAndPreservesExistingCards()
    {
        var context = new Context();
        var fail = true;
        context.Service.LoadSectionAsyncHandler = (_, _, start, _, _) => Task.FromResult(start == 0
            ? HomeSectionItemsLoadResult.Success(Cards(0, 48), 48, true)
            : fail ? HomeSectionItemsLoadResult.Failure(HomeLoadError.ServerTimeout)
                : HomeSectionItemsLoadResult.Success(Cards(48, 3), 51, false));
        await context.ViewModel.LoadAsync(HomeSectionKind.Movies);
        await context.ViewModel.LoadMoreAsync();

        Assert.IsTrue(context.ViewModel.IsLoadMoreErrorVisible);
        Assert.IsFalse(context.ViewModel.IsErrorVisible);
        Assert.AreEqual(48, context.ViewModel.Items.Count);
        Assert.AreEqual("重试加载更多", context.ViewModel.LoadMoreButtonText);
        fail = false;
        await ((AsyncRelayCommand)context.ViewModel.LoadMoreCommand).ExecuteAsync();

        Assert.AreEqual(48, context.Service.LastSectionStartIndex);
        Assert.AreEqual(51, context.ViewModel.Items.Count);
        Assert.IsFalse(context.ViewModel.IsLoadMoreErrorVisible);
    }

    [TestMethod]
    public async Task LoadAsync_ReturnFromDetailPreservesItemsCursorAndScrollUntilExplicitRefresh()
    {
        var context = new Context();
        context.Service.LoadSectionAsyncHandler = (_, _, start, _, _) => Task.FromResult(
            HomeSectionItemsLoadResult.Success(Cards(start, 48), start + 48, true));
        await context.ViewModel.LoadAsync(HomeSectionKind.Animation);
        await context.ViewModel.LoadMoreAsync();
        context.ViewModel.ScrollOffset = 720;
        var items = context.ViewModel.Items;
        context.ViewModel.Deactivate();
        await context.ViewModel.LoadAsync();

        Assert.AreSame(items, context.ViewModel.Items);
        Assert.AreEqual(720d, context.ViewModel.ScrollOffset);
        Assert.AreEqual(HomeSectionKind.Animation, context.ViewModel.SelectedSection);
        Assert.AreEqual(2, context.Service.LoadSectionCallCount);
        await context.ViewModel.LoadMoreAsync();
        Assert.AreEqual(96, context.Service.LastSectionStartIndex);
        await context.ViewModel.RefreshAsync();
        Assert.AreEqual(0, context.Service.LastSectionStartIndex);
        Assert.AreEqual(48, context.ViewModel.Items.Count);
        Assert.AreEqual(0d, context.ViewModel.ScrollOffset);
    }

    [TestMethod]
    public async Task RefreshAsync_PendingAndFailedRefreshKeepsSuccessfulItemsVisible()
    {
        var context = new Context();
        context.Service.LoadSectionAsyncHandler = (_, _, _, _, _) => Task.FromResult(
            HomeSectionItemsLoadResult.Success(Cards(0, 4), 4, false));
        await context.ViewModel.LoadAsync(HomeSectionKind.Movies);
        var pending = new TaskCompletionSource<HomeSectionItemsLoadResult>();
        context.Service.LoadSectionAsyncHandler = (_, _, _, _, _) => pending.Task;
        var refreshing = context.ViewModel.RefreshAsync();
        Assert.IsTrue(context.ViewModel.IsRefreshing);
        Assert.IsTrue(context.ViewModel.IsContentVisible);
        Assert.AreEqual(4, context.ViewModel.Items.Count);
        pending.SetResult(HomeSectionItemsLoadResult.Failure(HomeLoadError.Forbidden));
        await refreshing;
        Assert.IsTrue(context.ViewModel.IsRefreshErrorVisible);
        Assert.IsFalse(context.ViewModel.IsErrorVisible);
        Assert.AreEqual(4, context.ViewModel.Items.Count);
        Assert.IsNotNull(context.Sessions.CurrentSession);
    }

    [TestMethod]
    public async Task LoadAsync_RapidSectionSwitchCancelsAndRejectsLateUnauthorizedResult()
    {
        var context = new Context();
        var pending = new TaskCompletionSource<HomeSectionItemsLoadResult>();
        context.Service.LoadSectionAsyncHandler = (_, section, _, _, _) => section == HomeSectionKind.Movies
            ? pending.Task
            : Task.FromResult(HomeSectionItemsLoadResult.Success(Cards(100, 2), 2, false));
        var first = context.ViewModel.LoadAsync(HomeSectionKind.Movies);
        var firstToken = context.Service.LastSectionCancellationToken;
        Assert.IsTrue(context.ViewModel.IsInitialLoading);
        await context.ViewModel.LoadAsync(HomeSectionKind.Series);
        pending.SetResult(HomeSectionItemsLoadResult.Failure(HomeLoadError.Unauthorized));
        await first;

        Assert.IsTrue(firstToken.IsCancellationRequested);
        Assert.AreEqual(HomeSectionKind.Series, context.ViewModel.SelectedSection);
        Assert.AreEqual("item-100", context.ViewModel.Items[0].Id);
        Assert.AreEqual(0, context.AuthStore.ClearCallCount);
        Assert.IsNotNull(context.Sessions.CurrentSession);
    }

    [TestMethod]
    public async Task Deactivate_CancelsPendingPageAndDiscardsLateSuccess()
    {
        var context = new Context();
        var pending = new TaskCompletionSource<HomeSectionItemsLoadResult>();
        context.Service.LoadSectionAsyncHandler = (_, _, _, _, _) => pending.Task;
        var loading = context.ViewModel.LoadAsync(HomeSectionKind.Movies);
        context.ViewModel.Deactivate();
        pending.SetResult(HomeSectionItemsLoadResult.Success(Cards(0, 4), 4, false));
        await loading;

        Assert.IsTrue(context.Service.LastSectionCancellationToken.IsCancellationRequested);
        Assert.IsFalse(context.ViewModel.IsLoading);
        Assert.AreEqual(0, context.ViewModel.Items.Count);
    }

    [TestMethod]
    public async Task LoadAsync_NewSessionClearsPreviousCardsBeforeLoading()
    {
        var context = new Context();
        context.Service.LoadSectionAsyncHandler = (_, _, _, _, _) => Task.FromResult(
            HomeSectionItemsLoadResult.Success(Cards(0, 3), 3, false));
        await context.ViewModel.LoadAsync(HomeSectionKind.Movies);
        var pending = new TaskCompletionSource<HomeSectionItemsLoadResult>();
        var replacement = Session("user-2");
        context.Sessions.SetSession(replacement);
        context.Service.LoadSectionAsyncHandler = (_, _, _, _, _) => pending.Task;
        var loading = context.ViewModel.LoadAsync(HomeSectionKind.Movies);
        Assert.AreEqual(0, context.ViewModel.Items.Count);
        Assert.IsTrue(context.ViewModel.IsInitialLoading);
        Assert.AreSame(replacement, context.Service.LastSectionSession);
        pending.SetResult(HomeSectionItemsLoadResult.Success(Cards(100, 1), 1, false));
        await loading;
        Assert.AreEqual("item-100", context.ViewModel.Items.Single().Id);
        context.ViewModel.ResetSession();
        Assert.AreEqual(0, context.ViewModel.Items.Count);
        Assert.IsNull(context.ViewModel.SelectedSection);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LoadAsync_UnauthorizedReturnsToLoginEvenWhenPersistentCleanupFails(bool cleanupFails)
    {
        var context = new Context();
        context.Service.LoadSectionAsyncHandler = (_, _, _, _, _) => Task.FromResult(
            HomeSectionItemsLoadResult.Failure(HomeLoadError.Unauthorized));
        if (cleanupFails) context.AuthStore.ClearAsyncHandler = _ => throw new IOException("Storage unavailable");
        await context.ViewModel.LoadAsync(HomeSectionKind.Movies);
        Assert.AreEqual(1, context.AuthStore.ClearCallCount);
        Assert.IsNull(context.Sessions.CurrentSession);
        Assert.AreEqual(AppPage.Login, context.Navigation.CurrentPage);
        Assert.IsFalse(context.ViewModel.IsLoading);
    }

    [TestMethod]
    public async Task LoadAsync_SessionReplacedDuringUnauthorizedCleanupKeepsNewSession()
    {
        var context = new Context();
        var cleanup = new TaskCompletionSource();
        context.AuthStore.ClearAsyncHandler = _ => cleanup.Task;
        context.Service.LoadSectionAsyncHandler = (_, _, _, _, _) => Task.FromResult(
            HomeSectionItemsLoadResult.Failure(HomeLoadError.Unauthorized));
        var loading = context.ViewModel.LoadAsync(HomeSectionKind.Movies);
        var replacement = Session("user-2");
        context.Sessions.SetSession(replacement);
        cleanup.SetResult();
        await loading;
        Assert.AreSame(replacement, context.Sessions.CurrentSession);
        Assert.AreNotEqual(AppPage.Login, context.Navigation.CurrentPage);
    }

    [DataTestMethod]
    [DataRow("Movie", null, null, "item-1", null)]
    [DataRow("Episode", "series-1", "season-1", "series-1", "item-1")]
    [DataRow("Episode", null, null, "item-1", null)]
    public void OpenMediaCommand_PreservesSectionReturnAndEpisodeContext(
        string type, string? seriesId, string? seasonId, string targetId, string? focusedEpisodeId)
    {
        var context = new Context();
        DetailNavigationParameter? parameter = null;
        context.Navigation.CurrentPageChanged += (_, args) => parameter = args.Parameter as DetailNavigationParameter;
        context.ViewModel.OpenMediaCommand.Execute(new HomeMediaCardViewModel(new MediaCard(
            "item-1", "Item", type, 2024, null, 40, seriesId: seriesId, seasonId: seasonId)));
        Assert.IsNotNull(parameter);
        Assert.AreEqual(targetId, parameter.ItemId);
        Assert.AreEqual(AppPage.HomeSection, parameter.ReturnPage);
        Assert.AreEqual(focusedEpisodeId, parameter.FocusedEpisodeId);
        Assert.AreEqual(seasonId, parameter.SelectedSeasonId);
        Assert.AreEqual(focusedEpisodeId, parameter.FallbackItemId);
    }

    private static IReadOnlyList<MediaCard> Cards(int start, int count) => Enumerable.Range(start, count)
        .Select(index => new MediaCard($"item-{index}", $"Movie {index}", "Movie", 2024, null, index == 0 ? 35 : null))
        .ToArray();

    private static AuthSession Session(string userId) => new(
        "http://media.local", "test-token", userId, "Test User", "test-server");

    private sealed class Context
    {
        public TestHomeService Service { get; } = new();
        public CurrentSessionService Sessions { get; } = new();
        public TestAuthSessionStore AuthStore { get; } = new();
        public NavigationService Navigation { get; } = new();
        public HomeSectionViewModel ViewModel { get; }

        public Context()
        {
            Sessions.SetSession(Session("user-1"));
            ViewModel = new HomeSectionViewModel(Navigation, Service, Sessions, AuthStore, _ => { });
        }
    }
}
