using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Caching;
using EmbyPlayer.Core.Library;
using EmbyPlayer.Core.Settings;
using EmbyPlayer.UI.Diagnostics;
using EmbyPlayer.UI.Navigation;

namespace EmbyPlayer.UI.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private const string AutoLanguageOption = "自动";
    private const string PartialAuthenticationCleanupMessage =
        "安全令牌已清除，但本地账户信息未能完全清理，请稍后重试。";
    private readonly IAccountSessionService accountSessionService;
    private readonly IAppSettingsService appSettingsService;
    private readonly IAuthSessionStore authSessionStore;
    private readonly ICurrentSessionService currentSessionService;
    private readonly IMediaLibraryScanService mediaLibraryScanService;
    private readonly INavigationService navigationService;
    private readonly IDiagnosticExportDestinationPicker diagnosticExportDestinationPicker;
    private readonly IDiagnosticExportService diagnosticExportService;
    private readonly Action<string> showLoginError;
    private readonly AsyncRelayCommand diagnosticExportCommand;
    private readonly AsyncRelayCommand retrySaveCommand;
    private readonly AsyncRelayCommand saveAndLeaveCommand;
    private readonly AsyncRelayCommand saveCommand;
    private readonly AsyncRelayCommand confirmAccountActionCommand;
    private readonly RelayCommand confirmRestoreDefaultsCommand;
    private readonly RelayCommand continueEditingCommand;
    private readonly RelayCommand discardChangesCommand;
    private readonly RelayCommand requestLogoutCommand;
    private readonly RelayCommand requestSwitchServerCommand;
    private readonly RelayCommand restoreDefaultsCommand;
    private CancellationTokenSource? libraryScanCancellation;
    private CancellationTokenSource? toastCancellation;
    private int libraryScanGeneration;
    private PlayerPreferences savedSnapshot = PlayerPreferences.Default;
    private PendingNavigation? pendingNavigation;
    private bool pendingWindowClose;
    private bool suppressNavigationGuard;
    private bool isApplyingSnapshot;
    private bool isDirty;
    private bool isLibraryScanRunning;
    private bool isLoading;
    private bool isSaving;
    private bool isSaveFailed;
    private bool isRestoreDefaultsDialogOpen;
    private bool isUnsavedLeaveDialogOpen;
    private bool isAccountActionRunning;
    private bool isDiagnosticExportRunning;
    private AccountAction pendingAccountAction;
    private bool autoPlayNextEpisode = PlayerPreferences.Default.AutoPlayNextEpisode;
    private bool rememberLastVolume = PlayerPreferences.Default.RememberLastVolume;
    private int defaultVolume = PlayerPreferences.Default.DefaultVolume;
    private int seekSeconds = PlayerPreferences.Default.SeekSeconds;
    private int controlsHideSeconds = PlayerPreferences.Default.ControlsHideSeconds;
    private string defaultSubtitleLanguage = PlayerPreferences.Default.DefaultSubtitleLanguage;
    private bool defaultSubtitlesEnabled = PlayerPreferences.Default.DefaultSubtitlesEnabled;
    private string defaultAudioLanguage = PlayerPreferences.Default.DefaultAudioLanguage;
    private string? saveErrorMessage;
    private string? libraryScanMessage;
    private string? accountActionErrorMessage;
    private string? diagnosticExportMessage;
    private string? toastMessage;
    private bool isToastVisible;

    public SettingsViewModel(
        INavigationService navigationService,
        IAppSettingsService appSettingsService,
        ICurrentSessionService currentSessionService,
        IMediaLibraryScanService mediaLibraryScanService,
        IAuthSessionStore authSessionStore,
        IAccountSessionService accountSessionService,
        Action<string> showLoginError,
        ICacheManagementService? cacheManagementService = null)
        : this(
            navigationService,
            appSettingsService,
            currentSessionService,
            mediaLibraryScanService,
            authSessionStore,
            accountSessionService,
            showLoginError,
            new FileDiagnosticExportService(),
            new SaveFileDiagnosticExportDestinationPicker(),
            cacheManagementService)
    {
    }

    internal SettingsViewModel(
        INavigationService navigationService,
        IAppSettingsService appSettingsService,
        ICurrentSessionService currentSessionService,
        IMediaLibraryScanService mediaLibraryScanService,
        IAuthSessionStore authSessionStore,
        IAccountSessionService accountSessionService,
        Action<string> showLoginError,
        IDiagnosticExportService diagnosticExportService,
        IDiagnosticExportDestinationPicker diagnosticExportDestinationPicker,
        ICacheManagementService? cacheManagementService = null)
    {
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        this.appSettingsService = appSettingsService ?? throw new ArgumentNullException(nameof(appSettingsService));
        this.currentSessionService = currentSessionService ?? throw new ArgumentNullException(nameof(currentSessionService));
        this.mediaLibraryScanService = mediaLibraryScanService ?? throw new ArgumentNullException(nameof(mediaLibraryScanService));
        this.authSessionStore = authSessionStore ?? throw new ArgumentNullException(nameof(authSessionStore));
        this.accountSessionService = accountSessionService ?? throw new ArgumentNullException(nameof(accountSessionService));
        this.showLoginError = showLoginError ?? throw new ArgumentNullException(nameof(showLoginError));
        this.diagnosticExportService = diagnosticExportService
            ?? throw new ArgumentNullException(nameof(diagnosticExportService));
        this.diagnosticExportDestinationPicker = diagnosticExportDestinationPicker
            ?? throw new ArgumentNullException(nameof(diagnosticExportDestinationPicker));
        CacheManagement = new CacheManagementViewModel(cacheManagementService, currentSessionService,
            authSessionStore, NavigateToLoginAfterSessionExpired);
        Shortcuts = new ShortcutSettingsViewModel(() => !IsSaving && !IsLoading);
        Shortcuts.Changed += (_, _) => { ClearSaveFailure(); UpdateDirtyState(); };

        SubtitleLanguageOptions = CreateLanguageOptions();
        AudioLanguageOptions = CreateLanguageOptions();
        SeekSecondsOptions = new ReadOnlyCollection<int>(new[] { 5, 10, 15, 30, 60 });
        ControlsHideSecondsOptions = new ReadOnlyCollection<int>(Enumerable.Range(1, 10).ToArray());

        saveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
        retrySaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
        saveAndLeaveCommand = new AsyncRelayCommand(SaveAndLeaveAsync, CanSave);
        restoreDefaultsCommand = new RelayCommand(_ => OpenRestoreDefaultsDialog(), _ => !IsSaving);
        confirmRestoreDefaultsCommand = new RelayCommand(_ => ConfirmRestoreDefaults(), _ => !IsSaving);
        CancelRestoreDefaultsCommand = new RelayCommand(_ => IsRestoreDefaultsDialogOpen = false);
        CancelDialogCommand = new RelayCommand(_ => CancelDialog());
        discardChangesCommand = new RelayCommand(_ => DiscardChanges(), _ => !IsSaving);
        continueEditingCommand = new RelayCommand(_ => ContinueEditing());
        requestLogoutCommand = new RelayCommand(
            _ => OpenAccountActionDialog(AccountAction.Logout),
            _ => CanRequestAccountAction());
        requestSwitchServerCommand = new RelayCommand(
            _ => OpenAccountActionDialog(AccountAction.SwitchServer),
            _ => CanRequestAccountAction());
        confirmAccountActionCommand = new AsyncRelayCommand(
            ConfirmAccountActionAsync,
            () => IsAccountActionDialogOpen && !IsAccountActionRunning);
        diagnosticExportCommand = new AsyncRelayCommand(
            ExportDiagnosticLogsAsync,
            () => !IsDiagnosticExportRunning);

        SaveCommand = saveCommand;
        RetrySaveCommand = retrySaveCommand;
        SaveAndLeaveCommand = saveAndLeaveCommand;
        RestoreDefaultsCommand = restoreDefaultsCommand;
        ConfirmRestoreDefaultsCommand = confirmRestoreDefaultsCommand;
        DiscardChangesCommand = discardChangesCommand;
        ContinueEditingCommand = continueEditingCommand;
        RequestLogoutCommand = requestLogoutCommand;
        RequestSwitchServerCommand = requestSwitchServerCommand;
        ConfirmAccountActionCommand = confirmAccountActionCommand;
        ExportDiagnosticLogsCommand = diagnosticExportCommand;
        NavigateHomeCommand = new RelayCommand(_ => navigationService.NavigateTo(AppPage.Home));
        ScanMediaLibraryCommand = new AsyncRelayCommand(
            RequestMediaLibraryScanAsync,
            () => currentSessionService.CurrentSession is not null);

        navigationService.Navigating += OnNavigating;
        navigationService.CurrentPageChanged += (_, e) =>
        {
            if (e.PreviousPage == AppPage.Settings && e.CurrentPage != AppPage.Settings) CacheManagement.Cancel();
        };
    }

    public CacheManagementViewModel CacheManagement { get; }

    public ShortcutSettingsViewModel Shortcuts { get; }

    public event EventHandler? WindowCloseApproved;

    public IReadOnlyList<int> SeekSecondsOptions { get; }

    public IReadOnlyList<int> ControlsHideSecondsOptions { get; }

    public ObservableCollection<LanguageSettingOption> SubtitleLanguageOptions { get; }

    public ObservableCollection<LanguageSettingOption> AudioLanguageOptions { get; }

    public PlayerPreferences SavedSnapshot => savedSnapshot;

    public string UserDisplayName => currentSessionService.CurrentSession?.UserName ?? "未登录";

    public string CurrentServerText => currentSessionService.CurrentSession?.ServerBase ?? "未连接";

    public string UnavailableActionToolTip => "将在下一阶段接入";

    public bool IsLibraryScanRunning
    {
        get => isLibraryScanRunning;
        private set
        {
            if (isLibraryScanRunning == value)
            {
                return;
            }

            isLibraryScanRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MediaLibraryScanButtonText));
        }
    }

    public string? LibraryScanMessage
    {
        get => libraryScanMessage;
        private set
        {
            if (libraryScanMessage == value)
            {
                return;
            }

            libraryScanMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasLibraryScanMessage));
        }
    }

    public bool HasLibraryScanMessage => !string.IsNullOrWhiteSpace(LibraryScanMessage);

    public string MediaLibraryScanButtonText => IsLibraryScanRunning ? "正在提交…" : "扫描媒体库";

    public bool IsDiagnosticExportRunning
    {
        get => isDiagnosticExportRunning;
        private set
        {
            if (isDiagnosticExportRunning == value)
            {
                return;
            }

            isDiagnosticExportRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DiagnosticExportButtonText));
            diagnosticExportCommand.NotifyCanExecuteChanged();
        }
    }

    public string DiagnosticExportButtonText => IsDiagnosticExportRunning ? "正在导出…" : "导出日志";

    public string? DiagnosticExportMessage
    {
        get => diagnosticExportMessage;
        private set
        {
            if (diagnosticExportMessage == value)
            {
                return;
            }

            diagnosticExportMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDiagnosticExportMessage));
        }
    }

    public bool HasDiagnosticExportMessage => !string.IsNullOrWhiteSpace(DiagnosticExportMessage);

    public bool AutoPlayNextEpisode
    {
        get => autoPlayNextEpisode;
        set => SetDraftValue(ref autoPlayNextEpisode, value);
    }

    public bool RememberLastVolume
    {
        get => rememberLastVolume;
        set => SetDraftValue(ref rememberLastVolume, value);
    }

    public int DefaultVolume
    {
        get => defaultVolume;
        set => SetDraftValue(ref defaultVolume, Math.Clamp(value, 0, 100));
    }

    public int SeekSeconds
    {
        get => seekSeconds;
        set => SetDraftValue(ref seekSeconds, Math.Clamp(value, 5, 60));
    }

    public int ControlsHideSeconds
    {
        get => controlsHideSeconds;
        set => SetDraftValue(ref controlsHideSeconds, Math.Clamp(value, 1, 10));
    }

    public string DefaultSubtitleLanguage
    {
        get => defaultSubtitleLanguage;
        set => SetDraftValue(ref defaultSubtitleLanguage, NormalizeLanguageValue(value));
    }

    public bool DefaultSubtitlesEnabled
    {
        get => defaultSubtitlesEnabled;
        set => SetDraftValue(ref defaultSubtitlesEnabled, value);
    }

    public string DefaultAudioLanguage
    {
        get => defaultAudioLanguage;
        set => SetDraftValue(ref defaultAudioLanguage, NormalizeLanguageValue(value));
    }

    public bool IsDirty
    {
        get => isDirty;
        private set
        {
            if (isDirty == value)
            {
                return;
            }

            isDirty = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DialogDetail));
            NotifyCommandStates();
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
            OnPropertyChanged();
            Shortcuts.NotifyCommandStates();
        }
    }

    public bool IsSaving
    {
        get => isSaving;
        private set
        {
            if (isSaving == value)
            {
                return;
            }

            isSaving = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SaveButtonText));
            OnPropertyChanged(nameof(UnsavedTitle));
            OnPropertyChanged(nameof(UnsavedDetail));
            NotifyCommandStates();
        }
    }

    public bool IsSaveFailed
    {
        get => isSaveFailed;
        private set
        {
            if (isSaveFailed == value)
            {
                return;
            }

            isSaveFailed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SaveButtonText));
            OnPropertyChanged(nameof(UnsavedTitle));
            OnPropertyChanged(nameof(UnsavedDetail));
        }
    }

    public string? SaveErrorMessage
    {
        get => saveErrorMessage;
        private set
        {
            if (saveErrorMessage == value)
            {
                return;
            }

            saveErrorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UnsavedDetail));
        }
    }

    public bool IsRestoreDefaultsDialogOpen
    {
        get => isRestoreDefaultsDialogOpen;
        private set
        {
            if (isRestoreDefaultsDialogOpen == value)
            {
                return;
            }

            isRestoreDefaultsDialogOpen = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAnyDialogOpen));
            OnPropertyChanged(nameof(DialogTitle));
            OnPropertyChanged(nameof(DialogMessage));
            OnPropertyChanged(nameof(DialogDetail));
            NotifyCommandStates();
        }
    }

    public bool IsUnsavedLeaveDialogOpen
    {
        get => isUnsavedLeaveDialogOpen;
        private set
        {
            if (isUnsavedLeaveDialogOpen == value)
            {
                return;
            }

            isUnsavedLeaveDialogOpen = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAnyDialogOpen));
            OnPropertyChanged(nameof(DialogTitle));
            OnPropertyChanged(nameof(DialogMessage));
            OnPropertyChanged(nameof(DialogDetail));
            NotifyCommandStates();
        }
    }

    public bool IsAccountActionDialogOpen => pendingAccountAction != AccountAction.None;

    public bool IsLogoutDialogOpen => pendingAccountAction == AccountAction.Logout;

    public bool IsSwitchServerDialogOpen => pendingAccountAction == AccountAction.SwitchServer;

    public bool IsAccountActionRunning
    {
        get => isAccountActionRunning;
        private set
        {
            if (isAccountActionRunning == value)
            {
                return;
            }

            isAccountActionRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDialogCancelEnabled));
            OnPropertyChanged(nameof(AccountActionConfirmText));
            NotifyCommandStates();
        }
    }

    public string? AccountActionErrorMessage
    {
        get => accountActionErrorMessage;
        private set
        {
            if (accountActionErrorMessage == value)
            {
                return;
            }

            accountActionErrorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAccountActionError));
        }
    }

    public bool HasAccountActionError => !string.IsNullOrWhiteSpace(AccountActionErrorMessage);

    public bool IsDialogCancelEnabled => !IsAccountActionRunning;

    public bool IsAnyDialogOpen => IsRestoreDefaultsDialogOpen
        || IsUnsavedLeaveDialogOpen
        || IsAccountActionDialogOpen;

    public string DialogTitle => pendingAccountAction switch
    {
        AccountAction.Logout => "退出登录？",
        AccountAction.SwitchServer => "切换服务器？",
        _ => IsUnsavedLeaveDialogOpen ? "保存设置更改？" : "恢复默认设置？"
    };

    public string DialogMessage => pendingAccountAction switch
    {
        AccountAction.Logout => "将清除当前账户的登录状态，并返回登录页面。",
        AccountAction.SwitchServer => "将清除当前账户的登录状态和服务器地址，并返回服务器连接页面。",
        _ => IsUnsavedLeaveDialogOpen
            ? "离开设置页面前，请选择保存本次更改或放弃更改。"
            : "将播放、音量、字幕与音轨选项恢复为默认状态。"
    };

    public string DialogDetail => pendingAccountAction switch
    {
        AccountAction.Logout => IsDirty
            ? "服务器地址和通用偏好将保留；未保存的设置更改会被放弃。"
            : "服务器地址、设备标识和通用偏好将保留。",
        AccountAction.SwitchServer => IsDirty
            ? "设备标识和通用偏好将保留；未保存的设置更改会被放弃。"
            : "设备标识和通用偏好将保留。",
        _ => IsUnsavedLeaveDialogOpen
            ? "继续编辑将留在当前页面。"
            : "恢复后仍需保存更改。"
    };

    public string AccountActionConfirmText => IsAccountActionRunning
        ? "正在处理…"
        : IsSwitchServerDialogOpen
            ? "切换服务器"
            : "退出登录";

    public string SaveButtonText => IsSaving
        ? "正在保存"
        : IsSaveFailed
            ? "重试保存"
            : "保存更改";

    public string UnsavedTitle => IsSaving
        ? "正在保存设置..."
        : IsSaveFailed
            ? "设置保存失败"
            : "有未保存的更改";

    public string UnsavedDetail => IsSaving
        ? "正在应用本次更改。"
        : IsSaveFailed
            ? SaveErrorMessage ?? "请稍后重试。"
            : "保存后将在后续播放任务中生效。";

    public string? ToastMessage
    {
        get => toastMessage;
        private set
        {
            if (toastMessage == value)
            {
                return;
            }

            toastMessage = value;
            OnPropertyChanged();
        }
    }

    public bool IsToastVisible
    {
        get => isToastVisible;
        private set
        {
            if (isToastVisible == value)
            {
                return;
            }

            isToastVisible = value;
            OnPropertyChanged();
        }
    }

    public ICommand SaveCommand { get; }

    public ICommand ScanMediaLibraryCommand { get; }

    public ICommand RetrySaveCommand { get; }

    public ICommand RestoreDefaultsCommand { get; }

    public ICommand ConfirmRestoreDefaultsCommand { get; }

    public ICommand CancelRestoreDefaultsCommand { get; }

    public ICommand CancelDialogCommand { get; }

    public ICommand DiscardChangesCommand { get; }

    public ICommand SaveAndLeaveCommand { get; }

    public ICommand ContinueEditingCommand { get; }

    public ICommand RequestLogoutCommand { get; }

    public ICommand RequestSwitchServerCommand { get; }

    public ICommand ConfirmAccountActionCommand { get; }

    public ICommand ExportDiagnosticLogsCommand { get; }

    public ICommand NavigateHomeCommand { get; }

    public async Task LoadAsync()
    {
        if (IsLoading || IsSaving || IsDirty)
        {
            return;
        }

        IsLoading = true;
        var cacheUsageLoad = CacheManagement.RefreshUsageAsync();
        try
        {
            var preferences = await appSettingsService
                .GetPlayerPreferencesAsync(CancellationToken.None)
                .ConfigureAwait(true);
            ApplySnapshot(preferences.Normalize());
            OnPropertyChanged(nameof(UserDisplayName));
            OnPropertyChanged(nameof(CurrentServerText));
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Failed to load player settings: {exception}");
            ApplySnapshot(PlayerPreferences.Default);
            ShowToast("无法加载设置，已显示默认值");
        }
        finally
        {
            await cacheUsageLoad.ConfigureAwait(true);
            IsLoading = false;
        }
    }

    public bool RequestWindowClose()
    {
        if (IsAccountActionDialogOpen)
        {
            return false;
        }

        if (navigationService.CurrentPage != AppPage.Settings || !IsDirty)
        {
            return true;
        }

        pendingNavigation = null;
        pendingWindowClose = true;
        IsUnsavedLeaveDialogOpen = true;
        return false;
    }

    private static ObservableCollection<LanguageSettingOption> CreateLanguageOptions()
    {
        return new ObservableCollection<LanguageSettingOption>(
            new[]
            {
                new LanguageSettingOption(AutoLanguageOption, string.Empty),
                new LanguageSettingOption("简体中文", "zh-CN"),
                new LanguageSettingOption("繁体中文", "zh-TW"),
                new LanguageSettingOption("English", "en"),
                new LanguageSettingOption("日本語", "ja"),
                new LanguageSettingOption("한국어", "ko")
            });
    }

    private void SetDraftValue<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
        if (!isApplyingSnapshot)
        {
            ClearSaveFailure();
            UpdateDirtyState();
        }
    }

    private void ApplySnapshot(PlayerPreferences preferences)
    {
        savedSnapshot = preferences.Normalize();
        EnsureLanguageOption(SubtitleLanguageOptions, savedSnapshot.DefaultSubtitleLanguage);
        EnsureLanguageOption(AudioLanguageOptions, savedSnapshot.DefaultAudioLanguage);

        isApplyingSnapshot = true;
        AutoPlayNextEpisode = savedSnapshot.AutoPlayNextEpisode;
        RememberLastVolume = savedSnapshot.RememberLastVolume;
        DefaultVolume = savedSnapshot.DefaultVolume;
        SeekSeconds = savedSnapshot.SeekSeconds;
        ControlsHideSeconds = savedSnapshot.ControlsHideSeconds;
        DefaultSubtitleLanguage = savedSnapshot.DefaultSubtitleLanguage;
        DefaultSubtitlesEnabled = savedSnapshot.DefaultSubtitlesEnabled;
        DefaultAudioLanguage = savedSnapshot.DefaultAudioLanguage;
        Shortcuts.Load(savedSnapshot.EffectiveShortcuts);
        isApplyingSnapshot = false;

        ClearSaveFailure();
        IsDirty = false;
        OnPropertyChanged(nameof(SavedSnapshot));
    }

    private static void EnsureLanguageOption(ICollection<LanguageSettingOption> options, string value)
    {
        var normalized = NormalizeLanguageValue(value);
        if (options.All(option => !string.Equals(option.Value, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(new LanguageSettingOption(normalized, normalized));
        }
    }

    private void UpdateDirtyState()
    {
        IsDirty = BuildDraft(savedSnapshot.LastVolume) != savedSnapshot;
    }

    private PlayerPreferences BuildDraft(int? lastVolume)
    {
        return new PlayerPreferences(
            DefaultVolume,
            SeekSeconds,
            ControlsHideSeconds,
            DefaultSubtitleLanguage,
            DefaultAudioLanguage,
            RememberLastVolume,
            lastVolume,
            AutoPlayNextEpisode,
            Shortcuts.Bindings,
            DefaultSubtitlesEnabled).Normalize();
    }

    private bool CanSave()
    {
        return IsDirty && !IsSaving;
    }

    private async Task SaveAsync()
    {
        await SaveInternalAsync().ConfigureAwait(true);
    }

    private async Task RequestMediaLibraryScanAsync()
    {
        var session = currentSessionService.CurrentSession;
        if (session is null)
        {
            return;
        }

        var scanGeneration = ++libraryScanGeneration;
        using var scanCancellation = new CancellationTokenSource();
        libraryScanCancellation = scanCancellation;
        IsLibraryScanRunning = true;
        LibraryScanMessage = null;
        try
        {
            var result = await mediaLibraryScanService
                .RequestScanAsync(session, scanCancellation.Token)
                .ConfigureAwait(true);

            if (!IsCurrentLibraryScan(scanGeneration))
            {
                return;
            }

            if (result.IsSuccess)
            {
                LibraryScanMessage = "扫描请求已提交，服务器将在后台扫描媒体文件。";
                return;
            }

            if (result.Reason == MediaLibraryScanResult.FailureReason.Unauthorized)
            {
                try
                {
                    await authSessionStore.ClearAsync(scanCancellation.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                    when (scanCancellation.IsCancellationRequested
                        && !IsCurrentLibraryScan(scanGeneration))
                {
                    return;
                }
                catch (Exception exception)
                {
                    Trace.TraceError($"Failed to clear expired media-scan session: {exception}");
                }

                if (!IsCurrentLibraryScan(scanGeneration))
                {
                    return;
                }

                currentSessionService.ClearSession();
                NavigateToLoginAfterSessionExpired();
                return;
            }

            LibraryScanMessage = result.Reason switch
            {
                MediaLibraryScanResult.FailureReason.Forbidden => "需要 Emby 服务器管理员权限才能扫描媒体库。",
                MediaLibraryScanResult.FailureReason.ServerUnreachable => "无法连接服务器，扫描请求未提交。",
                MediaLibraryScanResult.FailureReason.ServerTimeout => "扫描请求超时，请稍后重试。",
                MediaLibraryScanResult.FailureReason.Cancelled => "扫描请求已取消。",
                _ => "扫描请求提交失败，请稍后重试。"
            };
        }
        catch (OperationCanceledException) when (scanCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (IsCurrentLibraryScan(scanGeneration))
            {
                libraryScanCancellation = null;
                IsLibraryScanRunning = false;
            }
        }
    }

    private async Task ExportDiagnosticLogsAsync()
    {
        string? destinationPath;
        try
        {
            destinationPath = diagnosticExportDestinationPicker.PickDestinationPath();
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                $"Diagnostic export destination selection failed: type={exception.GetType().Name} hresult={exception.HResult}.");
            DiagnosticExportMessage = "无法选择保存位置，请稍后重试。";
            return;
        }

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            DiagnosticExportMessage = "已取消导出。";
            return;
        }

        IsDiagnosticExportRunning = true;
        DiagnosticExportMessage = null;
        try
        {
            var result = await diagnosticExportService
                .ExportAsync(destinationPath, CancellationToken.None)
                .ConfigureAwait(true);
            DiagnosticExportMessage = result.IncludedLogCount == 0
                ? "诊断包已导出；当前没有可用日志，包内仅包含说明文件。"
                : $"诊断包已导出，共包含 {result.IncludedLogCount} 个日志文件。";
            ShowToast("诊断日志已导出");
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                $"Diagnostic export failed: type={exception.GetType().Name} hresult={exception.HResult}.");
            DiagnosticExportMessage = "导出失败，请确认目标文件未被占用并且有写入权限。";
        }
        finally
        {
            IsDiagnosticExportRunning = false;
        }
    }

    private async Task<bool> SaveInternalAsync()
    {
        if (!CanSave())
        {
            return !IsDirty;
        }

        IsSaving = true;
        ClearSaveFailure();
        try
        {
            var preferences = await appSettingsService
                .UpdatePlayerPreferencesAsync(
                    currentPreferences => BuildDraft(currentPreferences.LastVolume),
                    CancellationToken.None)
                .ConfigureAwait(true);
            ApplySnapshot(preferences);
            ShowToast("设置已保存");
            return true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Failed to save player settings: {exception}");
            IsSaveFailed = true;
            SaveErrorMessage = "设置保存失败，请稍后重试。";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void OpenRestoreDefaultsDialog()
    {
        IsUnsavedLeaveDialogOpen = false;
        IsRestoreDefaultsDialogOpen = true;
    }

    private bool CanRequestAccountAction()
    {
        return !IsSaving && !IsAccountActionRunning && !IsAnyDialogOpen;
    }

    private void OpenAccountActionDialog(AccountAction action)
    {
        if (!CanRequestAccountAction())
        {
            return;
        }

        pendingNavigation = null;
        pendingWindowClose = false;
        AccountActionErrorMessage = null;
        SetPendingAccountAction(action);
    }

    private async Task ConfirmAccountActionAsync()
    {
        var action = pendingAccountAction;
        if (action == AccountAction.None || IsAccountActionRunning)
        {
            return;
        }

        CancelPendingLibraryScan();
        CacheManagement.Cancel();
        IsAccountActionRunning = true;
        AccountActionErrorMessage = null;
        try
        {
            if (action == AccountAction.Logout)
            {
                await accountSessionService
                    .LogoutAsync(CancellationToken.None)
                    .ConfigureAwait(true);
            }
            else
            {
                await accountSessionService
                    .SwitchServerAsync(CancellationToken.None)
                    .ConfigureAwait(true);
            }

            CompleteAccountAction(action);
        }
        catch (AuthSessionClearPartialFailureException exception)
        {
            Trace.TraceWarning(
                $"Account action cleared the secure token, but metadata cleanup failed: {exception.InnerException}");
            if (action == AccountAction.SwitchServer)
            {
                CompleteAccountAction(
                    action,
                    new ServerConnectionNavigationParameter(
                        ServerConnectionNavigationParameter.AuthenticationCleanupPartialFailureMessage));
            }
            else
            {
                CompleteAccountAction(action);
                showLoginError(PartialAuthenticationCleanupMessage);
            }
        }
        catch (SwitchServerPartialFailureException exception)
            when (action == AccountAction.SwitchServer)
        {
            Trace.TraceWarning(
                $"Switch server cleared the session but retained the saved address: {exception.InnerException}");
            CompleteAccountAction(
                action,
                new ServerConnectionNavigationParameter(
                    ServerConnectionNavigationParameter.SwitchServerPartialFailureMessage));
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Failed to complete account action: {exception}");
            OnPropertyChanged(nameof(UserDisplayName));
            OnPropertyChanged(nameof(CurrentServerText));
            AccountActionErrorMessage = action == AccountAction.Logout
                ? "退出登录失败，请稍后重试。"
                : "切换服务器失败，请稍后重试。";
        }
        finally
        {
            IsAccountActionRunning = false;
        }
    }

    private void CancelPendingLibraryScan()
    {
        libraryScanGeneration++;
        libraryScanCancellation?.Cancel();
        libraryScanCancellation = null;
        IsLibraryScanRunning = false;
        LibraryScanMessage = null;
    }

    private void CompleteAccountAction(AccountAction action, object? navigationParameter = null)
    {
        ApplySnapshot(savedSnapshot);
        SetPendingAccountAction(AccountAction.None);
        OnPropertyChanged(nameof(UserDisplayName));
        OnPropertyChanged(nameof(CurrentServerText));
        navigationService.NavigateTo(
            action == AccountAction.Logout ? AppPage.Login : AppPage.ServerConnection,
            navigationParameter);
    }

    private bool IsCurrentLibraryScan(int scanGeneration)
    {
        return scanGeneration == libraryScanGeneration;
    }

    private void NavigateToLoginAfterSessionExpired()
    {
        pendingNavigation = null;
        pendingWindowClose = false;
        IsRestoreDefaultsDialogOpen = false;
        IsUnsavedLeaveDialogOpen = false;
        SetPendingAccountAction(AccountAction.None);

        suppressNavigationGuard = true;
        try
        {
            navigationService.NavigateTo(AppPage.Login);
        }
        finally
        {
            suppressNavigationGuard = false;
        }

        showLoginError("登录状态已失效，请重新登录");
    }

    private void ConfirmRestoreDefaults()
    {
        IsRestoreDefaultsDialogOpen = false;
        var defaults = PlayerPreferences.Default;
        isApplyingSnapshot = true;
        AutoPlayNextEpisode = defaults.AutoPlayNextEpisode;
        RememberLastVolume = defaults.RememberLastVolume;
        DefaultVolume = defaults.DefaultVolume;
        SeekSeconds = defaults.SeekSeconds;
        ControlsHideSeconds = defaults.ControlsHideSeconds;
        DefaultSubtitleLanguage = defaults.DefaultSubtitleLanguage;
        DefaultSubtitlesEnabled = defaults.DefaultSubtitlesEnabled;
        DefaultAudioLanguage = defaults.DefaultAudioLanguage;
        Shortcuts.Load(defaults.EffectiveShortcuts);
        isApplyingSnapshot = false;
        ClearSaveFailure();
        UpdateDirtyState();
        ShowToast("已恢复默认设置，保存后生效");
    }

    private void OnNavigating(object? sender, AppPageNavigatingEventArgs e)
    {
        if (suppressNavigationGuard)
        {
            return;
        }

        if (IsAccountActionDialogOpen)
        {
            e.Cancel = true;
            return;
        }

        if (e.CurrentPage != AppPage.Settings
            || e.TargetPage == AppPage.Settings
            || !IsDirty)
        {
            return;
        }

        e.Cancel = true;
        pendingWindowClose = false;
        pendingNavigation = new PendingNavigation(e.TargetPage, e.Parameter);
        IsRestoreDefaultsDialogOpen = false;
        IsUnsavedLeaveDialogOpen = true;
    }

    private void ContinueEditing()
    {
        pendingNavigation = null;
        pendingWindowClose = false;
        IsUnsavedLeaveDialogOpen = false;
    }

    private void CancelDialog()
    {
        if (IsAccountActionDialogOpen)
        {
            if (!IsAccountActionRunning)
            {
                SetPendingAccountAction(AccountAction.None);
            }

            return;
        }

        if (IsRestoreDefaultsDialogOpen)
        {
            IsRestoreDefaultsDialogOpen = false;
            return;
        }

        ContinueEditing();
    }

    private void SetPendingAccountAction(AccountAction action)
    {
        if (pendingAccountAction == action)
        {
            return;
        }

        pendingAccountAction = action;
        if (action == AccountAction.None)
        {
            AccountActionErrorMessage = null;
        }

        OnPropertyChanged(nameof(IsAccountActionDialogOpen));
        OnPropertyChanged(nameof(IsLogoutDialogOpen));
        OnPropertyChanged(nameof(IsSwitchServerDialogOpen));
        OnPropertyChanged(nameof(IsAnyDialogOpen));
        OnPropertyChanged(nameof(DialogTitle));
        OnPropertyChanged(nameof(DialogMessage));
        OnPropertyChanged(nameof(DialogDetail));
        OnPropertyChanged(nameof(AccountActionConfirmText));
        NotifyCommandStates();
    }

    private void DiscardChanges()
    {
        ApplySnapshot(savedSnapshot);
        IsUnsavedLeaveDialogOpen = false;
        ShowToast("已放弃未保存的更改");
        ContinuePendingDestination();
    }

    private async Task SaveAndLeaveAsync()
    {
        if (!await SaveInternalAsync().ConfigureAwait(true))
        {
            return;
        }

        IsUnsavedLeaveDialogOpen = false;
        ContinuePendingDestination();
    }

    private void ContinuePendingDestination()
    {
        var navigation = pendingNavigation;
        var closeWindow = pendingWindowClose;
        pendingNavigation = null;
        pendingWindowClose = false;

        if (closeWindow)
        {
            WindowCloseApproved?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (navigation is null)
        {
            return;
        }

        suppressNavigationGuard = true;
        try
        {
            navigationService.NavigateTo(navigation.Page, navigation.Parameter);
        }
        finally
        {
            suppressNavigationGuard = false;
        }
    }

    private void ClearSaveFailure()
    {
        IsSaveFailed = false;
        SaveErrorMessage = null;
    }

    private void NotifyCommandStates()
    {
        Shortcuts.NotifyCommandStates();
        saveCommand.NotifyCanExecuteChanged();
        retrySaveCommand.NotifyCanExecuteChanged();
        saveAndLeaveCommand.NotifyCanExecuteChanged();
        restoreDefaultsCommand.RaiseCanExecuteChanged();
        confirmRestoreDefaultsCommand.RaiseCanExecuteChanged();
        discardChangesCommand.RaiseCanExecuteChanged();
        requestLogoutCommand.RaiseCanExecuteChanged();
        requestSwitchServerCommand.RaiseCanExecuteChanged();
        confirmAccountActionCommand.NotifyCanExecuteChanged();
    }

    private async void ShowToast(string message)
    {
        toastCancellation?.Cancel();
        toastCancellation?.Dispose();
        toastCancellation = new CancellationTokenSource();
        var cancellationToken = toastCancellation.Token;

        ToastMessage = message;
        IsToastVisible = true;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2.2), cancellationToken).ConfigureAwait(true);
            IsToastVisible = false;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string NormalizeLanguageValue(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private sealed record PendingNavigation(AppPage Page, object? Parameter);

    private enum AccountAction
    {
        None,
        Logout,
        SwitchServer
    }
}

public sealed record LanguageSettingOption(string DisplayName, string Value);
