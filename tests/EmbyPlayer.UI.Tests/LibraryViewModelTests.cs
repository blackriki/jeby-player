using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Library;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class LibraryViewModelTests
{
    [TestMethod]
    public async Task InitialItemsLoading_AllowsQueryChangeAndCancelsOldFirstPage()
    {
        var context = CreateContext();
        var pending = new TaskCompletionSource<LibraryItemsLoadResult>();
        var firstPageToken = CancellationToken.None;
        context.LibraryService.LoadLibraryQueryAsyncHandler = (_, _, _, _, options, token) =>
        {
            if (options.SortField == LibrarySortField.Title)
            {
                firstPageToken = token;
                return pending.Task;
            }

            return Task.FromResult(LibraryItemsLoadResult.Success(CreateItems("new-query")));
        };
        var initialLoad = context.ViewModel.LoadAsync();
        Assert.IsFalse(context.ViewModel.IsLoadingLibraries);
        Assert.IsTrue(context.ViewModel.IsInitialItemsLoading);
        Assert.IsTrue(context.ViewModel.CanChangeQuery);

        await context.ViewModel.UpdateQueryAsync(new LibraryQuery(LibrarySortField.Year));
        Assert.IsTrue(firstPageToken.IsCancellationRequested);
        pending.SetResult(LibraryItemsLoadResult.Success(CreateItems("old-query")));
        await initialLoad;

        Assert.AreEqual("new-query-item", context.ViewModel.Items.Single().Id);
        Assert.IsFalse(context.ViewModel.IsLoading);
    }

    [TestMethod]
    public async Task Query_DefaultsAndSameValueDoNotRequestAgain()
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync();

        Assert.AreEqual(new LibraryQuery(), context.LibraryService.LastQuery);
        await context.ViewModel.UpdateQueryAsync(new LibraryQuery());

        Assert.AreEqual(1, context.LibraryService.LoadLibraryItemsCallCount);
        Assert.IsFalse(context.ViewModel.ResetQueryCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task Query_ChangeResetsPageAndPaginationUsesSameOptions()
    {
        var context = CreateContext();
        context.LibraryService.LoadLibraryQueryAsyncHandler = (_, library, start, _, _, _) =>
            Task.FromResult(LibraryItemsLoadResult.Success(CreatePagedItems(library.Id, start), 250));
        await context.ViewModel.LoadAsync();
        await context.ViewModel.LoadMoreAsync();
        var selectedQuery = new LibraryQuery(LibrarySortField.Year, LibrarySortDirection.Descending,
            LibraryWatchedFilter.Unwatched, true);

        await context.ViewModel.UpdateQueryAsync(selectedQuery);

        Assert.AreEqual(0, context.LibraryService.LastStartIndex);
        Assert.AreEqual(1, context.ViewModel.Items.Count);
        Assert.AreEqual(selectedQuery, context.LibraryService.LastQuery);
        await context.ViewModel.LoadMoreAsync();
        Assert.AreEqual(100, context.LibraryService.LastStartIndex);
        Assert.AreEqual(selectedQuery, context.LibraryService.LastQuery);
        Assert.AreEqual(2, context.ViewModel.Items.Count);
    }

    [TestMethod]
    public async Task Query_FailureClearsPreviousQueryAndRetryKeepsNewOptions()
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync();
        var pending = new TaskCompletionSource<LibraryItemsLoadResult>();
        context.LibraryService.LoadLibraryQueryAsyncHandler = (_, _, _, _, _, _) => pending.Task;

        var change = context.ViewModel.UpdateQueryAsync(new LibraryQuery(FavoritesOnly: true));
        Assert.IsTrue(context.ViewModel.IsInitialItemsLoading);
        Assert.AreEqual(0, context.ViewModel.Items.Count);
        Assert.IsFalse(context.ViewModel.IsItemsContentVisible);
        Assert.IsTrue(context.ViewModel.CanChangeQuery);
        pending.SetResult(LibraryItemsLoadResult.Failure(LibraryLoadError.Forbidden));
        await change;

        Assert.IsTrue(context.ViewModel.IsContentErrorVisible);
        Assert.IsFalse(context.ViewModel.IsContentRefreshErrorVisible);
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        context.LibraryService.LoadLibraryQueryAsyncHandler = (_, _, _, _, _, _) =>
            Task.FromResult(LibraryItemsLoadResult.Success(Array.Empty<LibraryMediaItem>(), 0));
        await ((AsyncRelayCommand)context.ViewModel.RetryCommand).ExecuteAsync();
        Assert.IsTrue(context.LibraryService.LastQuery!.FavoritesOnly);
        Assert.IsTrue(context.ViewModel.IsItemsContentVisible);
        Assert.IsTrue(context.ViewModel.IsItemsEmpty);
        Assert.AreEqual("没有符合筛选条件的内容", context.ViewModel.EmptyItemsTitle);
        Assert.IsFalse(context.ViewModel.HasContentError);
    }

    [DataTestMethod]
    [DataRow(LibraryLoadError.None)]
    [DataRow(LibraryLoadError.ServerUnreachable)]
    [DataRow(LibraryLoadError.Unauthorized)]
    public async Task Query_ChangeCancelsOldRequestAndIgnoresLateResult(LibraryLoadError oldError)
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync();
        var pending = new TaskCompletionSource<LibraryItemsLoadResult>();
        var oldToken = CancellationToken.None;
        context.LibraryService.LoadLibraryQueryAsyncHandler = (_, _, _, _, options, token) =>
        {
            if (options.SortField == LibrarySortField.DateAdded)
            {
                oldToken = token;
                return pending.Task;
            }

            return Task.FromResult(LibraryItemsLoadResult.Success(CreateItems("new-query")));
        };
        var oldLoad = context.ViewModel.UpdateQueryAsync(new LibraryQuery(LibrarySortField.DateAdded));
        await context.ViewModel.UpdateQueryAsync(new LibraryQuery(LibrarySortField.Year));
        Assert.IsTrue(oldToken.IsCancellationRequested);

        pending.SetResult(oldError == LibraryLoadError.None
            ? LibraryItemsLoadResult.Success(CreateItems("old-query"))
            : LibraryItemsLoadResult.Failure(oldError));
        await oldLoad;

        Assert.AreEqual("new-query-item", context.ViewModel.Items.Single().Id);
        Assert.AreEqual(LibrarySortField.Year, context.ViewModel.SortField);
        Assert.IsFalse(context.ViewModel.HasContentError);
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SwitchingLibraryOrQuery_CancelsPaginationAndRejectsOldAppend(bool changeQuery)
    {
        var context = CreateContext();
        var pending = new TaskCompletionSource<LibraryItemsLoadResult>();
        var pageToken = CancellationToken.None;
        context.LibraryService.LoadLibraryQueryAsyncHandler = (_, library, start, _, _, token) =>
        {
            if (start > 0)
            {
                pageToken = token;
                return pending.Task;
            }

            return Task.FromResult(LibraryItemsLoadResult.Success(CreateItems(library.Id), 150));
        };
        await context.ViewModel.LoadAsync();
        var oldPage = context.ViewModel.LoadMoreAsync();
        if (changeQuery)
        {
            await context.ViewModel.UpdateQueryAsync(new LibraryQuery(FavoritesOnly: true));
        }
        else
        {
            await context.ViewModel.SelectLibraryAsync(context.ViewModel.Libraries[1]);
        }

        Assert.IsTrue(pageToken.IsCancellationRequested);
        pending.SetResult(LibraryItemsLoadResult.Success(CreateItems("old-page"), 150));
        await oldPage;

        Assert.AreEqual(1, context.ViewModel.Items.Count);
        Assert.AreEqual(changeQuery ? "library-1-item" : "library-2-item", context.ViewModel.Items[0].Id);
        Assert.AreEqual(0, context.LibraryService.LastStartIndex);
        Assert.IsFalse(context.ViewModel.IsLoadingMoreItems);
    }

    [TestMethod]
    public async Task Pagination_DeduplicatesAndStopsAtTotalOrEmptyPage()
    {
        var context = CreateContext();
        context.LibraryService.LoadLibraryQueryAsyncHandler = (_, library, _, _, _, _) =>
            Task.FromResult(LibraryItemsLoadResult.Success(CreateItems(library.Id), 150));
        await context.ViewModel.LoadAsync();
        await context.ViewModel.LoadMoreAsync();
        Assert.AreEqual(1, context.ViewModel.Items.Count);
        Assert.IsFalse(context.ViewModel.HasMoreItems);

        context.LibraryService.LoadLibraryQueryAsyncHandler = (_, library, start, _, _, _) =>
            Task.FromResult(LibraryItemsLoadResult.Success(start == 0 ? CreateItems(library.Id) : [], 500));
        await context.ViewModel.LoadAsync();
        await context.ViewModel.LoadMoreAsync();
        Assert.IsFalse(context.ViewModel.HasMoreItems);
    }

    [TestMethod]
    public async Task Deactivate_CancelsLibraryListAndIgnoresLateUnauthorized()
    {
        var context = CreateContext();
        var pending = new TaskCompletionSource<LibraryLoadResult>();
        var token = CancellationToken.None;
        context.LibraryService.LoadLibrariesAsyncHandler = (_, cancellationToken) =>
        {
            token = cancellationToken;
            return pending.Task;
        };
        var load = context.ViewModel.LoadAsync();

        context.ViewModel.Deactivate();
        pending.SetResult(LibraryLoadResult.Failure(LibraryLoadError.Unauthorized));
        await load;

        Assert.IsTrue(token.IsCancellationRequested);
        Assert.IsFalse(context.ViewModel.IsLoading);
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreEqual(0, context.LibraryService.LoadLibraryItemsCallCount);
    }

    [TestMethod]
    public async Task Reentry_PreservesQueryWithinSessionAndResetsForNewSession()
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync();
        var options = new LibraryQuery(LibrarySortField.DateAdded, FavoritesOnly: true);
        await context.ViewModel.UpdateQueryAsync(options);
        context.ViewModel.Deactivate();
        await context.ViewModel.LoadAsync();
        Assert.AreEqual(options, context.LibraryService.LastQuery);

        context.CurrentSessionService.SetSession(new AuthSession("https://other.example.test", "other-test-token",
            "other-user", "Other user", "other-server"));
        var pending = new TaskCompletionSource<LibraryLoadResult>();
        context.LibraryService.LoadLibrariesAsyncHandler = (_, _) => pending.Task;
        var load = context.ViewModel.LoadAsync();
        Assert.AreEqual(new LibraryQuery(), context.ViewModel.Query);
        Assert.AreEqual(0, context.ViewModel.Items.Count);
        Assert.IsNull(context.ViewModel.SelectedLibrary);
        Assert.IsFalse(context.ViewModel.IsLibrariesContentVisible);
        pending.SetResult(CreateLibrariesResult());
        await load;
        Assert.AreEqual(new LibraryQuery(), context.LibraryService.LastQuery);
    }

    [TestMethod]
    public async Task NewSessionAndRequest_IgnoreOldFailureWithoutEndingNewLoading()
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync();
        var oldPending = new TaskCompletionSource<LibraryItemsLoadResult>();
        context.LibraryService.LoadLibraryQueryAsyncHandler = (_, _, _, _, _, _) => oldPending.Task;
        var oldLoad = context.ViewModel.UpdateQueryAsync(new LibraryQuery(FavoritesOnly: true));
        context.CurrentSessionService.SetSession(new AuthSession("https://other.example.test", "other-test-token",
            "other-user", "Other user", "other-server"));
        var newPending = new TaskCompletionSource<LibraryLoadResult>();
        context.LibraryService.LoadLibrariesAsyncHandler = (_, _) => newPending.Task;
        var newLoad = context.ViewModel.LoadAsync();

        oldPending.SetResult(LibraryItemsLoadResult.Failure(LibraryLoadError.Unauthorized));
        await oldLoad;
        Assert.IsTrue(context.ViewModel.IsInitialLibrariesLoading);
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreEqual("other-user", context.CurrentSessionService.CurrentSession!.UserId);
        newPending.SetResult(LibraryLoadResult.Success([]));
        await newLoad;
    }

    [TestMethod]
    public async Task UnauthorizedCleanup_CompletionCannotClearReplacementSession()
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync();
        var cleanup = new TaskCompletionSource();
        context.AuthSessionStore.ClearAsyncHandler = _ => cleanup.Task;
        context.LibraryService.LoadLibraryQueryAsyncHandler = (_, _, _, _, _, _) =>
            Task.FromResult(LibraryItemsLoadResult.Failure(LibraryLoadError.Unauthorized));
        var load = context.ViewModel.UpdateQueryAsync(new LibraryQuery(FavoritesOnly: true));
        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        var replacement = new AuthSession("https://other.example.test", "other-test-token", "other-user", "Other", "other-server");
        context.CurrentSessionService.SetSession(replacement);

        cleanup.SetResult();
        await load;

        Assert.AreSame(replacement, context.CurrentSessionService.CurrentSession);
        Assert.AreNotEqual(AppPage.Login, context.NavigationService.CurrentPage);
    }

    [TestMethod]
    public async Task CancelledOrThrowingRequest_UsesQuietCancellationOrFriendlyError()
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync();
        context.LibraryService.LoadLibraryQueryAsyncHandler = (_, _, _, _, _, _) =>
            Task.FromResult(LibraryItemsLoadResult.Failure(LibraryLoadError.Cancelled));
        await context.ViewModel.UpdateQueryAsync(new LibraryQuery(FavoritesOnly: true));
        Assert.IsFalse(context.ViewModel.HasContentError);
        context.LibraryService.LoadLibraryQueryAsyncHandler = (_, _, _, _, _, _) => throw new IOException("fixture");
        await context.ViewModel.SelectLibraryAsync(context.ViewModel.SelectedLibrary!);
        Assert.IsTrue(context.ViewModel.IsContentErrorVisible);
        Assert.IsFalse(context.ViewModel.IsLoading);
    }

    [TestMethod]
    public async Task LoadAsync_ShowsLoadingWhileLibrariesArePending()
    {
        var context = CreateContext();
        var pendingLoad = new TaskCompletionSource<LibraryLoadResult>();
        context.LibraryService.LoadLibrariesAsyncHandler = (_, _) => pendingLoad.Task;

        Assert.IsFalse(context.ViewModel.IsLibrariesContentVisible);
        var loadTask = context.ViewModel.LoadAsync();

        Assert.IsTrue(context.ViewModel.IsLoadingLibraries);
        Assert.IsTrue(context.ViewModel.IsInitialLibrariesLoading);
        Assert.IsFalse(context.ViewModel.IsLibrariesEmpty && context.ViewModel.IsLibrariesContentVisible);
        pendingLoad.SetResult(CreateLibrariesResult());
        await loadTask;
        Assert.IsFalse(context.ViewModel.IsLoadingLibraries);
    }

    [TestMethod]
    public async Task LoadAsync_SuccessDisplaysLibrariesAndDefaultLibraryItems()
    {
        var context = CreateContext();

        await context.ViewModel.LoadAsync();

        Assert.AreEqual(2, context.ViewModel.Libraries.Count);
        Assert.AreEqual("library-1", context.ViewModel.SelectedLibrary!.Id);
        Assert.AreEqual(1, context.ViewModel.Items.Count);
        Assert.AreEqual("Movie One", context.ViewModel.Items[0].Title);
    }

    [TestMethod]
    public async Task LoadAsync_EmptyLibrariesShowsEmptyState()
    {
        var context = CreateContext();
        context.LibraryService.LoadLibrariesAsyncHandler = (_, _) =>
            Task.FromResult(LibraryLoadResult.Success(Array.Empty<LibraryItem>()));

        await context.ViewModel.LoadAsync();

        Assert.IsTrue(context.ViewModel.IsLibrariesEmpty);
        Assert.IsTrue(context.ViewModel.IsLibrariesContentVisible);
        Assert.IsNull(context.ViewModel.SelectedLibrary);
        Assert.AreEqual(0, context.LibraryService.LoadLibraryItemsCallCount);
    }

    [TestMethod]
    public async Task LoadAsync_WithRequestedLibraryId_SelectsRequestedLibrary()
    {
        var context = CreateContext();

        await context.ViewModel.LoadAsync("library-2");

        Assert.AreEqual("library-2", context.ViewModel.SelectedLibrary!.Id);
        Assert.AreEqual("library-2", context.LibraryService.LastLibrary!.Id);
    }

    [TestMethod]
    public async Task SelectLibraryAsync_LoadsSelectedLibraryItems()
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync();
        var secondLibrary = context.ViewModel.Libraries[1];

        await context.ViewModel.SelectLibraryAsync(secondLibrary);

        Assert.AreEqual("library-2", context.LibraryService.LastLibrary!.Id);
        Assert.AreEqual("library-2-item", context.ViewModel.Items[0].Id);
    }

    [TestMethod]
    public async Task SelectLibraryAsync_LoadsFirstPageWithPageSize()
    {
        var context = CreateContext();

        await context.ViewModel.LoadAsync();

        Assert.AreEqual(0, context.LibraryService.LastStartIndex);
        Assert.AreEqual(100, context.LibraryService.LastLimit);
    }

    [TestMethod]
    public async Task LoadMoreCommand_LoadsNextPageAndAppendsItems()
    {
        var context = CreateContext();
        context.LibraryService.LoadLibraryItemsAsyncHandler = (_, library, startIndex, _, _) =>
            Task.FromResult(LibraryItemsLoadResult.Success(
                CreatePagedItems(library.Id, startIndex),
                totalRecordCount: 150));
        await context.ViewModel.LoadAsync();

        await ((AsyncRelayCommand)context.ViewModel.LoadMoreCommand).ExecuteAsync();

        Assert.AreEqual(2, context.ViewModel.Items.Count);
        Assert.AreEqual("library-1-item-0", context.ViewModel.Items[0].Id);
        Assert.AreEqual("library-1-item-100", context.ViewModel.Items[1].Id);
        Assert.AreEqual(100, context.LibraryService.LastStartIndex);
    }

    [TestMethod]
    public async Task LoadMoreCommand_FailureKeepsExistingItemsAndShowsError()
    {
        var context = CreateContext();
        context.LibraryService.LoadLibraryItemsAsyncHandler = (_, library, startIndex, _, _) =>
            startIndex == 0
                ? Task.FromResult(LibraryItemsLoadResult.Success(
                    CreatePagedItems(library.Id, startIndex),
                    totalRecordCount: 150))
                : Task.FromResult(LibraryItemsLoadResult.Failure(LibraryLoadError.ServerUnreachable));
        await context.ViewModel.LoadAsync();

        await ((AsyncRelayCommand)context.ViewModel.LoadMoreCommand).ExecuteAsync();

        Assert.AreEqual(1, context.ViewModel.Items.Count);
        Assert.IsTrue(context.ViewModel.HasContentError);
        Assert.IsTrue(context.ViewModel.IsItemsContentVisible);
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
    }

    [TestMethod]
    public async Task SelectLibraryAsync_EmptyItemsShowsEmptyState()
    {
        var context = CreateContext();
        context.LibraryService.LoadLibraryItemsAsyncHandler = (_, _, _, _, _) =>
            Task.FromResult(LibraryItemsLoadResult.Success(Array.Empty<LibraryMediaItem>()));

        await context.ViewModel.LoadAsync();

        Assert.IsTrue(context.ViewModel.IsItemsEmpty);
    }

    [TestMethod]
    public async Task LoadAsync_LibraryFailureShowsChineseError()
    {
        var context = CreateContext();
        context.LibraryService.LoadLibrariesAsyncHandler = (_, _) =>
            Task.FromResult(LibraryLoadResult.Failure(LibraryLoadError.ServerUnreachable));

        await context.ViewModel.LoadAsync();

        Assert.IsTrue(context.ViewModel.HasLibraryError);
        StringAssert.Contains(context.ViewModel.LibraryErrorMessage!, "无法加载媒体库");
    }

    [TestMethod]
    public async Task SelectLibraryAsync_ContentFailureKeepsLibrariesAndShowsContentError()
    {
        var context = CreateContext();
        context.LibraryService.LoadLibraryItemsAsyncHandler = (_, _, _, _, _) =>
            Task.FromResult(LibraryItemsLoadResult.Failure(LibraryLoadError.ServerUnreachable));

        await context.ViewModel.LoadAsync();

        Assert.AreEqual(2, context.ViewModel.Libraries.Count);
        Assert.IsFalse(context.ViewModel.HasLibraryError);
        Assert.IsTrue(context.ViewModel.HasContentError);
        StringAssert.Contains(context.ViewModel.ContentErrorMessage!, "无法加载媒体库");
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
    }

    [TestMethod]
    public async Task RetryCommand_ReloadsAfterLibraryError()
    {
        var context = CreateContext();
        context.LibraryService.LoadLibrariesAsyncHandler = (_, _) =>
            Task.FromResult(LibraryLoadResult.Failure(LibraryLoadError.ServerUnreachable));
        await context.ViewModel.LoadAsync();
        context.LibraryService.LoadLibrariesAsyncHandler = (_, _) =>
            Task.FromResult(CreateLibrariesResult());

        await ((AsyncRelayCommand)context.ViewModel.RetryCommand).ExecuteAsync();

        Assert.AreEqual(2, context.LibraryService.LoadLibrariesCallCount);
        Assert.IsFalse(context.ViewModel.HasLibraryError);
        Assert.AreEqual(2, context.ViewModel.Libraries.Count);
    }

    [TestMethod]
    public async Task RefreshCurrentLibrary_KeepsExistingItemsUntilRequestCompletes()
    {
        var context = CreateContext();
        await context.ViewModel.LoadAsync();
        var existingItem = context.ViewModel.Items[0];
        var pendingRefresh = new TaskCompletionSource<LibraryItemsLoadResult>();
        context.LibraryService.LoadLibraryItemsAsyncHandler = (_, _, _, _, _) => pendingRefresh.Task;

        var refreshTask = ((AsyncRelayCommand)context.ViewModel.RetryCommand).ExecuteAsync();

        Assert.IsTrue(context.ViewModel.IsRefreshingItems);
        Assert.IsTrue(context.ViewModel.IsItemsContentVisible);
        Assert.AreSame(existingItem, context.ViewModel.Items[0]);
        Assert.IsFalse(context.ViewModel.RetryCommand.CanExecute(null));

        pendingRefresh.SetResult(LibraryItemsLoadResult.Failure(LibraryLoadError.ServerUnreachable));
        await refreshTask;

        Assert.AreSame(existingItem, context.ViewModel.Items[0]);
        Assert.IsTrue(context.ViewModel.IsContentRefreshErrorVisible);
        Assert.IsFalse(context.ViewModel.IsContentErrorVisible);
    }

    [TestMethod]
    public async Task LoadCommand_UnauthorizedStillClearsRuntimeWhenPersistentCleanupFails()
    {
        var context = CreateContext();
        context.AuthSessionStore.ClearAsyncHandler = _ =>
            throw new IOException("credential cleanup failed");
        context.LibraryService.LoadLibrariesAsyncHandler = (_, _) =>
            Task.FromResult(LibraryLoadResult.Failure(LibraryLoadError.Unauthorized));

        await ((AsyncRelayCommand)context.ViewModel.LoadCommand).ExecuteAsync();

        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.IsNull(context.CurrentSessionService.CurrentSession);
        Assert.AreEqual(AppPage.Login, context.NavigationService.CurrentPage);
        StringAssert.Contains(context.LoginErrorMessage!, "登录状态已失效");
    }

    [TestMethod]
    public async Task LoadAsync_ForbiddenKeepsSessionAndShowsPermissionError()
    {
        var context = CreateContext();
        context.LibraryService.LoadLibrariesAsyncHandler = (_, _) =>
            Task.FromResult(LibraryLoadResult.Failure(LibraryLoadError.Forbidden));

        await context.ViewModel.LoadAsync();

        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
        Assert.AreNotEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual("没有权限访问此媒体库", context.ViewModel.LibraryErrorMessage);
    }

    [TestMethod]
    public async Task SelectLibraryAsync_UnauthorizedClearsSessionAndNavigatesToLogin()
    {
        var context = CreateContext();
        context.LibraryService.LoadLibraryItemsAsyncHandler = (_, _, _, _, _) =>
            Task.FromResult(LibraryItemsLoadResult.Failure(LibraryLoadError.Unauthorized));

        await context.ViewModel.LoadAsync();

        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.IsNull(context.CurrentSessionService.CurrentSession);
        Assert.AreEqual(AppPage.Login, context.NavigationService.CurrentPage);
    }

    [TestMethod]
    public void OpenMediaCommand_NavigatesToDetailPlaceholder()
    {
        var context = CreateContext();
        var item = new LibraryMediaItemViewModel(CreateItems("library-1")[0]);
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;

        context.ViewModel.OpenMediaCommand.Execute(item);

        Assert.AreEqual(AppPage.Detail, context.NavigationService.CurrentPage);
        Assert.AreEqual("library-1-item", navigationParameter);
    }

    [TestMethod]
    public void LibraryMediaItemViewModel_MapsPlayedBadgeState()
    {
        var item = new LibraryMediaItemViewModel(new LibraryMediaItem(
            "movie-1",
            "Movie",
            "Movie",
            2024,
            null,
            null,
            IsPlayed: true));

        Assert.IsTrue(item.IsPlayed);
    }

    [TestMethod]
    public async Task LibraryPage_ProgressValueBinding_IsOneWay()
    {
        var repositoryRoot = FindRepositoryRoot();
        var libraryPagePath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "LibraryPage.xaml");
        var xaml = await File.ReadAllTextAsync(libraryPagePath);

        StringAssert.Contains(xaml, "Value=\"{Binding ProgressValue, Mode=OneWay}\"");
    }

    private static LibraryViewModelTestContext CreateContext()
    {
        return new LibraryViewModelTestContext();
    }

    private static LibraryLoadResult CreateLibrariesResult()
    {
        return LibraryLoadResult.Success(new[]
        {
            new LibraryItem("library-1", "Movies", "movies"),
            new LibraryItem("library-2", "TV Shows", "tvshows")
        });
    }

    private static IReadOnlyList<LibraryMediaItem> CreateItems(string libraryId)
    {
        return new[]
        {
            new LibraryMediaItem(
                $"{libraryId}-item",
                libraryId == "library-2" ? "Series One" : "Movie One",
                libraryId == "library-2" ? "Series" : "Movie",
                2024,
                null,
                35)
        };
    }

    private static IReadOnlyList<LibraryMediaItem> CreatePagedItems(string libraryId, int startIndex)
    {
        return new[]
        {
            new LibraryMediaItem(
                $"{libraryId}-item-{startIndex}",
                $"Item {startIndex}",
                "Movie",
                2024,
                null,
                null)
        };
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

    private sealed class LibraryViewModelTestContext
    {
        public LibraryViewModelTestContext()
        {
            NavigationService = new NavigationService();
            LibraryService = new TestLibraryService();
            CurrentSessionService = new CurrentSessionService();
            AuthSessionStore = new TestAuthSessionStore();
            Session = new AuthSession(
                "http://media.local:8096",
                "test-access-token",
                "user-1",
                "Test User",
                "server-1");
            CurrentSessionService.SetSession(Session);
            LibraryService.LoadLibrariesAsyncHandler = (_, _) =>
                Task.FromResult(CreateLibrariesResult());
            LibraryService.LoadLibraryItemsAsyncHandler = (_, library, _, _, _) =>
                Task.FromResult(LibraryItemsLoadResult.Success(CreateItems(library.Id)));
            ViewModel = new LibraryViewModel(
                NavigationService,
                LibraryService,
                CurrentSessionService,
                AuthSessionStore,
                message => LoginErrorMessage = message);
        }

        public NavigationService NavigationService { get; }

        public TestLibraryService LibraryService { get; }

        public CurrentSessionService CurrentSessionService { get; }

        public TestAuthSessionStore AuthSessionStore { get; }

        public AuthSession Session { get; }

        public string? LoginErrorMessage { get; private set; }

        public LibraryViewModel ViewModel { get; }
    }
}
