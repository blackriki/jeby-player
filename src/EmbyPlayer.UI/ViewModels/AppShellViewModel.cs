using System.Collections.ObjectModel;
using System.Windows.Input;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Caching;
using EmbyPlayer.Core.Diagnostics;
using EmbyPlayer.Core.Home;
using EmbyPlayer.Core.Library;
using EmbyPlayer.Core.Search;
using EmbyPlayer.Core.Settings;
using EmbyPlayer.UI.Navigation;

namespace EmbyPlayer.UI.ViewModels;

public sealed class AppShellViewModel : ViewModelBase
{
    private const string NetworkValidationErrorMessage = "暂时无法验证登录状态，请检查网络或服务器。";
    private const string PermissionValidationErrorMessage = "当前账号没有权限访问服务器，请联系服务器管理员。";
    private const string PartialAuthenticationCleanupMessage =
        "安全令牌已清除，但本地账户信息未能完全清理，请稍后重试。";
    private const string ExpiredSessionCleanupFailureMessage =
        "登录状态已失效，但本地登录信息清理失败，请稍后重试。";
    private const string StartupFailureMessage =
        "无法读取本地登录状态，请重新登录或切换服务器。";
    private readonly IAccountSessionService accountSessionService;
    private readonly IAppSettingsService appSettingsService;
    private readonly IAuthSessionStore authSessionStore;
    private readonly IAuthSessionValidator authSessionValidator;
    private readonly ICurrentSessionService currentSessionService;
    private readonly IApplicationDiagnostics diagnostics;
    private readonly HomeViewModel homeViewModel;
    private readonly LibraryViewModel libraryViewModel;
    private readonly ILocalMediaSearchIndex? localMediaSearchIndex;
    private readonly LoginViewModel loginViewModel;
    private readonly MediaDetailViewModel mediaDetailViewModel;
    private readonly INavigationService navigationService;
    private readonly PlayerViewModel playerViewModel;
    private readonly SearchViewModel searchViewModel;
    private readonly ServerConnectionViewModel serverConnectionViewModel;
    private AppPage currentPage;
    private object currentPageViewModel;
    private bool hasInitialized;
    private bool isInitializing;
    private string? shellErrorMessage;
    private string shellSearchText = string.Empty;
    private readonly EmbyPlayer.Core.PlaybackQueue.IPlaybackQueueService? playbackQueue;

    public AppShellViewModel(
        INavigationService navigationService,
        ServerConnectionViewModel serverConnectionViewModel,
        LoginViewModel loginViewModel,
        HomeViewModel homeViewModel,
        LibraryViewModel libraryViewModel,
        SearchViewModel searchViewModel,
        MediaDetailViewModel mediaDetailViewModel,
        PlayerViewModel playerViewModel,
        IAppSettingsService appSettingsService,
        IAuthSessionStore authSessionStore,
        IAuthSessionValidator authSessionValidator,
        ICurrentSessionService currentSessionService,
        IAccountSessionService accountSessionService,
        IMediaLibraryScanService mediaLibraryScanService,
        ILocalMediaSearchIndex? localMediaSearchIndex = null,
        IApplicationDiagnostics? diagnostics = null,
        ICacheManagementService? cacheManagementService = null,
        PersonViewModel? personViewModel = null,
        WatchLaterViewModel? watchLaterViewModel = null,
        EmbyPlayer.Core.PlaybackQueue.IPlaybackQueueService? playbackQueue = null)
    {
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        this.serverConnectionViewModel = serverConnectionViewModel ?? throw new ArgumentNullException(nameof(serverConnectionViewModel));
        this.loginViewModel = loginViewModel ?? throw new ArgumentNullException(nameof(loginViewModel));
        this.homeViewModel = homeViewModel ?? throw new ArgumentNullException(nameof(homeViewModel));
        this.libraryViewModel = libraryViewModel ?? throw new ArgumentNullException(nameof(libraryViewModel));
        this.searchViewModel = searchViewModel ?? throw new ArgumentNullException(nameof(searchViewModel));
        this.mediaDetailViewModel = mediaDetailViewModel ?? throw new ArgumentNullException(nameof(mediaDetailViewModel));
        this.playerViewModel = playerViewModel ?? throw new ArgumentNullException(nameof(playerViewModel));
        this.appSettingsService = appSettingsService ?? throw new ArgumentNullException(nameof(appSettingsService));
        this.authSessionStore = authSessionStore ?? throw new ArgumentNullException(nameof(authSessionStore));
        this.authSessionValidator = authSessionValidator ?? throw new ArgumentNullException(nameof(authSessionValidator));
        this.currentSessionService = currentSessionService ?? throw new ArgumentNullException(nameof(currentSessionService));
        this.accountSessionService = accountSessionService ?? throw new ArgumentNullException(nameof(accountSessionService));
        this.localMediaSearchIndex = localMediaSearchIndex;
        this.diagnostics = diagnostics ?? ApplicationDiagnosticLog.Shared;
        PersonViewModel = personViewModel;
        WatchLaterViewModel = watchLaterViewModel;
        this.playbackQueue = playbackQueue;
        playbackQueue?.SetSession(currentSessionService.CurrentSession?.ServerBase, currentSessionService.CurrentSession?.UserId);
        SettingsViewModel = new SettingsViewModel(
            navigationService,
            appSettingsService,
            currentSessionService,
            mediaLibraryScanService,
            authSessionStore,
            accountSessionService,
            loginViewModel.ShowError,
            cacheManagementService);

        currentPage = navigationService.CurrentPage;
        currentPageViewModel = CreatePageViewModel(currentPage);

        NavigationItems = new ReadOnlyCollection<NavigationItemViewModel>(
            Array.Empty<NavigationItemViewModel>());

        PlaceholderNavigationItems = new ReadOnlyCollection<NavigationItemViewModel>(
            new List<NavigationItemViewModel>
            {
                new("连接服务器", AppPage.ServerConnection, true),
                new("登录", AppPage.Login, true),
                new("首页", AppPage.Home, true),
                new("媒体库", AppPage.Library, true),
                new("媒体详情", AppPage.Detail, true),
                new("播放器", AppPage.Player, true),
                new("设置", AppPage.Settings, true)
            });

        NavigateCommand = new RelayCommand(parameter =>
        {
            if (parameter is AppPage page)
            {
                NavigateTo(page);
            }
        });
        LogoutCommand = new AsyncRelayCommand(LogoutAsync);
        SubmitSearchCommand = new RelayCommand(_ => SubmitSearch());

        navigationService.CurrentPageChanged += OnCurrentPageChanged;
    }

    public PersonViewModel? PersonViewModel { get; }
    public WatchLaterViewModel? WatchLaterViewModel { get; }

    public AppPage CurrentPage
    {
        get => currentPage;
        private set
        {
            if (currentPage == value)
            {
                return;
            }

            currentPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTopBarVisible));
            OnPropertyChanged(nameof(IsPlaceholderNavigationVisible));
        }
    }

    public object CurrentPageViewModel
    {
        get => currentPageViewModel;
        private set
        {
            currentPageViewModel = value;
            OnPropertyChanged();
        }
    }

    public bool IsTopBarVisible => false;

    public bool IsPlaceholderNavigationVisible => false;

    public bool IsStartupLoading => !hasInitialized;

    public string UserDisplayName => currentSessionService.CurrentSession?.UserName ?? "用户，占位";

    public string CurrentServerText => currentSessionService.CurrentSession?.ServerBase ?? "\u672a\u8fde\u63a5";

    public string? ShellErrorMessage
    {
        get => shellErrorMessage;
        private set
        {
            if (shellErrorMessage == value)
            {
                return;
            }

            shellErrorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasShellError));
        }
    }

    public bool HasShellError => !string.IsNullOrWhiteSpace(ShellErrorMessage);

    public string ShellSearchText
    {
        get => shellSearchText;
        set
        {
            if (shellSearchText == value)
            {
                return;
            }

            shellSearchText = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<NavigationItemViewModel> NavigationItems { get; }

    public IReadOnlyList<NavigationItemViewModel> PlaceholderNavigationItems { get; }

    public SettingsViewModel SettingsViewModel { get; }

    public ICommand NavigateCommand { get; }

    public ICommand LogoutCommand { get; }

    public ICommand SubmitSearchCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (hasInitialized || isInitializing)
        {
            return;
        }

        isInitializing = true;
        diagnostics.Write("startup", "start");
        try
        {
            await InitializeCoreAsync(cancellationToken).ConfigureAwait(true);
            hasInitialized = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RecoverFromStartupFailure(exception);
        }
        finally
        {
            isInitializing = false;
            OnPropertyChanged(nameof(IsStartupLoading));
        }
    }

    public Task PrepareForApplicationExitAsync()
    {
        return CurrentPage == AppPage.Player
            ? playerViewModel.PrepareForApplicationExitAsync()
            : Task.CompletedTask;
    }

    public void RecoverFromStartupFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        System.Diagnostics.Trace.TraceError($"Startup session recovery failed: {exception}");
        diagnostics.Write(
            "startup",
            "failure",
            $"exceptionType={exception.GetType().Name}");
        currentSessionService.ClearSession();
        hasInitialized = true;
        OnPropertyChanged(nameof(UserDisplayName));
        OnPropertyChanged(nameof(CurrentServerText));
        OnPropertyChanged(nameof(IsStartupLoading));

        var message = exception is AuthSessionClearPartialFailureException
            ? PartialAuthenticationCleanupMessage
            : StartupFailureMessage;
        NavigateDuringStartup(AppPage.Login);
        ShellErrorMessage = message;
        loginViewModel.ShowError(message);
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        ShellErrorMessage = null;
        var lastServerBase = await appSettingsService
            .GetLastServerBaseAsync(cancellationToken)
            .ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(lastServerBase))
        {
            // The initial connection page has no navigation event to load its settings.
            // Keep that work out of the auto-login path, where the page stays hidden.
            if (CurrentPage == AppPage.ServerConnection)
            {
                await serverConnectionViewModel.LoadLastServerBaseAsync(cancellationToken).ConfigureAwait(true);
            }
            WriteStartupState(hasServer: false, hasSession: false);
            NavigateDuringStartup(AppPage.ServerConnection);
            return;
        }

        var session = await authSessionStore
            .LoadAsync(cancellationToken)
            .ConfigureAwait(true);
        if (session is null)
        {
            WriteStartupState(hasServer: true, hasSession: false);
            NavigateDuringStartup(AppPage.Login);
            return;
        }

        WriteStartupState(hasServer: true, hasSession: true);

        currentSessionService.SetSession(session);
        OnPropertyChanged(nameof(UserDisplayName));
        OnPropertyChanged(nameof(CurrentServerText));

        var validationResult = await authSessionValidator
            .ValidateAsync(session, cancellationToken)
            .ConfigureAwait(true);
        diagnostics.Write(
            "startup",
            "session-validation",
            $"result={validationResult.Status}");

        if (validationResult.IsValid)
        {
            NavigateDuringStartup(AppPage.Home);
            return;
        }

        if (validationResult.Status == AuthSessionValidationStatus.InvalidToken)
        {
            string? cleanupWarning = null;
            try
            {
                await authSessionStore
                    .ClearAsync(cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (AuthSessionClearPartialFailureException exception)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"Expired session token was cleared, but metadata cleanup failed: {exception.InnerException}");
                cleanupWarning = PartialAuthenticationCleanupMessage;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError(
                    $"Failed to clear expired startup session: {exception}");
                cleanupWarning = ExpiredSessionCleanupFailureMessage;
            }

            currentSessionService.ClearSession();
            OnPropertyChanged(nameof(UserDisplayName));
            OnPropertyChanged(nameof(CurrentServerText));
            NavigateDuringStartup(AppPage.Login);
            if (cleanupWarning is not null)
            {
                ShellErrorMessage = cleanupWarning;
                loginViewModel.ShowError(cleanupWarning);
            }

            return;
        }

        if (validationResult.Status == AuthSessionValidationStatus.Forbidden)
        {
            NavigateDuringStartup(AppPage.Login);
            loginViewModel.ShowError(PermissionValidationErrorMessage);
            return;
        }

        NavigateDuringStartup(AppPage.Login);
        loginViewModel.ShowError(NetworkValidationErrorMessage);
    }

    private void WriteStartupState(bool hasServer, bool hasSession)
    {
        diagnostics.Write(
            "startup",
            "session-state",
            $"hasServer={hasServer.ToString().ToLowerInvariant()} "
            + $"hasSession={hasSession.ToString().ToLowerInvariant()}");
    }

    private void NavigateDuringStartup(AppPage targetPage)
    {
        diagnostics.Write(
            "startup",
            "route",
            $"targetPageCategory={targetPage.ToString().ToLowerInvariant()}");
        navigationService.NavigateTo(targetPage);
    }

    public void NavigateTo(AppPage page)
    {
        navigationService.NavigateTo(page);
    }

    public bool ActivateSearch()
    {
        if (CurrentPage == AppPage.Player)
        {
            return false;
        }

        if (CurrentPage == AppPage.Search)
        {
            searchViewModel.RequestSearchFocus();
        }
        else
        {
            navigationService.NavigateTo(AppPage.Search);
        }

        return true;
    }

    public void CancelPendingOperations()
    {
        PersonViewModel?.Deactivate();
        WatchLaterViewModel?.Deactivate();
        playerViewModel.QueueViewModel?.Deactivate();
        homeViewModel.CancelPendingLoad();
        mediaDetailViewModel.CancelPendingLoad();
    }

    private void SubmitSearch()
    {
        navigationService.NavigateTo(AppPage.Search, ShellSearchText);
    }

    private async Task LogoutAsync()
    {
        ShellErrorMessage = null;

        try
        {
            await accountSessionService
                .LogoutAsync(CancellationToken.None)
                .ConfigureAwait(true);
            OnPropertyChanged(nameof(UserDisplayName));
            OnPropertyChanged(nameof(CurrentServerText));
            navigationService.NavigateTo(AppPage.Login);
        }
        catch (AuthSessionClearPartialFailureException exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"Logout cleared the secure token, but metadata cleanup failed: {exception.InnerException}");
            OnPropertyChanged(nameof(UserDisplayName));
            OnPropertyChanged(nameof(CurrentServerText));
            navigationService.NavigateTo(AppPage.Login);
            ShellErrorMessage = PartialAuthenticationCleanupMessage;
            loginViewModel.ShowError(PartialAuthenticationCleanupMessage);
        }
        catch
        {
            ShellErrorMessage = "退出登录失败，请稍后重试。";
        }
    }

    private void OnCurrentPageChanged(object? sender, AppPageChangedEventArgs e)
    {
        if (e.CurrentPage is AppPage.Login or AppPage.ServerConnection)
        {
            libraryViewModel.ResetSession();
            homeViewModel.SectionViewModel.ResetSession();
            PersonViewModel?.ResetSession();
            WatchLaterViewModel?.ResetSession();
            homeViewModel.ResetPersonalMedia();
            playbackQueue?.SetSession(null, null);
        }
        else if (e.PreviousPage == AppPage.Library && e.CurrentPage != AppPage.Library)
        {
            libraryViewModel.Deactivate();
        }

        if (e.PreviousPage == AppPage.Detail && e.CurrentPage != AppPage.Detail)
        {
            mediaDetailViewModel.CancelPendingLoad();
        }
        if (e.CurrentPage is not (AppPage.Login or AppPage.ServerConnection))
            playbackQueue?.SetSession(currentSessionService.CurrentSession?.ServerBase, currentSessionService.CurrentSession?.UserId);
        if (e.PreviousPage == AppPage.WatchLater && e.CurrentPage != AppPage.WatchLater)
            WatchLaterViewModel?.Deactivate();
        if (e.PreviousPage == AppPage.PlaybackQueue && e.CurrentPage is not (AppPage.PlaybackQueue or AppPage.Player))
            playerViewModel.QueueViewModel?.Deactivate();

        if (e.PreviousPage == AppPage.Person && e.CurrentPage != AppPage.Person)
        {
            PersonViewModel?.Deactivate();
        }

        if (e.PreviousPage == AppPage.HomeSection && e.CurrentPage != AppPage.HomeSection)
        {
            homeViewModel.SectionViewModel.Deactivate();
        }

        if (e.PreviousPage == AppPage.Search && e.CurrentPage != AppPage.Search)
        {
            searchViewModel.Deactivate();
        }

        CurrentPage = e.CurrentPage;
        CurrentPageViewModel = CreatePageViewModel(e.CurrentPage);
        InitializeCurrentPageIfNeeded(e.CurrentPage, e.Parameter, e.PreviousPage);
    }

    private object CreatePageViewModel(AppPage page)
    {
        return page switch
        {
            AppPage.ServerConnection => serverConnectionViewModel,
            AppPage.Login => loginViewModel,
            AppPage.Home => homeViewModel,
            AppPage.HomeSection => homeViewModel.SectionViewModel,
            AppPage.Library => libraryViewModel,
            AppPage.Search => searchViewModel,
            AppPage.Detail => mediaDetailViewModel,
            AppPage.Person when PersonViewModel is not null => PersonViewModel,
            AppPage.WatchLater when WatchLaterViewModel is not null => WatchLaterViewModel,
            AppPage.PlaybackQueue when playerViewModel.QueueViewModel is not null => playerViewModel.QueueViewModel,
            AppPage.Player => playerViewModel,
            AppPage.Settings => SettingsViewModel,
            _ => CreatePlaceholderPageViewModel(page)
        };
    }

    private void InitializeCurrentPageIfNeeded(AppPage page, object? parameter, AppPage previousPage)
    {
        if (page == AppPage.ServerConnection)
        {
            var statusMessage = (parameter as ServerConnectionNavigationParameter)?.Message;
            _ = serverConnectionViewModel.LoadLastServerBaseAsync(
                CancellationToken.None,
                statusMessage);
        }

        if (page == AppPage.Login)
        {
            _ = loginViewModel.InitializeAsync(CancellationToken.None);
        }

        if (page == AppPage.Home)
        {
            StartPreparingLocalSearchIndex();
            StartHomeNavigation();
        }

        if (page == AppPage.Library)
        {
            _ = libraryViewModel.LoadAsync(parameter as string);
        }

        if (page == AppPage.HomeSection)
        {
            _ = homeViewModel.SectionViewModel.LoadAsync(parameter as HomeSectionKind?);
        }

        if (page == AppPage.Search)
        {
            if (parameter is SearchNavigationParameter { FavoritesOnly: true })
            {
                _ = searchViewModel.LoadFavoritesAsync();
                return;
            }

            var requestedKeyword = parameter is SearchNavigationParameter searchParameter
                ? searchParameter.Keyword
                : parameter as string;
            if (requestedKeyword is not null)
            {
                ShellSearchText = requestedKeyword;
            }

            _ = searchViewModel.LoadAsync(requestedKeyword);
        }

        if (page == AppPage.Detail)
        {
            if (parameter is DetailNavigationParameter detailParameter)
            {
                _ = mediaDetailViewModel.LoadAsync(detailParameter);
                return;
            }

            AppPage? returnPage = previousPage is AppPage.Home or AppPage.HomeSection or AppPage.Library or AppPage.Search
                ? previousPage
                : null;
            _ = mediaDetailViewModel.LoadAsync(parameter as string, returnPage);
        }

        if (page == AppPage.Settings)
        {
            _ = SettingsViewModel.LoadAsync();
        }
        if (page == AppPage.WatchLater && WatchLaterViewModel is not null)
            _ = WatchLaterViewModel.LoadAsync();

        if (page == AppPage.Person && PersonViewModel is not null)
        {
            _ = PersonViewModel.LoadAsync(parameter as PersonNavigationParameter);
        }

        if (page == AppPage.Player && parameter is PlayerNavigationParameter playerParameter)
        {
            playerViewModel.Load(playerParameter);
        }
    }

    private void StartPreparingLocalSearchIndex()
    {
        var session = currentSessionService.CurrentSession;
        if (session is null || localMediaSearchIndex is null)
        {
            return;
        }

        _ = PrepareLocalSearchIndexAsync(session);
    }

    private void StartHomeNavigation()
    {
        var navigationTask = homeViewModel.NavigateAsync();
        if (!navigationTask.IsCompletedSuccessfully)
        {
            _ = ObserveHomeNavigationAsync(navigationTask);
        }
    }

    private static async Task ObserveHomeNavigationAsync(Task navigationTask)
    {
        try
        {
            await navigationTask.ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Unable to initialize Home page: {exception.Message}");
        }
    }

    private async Task PrepareLocalSearchIndexAsync(AuthSession session)
    {
        try
        {
            await localMediaSearchIndex!
                .PrepareAsync(session, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Unable to prepare local media search index: {exception.Message}");
        }
    }

    private static PlaceholderPageViewModel CreatePlaceholderPageViewModel(AppPage page)
    {
        return page switch
        {
            AppPage.ServerConnection => new PlaceholderPageViewModel(
                page,
                "连接服务器",
                "此页后续用于输入 Emby Server 地址并连接服务器。当前为占位页面。",
                "占位页面"),
            AppPage.Login => new PlaceholderPageViewModel(
                page,
                "登录",
                "此页后续用于输入用户名和密码登录 Emby Server。当前为占位页面。",
                "占位页面"),
            AppPage.Home => new PlaceholderPageViewModel(
                page,
                "首页",
                "此页后续展示继续观看、最近添加和媒体库入口。当前为占位页面。",
                "占位页面"),
            AppPage.Library => new PlaceholderPageViewModel(
                page,
                "媒体库",
                "此页后续展示电影、电视剧和合集。当前为占位页面。",
                "占位页面"),
            AppPage.Detail => new PlaceholderPageViewModel(
                page,
                "媒体详情",
                "此页后续展示海报、背景图、简介、播放按钮和剧集信息。当前为占位页面。",
                "占位页面"),
            AppPage.Player => new PlaceholderPageViewModel(
                page,
                "播放器",
                "此页后续承载视频播放画面和播放控制栏。当前为占位页面。",
                "占位页面"),
            AppPage.Settings => new PlaceholderPageViewModel(
                page,
                "设置",
                "此页后续用于管理播放、字幕、音轨和界面设置。当前为占位页面。",
                "占位页面"),
            _ => throw new ArgumentOutOfRangeException(nameof(page), page, "Unsupported app page.")
        };
    }
}
