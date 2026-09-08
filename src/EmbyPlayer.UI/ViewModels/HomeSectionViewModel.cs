using System.Diagnostics;
using System.Windows.Input;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Home;
using EmbyPlayer.UI.Navigation;

namespace EmbyPlayer.UI.ViewModels;

public sealed class HomeSectionViewModel : ViewModelBase
{
    private const int PageSize = 48;
    private readonly INavigationService navigationService;
    private readonly IHomeService homeService;
    private readonly ICurrentSessionService currentSessionService;
    private readonly IAuthSessionStore authSessionStore;
    private readonly Action<string> showLoginError;
    private AuthSession? loadedSession;
    private CancellationTokenSource? loadCancellation;
    private long generation;
    private HomeSectionKind? selectedSection;
    private IReadOnlyList<HomeMediaCardViewModel> items = Array.Empty<HomeMediaCardViewModel>();
    private bool hasLoadedSuccessfully;
    private bool isLoading;
    private bool isLoadingMore;
    private bool hasMore;
    private int nextStartIndex;
    private string? errorMessage;
    private string? loadMoreErrorMessage;

    public HomeSectionViewModel(
        INavigationService navigationService,
        IHomeService homeService,
        ICurrentSessionService currentSessionService,
        IAuthSessionStore authSessionStore,
        Action<string> showLoginError)
    {
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        this.homeService = homeService ?? throw new ArgumentNullException(nameof(homeService));
        this.currentSessionService = currentSessionService ?? throw new ArgumentNullException(nameof(currentSessionService));
        this.authSessionStore = authSessionStore ?? throw new ArgumentNullException(nameof(authSessionStore));
        this.showLoginError = showLoginError ?? throw new ArgumentNullException(nameof(showLoginError));
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsLoading && !IsLoadingMore);
        RetryCommand = new AsyncRelayCommand(RefreshAsync, () => !IsLoading && !IsLoadingMore);
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync, () => CanLoadMore);
        NavigateHomeCommand = new RelayCommand(_ => navigationService.NavigateTo(AppPage.Home));
        OpenMediaCommand = new RelayCommand(parameter =>
        {
            if (parameter is HomeMediaCardViewModel card)
            {
                var isEpisode = string.Equals(card.Type, "Episode", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(card.SeriesId);
                navigationService.NavigateTo(AppPage.Detail, isEpisode
                    ? new DetailNavigationParameter(card.SeriesId, AppPage.HomeSection,
                        SelectedSeasonId: card.SeasonId, FocusedEpisodeId: card.Id, FallbackItemId: card.Id)
                    : new DetailNavigationParameter(card.Id, AppPage.HomeSection));
            }
        });
    }

    public HomeSectionKind? SelectedSection => selectedSection;

    public string Title => selectedSection switch
    {
        HomeSectionKind.ContinueWatching => "继续观看",
        HomeSectionKind.RecentlyAdded => "最近添加",
        HomeSectionKind.Movies => "电影",
        HomeSectionKind.Series => "电视节目",
        HomeSectionKind.Animation => "动画",
        HomeSectionKind.BoxSets => "合集",
        _ => "全部作品"
    };

    public IReadOnlyList<HomeMediaCardViewModel> Items => items;
    public bool IsLoading => isLoading;
    public bool IsLoadingMore => isLoadingMore;
    public bool HasMore => hasMore;
    public bool IsInitialLoading => IsLoading && !hasLoadedSuccessfully;
    public bool IsRefreshing => IsLoading && hasLoadedSuccessfully;
    public bool IsContentVisible => hasLoadedSuccessfully;
    public bool IsEmpty => hasLoadedSuccessfully && !IsLoading && Items.Count == 0 && errorMessage is null;
    public bool IsErrorVisible => errorMessage is not null && !IsLoading && !hasLoadedSuccessfully;
    public bool IsRefreshErrorVisible => errorMessage is not null && !IsLoading && hasLoadedSuccessfully;
    public bool IsLoadMoreErrorVisible => loadMoreErrorMessage is not null && !IsLoadingMore;
    public bool IsLoadMoreButtonVisible => IsContentVisible && HasMore && !IsLoadingMore;
    public bool CanLoadMore => IsContentVisible && HasMore && !IsLoading && !IsLoadingMore;
    public string? ErrorMessage => errorMessage;
    public string? LoadMoreErrorMessage => loadMoreErrorMessage;
    public string LoadMoreButtonText => loadMoreErrorMessage is null ? "加载更多" : "重试加载更多";
    public double ScrollOffset { get; set; }

    public ICommand RefreshCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand NavigateHomeCommand { get; }
    public ICommand OpenMediaCommand { get; }

    public Task LoadAsync(HomeSectionKind? section = null)
    {
        var session = currentSessionService.CurrentSession;
        if (session is null)
        {
            ResetSession();
            navigationService.NavigateTo(AppPage.Login);
            showLoginError("请先登录");
            return Task.CompletedTask;
        }

        if (!ReferenceEquals(loadedSession, session))
        {
            ResetSession();
            loadedSession = session;
        }

        if (section is null && selectedSection.HasValue && hasLoadedSuccessfully)
        {
            return Task.CompletedTask;
        }

        Deactivate();
        selectedSection = section ?? selectedSection ?? HomeSectionKind.ContinueWatching;
        ClearItems();
        NotifyStateChanged();
        return LoadPageAsync(session, append: false);
    }

    public Task RefreshAsync()
    {
        var session = currentSessionService.CurrentSession;
        if (session is null || !ReferenceEquals(session, loadedSession) || selectedSection is null)
        {
            return LoadAsync(selectedSection);
        }

        Deactivate();
        return LoadPageAsync(session, append: false);
    }

    public Task LoadMoreAsync()
    {
        var session = currentSessionService.CurrentSession;
        if (session is null || !ReferenceEquals(session, loadedSession))
        {
            return LoadAsync(selectedSection);
        }

        return CanLoadMore ? LoadPageAsync(session, append: true) : Task.CompletedTask;
    }

    public void Deactivate()
    {
        generation++;
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        loadCancellation = null;
        isLoading = false;
        isLoadingMore = false;
        NotifyStateChanged();
    }

    public void ResetSession()
    {
        Deactivate();
        loadedSession = null;
        selectedSection = null;
        ClearItems();
        NotifyStateChanged();
    }

    private async Task LoadPageAsync(AuthSession session, bool append)
    {
        var requestGeneration = ++generation;
        var cancellation = new CancellationTokenSource();
        loadCancellation = cancellation;
        var cancellationToken = cancellation.Token;
        var startIndex = append ? nextStartIndex : 0;
        isLoading = !append;
        isLoadingMore = append;
        if (append) loadMoreErrorMessage = null;
        else errorMessage = null;
        NotifyStateChanged();

        try
        {
            var result = await homeService.LoadSectionAsync(
                session, selectedSection!.Value, startIndex, PageSize, cancellationToken).ConfigureAwait(true);
            if (!IsCurrentRequest(session, requestGeneration, cancellationToken)) return;

            if (result.IsSuccess)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var loadedItems = append ? Items.ToList() : new List<HomeMediaCardViewModel>();
                foreach (var item in loadedItems) seen.Add(item.Id);
                foreach (var item in result.Items)
                {
                    if (seen.Add(item.Id)) loadedItems.Add(new HomeMediaCardViewModel(item));
                }

                items = loadedItems;
                nextStartIndex = result.NextStartIndex;
                hasMore = result.HasMore;
                hasLoadedSuccessfully = true;
                if (!append)
                {
                    loadMoreErrorMessage = null;
                    ScrollOffset = 0;
                    OnPropertyChanged(nameof(ScrollOffset));
                }
                return;
            }

            if (result.Error == HomeLoadError.Unauthorized)
            {
                await ExpiredSessionRecovery.ClearAsync(
                    authSessionStore, currentSessionService, "home section", session, cancellationToken).ConfigureAwait(true);
                if (requestGeneration != generation || cancellationToken.IsCancellationRequested
                    || currentSessionService.CurrentSession is not null) return;
                ResetSession();
                navigationService.NavigateTo(AppPage.Login);
                showLoginError("登录状态已失效，请重新登录");
            }
            else if (result.Error != HomeLoadError.Cancelled)
            {
                SetError(result.Error, append);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leaving or replacing the section abandons this request.
        }
        catch (Exception exception)
        {
            Trace.TraceError("Home section loading failed: {0}", exception.GetType().Name);
            if (IsCurrentRequest(session, requestGeneration, cancellationToken))
                SetError(HomeLoadError.InvalidResponse, append);
        }
        finally
        {
            if (IsCurrentRequest(session, requestGeneration, cancellationToken))
            {
                isLoading = false;
                isLoadingMore = false;
                loadCancellation = null;
                NotifyStateChanged();
            }
            cancellation.Dispose();
        }
    }

    private bool IsCurrentRequest(AuthSession session, long requestGeneration, CancellationToken cancellationToken)
    {
        return requestGeneration == generation && !cancellationToken.IsCancellationRequested
            && ReferenceEquals(session, loadedSession)
            && ReferenceEquals(session, currentSessionService.CurrentSession);
    }

    private void ClearItems()
    {
        items = Array.Empty<HomeMediaCardViewModel>();
        hasLoadedSuccessfully = false;
        hasMore = false;
        nextStartIndex = 0;
        errorMessage = null;
        loadMoreErrorMessage = null;
        ScrollOffset = 0;
        OnPropertyChanged(nameof(ScrollOffset));
    }

    private void SetError(HomeLoadError error, bool append)
    {
        var message = error switch
        {
            HomeLoadError.Forbidden => "没有权限加载此分类",
            HomeLoadError.ServerTimeout => "加载超时，请稍后重试",
            HomeLoadError.ServerUnreachable => "无法加载作品，请检查网络或服务器",
            HomeLoadError.InvalidResponse => "作品数据无法识别，请稍后重试",
            _ => "加载失败，请稍后重试"
        };
        if (append) loadMoreErrorMessage = message;
        else errorMessage = message;
    }

    private void NotifyStateChanged()
    {
        foreach (var name in new[]
        {
            nameof(SelectedSection), nameof(Title), nameof(Items), nameof(IsLoading), nameof(IsLoadingMore),
            nameof(HasMore), nameof(IsInitialLoading), nameof(IsRefreshing), nameof(IsContentVisible),
            nameof(IsEmpty), nameof(IsErrorVisible), nameof(IsRefreshErrorVisible), nameof(IsLoadMoreErrorVisible),
            nameof(IsLoadMoreButtonVisible), nameof(CanLoadMore), nameof(ErrorMessage),
            nameof(LoadMoreErrorMessage), nameof(LoadMoreButtonText)
        }) OnPropertyChanged(name);
        ((AsyncRelayCommand)RefreshCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)RetryCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)LoadMoreCommand).NotifyCanExecuteChanged();
    }
}
