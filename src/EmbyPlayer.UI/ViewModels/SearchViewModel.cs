using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Diagnostics;
using EmbyPlayer.Core.Search;
using EmbyPlayer.Core.Settings;
using EmbyPlayer.UI.Navigation;

namespace EmbyPlayer.UI.ViewModels;

public sealed class SearchViewModel : ViewModelBase
{
    private const int DebounceMilliseconds = 350;
    private const int MaximumHistoryCount = 8;
    private const string SessionExpiredMessage = "登录状态已失效，请重新登录";
    private readonly IAppSettingsService appSettingsService;
    private readonly IAuthSessionStore authSessionStore;
    private readonly ICurrentSessionService currentSessionService;
    private readonly IApplicationDiagnostics diagnostics;
    private readonly INavigationService navigationService;
    private readonly ISearchService searchService;
    private readonly Action<string> showLoginError;
    private CancellationTokenSource? debounceCancellation;
    private CancellationTokenSource? searchCancellation;
    private string? displayedKeyword;
    private string keyword = string.Empty;
    private string? lastExecutedKeyword;
    private string? errorMessage;
    private bool hasCompletedSearch;
    private bool historyLoaded;
    private bool isFavoritesMode;
    private bool isLoading;
    private bool suppressKeywordSearch;
    private long searchVersion;
    private double scrollOffset;
    private IReadOnlyList<string> searchHistory = Array.Empty<string>();
    private IReadOnlyList<SearchResultItemViewModel> allResults = Array.Empty<SearchResultItemViewModel>();
    private IReadOnlyList<SearchResultItemViewModel> defaultResults = Array.Empty<SearchResultItemViewModel>();
    private SearchExecutionDiagnostics? lastSearchDiagnostics;
    private IReadOnlyList<SearchResultItemViewModel> results = Array.Empty<SearchResultItemViewModel>();
    private IReadOnlyList<SearchFilterViewModel> visibleFilters = Array.Empty<SearchFilterViewModel>();

    public SearchViewModel(
        INavigationService navigationService,
        ISearchService searchService,
        ICurrentSessionService currentSessionService,
        IAuthSessionStore authSessionStore,
        IAppSettingsService appSettingsService,
        Action<string> showLoginError,
        IApplicationDiagnostics? diagnostics = null)
    {
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        this.searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        this.currentSessionService = currentSessionService ?? throw new ArgumentNullException(nameof(currentSessionService));
        this.authSessionStore = authSessionStore ?? throw new ArgumentNullException(nameof(authSessionStore));
        this.appSettingsService = appSettingsService ?? throw new ArgumentNullException(nameof(appSettingsService));
        this.showLoginError = showLoginError ?? throw new ArgumentNullException(nameof(showLoginError));
        this.diagnostics = diagnostics ?? ApplicationDiagnosticLog.Shared;

        NavigateHomeCommand = new RelayCommand(_ => navigationService.NavigateTo(AppPage.Home));
        RetryCommand = new AsyncRelayCommand(
            RetryAsync,
            () => !IsLoading && (IsFavoritesMode || CanSearch(Keyword)));
        ClearSearchCommand = new RelayCommand(_ => ClearSearch(), _ => !string.IsNullOrEmpty(Keyword));
        SearchHistoryCommand = new RelayCommand(parameter =>
        {
            if (parameter is string historyKeyword)
            {
                _ = SearchFromHistoryAsync(historyKeyword);
            }
        });
        RemoveHistoryCommand = new RelayCommand(parameter =>
        {
            if (parameter is string historyKeyword)
            {
                _ = RemoveHistoryAsync(historyKeyword);
            }
        });
        ClearHistoryCommand = new RelayCommand(_ => _ = ClearHistoryAsync(), _ => SearchHistory.Count > 0);
        SelectFilterCommand = new RelayCommand(parameter =>
        {
            if (parameter is SearchFilterViewModel filter)
            {
                SelectFilter(filter);
            }
        });
        OpenResultCommand = new RelayCommand(parameter =>
        {
            if (parameter is SearchResultItemViewModel item)
            {
                navigationService.NavigateTo(AppPage.Detail, item.Id);
            }
        });

        Filters = new[]
        {
            new SearchFilterViewModel("all", "全部"),
            new SearchFilterViewModel("movie", "电影"),
            new SearchFilterViewModel("series", "电视剧"),
            new SearchFilterViewModel("episode", "单集"),
            new SearchFilterViewModel("boxset", "合集"),
            new SearchFilterViewModel("playlist", "播放列表"),
            new SearchFilterViewModel("video", "视频")
        };
        Filters[0].IsSelected = true;
        visibleFilters = new[] { Filters[0] };
    }

    public event EventHandler? SearchFocusRequested;

    public string Keyword
    {
        get => keyword;
        set
        {
            var normalized = value ?? string.Empty;
            if (keyword == normalized)
            {
                return;
            }

            keyword = normalized;
            NotifySearchStateChanged();
            (ClearSearchCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RetryCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();

            if (!suppressKeywordSearch)
            {
                ScheduleDebouncedSearch();
            }
        }
    }

    public bool IsFavoritesMode
    {
        get => isFavoritesMode;
        private set
        {
            if (isFavoritesMode == value)
            {
                return;
            }

            isFavoritesMode = value;
            NotifySearchStateChanged();
        }
    }

    public IReadOnlyList<SearchResultItemViewModel> Results
    {
        get => results;
        private set
        {
            results = value;
            OnPropertyChanged();
            NotifySearchStateChanged();
        }
    }

    public IReadOnlyList<string> SearchHistory
    {
        get => searchHistory;
        private set
        {
            searchHistory = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSearchHistory));
            OnPropertyChanged(nameof(IsHistoryVisible));
            OnPropertyChanged(nameof(IsInitialHintVisible));
            (ClearHistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsLoading
    {
        get => isLoading;
        private set
        {
            if (isLoading == value)
            {
                return;
            }

            isLoading = value;
            NotifySearchStateChanged();
            (RetryCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        }
    }

    public string? ErrorMessage
    {
        get => errorMessage;
        private set
        {
            if (errorMessage == value)
            {
                return;
            }

            errorMessage = value;
            NotifySearchStateChanged();
        }
    }

    public double ScrollOffset
    {
        get => scrollOffset;
        set => scrollOffset = Math.Max(0, value);
    }

    public SearchExecutionDiagnostics? LastSearchDiagnostics
    {
        get => lastSearchDiagnostics;
        private set
        {
            if (Equals(lastSearchDiagnostics, value))
            {
                return;
            }

            lastSearchDiagnostics = value;
            OnPropertyChanged();
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasResults => Results.Count > 0;

    public bool HasSearchHistory => SearchHistory.Count > 0;

    public bool HasKeyword => !string.IsNullOrEmpty(Keyword);

    public bool IsInitialLoading => IsLoading && allResults.Count == 0;

    public bool IsRefreshing => IsLoading && allResults.Count > 0;

    public bool IsErrorVisible => HasError && !IsLoading && allResults.Count == 0;

    public bool IsRefreshErrorVisible => HasError && !IsLoading && allResults.Count > 0;

    public bool IsContentVisible => !IsInitialLoading && !IsErrorVisible;

    public bool IsResultsVisible => HasResults;

    public bool IsInitialState => !IsFavoritesMode && !IsLoading && !HasError && !hasCompletedSearch;

    public bool IsHistoryVisible => IsInitialState && string.IsNullOrWhiteSpace(Keyword) && HasSearchHistory;

    public bool IsInitialHintVisible => IsInitialState && !IsHistoryVisible;

    public bool IsEmptyKeywordVisible => IsInitialHintVisible;

    public bool IsNoResultsVisible => !IsLoading && !HasError && hasCompletedSearch && !HasResults;

    public bool IsResultSummaryVisible => allResults.Count > 0;

    public bool AreFiltersVisible => allResults.Count > 0;

    public string InitialHintTitle => string.IsNullOrWhiteSpace(Keyword)
        ? "搜索你的媒体库"
        : "请继续输入关键词";

    public string InitialHintMessage => string.IsNullOrWhiteSpace(Keyword)
        ? "输入电影、电视剧、单集、合集或视频名称。"
        : "至少输入 2 个有效字符后开始搜索。";

    public string NoResultsTitle => IsFavoritesMode
        ? "暂无收藏内容"
        : $"没有找到与“{Keyword.Trim()}”相关的内容";

    public string NoResultsMessage => allResults.Count > 0
        ? "当前筛选下没有结果，请切换到“全部”或其他类型。"
        : "请尝试更短的关键词，或检查名称是否正确。";

    public string ResultTitle => IsFavoritesMode ? "我的收藏" : $"“{displayedKeyword ?? Keyword.Trim()}”的搜索结果";

    public string ResultCountText => $"{Results.Count} 个结果";

    public IReadOnlyList<SearchFilterViewModel> Filters { get; }

    public IReadOnlyList<SearchFilterViewModel> VisibleFilters
    {
        get => visibleFilters;
        private set
        {
            visibleFilters = value;
            OnPropertyChanged();
        }
    }

    public ICommand NavigateHomeCommand { get; }

    public ICommand RetryCommand { get; }

    public ICommand ClearSearchCommand { get; }

    public ICommand SearchHistoryCommand { get; }

    public ICommand RemoveHistoryCommand { get; }

    public ICommand ClearHistoryCommand { get; }

    public ICommand SelectFilterCommand { get; }

    public ICommand OpenResultCommand { get; }

    public async Task LoadAsync(string? requestedKeyword)
    {
        await EnsureHistoryLoadedAsync().ConfigureAwait(true);

        if (requestedKeyword is null)
        {
            RequestSearchFocus();
            return;
        }

        var normalized = requestedKeyword.Trim();
        SetKeywordWithoutScheduling(normalized);
        if (CanSearch(normalized))
        {
            await ExecuteSearchAsync(normalized, force: false).ConfigureAwait(true);
        }
        else
        {
            ResetToInitialState(clearKeyword: false);
        }

        RequestSearchFocus();
    }

    public Task LoadFavoritesAsync()
    {
        return LoadFavoritesCoreAsync();
    }

    public Task SearchAsync(string? requestedKeyword)
    {
        var normalized = requestedKeyword?.Trim() ?? string.Empty;
        SetKeywordWithoutScheduling(normalized);
        return CanSearch(normalized)
            ? ExecuteSearchAsync(normalized, force: false)
            : ResetToInitialStateAsync(clearKeyword: string.IsNullOrEmpty(normalized));
    }

    public Task SearchNowAsync()
    {
        CancelDebounce();
        return CanSearch(Keyword)
            ? ExecuteSearchAsync(Keyword.Trim(), force: false)
            : Task.CompletedTask;
    }

    public void ClearSearch()
    {
        CancelPendingSearch();
        SetKeywordWithoutScheduling(string.Empty);
        ResetToInitialState(clearKeyword: false);
        RequestSearchFocus();
    }

    public void Deactivate()
    {
        CancelDebounce();
        CancelActiveRequest();
        searchVersion++;
        IsLoading = false;
    }

    public void RequestSearchFocus()
    {
        SearchFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ScheduleDebouncedSearch()
    {
        CancelDebounce();
        var normalized = Keyword.Trim();
        if (!CanSearch(normalized))
        {
            CancelPendingSearch();
            ResetToInitialState(clearKeyword: false);
            return;
        }

        ErrorMessage = null;
        debounceCancellation = new CancellationTokenSource();
        _ = DebounceSearchAsync(normalized, debounceCancellation.Token);
    }

    private async Task DebounceSearchAsync(string normalizedKeyword, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DebounceMilliseconds, cancellationToken).ConfigureAwait(true);
            await ExecuteSearchAsync(normalizedKeyword, force: false).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ExecuteSearchAsync(string normalizedKeyword, bool force)
    {
        normalizedKeyword = normalizedKeyword.Trim();
        if (!CanSearch(normalizedKeyword))
        {
            return;
        }

        if (!force
            && string.Equals(lastExecutedKeyword, normalizedKeyword, StringComparison.OrdinalIgnoreCase)
            && (IsLoading || hasCompletedSearch || HasError))
        {
            return;
        }

        CancelDebounce();
        CancelActiveRequest();
        var requestVersion = ++searchVersion;
        searchCancellation = new CancellationTokenSource();
        var cancellationToken = searchCancellation.Token;

        IsFavoritesMode = false;
        SetKeywordWithoutScheduling(normalizedKeyword);
        lastExecutedKeyword = normalizedKeyword;
        hasCompletedSearch = false;
        ErrorMessage = null;

        var session = currentSessionService.CurrentSession;
        if (session is null)
        {
            navigationService.NavigateTo(AppPage.Login);
            showLoginError("请先登录");
            return;
        }

        IsLoading = true;
        await RecordHistoryAsync(normalizedKeyword, cancellationToken).ConfigureAwait(true);

        try
        {
            var result = await searchService
                .SearchAsync(session, normalizedKeyword, cancellationToken)
                .ConfigureAwait(true);

            if (!IsCurrentRequest(requestVersion, normalizedKeyword))
            {
                return;
            }

            await ApplyResultAsync(result, requestVersion).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Search request failed: {exception.Message}");
            if (IsCurrentRequest(requestVersion, normalizedKeyword))
            {
                ErrorMessage = "搜索失败，请稍后重试";
            }
        }
        finally
        {
            if (requestVersion == searchVersion)
            {
                IsLoading = false;
            }
        }
    }

    private async Task LoadFavoritesCoreAsync()
    {
        CancelPendingSearch();
        var requestVersion = ++searchVersion;
        searchCancellation = new CancellationTokenSource();
        var cancellationToken = searchCancellation.Token;

        IsFavoritesMode = true;
        SetKeywordWithoutScheduling(string.Empty);
        ErrorMessage = null;
        hasCompletedSearch = false;
        SetAllResults(Array.Empty<SearchResultItemViewModel>());

        var session = currentSessionService.CurrentSession;
        if (session is null)
        {
            navigationService.NavigateTo(AppPage.Login);
            showLoginError("请先登录");
            return;
        }

        IsLoading = true;
        try
        {
            var result = await searchService
                .LoadFavoritesAsync(session, cancellationToken)
                .ConfigureAwait(true);
            if (requestVersion == searchVersion)
            {
                await ApplyResultAsync(result, requestVersion).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            diagnostics.Write(
                "search",
                "failed",
                $"operation=favorites exceptionType={exception.GetType().Name}");
            if (requestVersion == searchVersion)
            {
                ErrorMessage = "收藏加载失败，请稍后重试";
            }
        }
        finally
        {
            if (requestVersion == searchVersion)
            {
                IsLoading = false;
            }
        }
    }

    private Task RetryAsync()
    {
        return IsFavoritesMode
            ? LoadFavoritesCoreAsync()
            : ExecuteSearchAsync(Keyword.Trim(), force: true);
    }

    private async Task ApplyResultAsync(SearchLoadResult result, long requestVersion)
    {
        if (requestVersion != searchVersion)
        {
            return;
        }

        if (result.IsSuccess && result.Items is not null)
        {
            LastSearchDiagnostics = result.Diagnostics;
            SetAllResults(result.Items
                .Select(item => new SearchResultItemViewModel(item))
                .ToArray());
            displayedKeyword = IsFavoritesMode ? null : Keyword.Trim();
            hasCompletedSearch = true;
            ErrorMessage = null;
            NotifySearchStateChanged();
            return;
        }

        if (result.Error == SearchLoadError.Cancelled)
        {
            return;
        }

        if (result.Error == SearchLoadError.Unauthorized)
        {
            await HandleExpiredSessionAsync().ConfigureAwait(true);
            return;
        }

        ErrorMessage = GetErrorMessage(result.Error);
    }

    private async Task SearchFromHistoryAsync(string historyKeyword)
    {
        var normalized = historyKeyword.Trim();
        if (!CanSearch(normalized))
        {
            return;
        }

        SetKeywordWithoutScheduling(normalized);
        await ExecuteSearchAsync(normalized, force: true).ConfigureAwait(true);
    }

    private async Task RemoveHistoryAsync(string historyKeyword)
    {
        SearchHistory = SearchHistory
            .Where(value => !string.Equals(value, historyKeyword, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        await SaveHistoryAsync().ConfigureAwait(true);
    }

    private async Task ClearHistoryAsync()
    {
        SearchHistory = Array.Empty<string>();
        await SaveHistoryAsync().ConfigureAwait(true);
    }

    private async Task EnsureHistoryLoadedAsync()
    {
        if (historyLoaded)
        {
            return;
        }

        historyLoaded = true;
        try
        {
            SearchHistory = (await appSettingsService
                    .GetSearchHistoryAsync(CancellationToken.None)
                    .ConfigureAwait(true))
                .Where(CanSearch)
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumHistoryCount)
                .ToArray();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Unable to load search history: {exception.Message}");
            SearchHistory = Array.Empty<string>();
        }
    }

    private async Task RecordHistoryAsync(string normalizedKeyword, CancellationToken cancellationToken)
    {
        await EnsureHistoryLoadedAsync().ConfigureAwait(true);
        SearchHistory = new[] { normalizedKeyword }
            .Concat(SearchHistory.Where(value =>
                !string.Equals(value, normalizedKeyword, StringComparison.OrdinalIgnoreCase)))
            .Take(MaximumHistoryCount)
            .ToArray();

        try
        {
            await appSettingsService
                .SaveSearchHistoryAsync(SearchHistory, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Unable to save search history: {exception.Message}");
        }
    }

    private async Task SaveHistoryAsync()
    {
        try
        {
            await appSettingsService
                .SaveSearchHistoryAsync(SearchHistory, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Unable to save search history: {exception.Message}");
        }
    }

    private async Task HandleExpiredSessionAsync()
    {
        await ExpiredSessionRecovery
            .ClearAsync(authSessionStore, currentSessionService, "search")
            .ConfigureAwait(true);
        navigationService.NavigateTo(AppPage.Login);
        showLoginError(SessionExpiredMessage);
    }

    private void SetAllResults(IReadOnlyList<SearchResultItemViewModel> value)
    {
        allResults = value;
        defaultResults = BuildDefaultResults(value);
        foreach (var filter in Filters)
        {
            filter.Count = filter.Key == "all"
                ? defaultResults.Count
                : value.Count(item => IsTypeMatch(item.RawType, filter.Key));
        }

        VisibleFilters = Filters
            .Where(filter => filter.Key == "all" || value.Any(item => IsTypeMatch(item.RawType, filter.Key)))
            .ToArray();
        SelectFilter(Filters[0]);
    }

    private void SelectFilter(SearchFilterViewModel selectedFilter)
    {
        foreach (var filter in Filters)
        {
            filter.IsSelected = ReferenceEquals(filter, selectedFilter);
        }

        Results = selectedFilter.Key == "all"
            ? defaultResults
            : allResults
                .Where(item => IsTypeMatch(item.RawType, selectedFilter.Key))
                .ToArray();
    }

    private static IReadOnlyList<SearchResultItemViewModel> BuildDefaultResults(
        IReadOnlyList<SearchResultItemViewModel> value)
    {
        var seriesTitles = value
            .Where(item => IsTypeMatch(item.RawType, "series"))
            .Select(item => NormalizeRelationshipTitle(item.Title))
            .Where(title => title.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        if (seriesTitles.Count == 0)
        {
            return value;
        }

        return value
            .Where(item => !ShouldCollapseEpisode(item, seriesTitles))
            .ToArray();
    }

    private static bool ShouldCollapseEpisode(
        SearchResultItemViewModel item,
        IReadOnlySet<string> seriesTitles)
    {
        if (!IsTypeMatch(item.RawType, "episode") ||
            item.MatchInfo?.IsSeriesNameOnlyMatch != true ||
            string.IsNullOrWhiteSpace(item.MatchInfo.SeriesName))
        {
            return false;
        }

        return seriesTitles.Contains(NormalizeRelationshipTitle(item.MatchInfo.SeriesName));
    }

    private static string NormalizeRelationshipTitle(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Normalize(NormalizationForm.FormKC).ToUpper(CultureInfo.InvariantCulture);
    }

    private void ResetToInitialState(bool clearKeyword)
    {
        if (clearKeyword)
        {
            SetKeywordWithoutScheduling(string.Empty);
        }

        IsFavoritesMode = false;
        lastExecutedKeyword = null;
        displayedKeyword = null;
        hasCompletedSearch = false;
        LastSearchDiagnostics = null;
        ErrorMessage = null;
        SetAllResults(Array.Empty<SearchResultItemViewModel>());
        IsLoading = false;
        NotifySearchStateChanged();
    }

    private Task ResetToInitialStateAsync(bool clearKeyword)
    {
        CancelPendingSearch();
        ResetToInitialState(clearKeyword);
        return Task.CompletedTask;
    }

    private void SetKeywordWithoutScheduling(string value)
    {
        suppressKeywordSearch = true;
        try
        {
            Keyword = value;
        }
        finally
        {
            suppressKeywordSearch = false;
        }
    }

    private void NotifySearchStateChanged()
    {
        OnPropertyChanged(nameof(Keyword));
        OnPropertyChanged(nameof(HasKeyword));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(IsInitialLoading));
        OnPropertyChanged(nameof(IsRefreshing));
        OnPropertyChanged(nameof(IsErrorVisible));
        OnPropertyChanged(nameof(IsRefreshErrorVisible));
        OnPropertyChanged(nameof(IsContentVisible));
        OnPropertyChanged(nameof(IsResultsVisible));
        OnPropertyChanged(nameof(IsInitialState));
        OnPropertyChanged(nameof(IsHistoryVisible));
        OnPropertyChanged(nameof(IsInitialHintVisible));
        OnPropertyChanged(nameof(IsEmptyKeywordVisible));
        OnPropertyChanged(nameof(IsNoResultsVisible));
        OnPropertyChanged(nameof(IsResultSummaryVisible));
        OnPropertyChanged(nameof(AreFiltersVisible));
        OnPropertyChanged(nameof(InitialHintTitle));
        OnPropertyChanged(nameof(InitialHintMessage));
        OnPropertyChanged(nameof(NoResultsTitle));
        OnPropertyChanged(nameof(NoResultsMessage));
        OnPropertyChanged(nameof(ResultTitle));
        OnPropertyChanged(nameof(ResultCountText));
    }

    private bool IsCurrentRequest(long requestVersion, string normalizedKeyword)
    {
        return requestVersion == searchVersion
            && string.Equals(Keyword.Trim(), normalizedKeyword, StringComparison.Ordinal);
    }

    private void CancelPendingSearch()
    {
        CancelDebounce();
        CancelActiveRequest();
        searchVersion++;
    }

    private void CancelDebounce()
    {
        debounceCancellation?.Cancel();
        debounceCancellation?.Dispose();
        debounceCancellation = null;
    }

    private void CancelActiveRequest()
    {
        searchCancellation?.Cancel();
        searchCancellation?.Dispose();
        searchCancellation = null;
    }

    private static bool CanSearch(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Trim().Length >= 2;
    }

    private static string GetErrorMessage(SearchLoadError error)
    {
        return error switch
        {
            SearchLoadError.Forbidden => "没有权限搜索或查看此内容",
            SearchLoadError.ServerTimeout => "搜索超时，请稍后重试",
            SearchLoadError.ServerUnreachable => "无法搜索媒体，请检查网络或服务器",
            _ => "搜索失败，请稍后重试"
        };
    }

    private static bool IsTypeMatch(string itemType, string filterKey)
    {
        return itemType.Trim().ToLowerInvariant() switch
        {
            "movie" => filterKey == "movie",
            "series" => filterKey == "series",
            "episode" => filterKey == "episode",
            "boxset" => filterKey == "boxset",
            "playlist" or "playlists" => filterKey == "playlist",
            "video" => filterKey == "video",
            _ => false
        };
    }
}
