using System.Windows.Input;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Playback;
using EmbyPlayer.Core.Series;
using EmbyPlayer.Core.Settings;
using EmbyPlayer.Player;
using EmbyPlayer.UI.Navigation;

namespace EmbyPlayer.UI.ViewModels;

public sealed partial class PlayerViewModel : ViewModelBase
{
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CompletionRemainingThreshold = TimeSpan.FromMinutes(2);
    private const double PlaybackCompletionThreshold = 90d;
    private const string SessionExpiredMessage = "登录状态已失效，请重新登录";
    private readonly INavigationService navigationService;
    private readonly IPlayerService playerService;
    private readonly IPlaybackReportService? playbackReportService;
    private readonly ICurrentSessionService? currentSessionService;
    private readonly IAuthSessionStore? authSessionStore;
    private readonly IAppSettingsService? appSettingsService;
    private readonly INextEpisodeService? nextEpisodeService;
    private readonly IPlaybackService? playbackService;
    private readonly Action<string>? showLoginError;
    private readonly IPlaybackReportScheduler playbackReportScheduler;
    private readonly SynchronizationContext? synchronizationContext;
    private readonly object stoppedReportGate = new();
    private readonly SemaphoreSlim playbackReportSemaphore = new(1, 1);
    private long closingPlaybackInstanceId;
    private long lastStoppedReportAttemptedInstanceId;
    private Task<StoppedReportOutcome> lastStoppedReportTask = Task.FromResult(
        new StoppedReportOutcome(null, SynchronizationFailed: false));
    private DetailNavigationParameter? backToDetailParameter;
    private string? errorMessage;
    private bool isLoading;
    private bool isCompleted;
    private bool acceptsPlayerEvents;
    private bool isPaused;
    private bool isPlayerLoaded;
    private bool isPlayerLoadInProgress;
    private bool isPlaying;
    private bool isSeeking;
    private bool isInitialPlayerConfigurationComplete;
    private bool preservePausedAfterSeek;
    private bool pauseStateLockedAfterSeek;
    private bool isSeekDragging;
    private bool hasReportedPlaying;
    private bool hasShownPlaybackReportWarning;
    private bool isHandlingExpiredSession;
    private bool isMuted;
    private bool isControlsOverlayVisible = true;
    private bool isFullscreen;
    private bool isTrackMenuOpen;
    private bool hasManualAudioSelection;
    private bool isAutoSelectingAudio;
    private bool hasAttemptedAutoAudioSelection;
    private bool hasManualSubtitleSelection;
    private bool isAutoSelectingSubtitle;
    private bool hasAttemptedAutoSubtitleSelection;
    private bool isNativePlaybackReady;
    private int? manualAudioMpvTrackId;
    private int? manualAudioStreamIndex;
    private int? pendingSubtitleMpvTrackId;
    private int? pendingSubtitleStreamIndex;
    private int? manualSubtitleMpvTrackId;
    private int? manualSubtitleStreamIndex;
    private int? activeAudioStreamIndex;
    private int? activeSubtitleStreamIndex;
    private double progressPercent;
    private double seekPercent;
    private int volume = 100;
    private int appliedVolumeLevel = 100;
    private IReadOnlyList<PlayerAudioTrackViewModel> audioTracks = Array.Empty<PlayerAudioTrackViewModel>();
    private IReadOnlyList<PlayerSubtitleTrackViewModel> subtitleTracks = Array.Empty<PlayerSubtitleTrackViewModel>();
    private TimeSpan? currentPosition;
    private TimeSpan? duration;
    private TimeSpan? seekPreviewPosition;
    private long currentPlaybackInstanceId;
    private long nextPlaybackInstanceId;
    private PlaybackInfo? playbackInfo;
    private string? logoUrl;
    private PlayerPreferences playerPreferences = PlayerPreferences.Default;
    private Task<PlayerPreferences> playerPreferencesLoadTask = Task.FromResult(PlayerPreferences.Default);
    private string? transientStatusMessage;
    private Task<PlayerOperationResult> stopBeforeLoadTask = Task.FromResult(PlayerOperationResult.Success());
    private IntPtr videoHostHandle;
    private NextEpisodeInfo? nextEpisode;
    private bool isNextEpisodePromptVisible;
    private bool isPreparingNextEpisode;
    private bool isAutoPlayCancelledForCurrentItem;
    private CancellationTokenSource? playbackLifecycleCancellation;
    private string? nextEpisodeStatusMessage;
    private bool isSkipSegmentVisible;
    private string skipSegmentButtonText = string.Empty;
    private TimeSpan? skipSegmentTarget;
    private long handledCompletionPlaybackInstanceId;
    private long completionThresholdReachedPlaybackInstanceId;

    public PlayerViewModel(
        INavigationService navigationService,
        IPlayerService playerService)
        : this(
            navigationService,
            playerService,
            null,
            null,
            null,
            null,
            new PlaybackReportScheduler())
    {
    }

    public PlayerViewModel(
        INavigationService navigationService,
        IPlayerService playerService,
        IPlaybackReportService? playbackReportService,
        ICurrentSessionService? currentSessionService,
        IAuthSessionStore? authSessionStore,
        Action<string>? showLoginError,
        IPlaybackReportScheduler playbackReportScheduler,
        IAppSettingsService? appSettingsService = null,
        INextEpisodeService? nextEpisodeService = null,
        IPlaybackService? playbackService = null,
        EmbyPlayer.Core.PlaybackQueue.IPlaybackQueueService? playbackQueue = null,
        IPlaybackPreviewService? playbackPreviewService = null,
        ILocalPlaybackPreviewService? localPlaybackPreviewService = null)
    {
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        this.playerService = playerService ?? throw new ArgumentNullException(nameof(playerService));
        this.playbackReportService = playbackReportService;
        this.currentSessionService = currentSessionService;
        this.authSessionStore = authSessionStore;
        this.appSettingsService = appSettingsService;
        this.nextEpisodeService = nextEpisodeService;
        this.playbackService = playbackService;
        this.playbackPreviewService = playbackPreviewService;
        this.localPlaybackPreviewService = localPlaybackPreviewService;
        this.showLoginError = showLoginError;
        this.playbackReportScheduler = playbackReportScheduler ?? throw new ArgumentNullException(nameof(playbackReportScheduler));
        synchronizationContext = SynchronizationContext.Current;
        this.playerService.StatusChanged += OnPlayerStatusChanged;
        this.playerService.ProgressChanged += OnPlayerProgressChanged;
        this.playbackReportScheduler.Tick += OnPlaybackReportTick;
        InitializeEnhancementCommands();
        InitializeRecoveryCommands();
        InitializePlaybackQueue(playbackQueue);
        TogglePlayPauseCommand = new AsyncRelayCommand(TogglePlayPauseAsync, () => CanTogglePlayPause);
        PlayCommand = new AsyncRelayCommand(PlayAsync, CanUsePlaybackControls);
        PauseCommand = new AsyncRelayCommand(PauseAsync, CanUsePlaybackControls);
        VolumeUpCommand = new AsyncRelayCommand(() => AdjustVolumeAsync(5), CanUseVolumeControls);
        VolumeDownCommand = new AsyncRelayCommand(() => AdjustVolumeAsync(-5), CanUseVolumeControls);
        ToggleMuteCommand = new AsyncRelayCommand(ToggleMuteAsync, CanUseVolumeControls);
        BackCommand = new AsyncRelayCommand(StopAndNavigateBackAsync);
        SkipSegmentCommand = new AsyncRelayCommand(SkipCurrentSegmentAsync, CanSkipCurrentSegment);
        PlayNextEpisodeCommand = new AsyncRelayCommand(PlayNextEpisodeAsync, CanPlayNextEpisode);
        CancelAutoPlayNextEpisodeCommand = new RelayCommand(
            _ => CancelAutoPlayNextEpisode(),
            _ => IsNextEpisodePromptVisible && !IsPreparingNextEpisode);
    }

    public PlaybackInfo? PlaybackInfo
    {
        get => playbackInfo;
        private set
        {
            playbackInfo = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPlaybackInfoVisible));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(MediaMetadataText));
            OnPropertyChanged(nameof(HasMediaMetadata));
            OnPropertyChanged(nameof(ContainerText));
            OnPropertyChanged(nameof(MediaSourceText));
            OnPropertyChanged(nameof(StreamModeText));
            OnPropertyChanged(nameof(AudioTrackCountText));
            OnPropertyChanged(nameof(SubtitleCountText));
            OnPropertyChanged(nameof(AudioMenuButtonText));
            OnPropertyChanged(nameof(SubtitleMenuButtonText));
            OnPropertyChanged(nameof(StartPositionText));
            OnPropertyChanged(nameof(PlaybackAddressText));
            OnPropertyChanged(nameof(RequiredHeadersText));
            NotifyTechnicalInfoChanged();
        }
    }

    public string Title => string.IsNullOrWhiteSpace(PlaybackInfo?.Title)
        ? "未命名媒体"
        : PlaybackInfo.Title;

    public string? LogoUrl
    {
        get => logoUrl;
        private set
        {
            if (string.Equals(logoUrl, value, StringComparison.Ordinal))
            {
                return;
            }

            logoUrl = value;
            OnPropertyChanged();
        }
    }

    public string MediaMetadataText
    {
        get
        {
            var parts = new List<string>(2);
            var typeText = PlaybackInfo?.MediaType?.Trim().ToLowerInvariant() switch
            {
                "movie" => "电影",
                "episode" => "单集",
                "series" => "剧集",
                "video" => "视频",
                "boxset" => "合集",
                "playlist" => "播放列表",
                _ => string.Empty
            };
            if (!string.IsNullOrEmpty(typeText))
            {
                parts.Add(typeText);
            }

            if (PlaybackInfo?.ProductionYear is > 0)
            {
                parts.Add(PlaybackInfo.ProductionYear.Value.ToString());
            }

            return string.Join(" · ", parts);
        }
    }

    public bool HasMediaMetadata => !string.IsNullOrWhiteSpace(MediaMetadataText);

    public string ContainerText => string.IsNullOrWhiteSpace(PlaybackInfo?.MediaSource.Container)
        ? "容器：未知"
        : $"容器：{PlaybackInfo.MediaSource.Container}";

    public string MediaSourceText => string.IsNullOrWhiteSpace(PlaybackInfo?.MediaSource.Id)
        ? "媒体源：未知"
        : $"媒体源：{PlaybackInfo.MediaSource.Id}";

    public string StreamModeText => PlaybackInfo?.RequiresTranscoding == true
        ? "播放方式：需要转码"
        : "播放方式：可直接播放";

    public string AudioTrackCountText => $"音轨数量：{PlaybackInfo?.AudioTracks.Count ?? 0}";

    public string SubtitleCountText => $"字幕数量：{PlaybackInfo?.Subtitles.Count ?? 0}";

    public IReadOnlyList<PlayerAudioTrackViewModel> AudioTracks
    {
        get => audioTracks;
        private set
        {
            audioTracks = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AudioMenuButtonText));
        }
    }

    public IReadOnlyList<PlayerSubtitleTrackViewModel> SubtitleTracks
    {
        get => subtitleTracks;
        private set
        {
            subtitleTracks = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitleMenuButtonText));
        }
    }

    public string AudioMenuButtonText => $"音轨 {AudioTracks.Count}";

    public string SubtitleMenuButtonText => $"字幕 {Math.Max(0, SubtitleTracks.Count - 1)}";

    public string CurrentAudioTrackText
    {
        get
        {
            var selected = AudioTracks.FirstOrDefault(track => track.IsSelected);
            return selected is null ? "当前音轨：未选择" : $"当前音轨：{selected.DisplayText}";
        }
    }

    public string CurrentSubtitleText
    {
        get
        {
            var selected = SubtitleTracks.FirstOrDefault(track => track.IsSelected);
            return selected is null ? "当前字幕：未选择" : $"当前字幕：{selected.DisplayText}";
        }
    }

    public string StartPositionText => $"起播位置：{FormatPosition(PlaybackInfo?.StartPositionTicks ?? 0)}";

    public string CurrentTimeText => FormatProgressTime(
        isSeekDragging && seekPreviewPosition.HasValue ? seekPreviewPosition : currentPosition,
        isDuration: false);

    public string DurationText => FormatProgressTime(duration, isDuration: true);

    public bool CanSeek => acceptsPlayerEvents
        && !IsSwitchingQuality
        && !IsAdvancingQueue
        && isPlayerLoaded
        && !isPlayerLoadInProgress
        && !IsSeeking
        && !HasError
        && IsValidDuration(duration);

    public bool CanTogglePlayPause => CanUsePlaybackControls() && !isCompleted && !IsSeeking;

    public int ControlsHideSeconds => playerPreferences.ControlsHideSeconds;

    public string PlayPauseButtonText
    {
        get
        {
            if (IsLoading || isPlayerLoadInProgress)
            {
                return "加载中";
            }

            if (HasError)
            {
                return "播放失败";
            }

            if (isCompleted)
            {
                return "播放结束";
            }

            return IsPlaying ? "暂停" : "播放";
        }
    }

    public string PlayPauseGlyph
    {
        get
        {
            if (IsLoading || isPlayerLoadInProgress)
            {
                return "…";
            }

            if (HasError)
            {
                return "!";
            }

            if (isCompleted)
            {
                return "■";
            }

            return IsPlaying ? "⏸" : "▶";
        }
    }

    public bool IsSeeking
    {
        get => isSeeking;
        private set
        {
            if (isSeeking == value)
            {
                return;
            }

            isSeeking = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CanSeek));
            UpdateNextEpisodePromptVisibility();
            NotifyCommandStatesChanged();
        }
    }

    public double SeekPercent
    {
        get => seekPercent;
        set
        {
            var clampedValue = NormalizePercent(value);
            if (Math.Abs(seekPercent - clampedValue) < 0.01)
            {
                return;
            }

            seekPercent = clampedValue;
            if (isSeekDragging)
            {
                seekPreviewPosition = CalculateSeekTarget(seekPercent);
                OnPropertyChanged(nameof(CurrentTimeText));
            }

            OnPropertyChanged();
        }
    }

    public double ProgressPercent
    {
        get => progressPercent;
        private set
        {
            var clampedValue = NormalizePercent(value);
            if (Math.Abs(progressPercent - clampedValue) < 0.01)
            {
                return;
            }

            progressPercent = clampedValue;
            OnPropertyChanged();
            if (!isSeekDragging && !IsSeeking)
            {
                SeekPercent = clampedValue;
            }
        }
    }

    public string PlaybackAddressText => string.IsNullOrWhiteSpace(PlaybackInfo?.PlaybackPath)
        ? "播放地址：未提供"
        : "播放地址：已解析，当前页面不显示完整地址";

    public string RequiredHeadersText
    {
        get
        {
            var count = PlaybackInfo?.MediaSource.RequiredHttpHeaders.Count ?? 0;
            return count == 0
                ? "请求头：无额外请求头"
                : $"请求头：已保留 {count} 个，当前页面不显示具体值";
        }
    }

    public int Volume
    {
        get => volume;
        private set
        {
            var clampedValue = Math.Clamp(value, 0, 100);
            if (volume == clampedValue)
            {
                return;
            }

            volume = clampedValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeText));
            OnPropertyChanged(nameof(VolumePercentText));
        }
    }

    public bool IsMuted
    {
        get => isMuted;
        private set
        {
            if (isMuted == value)
            {
                return;
            }

            isMuted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeText));
            OnPropertyChanged(nameof(MuteButtonText));
            OnPropertyChanged(nameof(MuteGlyph));
        }
    }

    public string VolumeText => IsMuted ? $"静音：{Volume}%" : $"音量：{Volume}%";

    public string VolumePercentText => $"{Volume}%";

    public string MuteButtonText => IsMuted ? "取消静音" : "静音";

    public string MuteGlyph => IsMuted ? "🔇" : "🔊";

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
            OnPropertyChanged(nameof(StatusText));
            UpdateNextEpisodePromptVisibility();
            NotifyCommandStatesChanged();
        }
    }

    public bool IsPlaying
    {
        get => isPlaying;
        private set
        {
            if (isPlaying == value)
            {
                return;
            }

            isPlaying = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            NotifyCommandStatesChanged();
        }
    }

    public bool IsPaused
    {
        get => isPaused;
        private set
        {
            if (isPaused == value)
            {
                return;
            }

            isPaused = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            NotifyCommandStatesChanged();
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
            OnPropertyChanged(nameof(StatusText));
            UpdateNextEpisodePromptVisibility();
            NotifyCommandStatesChanged();
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string StatusText
    {
        get
        {
            if (HasError)
            {
                return "播放失败";
            }

            if (IsSeeking)
            {
                return "正在跳转...";
            }

            if (!string.IsNullOrWhiteSpace(transientStatusMessage))
            {
                return transientStatusMessage;
            }

            if (IsLoading)
            {
                return "正在加载";
            }

            if (IsPaused)
            {
                return "已暂停";
            }

            if (isCompleted)
            {
                return "播放结束";
            }

            return IsPlaying ? "播放中" : "正在加载";
        }
    }

    public bool IsPlaybackInfoVisible => PlaybackInfo is not null;

    public bool IsControlsOverlayVisible
    {
        get => isControlsOverlayVisible;
        private set
        {
            if (isControlsOverlayVisible == value)
            {
                return;
            }

            isControlsOverlayVisible = value;
            OnPropertyChanged();
        }
    }

    public bool IsFullscreen
    {
        get => isFullscreen;
        private set
        {
            if (isFullscreen == value)
            {
                return;
            }

            isFullscreen = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FullscreenButtonText));
            OnPropertyChanged(nameof(FullscreenButtonTooltip));
        }
    }

    public string FullscreenButtonText => IsFullscreen ? "\u9000\u51fa" : "\u26f6";

    public string FullscreenButtonTooltip => IsFullscreen ? "\u9000\u51fa\u5168\u5c4f" : "\u8fdb\u5165\u5168\u5c4f";

    public ICommand PlayCommand { get; }

    public ICommand PauseCommand { get; }

    public ICommand TogglePlayPauseCommand { get; }

    public ICommand VolumeUpCommand { get; }

    public ICommand VolumeDownCommand { get; }

    public ICommand ToggleMuteCommand { get; }

    public ICommand BackCommand { get; }

    public ICommand SkipSegmentCommand { get; }

    public ICommand PlayNextEpisodeCommand { get; }

    public ICommand CancelAutoPlayNextEpisodeCommand { get; }

    public bool IsSkipSegmentVisible
    {
        get => isSkipSegmentVisible;
        private set
        {
            if (isSkipSegmentVisible == value)
            {
                return;
            }

            isSkipSegmentVisible = value;
            OnPropertyChanged();
        }
    }

    public string SkipSegmentButtonText
    {
        get => skipSegmentButtonText;
        private set
        {
            if (skipSegmentButtonText == value)
            {
                return;
            }

            skipSegmentButtonText = value;
            OnPropertyChanged();
        }
    }

    public NextEpisodeInfo? NextEpisode
    {
        get => nextEpisode;
        private set
        {
            if (Equals(nextEpisode, value))
            {
                return;
            }

            nextEpisode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasNextEpisode));
            OnPropertyChanged(nameof(NextEpisodeNumberText));
            NotifyNextEpisodeCommandStateChanged();
            UpdateNextEpisodePromptVisibility();
        }
    }

    public bool HasNextEpisode => NextEpisode is not null;

    public string NextEpisodeNumberText => NextEpisode is null
        ? string.Empty
        : $"S{NextEpisode.SeasonNumber}:E{NextEpisode.EpisodeNumber}";

    public bool IsNextEpisodePromptVisible
    {
        get => isNextEpisodePromptVisible;
        private set
        {
            if (isNextEpisodePromptVisible == value)
            {
                return;
            }

            isNextEpisodePromptVisible = value;
            OnPropertyChanged();
            NotifyAutoPlayStateChanged();
        }
    }

    public bool IsPreparingNextEpisode
    {
        get => isPreparingNextEpisode;
        private set
        {
            if (isPreparingNextEpisode == value)
            {
                return;
            }

            isPreparingNextEpisode = value;
            OnPropertyChanged();
            NotifyNextEpisodeCommandStateChanged();
            NotifyAutoPlayStateChanged();
        }
    }

    public bool IsAutoPlayNextEpisodeEnabled => playerPreferences.AutoPlayNextEpisode;

    public bool IsAutoPlayCancelledForCurrentItem
    {
        get => isAutoPlayCancelledForCurrentItem;
        private set
        {
            if (isAutoPlayCancelledForCurrentItem == value)
            {
                return;
            }

            isAutoPlayCancelledForCurrentItem = value;
            OnPropertyChanged();
            NotifyAutoPlayStateChanged();
        }
    }

    public bool IsAutoPlayCountdownVisible => IsNextEpisodePromptVisible
        && IsAutoPlayNextEpisodeEnabled
        && !IsAutoPlayCancelledForCurrentItem
        && !IsPreparingNextEpisode
        && !isCompleted
        && GetRemainingSeconds() is > 0 and <= 10;

    public string AutoPlayCountdownText
    {
        get
        {
            var remainingSeconds = GetRemainingSeconds();
            return remainingSeconds is > 0 and <= 10
                ? $"{remainingSeconds} 秒后播放下一集"
                : string.Empty;
        }
    }

    public string NextEpisodeStatusText => !string.IsNullOrWhiteSpace(nextEpisodeStatusMessage)
        ? nextEpisodeStatusMessage
        : IsAutoPlayCountdownVisible
            ? AutoPlayCountdownText
            : "即将播放下一集";

    public double AutoPlayCountdownProgress
    {
        get
        {
            var remainingSeconds = GetRemainingSeconds();
            if (remainingSeconds is not (> 0 and <= 10))
            {
                return 0d;
            }

            return Math.Clamp((10d - remainingSeconds.Value) / 10d * 100d, 0d, 100d);
        }
    }

    public void Load(PlayerNavigationParameter parameter)
    {
        LoadCore(parameter, playerAlreadyStopped: false);
    }

    private void LoadCore(PlayerNavigationParameter parameter, bool playerAlreadyStopped, PlaybackReloadState? reloadState = null, bool queueTransition = false)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        recoverySnapshot = null;
        BeginTrackPreferenceScope(parameter);
        IsAdvancingQueue = queueTransition;
        cancelledQueueInstanceId = 0;
        isQueuedPlayback = queueTransition || parameter.IsQueuedPlayback;
        queueLoadSessionVersion = playbackQueue?.Snapshot.SessionVersion ?? 0;
        var lifecycleToken = StartPlaybackLifecycle();
        var replacedPlaybackInstanceId = currentPlaybackInstanceId;
        _ = ReleaseSeekPreviewAsync(replacedPlaybackInstanceId);
        var playbackInstanceId = Interlocked.Increment(ref nextPlaybackInstanceId);
        currentPlaybackInstanceId = playbackInstanceId;
        acceptsPlayerEvents = true;
        backToDetailParameter = parameter.BackToDetailParameter;
        videoHostHandle = IntPtr.Zero;
        isPlayerLoaded = false;
        isPlayerLoadInProgress = false;
        isInitialPlayerConfigurationComplete = false;
        isCompleted = false;
        completionThresholdReachedPlaybackInstanceId = 0;
        hasReportedPlaying = false;
        hasShownPlaybackReportWarning = false;
        isHandlingExpiredSession = false;
        Volume = 100;
        appliedVolumeLevel = 100;
        IsMuted = false;
        IsSeeking = false;
        preservePausedAfterSeek = false;
        pauseStateLockedAfterSeek = false;
        isSeekDragging = false;
        seekPreviewPosition = null;
        transientStatusMessage = null;
        ResetPlaybackEnhancements(reloadState);
        isTrackMenuOpen = false;
        hasManualAudioSelection = false;
        manualAudioMpvTrackId = null;
        manualAudioStreamIndex = null;
        isAutoSelectingAudio = false;
        hasAttemptedAutoAudioSelection = false;
        hasManualSubtitleSelection = false;
        isAutoSelectingSubtitle = false;
        hasAttemptedAutoSubtitleSelection = false;
        isNativePlaybackReady = false;
        pendingSubtitleMpvTrackId = null;
        pendingSubtitleStreamIndex = null;
        manualSubtitleMpvTrackId = null;
        manualSubtitleStreamIndex = null;
        activeAudioStreamIndex = null;
        activeSubtitleStreamIndex = null;
        playerPreferences = PlayerPreferences.Default;
        OnPropertyChanged(nameof(ControlsHideSeconds));
        playerPreferencesLoadTask = LoadPlayerPreferencesAsync(playbackInstanceId);
        IsControlsOverlayVisible = true;
        IsFullscreen = reloadState?.IsFullscreen ?? false;
        IsPlaying = false;
        IsPaused = false;
        ErrorMessage = null;
        IsLoading = true;
        ResetNextEpisodeState();
        if (reloadState is not null) { IsAutoPlayCancelledForCurrentItem = reloadState.AutoPlayCancelled; }
        LogoUrl = parameter.LogoUrl;
        PlaybackInfo = parameter.PlaybackInfo;
        ResetTrackMenus(parameter.PlaybackInfo);
        ResetProgress(parameter.PlaybackInfo);
        stopBeforeLoadTask = playerAlreadyStopped
            ? Task.FromResult(PlayerOperationResult.Success())
            : StopPlayerForNewPlaybackAsync(replacedPlaybackInstanceId);
        _ = LoadNextEpisodeAsync(
            playbackInstanceId,
            parameter.PlaybackInfo.ItemId,
            lifecycleToken);
    }

    private async Task<PlayerPreferences> LoadPlayerPreferencesAsync(long playbackInstanceId)
    {
        try
        {
            var preferences = appSettingsService is null
                ? PlayerPreferences.Default
                : await appSettingsService
                    .GetPlayerPreferencesAsync(CancellationToken.None)
                    .ConfigureAwait(true);
            var normalized = preferences.Normalize();
            if (CanAcceptPlayerEvent(playbackInstanceId))
            {
                playerPreferences = normalized;
                OnPropertyChanged(nameof(ControlsHideSeconds));
                Volume = GetInitialVolume(normalized);
                NotifyAutoPlayStateChanged();
            }

            return normalized;
        }
        catch
        {
            if (CanAcceptPlayerEvent(playbackInstanceId))
            {
                playerPreferences = PlayerPreferences.Default;
                OnPropertyChanged(nameof(ControlsHideSeconds));
                Volume = GetInitialVolume(PlayerPreferences.Default);
                NotifyAutoPlayStateChanged();
            }

            return PlayerPreferences.Default;
        }
    }

    private async Task LoadNextEpisodeAsync(
        long playbackInstanceId,
        string itemId,
        CancellationToken cancellationToken)
    {
        var session = currentSessionService?.CurrentSession;
        if (nextEpisodeService is null || session is null)
        {
            if (CanContinuePlaybackOperation(playbackInstanceId, cancellationToken))
            {
                NextEpisode = null;
                UpdateNextEpisodePromptVisibility();
            }

            return;
        }

        NextEpisodeResult result;
        try
        {
            result = await nextEpisodeService
                .GetNextEpisodeAsync(session, itemId, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            if (CanContinuePlaybackOperation(playbackInstanceId, cancellationToken))
            {
                NextEpisode = null;
                UpdateNextEpisodePromptVisibility();
            }

            return;
        }

        if (!CanContinuePlaybackOperation(playbackInstanceId, cancellationToken))
        {
            return;
        }

        if (result.Error == NextEpisodeError.Unauthorized)
        {
            await HandleExpiredSessionAsync(playbackInstanceId).ConfigureAwait(true);
            return;
        }

        if (!result.IsSuccess)
        {
            NextEpisode = null;
            UpdateNextEpisodePromptVisibility();
            return;
        }

        NextEpisode = result.NextEpisode;
        UpdateNextEpisodePromptVisibility();
    }

    private bool CanSkipCurrentSegment()
    {
        return IsSkipSegmentVisible
            && skipSegmentTarget.HasValue
            && CanSeek;
    }

    private async Task SkipCurrentSegmentAsync()
    {
        var target = skipSegmentTarget;
        if (!CanSkipCurrentSegment() || !target.HasValue)
        {
            return;
        }

        await SeekToAsync(target.Value).ConfigureAwait(true);
    }

    private bool CanPlayNextEpisode()
    {
        return acceptsPlayerEvents
            && NextEpisode is not null
            && !IsSwitchingQuality
            && !IsAdvancingQueue
            && !IsPreparingNextEpisode
            && playbackService is not null
            && currentSessionService?.CurrentSession is not null;
    }

    private async Task PlayNextEpisodeAsync()
    {
        await PlayNextEpisodeCoreAsync().ConfigureAwait(true);
    }

    private async Task PlayNextEpisodeCoreAsync()
    {
        if (!CanPlayNextEpisode())
        {
            return;
        }

        var playbackInstanceId = currentPlaybackInstanceId;
        var requestedNextEpisode = NextEpisode!;
        var session = currentSessionService!.CurrentSession!;
        var cancellationToken = playbackLifecycleCancellation?.Token ?? CancellationToken.None;
        IsPreparingNextEpisode = true;
        IsNextEpisodePromptVisible = true;
        nextEpisodeStatusMessage = "正在准备下一集...";
        OnPropertyChanged(nameof(NextEpisodeStatusText));

        try
        {
            var result = await playbackService!
                .PreparePlaybackAsync(
                    session,
                    new PlaybackStartRequest(requestedNextEpisode.ItemId, requestedNextEpisode.Title, 0, "Episode"),
                    cancellationToken)
                .ConfigureAwait(true);
            if (!CanContinuePlaybackOperation(playbackInstanceId, cancellationToken))
            {
                return;
            }

            if (result.Error == PlaybackLoadError.Unauthorized)
            {
                await HandleExpiredSessionAsync(playbackInstanceId).ConfigureAwait(true);
                return;
            }

            if (!result.IsSuccess || result.PlaybackInfo is null)
            {
                await HandleNextEpisodeTransitionFailureAsync(
                        playbackInstanceId,
                        result.Error == PlaybackLoadError.Forbidden
                            ? "没有权限播放下一集"
                            : "下一集播放准备失败，请稍后重试")
                    .ConfigureAwait(true);
                return;
            }

            var stopResult = await StopCurrentPlaybackAsync(
                    playbackInstanceId,
                    "replace-playback")
                .ConfigureAwait(true);
            if (!CanContinuePlaybackOperation(playbackInstanceId, cancellationToken))
            {
                return;
            }

            if (!stopResult.IsSuccess)
            {
                await HandleNextEpisodeTransitionFailureAsync(
                        playbackInstanceId,
                        "无法切换下一集，请稍后重试")
                    .ConfigureAwait(true);
                return;
            }

            playbackReportScheduler.Stop(playbackInstanceId);
            var stoppedOutcome = await ReportStoppedOnceAsync(
                    playbackInstanceId,
                    isPausedSnapshot: IsPaused)
                .ConfigureAwait(true);
            if (!CanContinuePlaybackOperation(playbackInstanceId, cancellationToken))
            {
                return;
            }

            isPlayerLoaded = false;
            IsPlaying = false;
            IsPaused = false;
            IsLoading = true;

            var nextBackParameter = CreateNextEpisodeDetailParameter(
                backToDetailParameter,
                requestedNextEpisode);
            var existingVideoHostHandle = videoHostHandle;
            LoadCore(
                new PlayerNavigationParameter(
                    result.PlaybackInfo,
                    nextBackParameter,
                    requestedNextEpisode.LogoUrl),
                playerAlreadyStopped: true);
            await AttachVideoHostAsync(existingVideoHostHandle).ConfigureAwait(true);
            if (stoppedOutcome.SynchronizationFailed)
            {
                transientStatusMessage = "播放记录同步失败";
                OnPropertyChanged(nameof(StatusText));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            if (CanContinuePlaybackOperation(playbackInstanceId, cancellationToken))
            {
                await HandleNextEpisodeTransitionFailureAsync(
                        playbackInstanceId,
                        "下一集播放准备失败，请稍后重试")
                    .ConfigureAwait(true);
            }
        }
        finally
        {
            if (IsCurrentPlaybackInstance(playbackInstanceId))
            {
                IsPreparingNextEpisode = false;
                if (string.Equals(
                    nextEpisodeStatusMessage,
                    "正在准备下一集...",
                    StringComparison.Ordinal))
                {
                    nextEpisodeStatusMessage = null;
                    OnPropertyChanged(nameof(NextEpisodeStatusText));
                }
            }
        }
    }

    private static DetailNavigationParameter CreateNextEpisodeDetailParameter(
        DetailNavigationParameter? currentParameter,
        NextEpisodeInfo nextEpisode)
    {
        if (!string.IsNullOrWhiteSpace(nextEpisode.SeriesId))
        {
            var backTarget = currentParameter?.BackTarget;
            if (backTarget?.Page == AppPage.Detail
                && string.Equals(
                    backTarget.ItemId,
                    nextEpisode.SeriesId,
                    StringComparison.OrdinalIgnoreCase))
            {
                backTarget = null;
            }

            return new DetailNavigationParameter(
                nextEpisode.SeriesId,
                currentParameter?.ReturnPage,
                backTarget,
                nextEpisode.SeasonId ?? currentParameter?.SelectedSeasonId,
                nextEpisode.ItemId,
                nextEpisode.ItemId);
        }

        return new DetailNavigationParameter(
            nextEpisode.ItemId,
            currentParameter?.ReturnPage,
            currentParameter?.BackTarget);
    }

    private async Task HandleNextEpisodeTransitionFailureAsync(
        long playbackInstanceId,
        string message)
    {
        if (!CanAcceptPlayerEvent(playbackInstanceId))
        {
            return;
        }

        nextEpisodeStatusMessage = message;
        OnPropertyChanged(nameof(NextEpisodeStatusText));
        IsNextEpisodePromptVisible = true;
        if (isCompleted)
        {
            await ReportStoppedOnceAsync(playbackInstanceId, isPausedSnapshot: false).ConfigureAwait(true);
        }
    }

    public async Task AttachVideoHostAsync(IntPtr hostHandle)
    {
        if (hostHandle == IntPtr.Zero)
        {
            return;
        }

        var playbackInstanceId = currentPlaybackInstanceId;
        videoHostHandle = hostHandle;
        await LoadPlayerIfReadyAsync(playbackInstanceId).ConfigureAwait(true);
    }

    public void DetachVideoHost(IntPtr hostHandle)
    {
        if (hostHandle == IntPtr.Zero || videoHostHandle == hostHandle)
        {
            videoHostHandle = IntPtr.Zero;
            isPlayerLoaded = false;
            OnPropertyChanged(nameof(CanSeek));
        }
    }

    public async Task ReleasePlayerAsync()
    {
        var playbackInstanceId = currentPlaybackInstanceId;
        CancelPlaybackLifecycle();
        var previewRelease = ReleaseSeekPreviewAsync(playbackInstanceId);
        acceptsPlayerEvents = false;
        playbackReportScheduler.Stop(playbackInstanceId);
        videoHostHandle = IntPtr.Zero;
        IsSeeking = false;
        isSeekDragging = false;
        seekPreviewPosition = null;
        transientStatusMessage = null;
        IsLoading = false;
        IsPlaying = false;
        IsPaused = false;
        isPlayerLoaded = false;
        ResetNextEpisodeState();
        OnPropertyChanged(nameof(CanSeek));
        await StopCurrentPlaybackAsync(playbackInstanceId, "navigation").ConfigureAwait(true);
        await previewRelease.ConfigureAwait(true);
        if (IsCurrentPlaybackInstance(playbackInstanceId)) playbackQueue?.ClearCurrent(queueLoadSessionVersion);
    }

    public async Task PrepareForApplicationExitAsync()
    {
        var playbackInstanceId = currentPlaybackInstanceId;
        CancelPlaybackLifecycle();
        playbackReportScheduler.Stop(playbackInstanceId);
        await ReportStoppedOnceAsync(playbackInstanceId, isPausedSnapshot: IsPaused)
            .ConfigureAwait(true);
        await ReleasePlayerAsync().ConfigureAwait(true);
    }

    public async Task SelectAudioTrackAsync(PlayerAudioTrackViewModel? track)
    {
        await SelectAudioTrackCoreAsync(track, isManualSelection: true).ConfigureAwait(true);
    }

    private async Task SelectAudioTrackCoreAsync(
        PlayerAudioTrackViewModel? track,
        bool isManualSelection)
    {
        Interlocked.Increment(ref trackOperationsInFlight);
        NotifyEnhancementCommandStates();
        try { await SelectAudioTrackOperationAsync(track, isManualSelection).ConfigureAwait(true); }
        finally
        {
            Interlocked.Decrement(ref trackOperationsInFlight);
            NotifyEnhancementCommandStates();
        }
    }

    private async Task SelectAudioTrackOperationAsync(
        PlayerAudioTrackViewModel? track,
        bool isManualSelection)
    {
        if (track is null || !CanSelectTrack(isManualSelection))
        {
            return;
        }

        if (track.IsSelected)
        {
            if (isManualSelection)
            {
                hasManualAudioSelection = true;
                manualAudioMpvTrackId = track.MpvTrackId;
                manualAudioStreamIndex = track.MediaStreamIndex;
                RememberAudioTrackPreference(track, currentPlaybackInstanceId);
            }
            return;
        }

        var playbackInstanceId = currentPlaybackInstanceId;
        if (!CanAcceptPlayerEvent(playbackInstanceId))
        {
            return;
        }

        PlayerOperationResult result;
        if (!track.MpvTrackId.HasValue)
        {
            return;
        }

        try
        {
            result = await playerService
                .SelectAudioTrackAsync(playbackInstanceId, track.MpvTrackId.Value, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch
        {
            result = PlayerOperationResult.Failure(PlayerError.AudioTrackFailed);
        }

        if (!IsCurrentPlaybackInstance(playbackInstanceId))
        {
            return;
        }

        if (!result.IsSuccess)
        {
            transientStatusMessage = GetErrorMessage(result.Error);
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        if (isManualSelection)
        {
            hasManualAudioSelection = true;
            RememberAudioTrackPreference(track, playbackInstanceId);
            manualAudioMpvTrackId = track.MpvTrackId;
            manualAudioStreamIndex = track.MediaStreamIndex;
        }

        SetSelectedAudioTrack(track);
        activeAudioStreamIndex = track.MediaStreamIndex;
        transientStatusMessage = null;
        OnPropertyChanged(nameof(StatusText));
        QueueTrackSelectionProgressReport(playbackInstanceId, "audio-track");
    }

    public async Task SelectSubtitleTrackAsync(PlayerSubtitleTrackViewModel? track)
    {
        await SelectSubtitleTrackCoreAsync(track, isManualSelection: true).ConfigureAwait(true);
    }

    private async Task SelectSubtitleTrackCoreAsync(
        PlayerSubtitleTrackViewModel? track,
        bool isManualSelection)
    {
        Interlocked.Increment(ref trackOperationsInFlight);
        NotifyEnhancementCommandStates();
        try { await SelectSubtitleTrackOperationAsync(track, isManualSelection).ConfigureAwait(true); }
        finally
        {
            Interlocked.Decrement(ref trackOperationsInFlight);
            NotifyEnhancementCommandStates();
        }
    }

    private async Task SelectSubtitleTrackOperationAsync(
        PlayerSubtitleTrackViewModel? track,
        bool isManualSelection)
    {
        if (track is null || !CanSelectTrack(isManualSelection))
        {
            return;
        }

        if (track.IsSelected)
        {
            if (isManualSelection)
            {
                hasManualSubtitleSelection = true;
                manualSubtitleMpvTrackId = track.MpvTrackId;
                manualSubtitleStreamIndex = track.MediaStreamIndex;
                RememberSubtitleTrackPreference(track, currentPlaybackInstanceId);
            }
            return;
        }

        var playbackInstanceId = currentPlaybackInstanceId;
        if (!CanAcceptPlayerEvent(playbackInstanceId))
        {
            return;
        }

        var previousSelectedTrack = SubtitleTracks.FirstOrDefault(item => item.IsSelected);
        pendingSubtitleMpvTrackId = track.MpvTrackId;
        pendingSubtitleStreamIndex = track.MediaStreamIndex;
        PlayerOperationResult result;
        try
        {
            result = track.IsOffOption
                ? await playerService
                    .DisableSubtitleAsync(playbackInstanceId, CancellationToken.None)
                    .ConfigureAwait(true)
                : track.MpvTrackId.HasValue
                    ? await playerService
                        .SelectSubtitleTrackAsync(
                            playbackInstanceId,
                            track.MpvTrackId.Value,
                            CancellationToken.None)
                        .ConfigureAwait(true)
                    : track.IsExternal && track.MediaStreamIndex.HasValue
                        ? await playerService
                            .SelectExternalSubtitleAsync(
                                playbackInstanceId,
                                track.MediaStreamIndex.Value,
                                CancellationToken.None)
                            .ConfigureAwait(true)
                        : PlayerOperationResult.Failure(PlayerError.SubtitleTrackFailed);
        }
        catch
        {
            result = PlayerOperationResult.Failure(PlayerError.SubtitleTrackFailed);
        }

        pendingSubtitleMpvTrackId = null;
        pendingSubtitleStreamIndex = null;
        if (!IsCurrentPlaybackInstance(playbackInstanceId))
        {
            return;
        }

        if (!result.IsSuccess)
        {
            SetSelectedSubtitleTrack(previousSelectedTrack);
            transientStatusMessage = GetErrorMessage(result.Error);
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        if (isManualSelection)
        {
            hasManualSubtitleSelection = true;
            RememberSubtitleTrackPreference(track, playbackInstanceId);
            manualSubtitleMpvTrackId = track.MpvTrackId;
            manualSubtitleStreamIndex = track.MediaStreamIndex;
        }

        SetSelectedSubtitleTrack(track);
        activeSubtitleStreamIndex = track.MediaStreamIndex;
        transientStatusMessage = null;
        OnPropertyChanged(nameof(StatusText));
        QueueTrackSelectionProgressReport(playbackInstanceId, "subtitle-track");
    }

    private async Task LoadPlayerIfReadyAsync(long playbackInstanceId)
    {
        if (!IsCurrentPlaybackInstance(playbackInstanceId)
            || PlaybackInfo is null
            || videoHostHandle == IntPtr.Zero
            || isPlayerLoaded
            || isPlayerLoadInProgress)
        {
            return;
        }

        isPlayerLoadInProgress = true;
        IsLoading = true;
        ErrorMessage = null;
        ShowControlsOverlay();

        var loadAccepted = false;
        try
        {
            var stopResult = await stopBeforeLoadTask.ConfigureAwait(true);
            if (!IsCurrentPlaybackInstance(playbackInstanceId))
            {
                return;
            }

            if (!stopResult.IsSuccess)
            {
                ErrorMessage = GetErrorMessage(stopResult.Error);
                return;
            }

            if (PlaybackInfo is null || videoHostHandle == IntPtr.Zero)
            {
                return;
            }

            playerPreferences = (await playerPreferencesLoadTask.ConfigureAwait(true)).Normalize();
            NotifyShortcutBindingsChanged();
            if (!CanAcceptPlayerEvent(playbackInstanceId))
            {
                return;
            }

            Volume = pendingReloadState?.Volume ?? GetInitialVolume(playerPreferences);
            var result = await playerService
                .LoadAsync(
                    new PlayerLoadRequest(PlaybackInfo, videoHostHandle, playbackInstanceId, pendingReloadState?.IsPaused == true),
                    CancellationToken.None)
                .ConfigureAwait(true);
            if (!IsCurrentPlaybackInstance(playbackInstanceId))
            {
                return;
            }

            if (result.IsSuccess)
            {
                // A native failure can arrive before the accepted load call returns.
                if (HasError) { return; }
                isPlayerLoaded = true;
                IsPaused = pendingReloadState?.IsPaused ?? false;
                ErrorMessage = null;
                OnPropertyChanged(nameof(CanSeek));
                await ApplyInitialPlayerConfigurationAsync(playbackInstanceId).ConfigureAwait(true);
                loadAccepted = true;
                return;
            }

            ErrorMessage = GetErrorMessage(result.Error);
            await StopCurrentPlaybackAsync(playbackInstanceId, "load-failure").ConfigureAwait(true);
        }
        catch
        {
            if (IsCurrentPlaybackInstance(playbackInstanceId))
            {
                ErrorMessage = GetErrorMessage(PlayerError.PlaybackFailed);
                await StopCurrentPlaybackAsync(playbackInstanceId, "load-failure").ConfigureAwait(true);
            }
        }
        finally
        {
            if (IsCurrentPlaybackInstance(playbackInstanceId) && !loadAccepted)
            {
                IsLoading = false;
                if (isQueuedPlayback && HasError)
                {
                    playbackReportScheduler.Stop(playbackInstanceId);
                    await ReportStoppedOnceAsync(playbackInstanceId, false).ConfigureAwait(true);
                }
            }

            if (IsCurrentPlaybackInstance(playbackInstanceId))
            {
                isPlayerLoadInProgress = false;
            }
        }
    }

    private void OnPlayerStatusChanged(object? sender, PlayerStatusChangedEventArgs e)
    {
        if (synchronizationContext is null)
        {
            ApplyPlayerStatus(e);
            return;
        }

        synchronizationContext.Post(_ => ApplyPlayerStatus(e), null);
    }

    private void OnPlayerProgressChanged(object? sender, PlayerProgressChangedEventArgs e)
    {
        if (synchronizationContext is null)
        {
            ApplyPlayerProgress(e);
            return;
        }

        synchronizationContext.Post(_ => ApplyPlayerProgress(e), null);
    }

    private void ApplyPlayerStatus(PlayerStatusChangedEventArgs e)
    {
        if (!CanAcceptPlayerEvent(e.PlaybackInstanceId) || HasError)
        {
            return;
        }

        switch (e.State)
        {
            case PlayerPlaybackState.Playing:
                isNativePlaybackReady = true;
                CommitQueueCurrent();
                queueLoadCompletion?.TrySetResult(true);
                ErrorMessage = null;
                IsLoading = false;
                var keepPausedAfterSeek = pauseStateLockedAfterSeek || (IsSeeking && preservePausedAfterSeek);
                IsPaused = keepPausedAfterSeek;
                isCompleted = false;
                IsPlaying = !keepPausedAfterSeek;
                ApplyMpvTracks(e);
                _ = AutoSelectPreferredSubtitleAsync(e.PlaybackInstanceId);
                RestoreLocalSubtitleAfterReady();
                qualityLoadCompletion?.TrySetResult(true);
                OnPropertyChanged(nameof(StatusText));
                NotifyCommandStatesChanged();
                if (!keepPausedAfterSeek)
                {
                    ObservePlaybackReportTask(
                        ReportPlayingIfNeededAsync(e.PlaybackInstanceId),
                        e.PlaybackInstanceId,
                        "playing");
                }
                break;
            case PlayerPlaybackState.Paused:
                isNativePlaybackReady = true;
                CommitQueueCurrent();
                queueLoadCompletion?.TrySetResult(true);
                ErrorMessage = null;
                IsLoading = false;
                pauseStateLockedAfterSeek = false;
                IsPlaying = false;
                IsPaused = true;
                ApplyMpvTracks(e);
                _ = AutoSelectPreferredSubtitleAsync(e.PlaybackInstanceId);
                RestoreLocalSubtitleAfterReady();
                qualityLoadCompletion?.TrySetResult(true);
                ShowControlsOverlay();
                NotifyCommandStatesChanged();
                if (pendingReloadState is not null)
                {
                    ObservePlaybackReportTask(
                        ReportPlayingIfNeededAsync(e.PlaybackInstanceId),
                        e.PlaybackInstanceId,
                        "paused-quality-ready");
                }
                ObservePlaybackReportTask(
                    ReportProgressAsync(e.PlaybackInstanceId, isPausedSnapshot: true),
                    e.PlaybackInstanceId,
                    "pause");
                break;
            case PlayerPlaybackState.Completed:
                if (Volatile.Read(ref closingPlaybackInstanceId) == e.PlaybackInstanceId
                    || handledCompletionPlaybackInstanceId == e.PlaybackInstanceId)
                {
                    break;
                }

                ApplyNaturalCompletionSnapshot();
                IsLoading = false;
                pauseStateLockedAfterSeek = false;
                IsPlaying = false;
                IsPaused = false;
                isCompleted = true;
                ShowControlsOverlay();
                playbackReportScheduler.Stop(e.PlaybackInstanceId);
                UpdateNextEpisodePromptVisibility();
                handledCompletionPlaybackInstanceId = e.PlaybackInstanceId;
                _ = HandlePlaybackCompletedAsync(e.PlaybackInstanceId);
                OnPropertyChanged(nameof(StatusText));
                NotifyCommandStatesChanged();
                break;
            case PlayerPlaybackState.Failed:
                CaptureRecoverySnapshot();
                queueLoadCompletion?.TrySetResult(false);
                qualityLoadCompletion?.TrySetResult(false);
                IsLoading = false;
                pauseStateLockedAfterSeek = false;
                IsPlaying = false;
                IsPaused = false;
                isCompleted = false;
                ShowControlsOverlay();
                playbackReportScheduler.Stop(e.PlaybackInstanceId);
                ErrorMessage = GetErrorMessage(e.Error);
                failedPlayerStopTask = isQueuedPlayback
                    ? StopFailedQueuePlaybackAsync(e.PlaybackInstanceId)
                    : StopFailedPlaybackAsync(e.PlaybackInstanceId);
                break;
        }
    }

    private void ApplyPlayerProgress(PlayerProgressChangedEventArgs e)
    {
        if (!CanAcceptPlayerEvent(e.PlaybackInstanceId) || isCompleted || HasError)
        {
            return;
        }

        if (isSeekDragging || IsSeeking)
        {
            return;
        }

        if (e.Position.HasValue)
        {
            currentPosition = e.Position.Value;
            OnPropertyChanged(nameof(CurrentTimeText));
        }

        if (IsValidDuration(e.Duration))
        {
            duration = e.Duration;
            OnPropertyChanged(nameof(DurationText));
            OnPropertyChanged(nameof(CanSeek));
        }

        if (e.Percent.HasValue)
        {
            ProgressPercent = e.Percent.Value;
        }
        else
        {
            ProgressPercent = CalculateProgressPercent(currentPosition, duration);
        }

        UpdateNextEpisodePromptVisibility();
        EvaluatePlaybackCompletionThreshold(e.PlaybackInstanceId, e.IsPaused);
    }

    private async Task PlayAsync()
    {
        transientStatusMessage = null;
        OnPropertyChanged(nameof(StatusText));

        var result = await playerService
            .PlayAsync(CancellationToken.None)
            .ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            ErrorMessage = GetErrorMessage(result.Error);
            return;
        }

        ErrorMessage = null;
        pauseStateLockedAfterSeek = false;
        IsPaused = false;
        IsPlaying = true;
        isCompleted = false;
        OnPropertyChanged(nameof(StatusText));
        await ReportProgressAsync(currentPlaybackInstanceId, isPausedSnapshot: false).ConfigureAwait(true);
    }

    private async Task PauseAsync()
    {
        transientStatusMessage = null;
        OnPropertyChanged(nameof(StatusText));

        var result = await playerService
            .PauseAsync(CancellationToken.None)
            .ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            ErrorMessage = GetErrorMessage(result.Error);
            return;
        }

        ErrorMessage = null;
        pauseStateLockedAfterSeek = false;
        IsPlaying = false;
        IsPaused = true;
        isCompleted = false;
        OnPropertyChanged(nameof(StatusText));
        await ReportProgressAsync(currentPlaybackInstanceId, isPausedSnapshot: true).ConfigureAwait(true);
    }

    public async Task TogglePlayPauseAsync()
    {
        if (!CanTogglePlayPause)
        {
            return;
        }

        if (IsPlaying && !IsPaused)
        {
            await PauseAsync().ConfigureAwait(true);
            return;
        }

        await PlayAsync().ConfigureAwait(true);
    }

    public Task SeekRelativeAsync(TimeSpan offset)
    {
        if (!CanSeek)
        {
            return Task.CompletedTask;
        }

        var basePosition = currentPosition ?? TimeSpan.Zero;
        var target = ClampSeekTarget(basePosition + offset);
        return target.HasValue ? SeekToAsync(target.Value) : Task.CompletedTask;
    }

    public Task SeekToPercentAsync(double percent)
    {
        if (!CanSeek)
        {
            return Task.CompletedTask;
        }

        var target = CalculateSeekTarget(percent);
        return target.HasValue ? SeekToAsync(target.Value) : Task.CompletedTask;
    }

    public Task AdjustVolumeAsync(int delta)
    {
        if (!CanUseVolumeControls())
        {
            return Task.CompletedTask;
        }

        return SetVolumeAsync(Volume + delta);
    }

    public async Task SetVolumeAsync(double value)
    {
        if (!CanUseVolumeControls())
        {
            return;
        }

        var targetVolume = (int)Math.Round(
            double.IsNaN(value) || double.IsInfinity(value) ? Volume : value,
            MidpointRounding.AwayFromZero);
        targetVolume = Math.Clamp(targetVolume, 0, 100);
        if (targetVolume == Volume)
        {
            return;
        }

        await SetPlayerVolumeCoreAsync(currentPlaybackInstanceId, targetVolume, force: false, persistLastVolume: true)
            .ConfigureAwait(true);
    }

    private async Task ApplyInitialVolumeAsync(long playbackInstanceId)
    {
        var targetVolume = pendingReloadState?.Volume ?? GetInitialVolume(playerPreferences);
        await SetPlayerVolumeCoreAsync(playbackInstanceId, targetVolume, force: true, persistLastVolume: false)
            .ConfigureAwait(true);
    }

    private async Task ApplyInitialPlayerConfigurationAsync(long playbackInstanceId)
    {
        try
        {
            await ApplyInitialVolumeAsync(playbackInstanceId).ConfigureAwait(true);
            await SetPlayerMuteCoreAsync(playbackInstanceId, targetMuted: pendingReloadState?.IsMuted ?? false).ConfigureAwait(true);
            if (selectedPlaybackSpeed != 1)
                await ApplyRequestedPlaybackSpeedAsync(playbackInstanceId).ConfigureAwait(true);
            if (pendingReloadState is not null)
            {
                var token = playbackLifecycleCancellation?.Token ?? CancellationToken.None;
                await ApplySubtitleAdjustmentAsync(playbackInstanceId, SubtitleAdjustmentKind.DelaySeconds, subtitleDelaySeconds, token);
                await ApplySubtitleAdjustmentAsync(playbackInstanceId, SubtitleAdjustmentKind.Scale, subtitleScale, token);
                await ApplySubtitleAdjustmentAsync(playbackInstanceId, SubtitleAdjustmentKind.Position, subtitlePosition, token);
            }
            if (AudioTracks.Any(track => track.MpvTrackId.HasValue))
            {
                await AutoSelectPreferredAudioAsync(playbackInstanceId).ConfigureAwait(true);
            }

            if (pendingReloadState?.LocalSubtitlePath is not null || SubtitleTracks.Any(track =>
                    track.MpvTrackId.HasValue
                    || (track.IsExternal && track.MediaStreamIndex.HasValue)))
            {
                await AutoSelectPreferredSubtitleAsync(playbackInstanceId).ConfigureAwait(true);
            }
        }
        finally
        {
            if (IsCurrentPlaybackInstance(playbackInstanceId))
            {
                isInitialPlayerConfigurationComplete = true;
                if (!IsLoading && (IsPlaying || (IsPaused && pendingReloadState is not null)))
                {
                    ObservePlaybackReportTask(
                        ReportPlayingIfNeededAsync(playbackInstanceId),
                        playbackInstanceId,
                        "initial-player-configuration");
                }
            }
        }
    }

    private async Task SetPlayerVolumeCoreAsync(
        long playbackInstanceId,
        int targetVolume,
        bool force,
        bool persistLastVolume)
    {
        if (!IsCurrentPlaybackInstance(playbackInstanceId))
        {
            return;
        }

        targetVolume = Math.Clamp(targetVolume, 0, 100);
        if (!force && targetVolume == Volume)
        {
            return;
        }

        PlayerOperationResult result;
        try
        {
            result = await playerService
                .SetVolumeAsync(targetVolume, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch
        {
            result = PlayerOperationResult.Failure(PlayerError.VolumeFailed);
        }

        if (!result.IsSuccess)
        {
            transientStatusMessage = GetErrorMessage(result.Error);
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        if (!IsCurrentPlaybackInstance(playbackInstanceId))
        {
            return;
        }

        Volume = targetVolume;
        appliedVolumeLevel = targetVolume;
        transientStatusMessage = null;
        OnPropertyChanged(nameof(StatusText));
        if (persistLastVolume)
        {
            await SaveLastVolumeAsync(playbackInstanceId, targetVolume).ConfigureAwait(true);
        }
    }

    private static int GetInitialVolume(PlayerPreferences preferences)
    {
        var normalized = preferences.Normalize();
        return normalized.RememberLastVolume && normalized.LastVolume.HasValue
            ? normalized.LastVolume.Value
            : normalized.DefaultVolume;
    }

    private async Task SaveLastVolumeAsync(long playbackInstanceId, int targetVolume)
    {
        if (appSettingsService is null || !playerPreferences.RememberLastVolume)
        {
            return;
        }

        try
        {
            var updatedPreferences = await appSettingsService
                .UpdatePlayerPreferencesAsync(
                    currentPreferences => currentPreferences.RememberLastVolume
                        ? currentPreferences with { LastVolume = Math.Clamp(targetVolume, 0, 100) }
                        : currentPreferences,
                    CancellationToken.None)
                .ConfigureAwait(true);
            if (IsCurrentPlaybackInstance(playbackInstanceId))
            {
                playerPreferences = updatedPreferences.Normalize();
            }
        }
        catch
        {
            if (IsCurrentPlaybackInstance(playbackInstanceId))
            {
                transientStatusMessage = "\u97f3\u91cf\u5df2\u8c03\u6574\uff0c\u4f46\u672a\u80fd\u4fdd\u5b58\u4e0a\u6b21\u97f3\u91cf";
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public async Task ToggleMuteAsync()
    {
        if (!CanUseVolumeControls())
        {
            return;
        }

        await SetPlayerMuteCoreAsync(currentPlaybackInstanceId, !IsMuted).ConfigureAwait(true);
    }

    private async Task SetPlayerMuteCoreAsync(long playbackInstanceId, bool targetMuted)
    {
        if (!IsCurrentPlaybackInstance(playbackInstanceId))
        {
            return;
        }

        PlayerOperationResult result;
        try
        {
            result = await playerService
                .SetMuteAsync(targetMuted, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch
        {
            result = PlayerOperationResult.Failure(PlayerError.MuteFailed);
        }

        if (!result.IsSuccess)
        {
            transientStatusMessage = GetErrorMessage(result.Error);
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        if (!IsCurrentPlaybackInstance(playbackInstanceId))
        {
            return;
        }

        IsMuted = targetMuted;
        transientStatusMessage = null;
        OnPropertyChanged(nameof(StatusText));
    }

    private async Task StopAndNavigateBackAsync()
    {
        var playbackInstanceId = currentPlaybackInstanceId;
        CancelPlaybackLifecycle();
        playbackReportScheduler.Stop(playbackInstanceId);
        var stoppedOutcome = await ReportStoppedOnceAsync(
                playbackInstanceId,
                isPausedSnapshot: IsPaused)
            .ConfigureAwait(true);
        if (isHandlingExpiredSession || navigationService.CurrentPage == AppPage.Login)
        {
            return;
        }

        await ReleasePlayerAsync().ConfigureAwait(true);
        navigationService.NavigateTo(
            AppPage.Detail,
            backToDetailParameter is null
                ? null
                : backToDetailParameter with
                {
                    PlaybackState = stoppedOutcome.PlaybackState
                });
    }

    public void BeginSeekDrag()
    {
        if (!CanSeek)
        {
            return;
        }

        isSeekDragging = true;
        transientStatusMessage = null;
        seekPreviewPosition = CalculateSeekTarget(SeekPercent);
        ShowControlsOverlay();
        OnPropertyChanged(nameof(CurrentTimeText));
        OnPropertyChanged(nameof(StatusText));
    }

    public void UpdateSeekDrag(double percent)
    {
        if (!isSeekDragging || !CanSeek)
        {
            return;
        }

        SeekPercent = percent;
    }

    public async Task CompleteSeekDragAsync(double percent)
    {
        if (!isSeekDragging)
        {
            return;
        }

        isSeekDragging = false;
        SeekPercent = percent;
        var target = CalculateSeekTarget(SeekPercent);
        seekPreviewPosition = null;
        OnPropertyChanged(nameof(CurrentTimeText));

        if (!CanSeek || !target.HasValue)
        {
            SeekPercent = ProgressPercent;
            return;
        }

        await SeekToAsync(target.Value).ConfigureAwait(true);
    }

    private async Task SeekToAsync(TimeSpan target)
    {
        if (!CanSeek)
        {
            return;
        }

        if (PlaybackInfo is { StreamPositionOffsetTicks: > 0 } info && target.Ticks < info.StreamPositionOffsetTicks)
        {
            await SwitchQualityAsync(info.Quality, target.Ticks).ConfigureAwait(true);
            return;
        }

        var playbackInstanceId = currentPlaybackInstanceId;
        var previousPosition = currentPosition;
        var previousProgressPercent = progressPercent;
        var previousSeekPercent = seekPercent;
        var targetPercent = CalculateProgressPercent(target, duration);
        var wasPaused = IsPaused;

        isCompleted = false;
        ApplySeekSnapshot(target, targetPercent);
        ShowControlsOverlay();
        preservePausedAfterSeek = wasPaused;
        IsSeeking = true;
        try
        {
            var result = await playerService
                .SeekAsync(playbackInstanceId, target, CancellationToken.None)
                .ConfigureAwait(true);
            if (!IsCurrentPlaybackInstance(playbackInstanceId))
            {
                return;
            }

            transientStatusMessage = result.IsSuccess
                ? null
                : GetErrorMessage(result.Error);
            if (result.IsSuccess)
            {
                pauseStateLockedAfterSeek = wasPaused;
                RestorePlaybackStateAfterSeek(wasPaused);
                await ReportProgressAsync(playbackInstanceId, isPausedSnapshot: IsPaused).ConfigureAwait(true);
                return;
            }

            pauseStateLockedAfterSeek = false;
            RestoreSeekSnapshot(previousPosition, previousProgressPercent, previousSeekPercent);
        }
        catch
        {
            if (IsCurrentPlaybackInstance(playbackInstanceId))
            {
                pauseStateLockedAfterSeek = false;
                transientStatusMessage = GetErrorMessage(PlayerError.SeekFailed);
                RestoreSeekSnapshot(previousPosition, previousProgressPercent, previousSeekPercent);
            }
        }
        finally
        {
            if (IsCurrentPlaybackInstance(playbackInstanceId))
            {
                IsSeeking = false;
                preservePausedAfterSeek = false;
                RestorePlaybackStateAfterSeek(wasPaused);
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    private void RestorePlaybackStateAfterSeek(bool wasPaused)
    {
        if (wasPaused)
        {
            IsPaused = true;
            IsPlaying = false;
        }
        else if (!isCompleted && string.IsNullOrEmpty(ErrorMessage))
        {
            IsPaused = false;
            IsPlaying = true;
        }

        NotifyCommandStatesChanged();
    }

    private void ApplySeekSnapshot(TimeSpan position, double percent)
    {
        currentPosition = position;
        progressPercent = NormalizePercent(percent);
        seekPercent = progressPercent;
        OnPropertyChanged(nameof(CurrentTimeText));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(SeekPercent));
        OnPropertyChanged(nameof(StatusText));
        UpdateNextEpisodePromptVisibility();
    }

    private void RestoreSeekSnapshot(
        TimeSpan? position,
        double restoredProgressPercent,
        double restoredSeekPercent)
    {
        currentPosition = position;
        progressPercent = NormalizePercent(restoredProgressPercent);
        seekPercent = NormalizePercent(restoredSeekPercent);
        OnPropertyChanged(nameof(CurrentTimeText));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(SeekPercent));
        UpdateNextEpisodePromptVisibility();
    }

    public void CancelSeekDrag()
    {
        if (!isSeekDragging)
        {
            return;
        }

        isSeekDragging = false;
        seekPreviewPosition = null;
        SeekPercent = ProgressPercent;
        OnPropertyChanged(nameof(CurrentTimeText));
    }

    public void ShowControlsOverlay()
    {
        IsControlsOverlayVisible = true;
    }

    public void SetFullscreen(bool fullscreen)
    {
        IsFullscreen = fullscreen;
        ShowControlsOverlay();
    }

    public void HideControlsOverlayIfAllowed()
    {
        if (CanHideControlsOverlay())
        {
            IsControlsOverlayVisible = false;
            return;
        }

        ShowControlsOverlay();
    }

    public void SetTrackMenuOpen(bool isOpen)
    {
        isTrackMenuOpen = isOpen;
        if (isOpen)
        {
            ShowControlsOverlay();
        }
    }

    private bool CanUsePlaybackControls()
    {
        return isPlayerLoaded && !IsLoading && !HasError;
    }

    private bool CanUseVolumeControls()
    {
        return acceptsPlayerEvents && isPlayerLoaded && !IsLoading && !HasError;
    }

    private bool CanUseTrackControls()
    {
        return acceptsPlayerEvents && isPlayerLoaded && !IsLoading && !HasError && !IsSwitchingQuality && !IsAdvancingQueue && !isImportingLocalSubtitle;
    }

    private bool CanSelectTrack(bool isManualSelection)
    {
        return CanUseTrackControls()
            || (!isManualSelection && acceptsPlayerEvents && isPlayerLoaded && !HasError);
    }

    private bool CanHideControlsOverlay()
    {
        return acceptsPlayerEvents
            && IsPlaying
            && !IsPaused
            && !IsLoading
            && !HasError
            && !IsSeeking
            && !isSeekDragging
            && !isTrackMenuOpen
            && !IsPreparingNextEpisode;
    }

    private async Task<PlayerOperationResult> StopPlayerForNewPlaybackAsync(long replacedPlaybackInstanceId)
    {
        try
        {
            PlayerDebugDiagnostics.WriteStopRequested(
                replacedPlaybackInstanceId,
                "replace-playback");
            return await playerService.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch
        {
            return PlayerOperationResult.Failure(PlayerError.PlaybackFailed);
        }
    }

    private async Task<PlayerOperationResult> StopCurrentPlaybackAsync(
        long playbackInstanceId,
        string stopOrigin)
    {
        if (!IsCurrentPlaybackInstance(playbackInstanceId))
        {
            return PlayerOperationResult.Failure(PlayerError.PlaybackFailed);
        }

        try
        {
            PlayerDebugDiagnostics.WriteStopRequested(playbackInstanceId, stopOrigin);
            return await playerService.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch
        {
            return PlayerOperationResult.Failure(PlayerError.PlaybackFailed);
        }
    }

    private async Task StopFailedPlaybackAsync(long playbackInstanceId)
    {
        await StopCurrentPlaybackAsync(playbackInstanceId, "load-failure").ConfigureAwait(true);
        if (!IsCurrentPlaybackInstance(playbackInstanceId))
        {
            return;
        }

        isPlayerLoaded = false;
        NotifyCommandStatesChanged();
    }

    private bool IsCurrentPlaybackInstance(long playbackInstanceId)
    {
        return playbackInstanceId != 0 && playbackInstanceId == currentPlaybackInstanceId;
    }

    private CancellationToken StartPlaybackLifecycle()
    {
        CancelPlaybackLifecycle();
        playbackLifecycleCancellation = new CancellationTokenSource();
        return playbackLifecycleCancellation.Token;
    }

    private void CancelPlaybackLifecycle()
    {
        var cancellation = playbackLifecycleCancellation;
        playbackLifecycleCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private bool CanContinuePlaybackOperation(
        long playbackInstanceId,
        CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested
            && CanAcceptPlayerEvent(playbackInstanceId);
    }

    private bool CanAcceptPlayerEvent(long playbackInstanceId)
    {
        return acceptsPlayerEvents && IsCurrentPlaybackInstance(playbackInstanceId)
            && cancelledQueueInstanceId != playbackInstanceId;
    }

    private void ResetProgress(PlaybackInfo playbackInfo)
    {
        currentPosition = null;
        duration = TicksToDuration(playbackInfo.RunTimeTicks);
        progressPercent = 0;
        seekPercent = 0;
        seekPreviewPosition = null;
        OnPropertyChanged(nameof(CurrentTimeText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(SeekPercent));
        OnPropertyChanged(nameof(CanSeek));
    }

    private void ResetNextEpisodeState()
    {
        NextEpisode = null;
        IsNextEpisodePromptVisible = false;
        IsPreparingNextEpisode = false;
        IsAutoPlayCancelledForCurrentItem = false;
        nextEpisodeStatusMessage = null;
        OnPropertyChanged(nameof(NextEpisodeStatusText));
        SetSkipSegmentState(null);
        handledCompletionPlaybackInstanceId = 0;
        NotifyAutoPlayStateChanged();
    }

    private void UpdateNextEpisodePromptVisibility()
    {
        var skipTarget = FindSkipSegmentTarget(
            PlaybackInfo?.Markers,
            currentPosition,
            duration);
        var isIntroWindow = skipTarget?.Kind == SkipSegmentKind.Intro;
        var isCreditsWindow = skipTarget?.Kind == SkipSegmentKind.Credits;

        IsNextEpisodePromptVisible = NextEpisode is not null
            && !IsAutoPlayCancelledForCurrentItem
            && !isIntroWindow
            && (isCreditsWindow || IsInNextEpisodePromptWindow(currentPosition, duration));

        var visibleSkipTarget = skipTarget is { Kind: SkipSegmentKind.Intro }
            ? skipTarget
            : null;
        SetSkipSegmentState(!isCompleted && !IsLoading && CanSeek ? visibleSkipTarget : null);
        NotifyAutoPlayStateChanged();
    }

    private void SetSkipSegmentState(SkipSegmentTarget? target)
    {
        skipSegmentTarget = target?.Target;
        SkipSegmentButtonText = target?.Kind switch
        {
            SkipSegmentKind.Intro => "跳过片头",
            _ => string.Empty
        };
        IsSkipSegmentVisible = target is not null;
        NotifySkipSegmentCommandStateChanged();
    }

    private static SkipSegmentTarget? FindSkipSegmentTarget(
        IReadOnlyList<PlaybackMarker>? markers,
        TimeSpan? position,
        TimeSpan? totalDuration)
    {
        if (markers is not { Count: > 0 }
            || !position.HasValue
            || position.Value < TimeSpan.Zero)
        {
            return null;
        }

        var durationTicks = totalDuration is { } validDuration && validDuration > TimeSpan.Zero
            ? validDuration.Ticks
            : (long?)null;
        var orderedMarkers = markers
            .Where(marker => marker.StartPositionTicks >= 0
                && (!durationTicks.HasValue || marker.StartPositionTicks <= durationTicks.Value))
            .OrderBy(marker => marker.StartPositionTicks)
            .ThenBy(marker => marker.Type)
            .ToArray();
        var positionTicks = position.Value.Ticks;
        long? introStartTicks = null;

        foreach (var marker in orderedMarkers)
        {
            if (marker.Type == PlaybackMarkerType.IntroStart)
            {
                introStartTicks = marker.StartPositionTicks;
                continue;
            }

            if (marker.Type != PlaybackMarkerType.IntroEnd || !introStartTicks.HasValue)
            {
                continue;
            }

            if (marker.StartPositionTicks > introStartTicks.Value
                && positionTicks >= introStartTicks.Value
                && positionTicks < marker.StartPositionTicks)
            {
                return new SkipSegmentTarget(
                    SkipSegmentKind.Intro,
                    TimeSpan.FromTicks(marker.StartPositionTicks));
            }

            introStartTicks = null;
        }

        if (!durationTicks.HasValue)
        {
            return null;
        }

        var creditsStartTicks = orderedMarkers
            .Where(marker => marker.Type == PlaybackMarkerType.CreditsStart)
            .Select(marker => (long?)marker.StartPositionTicks)
            .FirstOrDefault();
        var creditsTargetTicks = Math.Max(0, durationTicks.Value - TimeSpan.FromSeconds(1).Ticks);
        return creditsStartTicks.HasValue
            && positionTicks >= creditsStartTicks.Value
            && positionTicks < creditsTargetTicks
                ? new SkipSegmentTarget(
                    SkipSegmentKind.Credits,
                    TimeSpan.FromTicks(creditsTargetTicks))
                : null;
    }

    private async Task HandlePlaybackCompletedAsync(long playbackInstanceId)
    {
        if (!CanAcceptPlayerEvent(playbackInstanceId))
        {
            return;
        }

        await ReportProgressAsync(playbackInstanceId, isPausedSnapshot: false).ConfigureAwait(true);
        if (!CanAcceptPlayerEvent(playbackInstanceId)
            || Volatile.Read(ref closingPlaybackInstanceId) == playbackInstanceId)
        {
            return;
        }

        if (playbackQueue?.Snapshot is { Pending.Count: > 0 } queueSnapshot)
        {
            if (queueSnapshot.AutoPlayEnabled)
            {
                await AdvanceQueueAsync().ConfigureAwait(true);
            }
            if (IsCurrentPlaybackInstance(playbackInstanceId))
                await ReportStoppedOnceAsync(playbackInstanceId, isPausedSnapshot: false).ConfigureAwait(true);
            return;
        }
        if (ShouldAutoPlayNextEpisode(playbackInstanceId))
        {
            await PlayNextEpisodeCoreAsync().ConfigureAwait(true);
            return;
        }

        await ReportStoppedOnceAsync(playbackInstanceId, isPausedSnapshot: false).ConfigureAwait(true);
    }

    private void EvaluatePlaybackCompletionThreshold(
        long playbackInstanceId,
        bool? isPausedSnapshot)
    {
        if (!IsPlaybackCompletionThresholdReached(currentPosition, duration)
            || completionThresholdReachedPlaybackInstanceId == playbackInstanceId
            || !CanReportPlayback(playbackInstanceId)
            || !IsPlaying
            || IsPaused
            || isPausedSnapshot == true)
        {
            return;
        }

        completionThresholdReachedPlaybackInstanceId = playbackInstanceId;
        // The watched threshold is not EOF. Keep periodic reports running, including after a failure.
        ObservePlaybackReportTask(
            ReportProgressAsync(playbackInstanceId, isPausedSnapshot: false),
            playbackInstanceId,
            "completion-threshold");
    }

    private void ApplyNaturalCompletionSnapshot()
    {
        var runTimeTicks = GetSafeRunTimeTicks();
        if (runTimeTicks is not > 0)
        {
            return;
        }

        duration = TimeSpan.FromTicks(runTimeTicks.Value);
        currentPosition = duration;
        ProgressPercent = 100d;
        OnPropertyChanged(nameof(CurrentTimeText));
        OnPropertyChanged(nameof(DurationText));
    }

    private bool IsPlaybackCompletionThresholdReached(
        TimeSpan? position,
        TimeSpan? totalDuration)
    {
        if (!position.HasValue
            || !IsValidDuration(totalDuration)
            || position.Value < TimeSpan.Zero)
        {
            return false;
        }

        if (CalculateProgressPercent(position, totalDuration) >= PlaybackCompletionThreshold)
        {
            return true;
        }

        var isLongFormMedia = string.Equals(PlaybackInfo?.MediaType, "Movie", StringComparison.OrdinalIgnoreCase)
            || string.Equals(PlaybackInfo?.MediaType, "Episode", StringComparison.OrdinalIgnoreCase);
        return isLongFormMedia
            && totalDuration!.Value > CompletionRemainingThreshold
            && totalDuration.Value - position.Value < CompletionRemainingThreshold;
    }

    private bool ShouldAutoPlayNextEpisode(long playbackInstanceId)
    {
        return IsCurrentPlaybackInstance(playbackInstanceId)
            && IsAutoPlayNextEpisodeEnabled
            && !IsAutoPlayCancelledForCurrentItem
            && NextEpisode is not null
            && !IsPreparingNextEpisode
            && !IsSwitchingQuality;
    }

    private void CancelAutoPlayNextEpisode()
    {
        IsAutoPlayCancelledForCurrentItem = true;
        IsNextEpisodePromptVisible = false;
    }

    private int? GetRemainingSeconds()
    {
        if (!currentPosition.HasValue || !duration.HasValue || duration.Value <= TimeSpan.Zero)
        {
            return null;
        }

        var remaining = duration.Value - currentPosition.Value;
        if (remaining <= TimeSpan.Zero)
        {
            return 0;
        }

        return (int)Math.Ceiling(remaining.TotalSeconds);
    }

    private void NotifyAutoPlayStateChanged()
    {
        OnPropertyChanged(nameof(IsAutoPlayNextEpisodeEnabled));
        OnPropertyChanged(nameof(IsAutoPlayCountdownVisible));
        OnPropertyChanged(nameof(AutoPlayCountdownText));
        OnPropertyChanged(nameof(NextEpisodeStatusText));
        OnPropertyChanged(nameof(AutoPlayCountdownProgress));
        if (CancelAutoPlayNextEpisodeCommand is RelayCommand command)
        {
            command.RaiseCanExecuteChanged();
        }
    }

    private static bool IsInNextEpisodePromptWindow(TimeSpan? position, TimeSpan? totalDuration)
    {
        if (!position.HasValue
            || !totalDuration.HasValue
            || totalDuration.Value <= TimeSpan.Zero
            || position.Value < TimeSpan.Zero)
        {
            return false;
        }

        var safePosition = position.Value > totalDuration.Value
            ? totalDuration.Value
            : position.Value;
        if (totalDuration.Value < TimeSpan.FromMinutes(5))
        {
            return safePosition.TotalMilliseconds / totalDuration.Value.TotalMilliseconds >= 0.85d;
        }

        return totalDuration.Value - safePosition <= TimeSpan.FromSeconds(45);
    }

    private void NotifyNextEpisodeCommandStateChanged()
    {
        if (PlayNextEpisodeCommand is AsyncRelayCommand command)
        {
            command.NotifyCanExecuteChanged();
        }
    }

    private void NotifySkipSegmentCommandStateChanged()
    {
        if (SkipSegmentCommand is AsyncRelayCommand command)
        {
            command.NotifyCanExecuteChanged();
        }
    }

    private void ResetTrackMenus(PlaybackInfo playbackInfo)
    {
        AudioTracks = playbackInfo.AudioTracks
            .Select(track => new PlayerAudioTrackViewModel(track))
            .ToArray();
        var selectedAudio = AudioTracks.FirstOrDefault(track => track.IsDefault)
            ?? AudioTracks.FirstOrDefault();
        if (selectedAudio is not null)
        {
            selectedAudio.IsSelected = true;
        }

        var subtitles = new List<PlayerSubtitleTrackViewModel>
        {
            new((PlaybackSubtitle?)null)
        };
        subtitles.AddRange(playbackInfo.Subtitles.Select(subtitle => new PlayerSubtitleTrackViewModel(subtitle)));
        SubtitleTracks = subtitles;
        // Server default metadata is a preference, not confirmation that MPV displays it.
        var selectedSubtitle = SubtitleTracks.First();
        selectedSubtitle.IsSelected = true;
        OnPropertyChanged(nameof(CurrentAudioTrackText));
        OnPropertyChanged(nameof(CurrentSubtitleText));
    }

    private void ApplyMpvTracks(PlayerStatusChangedEventArgs e)
    {
        if (e.Tracks.Count == 0)
        {
            return;
        }

        var audioTrackInfos = e.Tracks
            .Where(track => string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        activeAudioStreamIndex = null;
        if (audioTrackInfos.Length > 0)
        {
            AudioTracks = audioTrackInfos
                .Select(track => new PlayerAudioTrackViewModel(track))
                .ToArray();
            var confirmedAudioInfo = audioTrackInfos.FirstOrDefault(track => track.IsSelected == true);
            var confirmedAudio = FindAudioTrackByFlag(audioTrackInfos, track => track.IsSelected == true);
            var manualAudio = hasManualAudioSelection
                ? FindAudioTrack(manualAudioMpvTrackId, manualAudioStreamIndex)
                : null;
            activeAudioStreamIndex = confirmedAudioInfo?.MediaStreamIndex
                ?? manualAudio?.MediaStreamIndex;
            var selectedAudio = confirmedAudio
                ?? manualAudio
                ?? FindAudioTrackByFlag(audioTrackInfos, track => track.IsDefault == true)
                ?? AudioTracks.FirstOrDefault();
            if (selectedAudio is not null)
            {
                SetSelectedAudioTrack(selectedAudio);
            }

            if (isPlayerLoaded
                && !hasManualAudioSelection
                && !isAutoSelectingAudio
                && !hasAttemptedAutoAudioSelection)
            {
                _ = AutoSelectPreferredAudioAsync(e.PlaybackInstanceId);
            }
        }

        var subtitleTrackInfos = e.Tracks
            .Where(track => string.Equals(track.Type, "sub", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var activeExternalStreamIndex = activeSubtitleStreamIndex;
        activeSubtitleStreamIndex = null;
        var externalSubtitles = GetExternalSubtitlesMissingFromMpv(subtitleTrackInfos).ToArray();
        if (subtitleTrackInfos.Length == 0 && externalSubtitles.Length == 0)
        {
            return;
        }

        var subtitles = new List<PlayerSubtitleTrackViewModel> { new((PlaybackSubtitle?)null) };
        subtitles.AddRange(subtitleTrackInfos.Select(track => new PlayerSubtitleTrackViewModel(track)));
        subtitles.AddRange(externalSubtitles);
        SubtitleTracks = subtitles;

        var selectedSubtitleInfo = subtitleTrackInfos.FirstOrDefault(track => track.IsSelected == true);
        var selectedSubtitle = FindSelectedSubtitleTrack(subtitleTrackInfos);
        var selectedExternal = activeExternalStreamIndex.HasValue
            ? SubtitleTracks.FirstOrDefault(track =>
                !track.MpvTrackId.HasValue
                && track.IsExternal
                && track.MediaStreamIndex == activeExternalStreamIndex)
            : null;
        activeSubtitleStreamIndex = selectedSubtitleInfo?.MediaStreamIndex;
        if (selectedSubtitle is not null)
        {
            SetSelectedSubtitleTrack(selectedSubtitle);
        }
        else if (pendingSubtitleMpvTrackId.HasValue || pendingSubtitleStreamIndex.HasValue)
        {
            var pendingTrack = FindSubtitleTrack(
                pendingSubtitleMpvTrackId,
                pendingSubtitleStreamIndex);
            SetSelectedSubtitleTrack(pendingTrack);
            activeSubtitleStreamIndex = pendingTrack?.MediaStreamIndex;
        }
        else if (hasManualSubtitleSelection)
        {
            var manualTrack = FindSubtitleTrack(
                manualSubtitleMpvTrackId,
                manualSubtitleStreamIndex);
            SetSelectedSubtitleTrack(manualTrack);
            activeSubtitleStreamIndex = manualTrack?.MediaStreamIndex;
        }
        else if (selectedExternal is not null)
        {
            SetSelectedSubtitleTrack(selectedExternal);
            activeSubtitleStreamIndex = selectedExternal.MediaStreamIndex;
        }
        else
        {
            SetSelectedSubtitleTrack(SubtitleTracks.FirstOrDefault(track => track.IsOffOption));
        }

        if (isPlayerLoaded
            && !hasManualSubtitleSelection
            && !isAutoSelectingSubtitle
            && !hasAttemptedAutoSubtitleSelection)
        {
            _ = AutoSelectPreferredSubtitleAsync(e.PlaybackInstanceId);
        }
    }

    private PlayerAudioTrackViewModel? FindAudioTrackByFlag(
        IReadOnlyList<PlayerTrackInfo> trackInfos,
        Func<PlayerTrackInfo, bool> predicate)
    {
        for (var index = 0; index < trackInfos.Count && index < AudioTracks.Count; index++)
        {
            if (predicate(trackInfos[index]))
            {
                return AudioTracks[index];
            }
        }

        return null;
    }

    private PlayerSubtitleTrackViewModel? FindSelectedSubtitleTrack(IReadOnlyList<PlayerTrackInfo> trackInfos)
    {
        for (var index = 0; index < trackInfos.Count && index + 1 < SubtitleTracks.Count; index++)
        {
            if (trackInfos[index].IsSelected == true)
            {
                return SubtitleTracks[index + 1];
            }
        }

        return null;
    }

    private PlayerAudioTrackViewModel? FindAudioTrack(int? mpvTrackId, int? mediaStreamIndex)
    {
        return mpvTrackId.HasValue
            ? AudioTracks.FirstOrDefault(track => track.MpvTrackId == mpvTrackId)
            : mediaStreamIndex.HasValue
                ? AudioTracks.FirstOrDefault(track => track.MediaStreamIndex == mediaStreamIndex)
                : null;
    }

    private PlayerSubtitleTrackViewModel? FindSubtitleTrack(int? mpvTrackId, int? mediaStreamIndex)
    {
        return mpvTrackId.HasValue
            ? SubtitleTracks.FirstOrDefault(track => track.MpvTrackId == mpvTrackId)
            : mediaStreamIndex.HasValue
                ? SubtitleTracks.FirstOrDefault(track => track.MediaStreamIndex == mediaStreamIndex)
                : SubtitleTracks.FirstOrDefault(track => track.IsOffOption);
    }

    private IEnumerable<PlayerSubtitleTrackViewModel> GetExternalSubtitlesMissingFromMpv(
        IReadOnlyList<PlayerTrackInfo> trackInfos)
    {
        if (PlaybackInfo is null)
        {
            return Array.Empty<PlayerSubtitleTrackViewModel>();
        }

        var mappedStreamIndexes = trackInfos
            .Where(track => track.MediaStreamIndex.HasValue)
            .Select(track => track.MediaStreamIndex!.Value)
            .ToHashSet();
        return PlaybackInfo.Subtitles
            .Where(subtitle => subtitle.IsExternal && !mappedStreamIndexes.Contains(subtitle.Index))
            .Select(subtitle => new PlayerSubtitleTrackViewModel(subtitle))
            .ToArray();
    }

    private async Task AutoSelectPreferredSubtitleAsync(long playbackInstanceId)
    {
        if (!CanAcceptPlayerEvent(playbackInstanceId)
            || !isNativePlaybackReady || !isPlayerLoaded
            || hasManualSubtitleSelection
            || hasAttemptedAutoSubtitleSelection)
        {
            return;
        }

        // Local files must wait for native FileLoaded; LoadAsync only accepts a pending load.
        if (pendingReloadState?.LocalSubtitlePath is not null) return;
        var target = pendingReloadState?.SubtitleStreamIndex is int requestedSubtitle
            ? (requestedSubtitle == -1 ? SubtitleTracks.FirstOrDefault(track => track.IsOffOption)
                : SubtitleTracks.FirstOrDefault(track => track.MediaStreamIndex == requestedSubtitle))
            : FindSubtitleTrackWithPreferences();
        if (target is null)
        {
            return;
        }

        hasAttemptedAutoSubtitleSelection = true;
        isAutoSelectingSubtitle = true;
        try
        {
            await SelectSubtitleTrackCoreAsync(target, isManualSelection: false).ConfigureAwait(true);
        }
        finally
        {
            isAutoSelectingSubtitle = false;
        }
    }

    private async Task AutoSelectPreferredAudioAsync(long playbackInstanceId)
    {
        if (!CanAcceptPlayerEvent(playbackInstanceId)
            || hasManualAudioSelection
            || hasAttemptedAutoAudioSelection)
        {
            return;
        }

        hasAttemptedAutoAudioSelection = true;
        var target = pendingReloadState?.AudioStreamIndex is int requestedAudio
            ? AudioTracks.FirstOrDefault(track => track.MediaStreamIndex == requestedAudio)
            : FindAudioTrackWithPreferences();
        if (target is null || target.IsSelected)
        {
            return;
        }

        isAutoSelectingAudio = true;
        try
        {
            await SelectAudioTrackCoreAsync(target, isManualSelection: false).ConfigureAwait(true);
        }
        finally
        {
            isAutoSelectingAudio = false;
        }
    }

    private PlayerSubtitleTrackViewModel? FindPreferredSubtitleTrack()
    {
        if (IsAutomaticLanguagePreference(playerPreferences.DefaultSubtitleLanguage))
        {
            return null;
        }

        var availableSubtitles = SubtitleTracks
            .Where(track => !track.IsOffOption)
            .ToArray();
        if (availableSubtitles.Length == 0)
        {
            return null;
        }

        return availableSubtitles.FirstOrDefault(track => IsPreferredSubtitle(track, MatchStrength.Exact))
            ?? availableSubtitles.FirstOrDefault(track => IsPreferredSubtitle(track, MatchStrength.Alias))
            ?? availableSubtitles.FirstOrDefault(track => IsPreferredSubtitle(track, MatchStrength.Contains));
    }

    private PlayerAudioTrackViewModel? FindPreferredAudioTrack()
    {
        if (IsAutomaticLanguagePreference(playerPreferences.DefaultAudioLanguage))
        {
            return null;
        }

        return AudioTracks.FirstOrDefault(track => IsPreferredAudio(track, MatchStrength.Exact))
            ?? AudioTracks.FirstOrDefault(track => IsPreferredAudio(track, MatchStrength.Alias))
            ?? AudioTracks.FirstOrDefault(track => IsPreferredAudio(track, MatchStrength.Contains));
    }

    private bool IsPreferredSubtitle(PlayerSubtitleTrackViewModel track, MatchStrength strength)
    {
        return MatchesPreferredLanguage(
            new[] { track.Language, track.DisplayTitle, track.DisplayText },
            playerPreferences.DefaultSubtitleLanguage,
            strength);
    }

    private bool IsPreferredAudio(PlayerAudioTrackViewModel track, MatchStrength strength)
    {
        return MatchesPreferredLanguage(
            new[] { track.Language, track.DisplayTitle, track.DisplayText },
            playerPreferences.DefaultAudioLanguage,
            strength);
    }

    private static bool MatchesPreferredLanguage(
        IEnumerable<string?> values,
        string preferredLanguage,
        MatchStrength strength)
    {
        if (IsAutomaticLanguagePreference(preferredLanguage))
        {
            return false;
        }

        var preferred = preferredLanguage.Trim().ToLowerInvariant();
        var preferredAliases = GetLanguageAliases(preferred).ToArray();
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var normalized = value!.Trim().ToLowerInvariant();
            if (strength == MatchStrength.Exact && normalized == preferred)
            {
                return true;
            }

            if (strength == MatchStrength.Alias
                && preferredAliases.Any(alias => IsAliasMatch(normalized, alias)))
            {
                return true;
            }

            if (strength == MatchStrength.Contains
                && (normalized.Contains(preferred, StringComparison.OrdinalIgnoreCase)
                    || preferredAliases.Any(alias => normalized.Contains(alias, StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetLanguageAliases(string preferred)
    {
        if (IsChineseAlias(preferred))
        {
            return new[]
            {
                "zh",
                "zho",
                "chi",
                "chs",
                "cht",
                "cn",
                "sc",
                "tc",
                "chinese",
                "\u4e2d\u6587",
                "\u7b80\u4f53",
                "\u7e41\u4f53",
                "\u7e41\u9ad4"
            };
        }

        return preferred switch
        {
            "en" or "eng" or "english" or "\u82f1\u6587" => new[] { "en", "eng", "english", "\u82f1\u6587" },
            "ja" or "jpn" or "japanese" or "\u65e5\u6587" or "\u65e5\u8bed" => new[] { "ja", "jpn", "japanese", "\u65e5\u6587", "\u65e5\u8bed" },
            "ko" or "kor" or "korean" or "\u97e9\u6587" or "\u97e9\u8bed" => new[] { "ko", "kor", "korean", "\u97e9\u6587", "\u97e9\u8bed" },
            _ => new[] { preferred }
        };
    }

    private static bool IsAutomaticLanguagePreference(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            || string.Equals(value.Trim(), "\u81ea\u52a8", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value.Trim(), "\u81ea\u5b9a\u4e49", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsChineseAlias(string value)
    {
        return value is "zh" or "zho" or "chi" or "chs" or "cht" or "cn" or "sc" or "tc"
            || value.StartsWith("zh-", StringComparison.OrdinalIgnoreCase)
            || value.Contains("chinese", StringComparison.OrdinalIgnoreCase)
            || value.Contains("\u4e2d\u6587", StringComparison.Ordinal)
            || value.Contains("\u7b80\u4f53", StringComparison.Ordinal)
            || value.Contains("\u7e41\u4f53", StringComparison.Ordinal)
            || value.Contains("\u7e41\u9ad4", StringComparison.Ordinal);
    }

    private static bool IsAliasMatch(string normalized, string alias)
    {
        return normalized == alias
            || (alias == "zh" && normalized.StartsWith("zh-", StringComparison.OrdinalIgnoreCase));
    }

    private enum MatchStrength
    {
        Exact,
        Alias,
        Contains
    }

    private void SetSelectedAudioTrack(PlayerAudioTrackViewModel? selectedTrack)
    {
        foreach (var track in AudioTracks)
        {
            track.IsSelected = ReferenceEquals(track, selectedTrack);
        }

        OnPropertyChanged(nameof(CurrentAudioTrackText));
    }

    private void SetSelectedSubtitleTrack(PlayerSubtitleTrackViewModel? selectedTrack)
    {
        foreach (var track in SubtitleTracks)
        {
            track.IsSelected = ReferenceEquals(track, selectedTrack);
        }

        OnPropertyChanged(nameof(CurrentSubtitleText));
    }

    private void OnPlaybackReportTick(object? sender, long playbackInstanceId)
    {
        if (synchronizationContext is null)
        {
            ObservePlaybackReportTask(
                ReportPeriodicProgressAsync(playbackInstanceId),
                playbackInstanceId,
                "periodic");
            return;
        }

        synchronizationContext.Post(
            _ => ObservePlaybackReportTask(
                ReportPeriodicProgressAsync(playbackInstanceId),
                playbackInstanceId,
                "periodic"),
            null);
    }

    private void QueueTrackSelectionProgressReport(long playbackInstanceId, string operation)
    {
        if (!hasReportedPlaying
            || (!IsPlaying && !IsPaused)
            || currentSessionService?.CurrentSession is null
            || !CanReportPlayback(playbackInstanceId))
        {
            return;
        }

        ObservePlaybackReportTask(
            ReportProgressAsync(playbackInstanceId, IsPaused),
            playbackInstanceId,
            operation);
    }

    private void ObservePlaybackReportTask(
        Task reportTask,
        long playbackInstanceId,
        string operation)
    {
        _ = ObservePlaybackReportTaskAsync(reportTask, playbackInstanceId, operation);
    }

    private static async Task ObservePlaybackReportTaskAsync(
        Task reportTask,
        long playbackInstanceId,
        string operation)
    {
        try
        {
            await reportTask.ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Player] unhandled {operation} playback-report failure instance={playbackInstanceId}: {exception}");
        }
    }

    private async Task ReportPlayingIfNeededAsync(long playbackInstanceId)
    {
        if (hasReportedPlaying
            || !isInitialPlayerConfigurationComplete
            || !CanReportPlayback(playbackInstanceId))
        {
            return;
        }

        hasReportedPlaying = true;
        var session = currentSessionService?.CurrentSession;
        var info = PlaybackInfo;
        if (session is null || info is null || playbackReportService is null)
        {
            return;
        }

        PlaybackReportResult result;
        await playbackReportSemaphore.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!CanReportPlayback(playbackInstanceId))
            {
                return;
            }

            var snapshot = CapturePlaybackReportSnapshot(IsPaused);
            result = await playbackReportService
                .ReportPlayingAsync(
                    session,
                    new PlaybackReportStartRequest(
                        info.ItemId,
                        info.MediaSource.Id,
                        info.PlaySessionId,
                        snapshot.PositionTicks,
                        snapshot.RunTimeTicks,
                        snapshot.IsPaused,
                        snapshot.CanSeek,
                        GetPlayMethod(info),
                        snapshot.IsMuted,
                        snapshot.VolumeLevel,
                        snapshot.AudioStreamIndex,
                        snapshot.SubtitleStreamIndex),
                    CancellationToken.None)
                .ConfigureAwait(true);

            if (!await HandlePlaybackReportResultAsync(playbackInstanceId, result).ConfigureAwait(true)
                && CanReportPlayback(playbackInstanceId))
            {
                playbackReportScheduler.Start(playbackInstanceId, ProgressReportInterval);
            }
        }
        catch (Exception exception)
        {
            ShowPlaybackReportWarning(playbackInstanceId, exception.GetType().Name);
            if (CanReportPlayback(playbackInstanceId))
            {
                playbackReportScheduler.Start(playbackInstanceId, ProgressReportInterval);
            }

            return;
        }
        finally
        {
            playbackReportSemaphore.Release();
        }
    }

    private Task ReportPeriodicProgressAsync(long playbackInstanceId)
    {
        if (!CanReportPlayback(playbackInstanceId) || !IsPlaying || IsPaused)
        {
            return Task.CompletedTask;
        }

        return ReportProgressAsync(playbackInstanceId, isPausedSnapshot: false);
    }

    private async Task ReportProgressAsync(long playbackInstanceId, bool isPausedSnapshot)
    {
        if (!hasReportedPlaying || !CanReportPlayback(playbackInstanceId))
        {
            return;
        }

        var session = currentSessionService?.CurrentSession;
        var info = PlaybackInfo;
        if (session is null || info is null || playbackReportService is null)
        {
            return;
        }

        PlaybackReportResult result;
        await playbackReportSemaphore.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!hasReportedPlaying || !CanReportPlayback(playbackInstanceId))
            {
                return;
            }

            var snapshot = CapturePlaybackReportSnapshot(isPausedSnapshot);
            result = await playbackReportService
                .ReportProgressAsync(
                    session,
                    new PlaybackReportProgressRequest(
                        info.ItemId,
                        info.MediaSource.Id,
                        info.PlaySessionId,
                        snapshot.PositionTicks,
                        snapshot.RunTimeTicks,
                        snapshot.IsPaused,
                        snapshot.CanSeek,
                        GetPlayMethod(info),
                        snapshot.IsMuted,
                        snapshot.VolumeLevel,
                        snapshot.AudioStreamIndex,
                        snapshot.SubtitleStreamIndex),
                    CancellationToken.None)
                .ConfigureAwait(true);

            await HandlePlaybackReportResultAsync(playbackInstanceId, result).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowPlaybackReportWarning(playbackInstanceId, exception.GetType().Name);
            return;
        }
        finally
        {
            playbackReportSemaphore.Release();
        }
    }

    private Task<StoppedReportOutcome> ReportStoppedOnceAsync(
        long playbackInstanceId,
        bool isPausedSnapshot)
    {
        AuthSession session;
        IPlaybackReportService reportService;
        PlaybackReportStoppedRequest request;
        lock (stoppedReportGate)
        {
            if (lastStoppedReportAttemptedInstanceId == playbackInstanceId
                && playbackInstanceId != 0)
            {
                return lastStoppedReportTask;
            }

            var playbackState = CapturePlaybackReturnState();
            if (!CanReportPlayback(playbackInstanceId))
            {
                return Task.FromResult(CreateStoppedReportOutcome(
                    playbackState,
                    synchronizationFailed: true));
            }

            var currentSession = currentSessionService?.CurrentSession;
            var info = PlaybackInfo;
            var currentReportService = playbackReportService;
            if (currentSession is null || info is null || currentReportService is null)
            {
                return Task.FromResult(CreateStoppedReportOutcome(
                    playbackState,
                    synchronizationFailed: true));
            }

            session = currentSession;
            reportService = currentReportService;
            var snapshot = CapturePlaybackReportSnapshot(isPausedSnapshot);
            request = new PlaybackReportStoppedRequest(
                info.ItemId,
                info.MediaSource.Id,
                info.PlaySessionId,
                playbackState?.PositionTicks ?? GetSafePositionTicks(),
                playbackState?.RunTimeTicks ?? GetSafeRunTimeTicks(),
                snapshot.IsPaused,
                snapshot.CanSeek,
                GetPlayMethod(info),
                snapshot.IsMuted,
                snapshot.VolumeLevel,
                snapshot.AudioStreamIndex,
                snapshot.SubtitleStreamIndex);
            lastStoppedReportAttemptedInstanceId = playbackInstanceId;
            Volatile.Write(ref closingPlaybackInstanceId, playbackInstanceId);
            var completion = new TaskCompletionSource<StoppedReportOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lastStoppedReportTask = completion.Task;
            _ = CompleteStoppedReportAsync(
                playbackInstanceId,
                session,
                reportService,
                request,
                playbackState,
                completion);
            return lastStoppedReportTask;
        }
    }

    private async Task CompleteStoppedReportAsync(
        long playbackInstanceId,
        AuthSession session,
        IPlaybackReportService reportService,
        PlaybackReportStoppedRequest request,
        PlaybackReturnState? playbackState,
        TaskCompletionSource<StoppedReportOutcome> completion)
    {
        try
        {
            var synchronizationFailed = await SendStoppedReportAsync(
                    playbackInstanceId,
                    session,
                    reportService,
                    request)
                .ConfigureAwait(true);
            completion.TrySetResult(CreateStoppedReportOutcome(
                playbackState,
                synchronizationFailed));
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Player] unhandled stopped playback-report failure instance={playbackInstanceId}: {exception}");
            completion.TrySetResult(CreateStoppedReportOutcome(
                playbackState,
                synchronizationFailed: true));
        }
    }

    private async Task<bool> SendStoppedReportAsync(
        long playbackInstanceId,
        AuthSession session,
        IPlaybackReportService reportService,
        PlaybackReportStoppedRequest request)
    {
        PlaybackReportResult result;
        await playbackReportSemaphore.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!CanSendStoppedReport(playbackInstanceId))
            {
                return true;
            }

            result = await reportService
                .ReportStoppedAsync(session, request, CancellationToken.None)
                .ConfigureAwait(true);

            if (!result.IsSuccess && result.Error != PlaybackReportError.Unauthorized)
            {
                if (result.Error == PlaybackReportError.Cancelled)
                {
                    return false;
                }

                ShowPlaybackReportWarning(playbackInstanceId, result.Error.ToString());
                return true;
            }

            if (result.Error == PlaybackReportError.Unauthorized)
            {
                await HandlePlaybackReportResultAsync(playbackInstanceId, result).ConfigureAwait(true);
                return true;
            }

            return false;
        }
        catch (Exception exception)
        {
            ShowPlaybackReportWarning(playbackInstanceId, exception.GetType().Name);
            return true;
        }
        finally
        {
            playbackReportSemaphore.Release();
        }
    }

    private void ShowPlaybackReportWarning(long playbackInstanceId, string reason)
    {
        if (!CanAcceptPlayerEvent(playbackInstanceId) || hasShownPlaybackReportWarning)
        {
            return;
        }

        hasShownPlaybackReportWarning = true;
        System.Diagnostics.Debug.WriteLine(
            $"[Player] playback-report failed instance={playbackInstanceId} reason={reason}");
        transientStatusMessage = "播放记录同步失败";
        OnPropertyChanged(nameof(StatusText));
    }

    private async Task<bool> HandlePlaybackReportResultAsync(
        long playbackInstanceId,
        PlaybackReportResult result)
    {
        if (result.IsSuccess || result.Error == PlaybackReportError.Cancelled)
        {
            return false;
        }

        if (result.Error != PlaybackReportError.Unauthorized)
        {
            ShowPlaybackReportWarning(playbackInstanceId, result.Error.ToString());
            return false;
        }

        if (!CanAcceptPlayerEvent(playbackInstanceId))
        {
            return false;
        }

        await HandleExpiredSessionAsync(playbackInstanceId).ConfigureAwait(true);
        return true;
    }

    private async Task HandleExpiredSessionAsync(long playbackInstanceId)
    {
        if (isHandlingExpiredSession || !IsCurrentPlaybackInstance(playbackInstanceId))
        {
            return;
        }

        isHandlingExpiredSession = true;
        try
        {
            CancelPlaybackLifecycle();
            playbackReportScheduler.Stop(playbackInstanceId);
            acceptsPlayerEvents = false;
            await StopCurrentPlaybackAsync(playbackInstanceId, "unknown").ConfigureAwait(true);
            if (authSessionStore is not null)
            {
                try
                {
                    await authSessionStore.ClearAsync(CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Trace.TraceError(
                        $"Failed to clear expired playback session from persistent storage: {exception}");
                }
            }

            currentSessionService?.ClearSession();
            navigationService.NavigateTo(AppPage.Login);
            showLoginError?.Invoke(SessionExpiredMessage);
        }
        finally
        {
            isHandlingExpiredSession = false;
        }
    }

    private bool CanReportPlayback(long playbackInstanceId)
    {
        return playbackReportService is not null
            && acceptsPlayerEvents
            && IsCurrentPlaybackInstance(playbackInstanceId)
            && Volatile.Read(ref closingPlaybackInstanceId) != playbackInstanceId;
    }

    private bool CanSendStoppedReport(long playbackInstanceId)
    {
        return playbackReportService is not null
            && acceptsPlayerEvents
            && IsCurrentPlaybackInstance(playbackInstanceId)
            && navigationService.CurrentPage != AppPage.Login;
    }

    private PlaybackReturnState? CapturePlaybackReturnState()
    {
        var info = PlaybackInfo;
        if (info is null || string.IsNullOrWhiteSpace(info.ItemId))
        {
            return null;
        }

        var positionTicks = GetSafePositionTicks();
        var runTimeTicks = GetSafeRunTimeTicks();
        var playedPercentage = runTimeTicks is > 0
            ? Math.Clamp(positionTicks * 100d / runTimeTicks.Value, 0d, 100d)
            : (double?)null;
        var isPlayed = isCompleted || IsPlaybackCompletionThresholdReached(
            TimeSpan.FromTicks(positionTicks),
            runTimeTicks is > 0 ? TimeSpan.FromTicks(runTimeTicks.Value) : null);
        return new PlaybackReturnState(
            info.ItemId,
            positionTicks,
            runTimeTicks,
            isPlayed ? 100d : playedPercentage,
            isPlayed,
            IsSynchronized: true);
    }

    private PlaybackReportSnapshot CapturePlaybackReportSnapshot(bool isPausedSnapshot)
    {
        return new PlaybackReportSnapshot(
            GetSafePositionTicks(),
            GetSafeRunTimeTicks(),
            isPausedSnapshot,
            CanSeek,
            IsMuted,
            Math.Clamp(appliedVolumeLevel, 0, 100),
            activeAudioStreamIndex,
            activeSubtitleStreamIndex);
    }

    private static StoppedReportOutcome CreateStoppedReportOutcome(
        PlaybackReturnState? playbackState,
        bool synchronizationFailed)
    {
        return new StoppedReportOutcome(
            playbackState is null
                ? null
                : playbackState with { IsSynchronized = !synchronizationFailed },
            synchronizationFailed);
    }

    private long GetSafePositionTicks()
    {
        var ticks = TimeSpanToTicks(currentPosition);
        if (ticks <= 0)
        {
            ticks = Math.Max(0, PlaybackInfo?.StartPositionTicks ?? 0);
        }

        var runTimeTicks = GetSafeRunTimeTicks();
        if (runTimeTicks is > 0)
        {
            ticks = Math.Min(ticks, runTimeTicks.Value);
        }

        return Math.Max(0, ticks);
    }

    private long? GetSafeRunTimeTicks()
    {
        var ticks = TimeSpanToTicks(duration);
        if (ticks > 0)
        {
            return ticks;
        }

        var fallbackTicks = PlaybackInfo?.RunTimeTicks;
        return fallbackTicks is > 0 ? fallbackTicks : null;
    }

    private static long TimeSpanToTicks(TimeSpan? value)
    {
        if (!value.HasValue
            || double.IsNaN(value.Value.TotalSeconds)
            || double.IsInfinity(value.Value.TotalSeconds)
            || value.Value <= TimeSpan.Zero)
        {
            return 0;
        }

        return value.Value.Ticks;
    }

    private static string GetPlayMethod(PlaybackInfo info)
    {
        if (info.MediaInfo?.Method is PlaybackMethod.DirectPlay or PlaybackMethod.DirectStream or PlaybackMethod.Transcode)
        {
            return info.MediaInfo.Method.ToString();
        }
        if (info.RequiresTranscoding)
        {
            return "Transcode";
        }

        if (info.MediaSource.SupportsDirectPlay)
        {
            return "DirectPlay";
        }

        return info.MediaSource.SupportsDirectStream ? "DirectStream" : "DirectPlay";
    }

    private void NotifyCommandStatesChanged()
    {
        NotifyRecoveryCommandState();
        NotifyEnhancementCommandStates();
        if (PlayCommand is AsyncRelayCommand playCommand)
        {
            playCommand.NotifyCanExecuteChanged();
        }

        if (PauseCommand is AsyncRelayCommand pauseCommand)
        {
            pauseCommand.NotifyCanExecuteChanged();
        }

        if (TogglePlayPauseCommand is AsyncRelayCommand toggleCommand)
        {
            toggleCommand.NotifyCanExecuteChanged();
        }

        if (VolumeUpCommand is AsyncRelayCommand volumeUpCommand)
        {
            volumeUpCommand.NotifyCanExecuteChanged();
        }

        if (VolumeDownCommand is AsyncRelayCommand volumeDownCommand)
        {
            volumeDownCommand.NotifyCanExecuteChanged();
        }

        if (ToggleMuteCommand is AsyncRelayCommand muteCommand)
        {
            muteCommand.NotifyCanExecuteChanged();
        }

        NotifyNextEpisodeCommandStateChanged();
        NotifySkipSegmentCommandStateChanged();

        OnPropertyChanged(nameof(CanSeek));
        OnPropertyChanged(nameof(CanTogglePlayPause));
        OnPropertyChanged(nameof(PlayPauseButtonText));
        OnPropertyChanged(nameof(PlayPauseGlyph));
    }

    private sealed record StoppedReportOutcome(
        PlaybackReturnState? PlaybackState,
        bool SynchronizationFailed);

    private sealed record PlaybackReportSnapshot(
        long PositionTicks,
        long? RunTimeTicks,
        bool IsPaused,
        bool CanSeek,
        bool IsMuted,
        int VolumeLevel,
        int? AudioStreamIndex,
        int? SubtitleStreamIndex);

    private static string GetErrorMessage(PlayerError error)
    {
        return error switch
        {
            PlayerError.NoPlayableUrl => "未找到可播放地址",
            PlayerError.RuntimeMissing => "播放器运行文件缺失",
            PlayerError.InitializationFailed => "播放器初始化失败",
            PlayerError.HeaderSetupFailed => "播放器请求头设置失败",
            PlayerError.LoadFailed => "视频加载失败，请稍后重试",
            PlayerError.ResumeFailed => "继续播放失败，请稍后重试",
            PlayerError.SeekFailed => "跳转失败，请稍后重试",
            PlayerError.VolumeFailed => "音量调整失败",
            PlayerError.MuteFailed => "静音切换失败",
            PlayerError.AudioTrackFailed => "音轨切换失败",
            PlayerError.SubtitleTrackFailed => "字幕切换失败",
            _ => "播放失败"
        };
    }

    private static string FormatPosition(long ticks)
    {
        if (ticks <= 0)
        {
            return "从头开始";
        }

        var timeSpan = TimeSpan.FromTicks(ticks);
        if (timeSpan.TotalHours >= 1)
        {
            return $"{(int)timeSpan.TotalHours:D2}:{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}";
        }

        return $"{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}";
    }

    private static TimeSpan? TicksToDuration(long? ticks)
    {
        if (!ticks.HasValue || ticks.Value <= 0)
        {
            return null;
        }

        return TimeSpan.FromTicks(ticks.Value);
    }

    private static bool IsValidDuration(TimeSpan? value)
    {
        return value.HasValue && value.Value > TimeSpan.Zero;
    }

    private static double CalculateProgressPercent(TimeSpan? position, TimeSpan? duration)
    {
        if (!position.HasValue || !IsValidDuration(duration))
        {
            return 0;
        }

        var durationValue = duration.GetValueOrDefault();
        return Math.Clamp(position.Value.TotalSeconds / durationValue.TotalSeconds * 100d, 0d, 100d);
    }

    private TimeSpan? CalculateSeekTarget(double percent)
    {
        if (!IsValidDuration(duration))
        {
            return null;
        }

        var durationValue = duration.GetValueOrDefault();
        var targetSeconds = durationValue.TotalSeconds * NormalizePercent(percent) / 100d;
        targetSeconds = Math.Clamp(targetSeconds, 0d, durationValue.TotalSeconds);
        return TimeSpan.FromSeconds(targetSeconds);
    }

    private TimeSpan? ClampSeekTarget(TimeSpan target)
    {
        if (!IsValidDuration(duration))
        {
            return null;
        }

        var durationValue = duration.GetValueOrDefault();
        if (target < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return target > durationValue ? durationValue : target;
    }

    private static double NormalizePercent(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value)
            ? 0d
            : Math.Clamp(value, 0d, 100d);
    }

    private static string FormatProgressTime(TimeSpan? value, bool isDuration)
    {
        if (!value.HasValue || value.Value < TimeSpan.Zero)
        {
            return isDuration ? "--:--" : "00:00";
        }

        var timeSpan = value.Value;
        if (timeSpan.TotalHours >= 1)
        {
            return $"{(int)timeSpan.TotalHours:D2}:{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}";
        }

        return $"{(int)timeSpan.TotalMinutes:D2}:{timeSpan.Seconds:D2}";
    }

    private enum SkipSegmentKind
    {
        Intro,
        Credits
    }

    private sealed record SkipSegmentTarget(
        SkipSegmentKind Kind,
        TimeSpan Target);
}
