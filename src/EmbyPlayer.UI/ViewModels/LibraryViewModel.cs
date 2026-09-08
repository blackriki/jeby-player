using System.Windows.Input;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Library;
using EmbyPlayer.UI.Navigation;

namespace EmbyPlayer.UI.ViewModels;

public sealed class LibraryViewModel : ViewModelBase
{
    private const int PageSize = 100;
    private const string SessionExpiredMessage = "登录状态已失效，请重新登录";
    private readonly IAuthSessionStore authSessionStore;
    private readonly ICurrentSessionService currentSessionService;
    private readonly ILibraryService libraryService;
    private readonly INavigationService navigationService;
    private readonly Action<string> showLoginError;
    private string? contentErrorMessage;
    private bool hasLoadedItemsSuccessfully;
    private bool hasLoadedLibrariesSuccessfully;
    private bool isLoadingItems;
    private bool isLoadingLibraries;
    private bool isLoadingMoreItems;
    private IReadOnlyList<LibraryMediaItemViewModel> items = Array.Empty<LibraryMediaItemViewModel>();
    private IReadOnlyList<LibraryItemViewModel> libraries = Array.Empty<LibraryItemViewModel>();
    private string? libraryErrorMessage;
    private int nextStartIndex;
    private bool hasMoreItems;
    private LibraryItemViewModel? selectedLibrary;
    private AuthSession? contentSession;
    private CancellationTokenSource? loadCancellation;
    private long loadVersion;
    private LibraryQuery query = new();
    private LibraryQuery? loadedQuery;

    public LibraryViewModel(
        INavigationService navigationService,
        ILibraryService libraryService,
        ICurrentSessionService currentSessionService,
        IAuthSessionStore authSessionStore,
        Action<string> showLoginError)
    {
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        this.libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        this.currentSessionService = currentSessionService ?? throw new ArgumentNullException(nameof(currentSessionService));
        this.authSessionStore = authSessionStore ?? throw new ArgumentNullException(nameof(authSessionStore));
        this.showLoginError = showLoginError ?? throw new ArgumentNullException(nameof(showLoginError));

        LoadCommand = new AsyncRelayCommand(() => LoadAsync(null), () => !IsLoading);
        RetryCommand = new AsyncRelayCommand(RetryAsync, () => !IsLoading);
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync, () => CanLoadMoreItems);
        SelectLibraryCommand = new RelayCommand(parameter =>
        {
            if (parameter is LibraryItemViewModel library)
            {
                _ = SelectLibraryAsync(library);
            }
        });
        OpenMediaCommand = new RelayCommand(parameter =>
        {
            if (parameter is LibraryMediaItemViewModel mediaItem)
            {
                navigationService.NavigateTo(AppPage.Detail, mediaItem.Id);
            }
        });
        NavigateHomeCommand = new RelayCommand(_ => navigationService.NavigateTo(AppPage.Home));
        ResetQueryCommand = new RelayCommand(_ => _ = UpdateQueryAsync(new LibraryQuery()), _ => HasQueryOptions);
    }

    public bool IsLoadingLibraries
    {
        get => isLoadingLibraries;
        private set
        {
            if (isLoadingLibraries == value)
            {
                return;
            }

            isLoadingLibraries = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(IsInitialLibrariesLoading));
            OnPropertyChanged(nameof(IsRefreshingLibraries));
            OnPropertyChanged(nameof(IsLibraryErrorVisible));
            OnPropertyChanged(nameof(IsLibraryRefreshErrorVisible));
            OnPropertyChanged(nameof(IsLibrariesContentVisible));
            OnPropertyChanged(nameof(CanLoadMoreItems));
            OnPropertyChanged(nameof(CanChangeQuery));
            NotifyCommandsCanExecuteChanged();
        }
    }

    public bool IsLoadingItems
    {
        get => isLoadingItems;
        private set
        {
            if (isLoadingItems == value)
            {
                return;
            }

            isLoadingItems = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(IsInitialItemsLoading));
            OnPropertyChanged(nameof(IsRefreshingItems));
            OnPropertyChanged(nameof(IsContentErrorVisible));
            OnPropertyChanged(nameof(IsContentRefreshErrorVisible));
            OnPropertyChanged(nameof(IsItemsContentVisible));
            OnPropertyChanged(nameof(CanLoadMoreItems));
            NotifyCommandsCanExecuteChanged();
        }
    }

    public bool IsLoading => IsLoadingLibraries || IsLoadingItems || IsLoadingMoreItems;

    public bool IsLoadingMoreItems
    {
        get => isLoadingMoreItems;
        private set
        {
            if (isLoadingMoreItems == value)
            {
                return;
            }

            isLoadingMoreItems = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(CanLoadMoreItems));
            OnPropertyChanged(nameof(LoadMoreButtonText));
            NotifyCommandsCanExecuteChanged();
        }
    }

    public string? LibraryErrorMessage
    {
        get => libraryErrorMessage;
        private set
        {
            if (libraryErrorMessage == value)
            {
                return;
            }

            libraryErrorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasLibraryError));
            OnPropertyChanged(nameof(IsLibraryErrorVisible));
            OnPropertyChanged(nameof(IsLibraryRefreshErrorVisible));
            OnPropertyChanged(nameof(IsLibrariesContentVisible));
        }
    }

    public bool HasLibraryError => !string.IsNullOrWhiteSpace(LibraryErrorMessage);

    public bool IsInitialLibrariesLoading => IsLoadingLibraries && !hasLoadedLibrariesSuccessfully;

    public bool IsRefreshingLibraries => IsLoadingLibraries && hasLoadedLibrariesSuccessfully;

    public bool IsLibraryErrorVisible => HasLibraryError && !IsLoadingLibraries && !hasLoadedLibrariesSuccessfully;

    public bool IsLibraryRefreshErrorVisible => HasLibraryError && !IsLoadingLibraries && hasLoadedLibrariesSuccessfully;

    public string? ContentErrorMessage
    {
        get => contentErrorMessage;
        private set
        {
            if (contentErrorMessage == value)
            {
                return;
            }

            contentErrorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasContentError));
            OnPropertyChanged(nameof(IsContentErrorVisible));
            OnPropertyChanged(nameof(IsContentRefreshErrorVisible));
            OnPropertyChanged(nameof(IsItemsContentVisible));
        }
    }

    public bool HasContentError => !string.IsNullOrWhiteSpace(ContentErrorMessage);

    public bool IsInitialItemsLoading => IsLoadingItems && !hasLoadedItemsSuccessfully;

    public bool IsRefreshingItems => IsLoadingItems && hasLoadedItemsSuccessfully;

    public bool IsContentErrorVisible => HasContentError
        && !IsLoadingItems
        && !HasLibraryError
        && !hasLoadedItemsSuccessfully;

    public bool IsContentRefreshErrorVisible => HasContentError
        && !IsLoadingItems
        && !HasLibraryError
        && hasLoadedItemsSuccessfully;

    public bool IsLibrariesContentVisible => hasLoadedLibrariesSuccessfully;

    public bool IsItemsContentVisible => SelectedLibrary is not null
        && (hasLoadedItemsSuccessfully || (!IsLoadingItems && !HasContentError));

    public IReadOnlyList<LibraryItemViewModel> Libraries
    {
        get => libraries;
        private set
        {
            libraries = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLibrariesEmpty));
        }
    }

    public bool IsLibrariesEmpty => Libraries.Count == 0;

    public LibraryItemViewModel? SelectedLibrary
    {
        get => selectedLibrary;
        private set
        {
            if (selectedLibrary == value)
            {
                return;
            }

            if (selectedLibrary is not null)
            {
                selectedLibrary.IsSelected = false;
            }

            selectedLibrary = value;
            if (selectedLibrary is not null)
            {
                selectedLibrary.IsSelected = true;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedLibraryName));
            OnPropertyChanged(nameof(IsItemsContentVisible));
            OnPropertyChanged(nameof(CanLoadMoreItems));
            OnPropertyChanged(nameof(CanChangeQuery));
        }
    }

    public string SelectedLibraryName => SelectedLibrary?.Name ?? string.Empty;

    public LibraryQuery Query => query;

    public LibrarySortField SortField
    {
        get => query.SortField;
        set => _ = UpdateQueryAsync(query with { SortField = value });
    }

    public LibrarySortDirection SortDirection
    {
        get => query.SortDirection;
        set => _ = UpdateQueryAsync(query with { SortDirection = value });
    }

    public LibraryWatchedFilter WatchedFilter
    {
        get => query.WatchedFilter;
        set => _ = UpdateQueryAsync(query with { WatchedFilter = value });
    }

    public bool FavoritesOnly
    {
        get => query.FavoritesOnly;
        set => _ = UpdateQueryAsync(query with { FavoritesOnly = value });
    }

    public IReadOnlyList<KeyValuePair<LibrarySortField, string>> SortFieldOptions { get; } =
    [new(LibrarySortField.Title, "名称"), new(LibrarySortField.DateAdded, "添加时间"), new(LibrarySortField.Year, "年份")];

    public IReadOnlyList<KeyValuePair<LibrarySortDirection, string>> SortDirectionOptions { get; } =
    [new(LibrarySortDirection.Ascending, "升序"), new(LibrarySortDirection.Descending, "降序")];

    public IReadOnlyList<KeyValuePair<LibraryWatchedFilter, string>> WatchedFilterOptions { get; } =
    [new(LibraryWatchedFilter.All, "全部"), new(LibraryWatchedFilter.Unwatched, "未观看"), new(LibraryWatchedFilter.Watched, "已观看")];

    public IReadOnlyList<KeyValuePair<bool, string>> FavoriteFilterOptions { get; } =
    [new(false, "全部"), new(true, "仅收藏")];

    public bool CanChangeQuery => SelectedLibrary is not null && !IsLoadingLibraries;

    public bool HasQueryOptions => query != new LibraryQuery();

    public string SortSummary => $"{SortFieldOptions.First(option => option.Key == SortField).Value} {(SortDirection == LibrarySortDirection.Ascending ? "↑" : "↓")}";

    public string SortAutomationName => $"排序：{SortFieldOptions.First(option => option.Key == SortField).Value}，{(SortDirection == LibrarySortDirection.Ascending ? "升序" : "降序")}";

    public int ActiveFilterCount => (WatchedFilter == LibraryWatchedFilter.All ? 0 : 1) + (FavoritesOnly ? 1 : 0);

    public bool HasActiveFilters => ActiveFilterCount > 0;

    public string FilterSummary => HasActiveFilters ? $"筛选 {ActiveFilterCount}" : "筛选";

    public string FilterAutomationName => HasActiveFilters
        ? $"筛选：{WatchedFilterOptions.First(option => option.Key == WatchedFilter).Value}，{(FavoritesOnly ? "仅收藏" : "全部收藏状态")}"
        : "筛选：全部内容";

    public string EmptyItemsTitle => query.WatchedFilter != LibraryWatchedFilter.All || query.FavoritesOnly
        ? "没有符合筛选条件的内容" : "当前媒体库没有内容";

    public string EmptyItemsMessage => query.WatchedFilter != LibraryWatchedFilter.All || query.FavoritesOnly
        ? "请调整观看状态或收藏条件后重试。" : "当前媒体库下暂无可显示的媒体。";

    public IReadOnlyList<LibraryMediaItemViewModel> Items
    {
        get => items;
        private set
        {
            items = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsItemsEmpty));
            OnPropertyChanged(nameof(IsItemsContentVisible));
            OnPropertyChanged(nameof(CanLoadMoreItems));
        }
    }

    public bool IsItemsEmpty => Items.Count == 0;

    public bool HasMoreItems
    {
        get => hasMoreItems;
        private set
        {
            if (hasMoreItems == value)
            {
                return;
            }

            hasMoreItems = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanLoadMoreItems));
            NotifyCommandsCanExecuteChanged();
        }
    }

    public bool CanLoadMoreItems =>
        HasMoreItems && !IsLoadingLibraries && !IsLoadingItems && !IsLoadingMoreItems && SelectedLibrary is not null;

    public string LoadMoreButtonText => IsLoadingMoreItems ? "正在加载..." : "加载更多";

    public ICommand LoadCommand { get; }

    public ICommand RetryCommand { get; }

    public ICommand LoadMoreCommand { get; }

    public ICommand SelectLibraryCommand { get; }

    public ICommand OpenMediaCommand { get; }

    public ICommand NavigateHomeCommand { get; }

    public ICommand ResetQueryCommand { get; }

    public async Task LoadAsync(string? requestedLibraryId = null)
    {
        var session = GetSession();
        if (session is null)
        {
            return;
        }

        var request = BeginRequest(session);
        IsLoadingLibraries = true;
        LibraryErrorMessage = null;
        ContentErrorMessage = null;

        try
        {
            var result = await libraryService
                .LoadLibrariesAsync(session, request.Token)
                .ConfigureAwait(true);

            if (!IsCurrentRequest(request))
            {
                return;
            }

            if (!result.IsSuccess)
            {
                await HandleLoadFailureAsync(result.Error, true, request).ConfigureAwait(true);
                return;
            }

            Libraries = result.Libraries
                .Select(library => new LibraryItemViewModel(library))
                .ToArray();
            hasLoadedLibrariesSuccessfully = true;
            OnPropertyChanged(nameof(IsLibrariesContentVisible));
            OnPropertyChanged(nameof(IsLibraryErrorVisible));
            OnPropertyChanged(nameof(IsLibraryRefreshErrorVisible));

            if (Libraries.Count == 0)
            {
                SelectedLibrary = null;
                ResetItems();
                return;
            }

            var targetLibrary = FindRequestedLibrary(requestedLibraryId)
                ?? FindRequestedLibrary(SelectedLibrary?.Id)
                ?? Libraries[0];

            PrepareFirstPage(targetLibrary, request.Query);
            IsLoadingLibraries = false;
            await LoadLibraryItemsPageAsync(request, targetLibrary, 0, append: false).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RecordUnexpectedFailure(exception, request, isLibraryListFailure: !IsLoadingItems);
        }
        finally
        {
            FinishRequest(request);
        }
    }

    public async Task SelectLibraryAsync(LibraryItemViewModel library)
    {
        ArgumentNullException.ThrowIfNull(library);
        var session = GetSession();
        if (session is null)
        {
            return;
        }

        var request = BeginRequest(session);
        PrepareFirstPage(library, request.Query);
        try
        {
            await LoadLibraryItemsPageAsync(request, library, startIndex: 0, append: false)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RecordUnexpectedFailure(exception, request, isLibraryListFailure: false);
        }
        finally
        {
            FinishRequest(request);
        }
    }

    public async Task UpdateQueryAsync(LibraryQuery value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (query == value)
        {
            return;
        }

        query = value;
        NotifyQueryChanged();
        if (SelectedLibrary is not null)
        {
            await SelectLibraryAsync(SelectedLibrary).ConfigureAwait(true);
        }
    }

    public void Deactivate()
    {
        loadVersion++;
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        loadCancellation = null;
        IsLoadingLibraries = false;
        IsLoadingItems = false;
        IsLoadingMoreItems = false;
    }

    public void ResetSession()
    {
        Deactivate();
        contentSession = null;
        query = new LibraryQuery();
        NotifyQueryChanged();
        hasLoadedLibrariesSuccessfully = false;
        Libraries = Array.Empty<LibraryItemViewModel>();
        SelectedLibrary = null;
        LibraryErrorMessage = null;
        ContentErrorMessage = null;
        ResetItems();
        OnPropertyChanged(nameof(IsLibrariesContentVisible));
        OnPropertyChanged(nameof(IsLibraryErrorVisible));
        OnPropertyChanged(nameof(IsLibraryRefreshErrorVisible));
    }

    public async Task LoadMoreAsync()
    {
        if (!CanLoadMoreItems || SelectedLibrary is null)
        {
            return;
        }

        var session = GetSession();
        if (session is null)
        {
            return;
        }

        // A new account must reload its own libraries before requesting another page.
        if (SelectedLibrary is null)
        {
            return;
        }

        var request = BeginRequest(session);
        IsLoadingMoreItems = true;
        ContentErrorMessage = null;
        try
        {
            await LoadLibraryItemsPageAsync(request, SelectedLibrary, nextStartIndex, append: true)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RecordUnexpectedFailure(exception, request, isLibraryListFailure: false);
        }
        finally
        {
            FinishRequest(request);
        }
    }

    private async Task LoadLibraryItemsPageAsync(
        LoadRequest request,
        LibraryItemViewModel library,
        int startIndex,
        bool append)
    {
        var result = await libraryService
            .LoadLibraryItemsAsync(request.Session, library.Source, startIndex, PageSize, request.Query, request.Token)
            .ConfigureAwait(true);

        if (!IsCurrentRequest(request))
        {
            return;
        }

        if (!result.IsSuccess)
        {
            await HandleLoadFailureAsync(result.Error, false, request).ConfigureAwait(true);
            return;
        }

        var pageItems = result.Items
            .Select(item => new LibraryMediaItemViewModel(item))
            .ToArray();
        Items = append
            ? Items.Concat(pageItems).DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray()
            : pageItems.DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        hasLoadedItemsSuccessfully = true;
        loadedQuery = request.Query;
        OnPropertyChanged(nameof(IsItemsContentVisible));
        OnPropertyChanged(nameof(IsContentErrorVisible));
        OnPropertyChanged(nameof(IsContentRefreshErrorVisible));
        nextStartIndex = result.TotalRecordCount is null
            ? startIndex + pageItems.Length
            : startIndex + PageSize;
        HasMoreItems = result.TotalRecordCount is int totalRecordCount
            ? pageItems.Length > 0 && nextStartIndex < totalRecordCount
            : pageItems.Length >= PageSize;
    }

    private async Task RetryAsync()
    {
        if (HasLibraryError || Libraries.Count == 0)
        {
            await LoadAsync(SelectedLibrary?.Id).ConfigureAwait(true);
            return;
        }

        if (SelectedLibrary is not null)
        {
            await SelectLibraryAsync(SelectedLibrary).ConfigureAwait(true);
        }
    }

    private async Task HandleLoadFailureAsync(LibraryLoadError error, bool isLibraryListFailure, LoadRequest request)
    {
        if (!IsCurrentRequest(request) || error == LibraryLoadError.Cancelled)
        {
            return;
        }

        if (error == LibraryLoadError.Unauthorized)
        {
            await ExpiredSessionRecovery
                .ClearAsync(authSessionStore, currentSessionService, "library", request.Session, request.Token)
                .ConfigureAwait(true);
            if (request.Version != loadVersion || currentSessionService.CurrentSession is not null)
            {
                return;
            }

            ResetSession();
            navigationService.NavigateTo(AppPage.Login);
            showLoginError(SessionExpiredMessage);
            return;
        }

        if (isLibraryListFailure)
        {
            LibraryErrorMessage = GetErrorMessage(error);
            return;
        }

        ContentErrorMessage = GetErrorMessage(error);
    }

    private AuthSession? GetSession()
    {
        var session = currentSessionService.CurrentSession;
        if (!ReferenceEquals(contentSession, session))
        {
            ResetSession();
            contentSession = session;
        }

        if (session is null)
        {
            navigationService.NavigateTo(AppPage.Login);
            showLoginError("请先登录");
        }

        return session;
    }

    private LoadRequest BeginRequest(AuthSession session)
    {
        Deactivate();
        loadCancellation = new CancellationTokenSource();
        return new LoadRequest(session, query, loadVersion, loadCancellation.Token);
    }

    private bool IsCurrentRequest(LoadRequest request) => request.Version == loadVersion
        && !request.Token.IsCancellationRequested
        && ReferenceEquals(request.Session, currentSessionService.CurrentSession);

    private void FinishRequest(LoadRequest request)
    {
        if (request.Version == loadVersion)
        {
            loadCancellation?.Dispose();
            loadCancellation = null;
            IsLoadingLibraries = false;
            IsLoadingItems = false;
            IsLoadingMoreItems = false;
        }
    }

    private void PrepareFirstPage(LibraryItemViewModel library, LibraryQuery requestedQuery)
    {
        var keepItems = hasLoadedItemsSuccessfully && loadedQuery == requestedQuery
            && string.Equals(SelectedLibrary?.Id, library.Id, StringComparison.OrdinalIgnoreCase);
        SelectedLibrary = library;
        if (!keepItems)
        {
            ResetItems();
        }

        ContentErrorMessage = null;
        IsLoadingItems = true;
    }

    private void ResetItems()
    {
        hasLoadedItemsSuccessfully = false;
        loadedQuery = null;
        Items = Array.Empty<LibraryMediaItemViewModel>();
        HasMoreItems = false;
        nextStartIndex = 0;
        OnPropertyChanged(nameof(IsInitialItemsLoading));
        OnPropertyChanged(nameof(IsContentErrorVisible));
        OnPropertyChanged(nameof(IsContentRefreshErrorVisible));
    }

    private void NotifyQueryChanged()
    {
        OnPropertyChanged(nameof(Query));
        OnPropertyChanged(nameof(SortField));
        OnPropertyChanged(nameof(SortDirection));
        OnPropertyChanged(nameof(WatchedFilter));
        OnPropertyChanged(nameof(FavoritesOnly));
        OnPropertyChanged(nameof(HasQueryOptions));
        OnPropertyChanged(nameof(SortSummary));
        OnPropertyChanged(nameof(SortAutomationName));
        OnPropertyChanged(nameof(ActiveFilterCount));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(FilterSummary));
        OnPropertyChanged(nameof(FilterAutomationName));
        OnPropertyChanged(nameof(EmptyItemsTitle));
        OnPropertyChanged(nameof(EmptyItemsMessage));
        (ResetQueryCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RecordUnexpectedFailure(Exception exception, LoadRequest request, bool isLibraryListFailure)
    {
        System.Diagnostics.Trace.TraceError($"Library load failed: {exception}");
        if (IsCurrentRequest(request))
        {
            if (isLibraryListFailure)
            {
                LibraryErrorMessage = GetErrorMessage(LibraryLoadError.ServerError);
            }
            else
            {
                ContentErrorMessage = GetErrorMessage(LibraryLoadError.ServerError);
            }
        }
    }

    private sealed record LoadRequest(AuthSession Session, LibraryQuery Query, long Version, CancellationToken Token);

    private LibraryItemViewModel? FindRequestedLibrary(string? libraryId)
    {
        if (string.IsNullOrWhiteSpace(libraryId))
        {
            return null;
        }

        return Libraries.FirstOrDefault(
            library => string.Equals(library.Id, libraryId, StringComparison.OrdinalIgnoreCase));
    }

    private void NotifyCommandsCanExecuteChanged()
    {
        if (LoadCommand is AsyncRelayCommand loadCommand)
        {
            loadCommand.NotifyCanExecuteChanged();
        }

        if (RetryCommand is AsyncRelayCommand retryCommand)
        {
            retryCommand.NotifyCanExecuteChanged();
        }

        if (LoadMoreCommand is AsyncRelayCommand loadMoreCommand)
        {
            loadMoreCommand.NotifyCanExecuteChanged();
        }
    }

    private static string GetErrorMessage(LibraryLoadError error)
    {
        return error switch
        {
            LibraryLoadError.Forbidden => "没有权限访问此媒体库",
            LibraryLoadError.ServerTimeout => "媒体库加载超时，请稍后重试",
            LibraryLoadError.ServerUnreachable => "无法加载媒体库，请检查网络或服务器",
            LibraryLoadError.Cancelled => "媒体库加载已取消",
            _ => "媒体库加载失败，请稍后重试"
        };
    }
}
