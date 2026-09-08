using System.Windows.Input;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Home;
using EmbyPlayer.Core.Playback;
using EmbyPlayer.Core.Servers;
using EmbyPlayer.UI.Navigation;

namespace EmbyPlayer.UI.ViewModels;

public sealed partial class HomeViewModel : ViewModelBase
{
    private const int MaxHeroItems = 5;
    private const string SessionExpiredMessage = "登录状态已失效，请重新登录";
    private static readonly TimeSpan SnapshotFreshness = TimeSpan.FromSeconds(30);
    private readonly IAuthSessionStore authSessionStore;
    private readonly ICurrentSessionService currentSessionService;
    private readonly IHomeService homeService;
    private readonly INavigationService navigationService;
    private readonly IPlaybackService playbackService;
    private readonly Action<string> showLoginError;
    private readonly TimeProvider timeProvider;
    private CancellationTokenSource? loadCancellation;
    private Task? loadTask;
    private string? loadTaskScope;
    private string? currentScope;
    private string? errorMessage;
    private IReadOnlyList<HomeMediaCardViewModel> continueWatching = Array.Empty<HomeMediaCardViewModel>();
    private int currentHeroIndex = -1;
    private IReadOnlyList<HomeHeroItemViewModel> heroItems = Array.Empty<HomeHeroItemViewModel>();
    private bool hasLoadedSuccessfully;
    private DateTimeOffset? lastSuccessfulLoadUtc;
    private bool isLoading;
    private bool isPreparingPlayback;
    private IReadOnlyList<HomeLibraryViewModel> libraries = Array.Empty<HomeLibraryViewModel>();
    private IReadOnlyList<HomeMediaSectionViewModel> mediaSections = Array.Empty<HomeMediaSectionViewModel>();
    private IReadOnlyList<HomeMediaCardViewModel> recentlyAdded = Array.Empty<HomeMediaCardViewModel>();
    private string? playbackErrorMessage;
    private string? searchPlaceholderMessage;
    private string searchKeyword = string.Empty;
    private long scopeVersion;

    public HomeViewModel(
        INavigationService navigationService,
        IHomeService homeService,
        IPlaybackService playbackService,
        ICurrentSessionService currentSessionService,
        IAuthSessionStore authSessionStore,
        Action<string> showLoginError,
        TimeProvider? timeProvider = null,
        EmbyPlayer.Core.WatchLater.IWatchLaterStore? watchLaterStore = null,
        EmbyPlayer.Core.PlaybackQueue.IPlaybackQueueService? playbackQueue = null)
    {
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        this.homeService = homeService ?? throw new ArgumentNullException(nameof(homeService));
        this.playbackService = playbackService ?? throw new ArgumentNullException(nameof(playbackService));
        this.currentSessionService = currentSessionService ?? throw new ArgumentNullException(nameof(currentSessionService));
        this.authSessionStore = authSessionStore ?? throw new ArgumentNullException(nameof(authSessionStore));
        this.showLoginError = showLoginError ?? throw new ArgumentNullException(nameof(showLoginError));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        InitializePersonalMedia(watchLaterStore, playbackQueue);
        SectionViewModel = new HomeSectionViewModel(
            navigationService, homeService, currentSessionService, authSessionStore, showLoginError);

        LoadCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading);
        RetryCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading);
        OpenMediaCommand = new RelayCommand(parameter =>
        {
            if (parameter is HomeMediaCardViewModel mediaCard)
            {
                navigationService.NavigateTo(AppPage.Detail, mediaCard.Id);
            }
        });
        OpenContinueMediaCommand = new RelayCommand(parameter =>
        {
            if (parameter is HomeMediaCardViewModel mediaCard)
            {
                navigationService.NavigateTo(
                    AppPage.Detail,
                    CreateContinueDetailParameter(mediaCard));
            }
        });
        ContinueMediaCommand = new RelayCommand(
            parameter => StartPlayback(parameter, resume: true),
            CanPreparePlayback);
        PlayMediaCommand = new RelayCommand(
            parameter => StartPlayback(parameter, resume: false),
            CanPreparePlayback);
        SubmitSearchCommand = new RelayCommand(_ => SubmitSearch());
        OpenSettingsCommand = new RelayCommand(_ => navigationService.NavigateTo(AppPage.Settings));
        OpenFavoritesCommand = new RelayCommand(_ => navigationService.NavigateTo(AppPage.Search, SearchNavigationParameter.Favorites()));
        OpenWatchLaterCommand = new RelayCommand(_ => navigationService.NavigateTo(AppPage.WatchLater));
        OpenPlaybackQueueCommand = new RelayCommand(_ => navigationService.NavigateTo(AppPage.PlaybackQueue));
        NextHeroCommand = new RelayCommand(_ => MoveHero(1), _ => HasMultipleHeroCards);
        PreviousHeroCommand = new RelayCommand(_ => MoveHero(-1), _ => HasMultipleHeroCards);
        SelectHeroCommand = new RelayCommand(
            parameter => SelectHero(parameter as HomeHeroItemViewModel),
            parameter => parameter is HomeHeroItemViewModel heroItem && HeroItems.Contains(heroItem));
        OpenLibraryCommand = new RelayCommand(parameter =>
        {
            if (parameter is HomeLibraryViewModel library)
            {
                navigationService.NavigateTo(AppPage.Library, library.Id);
            }
        });
        OpenSectionCommand = new RelayCommand(parameter =>
        {
            var section = parameter is HomeSectionKind kind ? kind : parameter switch
            {
                "movies" => HomeSectionKind.Movies,
                "series" => HomeSectionKind.Series,
                "animation" => HomeSectionKind.Animation,
                "boxsets" => HomeSectionKind.BoxSets,
                _ => (HomeSectionKind?)null
            };
            if (section.HasValue)
            {
                navigationService.NavigateTo(AppPage.HomeSection, section.Value);
            }
        });
    }

    public HomeSectionViewModel SectionViewModel { get; }

    public ICommand OpenSectionCommand { get; }

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
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsInitialLoading));
            OnPropertyChanged(nameof(IsRefreshing));
            OnPropertyChanged(nameof(IsContentVisible));
            OnPropertyChanged(nameof(IsErrorVisible));
            OnPropertyChanged(nameof(IsRefreshErrorVisible));
            NotifyLoadCommandsCanExecuteChanged();
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
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(IsContentVisible));
            OnPropertyChanged(nameof(IsErrorVisible));
            OnPropertyChanged(nameof(IsRefreshErrorVisible));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool IsInitialLoading => IsLoading && !HasLoadedSuccessfully;

    public bool IsRefreshing => IsLoading && HasLoadedSuccessfully;

    public bool IsErrorVisible => HasError && !IsLoading && !HasLoadedSuccessfully;

    public bool IsRefreshErrorVisible => HasError && !IsLoading && HasLoadedSuccessfully;

    public bool IsContentVisible => HasLoadedSuccessfully;

    public bool HasLoadedSuccessfully
    {
        get => hasLoadedSuccessfully;
        private set
        {
            if (hasLoadedSuccessfully == value)
            {
                return;
            }

            hasLoadedSuccessfully = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsInitialLoading));
            OnPropertyChanged(nameof(IsRefreshing));
            OnPropertyChanged(nameof(IsContentVisible));
            OnPropertyChanged(nameof(IsErrorVisible));
            OnPropertyChanged(nameof(IsRefreshErrorVisible));
        }
    }

    public bool IsPreparingPlayback
    {
        get => isPreparingPlayback;
        private set
        {
            if (isPreparingPlayback == value)
            {
                return;
            }

            isPreparingPlayback = value;
            OnPropertyChanged();
            NotifyPlaybackCommandsCanExecuteChanged();
        }
    }

    public string? PlaybackErrorMessage
    {
        get => playbackErrorMessage;
        private set
        {
            if (playbackErrorMessage == value)
            {
                return;
            }

            playbackErrorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPlaybackErrorVisible));
        }
    }

    public bool IsPlaybackErrorVisible => !string.IsNullOrWhiteSpace(PlaybackErrorMessage);

    public string WelcomeText => $"欢迎，{GetCurrentUserName()}";

    public string? SearchPlaceholderMessage
    {
        get => searchPlaceholderMessage;
        private set
        {
            if (searchPlaceholderMessage == value)
            {
                return;
            }

            searchPlaceholderMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSearchPlaceholderMessageVisible));
        }
    }

    public bool IsSearchPlaceholderMessageVisible => !string.IsNullOrWhiteSpace(SearchPlaceholderMessage);

    public string SearchKeyword
    {
        get => searchKeyword;
        set
        {
            if (searchKeyword == value)
            {
                return;
            }

            searchKeyword = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<HomeHeroItemViewModel> HeroItems
    {
        get => heroItems;
        private set
        {
            heroItems = value;
            currentHeroIndex = value.Count > 0 ? 0 : -1;

            for (var index = 0; index < value.Count; index++)
            {
                value[index].IsSelected = index == currentHeroIndex;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentHeroIndex));
            OnPropertyChanged(nameof(HeroCard));
            OnPropertyChanged(nameof(HasHeroCard));
            OnPropertyChanged(nameof(HasMultipleHeroCards));
            NotifyHeroCommandsCanExecuteChanged();
        }
    }

    public int CurrentHeroIndex => currentHeroIndex;

    public HomeMediaCardViewModel? HeroCard => currentHeroIndex >= 0 && currentHeroIndex < HeroItems.Count
        ? HeroItems[currentHeroIndex].Card
        : null;

    public bool HasHeroCard => HeroCard is not null;

    public bool HasMultipleHeroCards => HeroItems.Count > 1;

    public IReadOnlyList<HomeMediaCardViewModel> ContinueWatching
    {
        get => continueWatching;
        private set
        {
            continueWatching = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsContinueWatchingEmpty));
        }
    }

    public IReadOnlyList<HomeMediaCardViewModel> RecentlyAdded
    {
        get => recentlyAdded;
        private set
        {
            recentlyAdded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRecentlyAddedEmpty));
        }
    }

    public IReadOnlyList<HomeMediaSectionViewModel> MediaSections
    {
        get => mediaSections;
        private set
        {
            mediaSections = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<HomeLibraryViewModel> Libraries
    {
        get => libraries;
        private set
        {
            libraries = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLibrariesEmpty));
        }
    }

    public bool IsContinueWatchingEmpty => ContinueWatching.Count == 0;

    public bool IsRecentlyAddedEmpty => RecentlyAdded.Count == 0;

    public bool IsLibrariesEmpty => Libraries.Count == 0;

    public ICommand LoadCommand { get; }

    public ICommand RetryCommand { get; }

    public ICommand OpenMediaCommand { get; }

    public ICommand OpenContinueMediaCommand { get; }

    public ICommand ContinueMediaCommand { get; }

    public ICommand PlayMediaCommand { get; }

    public ICommand OpenLibraryCommand { get; }

    public ICommand SubmitSearchCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand OpenFavoritesCommand { get; }
    public ICommand OpenWatchLaterCommand { get; }
    public ICommand OpenPlaybackQueueCommand { get; }

    public ICommand NextHeroCommand { get; }

    public ICommand PreviousHeroCommand { get; }

    public ICommand SelectHeroCommand { get; }

    public Task NavigateAsync()
    {
        return Task.WhenAll(EnsureLoadedAsync(force: false, returnBackgroundRefresh: false), LoadWatchLaterPreviewAsync());
    }

    public Task LoadAsync()
    {
        return Task.WhenAll(EnsureLoadedAsync(force: true, returnBackgroundRefresh: true), LoadWatchLaterPreviewAsync());
    }

    public void CancelPendingLoad()
    {
        CancelWatchLaterPreviewLoad();
        scopeVersion++;
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        loadCancellation = null;
        loadTask = null;
        loadTaskScope = null;
        IsLoading = false;
    }

    private Task EnsureLoadedAsync(bool force, bool returnBackgroundRefresh)
    {
        var session = currentSessionService.CurrentSession;
        if (session is null)
        {
            navigationService.NavigateTo(AppPage.Login);
            showLoginError("请先登录");
            return Task.CompletedTask;
        }

        var scope = CreateScope(session);
        EnsureScope(scope);

        if (loadTask is not null && string.Equals(loadTaskScope, scope, StringComparison.Ordinal))
        {
            return HasLoadedSuccessfully && !returnBackgroundRefresh
                ? Task.CompletedTask
                : loadTask;
        }

        if (!force && IsSnapshotFresh())
        {
            return Task.CompletedTask;
        }

        var startedLoad = StartLoad(session, scope);
        return HasLoadedSuccessfully && !returnBackgroundRefresh
            ? Task.CompletedTask
            : startedLoad;
    }

    private Task StartLoad(AuthSession session, string scope)
    {
        var cancellation = new CancellationTokenSource();
        var requestVersion = scopeVersion;
        IsLoading = true;
        ErrorMessage = null;
        PlaybackErrorMessage = null;
        OnPropertyChanged(nameof(WelcomeText));

        var startedLoad = ExecuteLoadAsync(session, scope, requestVersion, cancellation.Token);
        loadCancellation = cancellation;
        loadTask = startedLoad;
        loadTaskScope = scope;
        _ = ObserveLoadCompletionAsync(startedLoad, scope, requestVersion, cancellation);
        return startedLoad;
    }

    private async Task ExecuteLoadAsync(
        AuthSession session,
        string scope,
        long requestVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await homeService
                .LoadHomeAsync(session, cancellationToken)
                .ConfigureAwait(true);

            if (!IsCurrentRequest(scope, requestVersion))
            {
                return;
            }

            if (result.IsSuccess && result.Data is not null)
            {
                ApplyHomeData(result.Data);
                lastSuccessfulLoadUtc = timeProvider.GetUtcNow();
                return;
            }

            if (result.Error == HomeLoadError.Unauthorized)
            {
                await HandleExpiredSessionAsync().ConfigureAwait(true);
                return;
            }

            ErrorMessage = GetErrorMessage(result.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Scope changes and app shutdown intentionally abandon the old request.
        }
        catch
        {
            if (IsCurrentRequest(scope, requestVersion))
            {
                ErrorMessage = GetErrorMessage(HomeLoadError.InvalidResponse);
            }
        }
    }

    private async Task ObserveLoadCompletionAsync(
        Task startedLoad,
        string scope,
        long requestVersion,
        CancellationTokenSource cancellation)
    {
        try
        {
            await startedLoad.ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(loadTask, startedLoad))
            {
                loadTask = null;
                loadTaskScope = null;
                loadCancellation = null;
            }

            cancellation.Dispose();
            if (IsCurrentRequest(scope, requestVersion))
            {
                IsLoading = false;
            }
        }
    }

    private void EnsureScope(string scope)
    {
        if (string.Equals(currentScope, scope, StringComparison.Ordinal))
        {
            return;
        }

        scopeVersion++;
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        loadCancellation = null;
        loadTask = null;
        loadTaskScope = null;
        currentScope = scope;
        lastSuccessfulLoadUtc = null;
        ResetHomeSnapshot();
    }

    private bool IsSnapshotFresh()
    {
        return HasLoadedSuccessfully
            && lastSuccessfulLoadUtc is { } loadedAt
            && timeProvider.GetUtcNow() - loadedAt < SnapshotFreshness;
    }

    private bool IsCurrentRequest(string scope, long requestVersion)
    {
        return requestVersion == scopeVersion
            && string.Equals(currentScope, scope, StringComparison.Ordinal);
    }

    private static string CreateScope(AuthSession session)
    {
        var normalization = ServerUrlNormalizer.Normalize(session.ServerBase);
        var serverBase = normalization.IsSuccess && normalization.Server is not null
            ? normalization.Server.ServerBase
            : session.ServerBase.Trim().TrimEnd('/');

        return $"{serverBase.ToUpperInvariant()}\u001f{session.UserId.Trim().ToUpperInvariant()}";
    }

    private void ResetHomeSnapshot()
    {
        IsLoading = false;
        ErrorMessage = null;
        PlaybackErrorMessage = null;
        ContinueWatching = Array.Empty<HomeMediaCardViewModel>();
        RecentlyAdded = Array.Empty<HomeMediaCardViewModel>();
        MediaSections = Array.Empty<HomeMediaSectionViewModel>();
        HeroItems = Array.Empty<HomeHeroItemViewModel>();
        Libraries = Array.Empty<HomeLibraryViewModel>();
        HasLoadedSuccessfully = false;
        OnPropertyChanged(nameof(WelcomeText));
    }

    private void ApplyHomeData(HomeData homeData)
    {
        var loadedContinueWatching = homeData.ContinueWatching
            .Select(item => new HomeMediaCardViewModel(item))
            .ToArray();
        var loadedRecentlyAdded = homeData.RecentlyAdded
            .Select(item => new HomeMediaCardViewModel(item))
            .ToArray();

        ContinueWatching = loadedContinueWatching;
        RecentlyAdded = loadedRecentlyAdded;
        MediaSections = MapSections(homeData.MediaSections, MediaSections);
        HeroItems = BuildHeroItems(loadedContinueWatching, loadedRecentlyAdded);
        Libraries = homeData.Libraries
            .Select((item, index) => new { Item = item, Index = index })
            .OrderBy(entry => GetLibrarySortRank(entry.Item))
            .ThenBy(entry => entry.Index)
            .Select(entry => new HomeLibraryViewModel(entry.Item))
            .ToArray();
        HasLoadedSuccessfully = true;
    }

    private static IReadOnlyList<HomeMediaSectionViewModel> MapSections(
        IReadOnlyList<HomeMediaSection> sections,
        IReadOnlyList<HomeMediaSectionViewModel> currentSections)
    {
        return sections
            .Select(section => new HomeMediaSectionViewModel(
                section,
                currentSections.FirstOrDefault(current => current.Id == section.Id)))
            .Where(section => section.IsVisible)
            .ToArray();
    }

    private static IReadOnlyList<HomeHeroItemViewModel> BuildHeroItems(
        IReadOnlyList<HomeMediaCardViewModel> loadedContinueWatching,
        IReadOnlyList<HomeMediaCardViewModel> loadedRecentlyAdded)
    {
        var selectedCards = new List<HomeMediaCardViewModel>(MaxHeroItems);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        AddCandidates(loadedRecentlyAdded.Where(item => item.HasDedicatedHeroImage));
        AddCandidates(loadedContinueWatching.Where(item => item.HasDedicatedHeroImage));
        AddCandidates(loadedContinueWatching);
        AddCandidates(loadedRecentlyAdded);

        return selectedCards
            .Select((card, index) => new HomeHeroItemViewModel(card, index + 1))
            .ToArray();

        void AddCandidates(IEnumerable<HomeMediaCardViewModel> candidates)
        {
            foreach (var candidate in candidates)
            {
                if (selectedCards.Count >= MaxHeroItems)
                {
                    return;
                }

                var identity = string.IsNullOrWhiteSpace(candidate.Id)
                    ? $"{candidate.Type}\u001f{candidate.Title}"
                    : candidate.Id;
                if (seenIds.Add(identity))
                {
                    selectedCards.Add(candidate);
                }
            }
        }
    }

    private void MoveHero(int direction)
    {
        if (!HasMultipleHeroCards)
        {
            return;
        }

        var nextIndex = (currentHeroIndex + direction) % HeroItems.Count;
        if (nextIndex < 0)
        {
            nextIndex += HeroItems.Count;
        }

        SetCurrentHeroIndex(nextIndex);
    }

    private void SelectHero(HomeHeroItemViewModel? heroItem)
    {
        if (heroItem is null)
        {
            return;
        }

        for (var index = 0; index < HeroItems.Count; index++)
        {
            if (ReferenceEquals(HeroItems[index], heroItem))
            {
                SetCurrentHeroIndex(index);
                return;
            }
        }
    }

    private void SetCurrentHeroIndex(int index)
    {
        if (index < 0 || index >= HeroItems.Count || index == currentHeroIndex)
        {
            return;
        }

        for (var itemIndex = 0; itemIndex < HeroItems.Count; itemIndex++)
        {
            HeroItems[itemIndex].IsSelected = itemIndex == index;
        }

        currentHeroIndex = index;
        OnPropertyChanged(nameof(CurrentHeroIndex));
        OnPropertyChanged(nameof(HeroCard));
    }

    private void NotifyHeroCommandsCanExecuteChanged()
    {
        if (NextHeroCommand is RelayCommand nextHeroCommand)
        {
            nextHeroCommand.RaiseCanExecuteChanged();
        }

        if (PreviousHeroCommand is RelayCommand previousHeroCommand)
        {
            previousHeroCommand.RaiseCanExecuteChanged();
        }

        if (SelectHeroCommand is RelayCommand selectHeroCommand)
        {
            selectHeroCommand.RaiseCanExecuteChanged();
        }
    }

    private static int GetLibrarySortRank(MediaLibrary library)
    {
        var type = library.Type.Trim().ToLowerInvariant();
        if (type is "movies" or "movie")
        {
            return 0;
        }

        if (type is "tvshows" or "tv" or "series")
        {
            return IsAnimationLibrary(library) ? 2 : 1;
        }

        if (IsAnimationLibrary(library))
        {
            return 2;
        }

        return type switch
        {
            "collections" or "boxsets" => 3,
            "playlists" => 4,
            _ => 5
        };
    }

    private static bool IsAnimationLibrary(MediaLibrary library)
    {
        return MediaLibraryClassifier.IsAnimationLibrary(library);
    }

    private async Task HandleExpiredSessionAsync()
    {
        await ExpiredSessionRecovery
            .ClearAsync(authSessionStore, currentSessionService, "home")
            .ConfigureAwait(true);
        OnPropertyChanged(nameof(WelcomeText));
        navigationService.NavigateTo(AppPage.Login);
        showLoginError(SessionExpiredMessage);
    }

    private void StartPlayback(object? parameter, bool resume)
    {
        if (parameter is HomeMediaCardViewModel mediaCard)
        {
            _ = PreparePlaybackAsync(mediaCard, resume);
        }
    }

    private async Task PreparePlaybackAsync(HomeMediaCardViewModel mediaCard, bool resume)
    {
        if (IsPreparingPlayback || !mediaCard.IsPlayableMedia)
        {
            return;
        }

        var session = currentSessionService.CurrentSession;
        if (session is null)
        {
            navigationService.NavigateTo(AppPage.Login);
            showLoginError("请先登录");
            return;
        }

        mediaCard.IsPreparingPlayback = true;
        IsPreparingPlayback = true;
        PlaybackErrorMessage = null;

        try
        {
            var startPositionTicks = resume ? mediaCard.ResumePositionTicks : 0;
            var request = new PlaybackStartRequest(
                mediaCard.Id,
                mediaCard.Title,
                startPositionTicks,
                mediaCard.Type,
                mediaCard.Year);
            var result = await playbackService
                .PreparePlaybackAsync(session, request, CancellationToken.None)
                .ConfigureAwait(true);

            if (result.IsSuccess && result.PlaybackInfo is not null)
            {
                navigationService.NavigateTo(
                    AppPage.Player,
                    new PlayerNavigationParameter(
                        result.PlaybackInfo,
                        CreateContinueDetailParameter(mediaCard),
                        mediaCard.LogoUrl));
                return;
            }

            if (result.Error == PlaybackLoadError.Unauthorized)
            {
                await HandleExpiredSessionAsync().ConfigureAwait(true);
                return;
            }

            PlaybackErrorMessage = GetPlaybackErrorMessage(result.Error);
        }
        catch
        {
            PlaybackErrorMessage = "播放准备失败，请稍后重试";
        }
        finally
        {
            IsPreparingPlayback = false;
            mediaCard.IsPreparingPlayback = false;
        }
    }

    private bool CanPreparePlayback(object? parameter)
    {
        return !IsPreparingPlayback
            && parameter is HomeMediaCardViewModel { IsPlayableMedia: true };
    }

    private static DetailNavigationParameter CreateContinueDetailParameter(
        HomeMediaCardViewModel mediaCard)
    {
        if (string.Equals(mediaCard.Type, "Episode", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(mediaCard.SeriesId))
        {
            return new DetailNavigationParameter(
                mediaCard.SeriesId,
                AppPage.Home,
                SelectedSeasonId: mediaCard.SeasonId,
                FocusedEpisodeId: mediaCard.Id,
                FallbackItemId: mediaCard.Id);
        }

        return new DetailNavigationParameter(mediaCard.Id, AppPage.Home);
    }

    private void NotifyPlaybackCommandsCanExecuteChanged()
    {
        if (ContinueMediaCommand is RelayCommand continueMediaCommand)
        {
            continueMediaCommand.RaiseCanExecuteChanged();
        }

        if (PlayMediaCommand is RelayCommand playMediaCommand)
        {
            playMediaCommand.RaiseCanExecuteChanged();
        }
    }

    private string GetCurrentUserName()
    {
        return currentSessionService.CurrentSession?.UserName ?? "用户";
    }

    private void NotifyLoadCommandsCanExecuteChanged()
    {
        if (LoadCommand is AsyncRelayCommand loadCommand)
        {
            loadCommand.NotifyCanExecuteChanged();
        }

        if (RetryCommand is AsyncRelayCommand retryCommand)
        {
            retryCommand.NotifyCanExecuteChanged();
        }
    }

    private void SubmitSearch()
    {
        var keyword = SearchKeyword.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            SearchPlaceholderMessage = "\u8bf7\u8f93\u5165\u641c\u7d22\u5173\u952e\u8bcd";
            return;
        }

        SearchPlaceholderMessage = null;
        navigationService.NavigateTo(AppPage.Search, SearchNavigationParameter.Search(keyword));
    }

    private static string GetErrorMessage(HomeLoadError error)
    {
        return error switch
        {
            HomeLoadError.Forbidden => "没有权限加载首页内容",
            HomeLoadError.ServerTimeout => "首页加载超时，请稍后重试",
            HomeLoadError.ServerUnreachable => "无法加载首页，请检查网络或服务器",
            HomeLoadError.Cancelled => "首页加载已取消",
            _ => "首页加载失败，请稍后重试"
        };
    }

    private static string GetPlaybackErrorMessage(PlaybackLoadError error)
    {
        return error switch
        {
            PlaybackLoadError.Forbidden => "没有权限播放此媒体",
            PlaybackLoadError.NoPlayableMediaSource => "未找到可播放的媒体源",
            PlaybackLoadError.NotFound => "媒体不存在或已不可用",
            PlaybackLoadError.ServerTimeout => "播放准备超时，请稍后重试",
            PlaybackLoadError.ServerUnreachable => "无法准备播放，请检查网络或服务器",
            PlaybackLoadError.Cancelled => "播放准备已取消",
            PlaybackLoadError.InvalidResponse => "播放信息无法识别，请稍后重试",
            _ => "播放准备失败，请稍后重试"
        };
    }
}
