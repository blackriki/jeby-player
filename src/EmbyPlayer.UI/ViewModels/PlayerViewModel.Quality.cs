using System.Windows.Input;
using EmbyPlayer.Core.Playback;
using EmbyPlayer.UI.Navigation;

namespace EmbyPlayer.UI.ViewModels;

public sealed partial class PlayerViewModel
{
    private bool isSwitchingQuality;
    private PlaybackReloadState? pendingReloadState;
    private TaskCompletionSource<bool>? qualityLoadCompletion;
    private Task failedPlayerStopTask = Task.CompletedTask;
    private int trackOperationsInFlight;

    public ICommand SelectQualityCommand { get; private set; } = null!;
    public bool IsSwitchingQuality
    {
        get => isSwitchingQuality;
        private set
        {
            isSwitchingQuality = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(QualityStatusText));
            NotifyCommandStatesChanged();
        }
    }

    public bool CanSwitchQuality => CanUsePlaybackControls() && acceptsPlayerEvents
        && playbackService is not null && currentSessionService?.CurrentSession is not null
        && !IsSwitchingQuality && !IsAdvancingQueue && !IsPreparingNextEpisode && !isCompleted && !IsSeeking && !isSeekDragging
        && Volatile.Read(ref trackOperationsInFlight) == 0 && !isAdjustingSubtitles;
    public string QualityButtonText => QualityLabel(PlaybackInfo?.Quality ?? PlaybackQuality.Original);
    public string QualityStatusText => IsSwitchingQuality ? "正在切换画质…" : "切换后保留播放位置";
    public IReadOnlyList<PlayerQualityOption> QualityOptions => Enum.GetValues<PlaybackQuality>()
        .Select(quality => new PlayerQualityOption(quality, QualityLabel(quality), PlaybackInfo?.Quality == quality)).ToArray();
    public string SourceResolutionText => PlaybackInfo?.MediaInfo is { Width: > 0, Height: > 0 } info
        ? $"{info.Width} × {info.Height}" : "服务器未提供";
    public string SourceBitrateText => PlaybackInfo?.MediaInfo?.Bitrate is > 0 and var bitrate
        ? $"{bitrate / 1_000_000d:0.##} Mbps" : "服务器未提供";
    public string SourceVideoCodecText => string.IsNullOrWhiteSpace(PlaybackInfo?.MediaInfo?.VideoCodec)
        ? "服务器未提供" : PlaybackInfo.MediaInfo.VideoCodec.ToUpperInvariant();
    public string PlaybackMethodText => PlaybackInfo?.MediaInfo?.Method switch
    {
        PlaybackMethod.DirectPlay => "原文件直连",
        PlaybackMethod.DirectStream => "直接串流（封装转换）",
        PlaybackMethod.Transcode => "服务器转码",
        _ => PlaybackInfo?.RequiresTranscoding == true ? "服务器转码" : "播放方式暂不可用"
    };

    private void InitializeQualityCommands()
    {
        SelectQualityCommand = new AsyncRelayCommand(
            value => value is PlaybackQuality quality ? SwitchQualityAsync(quality) : Task.CompletedTask,
            value => value is PlaybackQuality && CanSwitchQuality);
    }

    private void NotifyQualityInfoChanged()
    {
        OnPropertyChanged(nameof(QualityButtonText));
        OnPropertyChanged(nameof(QualityOptions));
        OnPropertyChanged(nameof(SourceResolutionText));
        OnPropertyChanged(nameof(SourceBitrateText));
        OnPropertyChanged(nameof(SourceVideoCodecText));
        OnPropertyChanged(nameof(PlaybackMethodText));
    }

    private static string QualityLabel(PlaybackQuality quality) => quality switch
    {
        PlaybackQuality.FullHd1080 => "1080p · 8 Mbps",
        PlaybackQuality.Hd720 => "720p · 4 Mbps",
        PlaybackQuality.Sd480 => "480p · 1.5 Mbps",
        _ => "原画"
    };

    public async Task SwitchQualityAsync(PlaybackQuality quality, long? requestedPositionTicks = null)
    {
        if (!CanSwitchQuality || PlaybackInfo is not { } originalInfo || (originalInfo.Quality == quality && !requestedPositionTicks.HasValue)
            || !Enum.IsDefined(quality)) { return; }

        var instance = currentPlaybackInstanceId;
        var operationInstance = instance;
        var token = playbackLifecycleCancellation?.Token ?? CancellationToken.None;
        var session = currentSessionService!.CurrentSession!;
        var requestedState = CaptureReloadState();
        IsSwitchingQuality = true;
        PlayerOptionsMessage = null;
        ShowControlsOverlay();
        try
        {
            var result = await playbackService!.PreparePlaybackAsync(session,
                new PlaybackStartRequest(originalInfo.ItemId, originalInfo.Title,
                    Math.Max(0, requestedPositionTicks ?? currentPosition?.Ticks ?? originalInfo.StartPositionTicks),
                    originalInfo.MediaType, originalInfo.ProductionYear, quality, originalInfo.MediaSource.Id,
                    requestedState.AudioStreamIndex, requestedState.SubtitleStreamIndex), token).ConfigureAwait(true);
            if (!CanContinuePlaybackOperation(instance, token)) { return; }
            if (result.Error == PlaybackLoadError.Unauthorized)
            {
                await HandleExpiredSessionAsync(instance).ConfigureAwait(true);
                return;
            }
            if (!result.IsSuccess || result.PlaybackInfo is null)
            {
                PlayerOptionsMessage = result.Error == PlaybackLoadError.Forbidden
                    ? "服务器不允许使用此画质，已保持原播放"
                    : "无法准备此画质，已保持原播放，请重试";
                return;
            }
            if (isCompleted)
            {
                PlayerOptionsMessage = "播放已结束，未切换画质";
                return;
            }

            var resumeTicks = Math.Max(0, requestedPositionTicks ?? currentPosition?.Ticks ?? originalInfo.StartPositionTicks);
            var state = CaptureReloadState();
            var host = videoHostHandle;
            var back = backToDetailParameter;
            var logo = LogoUrl;
            var stop = await StopCurrentPlaybackAsync(instance, "quality-switch").ConfigureAwait(true);
            if (!CanContinuePlaybackOperation(instance, token)) { return; }
            if (!stop.IsSuccess)
            {
                PlayerOptionsMessage = "无法切换画质，请重试";
                return;
            }
            playbackReportScheduler.Stop(instance);
            await ReportStoppedOnceAsync(instance, state.IsPaused).ConfigureAwait(true);
            if (!CanContinuePlaybackOperation(instance, token)) { return; }

            var prepared = result.PlaybackInfo with { StartPositionTicks = resumeTicks };
            var loadTask = ReloadQualityAsync(prepared, state, host, back, logo);
            operationInstance = currentPlaybackInstanceId;
            var loaded = await loadTask.ConfigureAwait(true);
            if (!CanAcceptPlayerEvent(operationInstance)) { return; }
            if (loaded)
            {
                PlayerOptionsMessage = WithLocalSubtitleRestoreStatus($"已切换至{QualityLabel(quality)}");
                return;
            }

            await StopCurrentPlaybackAsync(operationInstance, "quality-restore").ConfigureAwait(true);
            if (!CanAcceptPlayerEvent(operationInstance)) { return; }
            playbackReportScheduler.Stop(operationInstance);
            await ReportStoppedOnceAsync(operationInstance, state.IsPaused).ConfigureAwait(true);
            if (!CanAcceptPlayerEvent(operationInstance)) { return; }
            // The previous server session has been stopped; negotiate a fresh one for restoration.
            var restoreToken = playbackLifecycleCancellation?.Token ?? CancellationToken.None;
            var restored = await playbackService.PreparePlaybackAsync(session,
                new PlaybackStartRequest(originalInfo.ItemId, originalInfo.Title, resumeTicks,
                    originalInfo.MediaType, originalInfo.ProductionYear, originalInfo.Quality,
                    originalInfo.MediaSource.Id, state.AudioStreamIndex, state.SubtitleStreamIndex), restoreToken).ConfigureAwait(true);
            if (!CanContinuePlaybackOperation(operationInstance, restoreToken)) { return; }
            if (restored.Error == PlaybackLoadError.Unauthorized)
            {
                await HandleExpiredSessionAsync(operationInstance).ConfigureAwait(true);
                return;
            }
            if (restored.IsSuccess && restored.PlaybackInfo is not null)
            {
                var restoreTask = ReloadQualityAsync(restored.PlaybackInfo, state, host, back, logo);
                operationInstance = currentPlaybackInstanceId;
                if (await restoreTask.ConfigureAwait(true) && CanAcceptPlayerEvent(operationInstance))
                {
                    PlayerOptionsMessage = WithLocalSubtitleRestoreStatus("新画质播放失败，已恢复原画质");
                    return;
                }
            }
            if (CanAcceptPlayerEvent(operationInstance))
            {
                ErrorMessage = "画质切换及恢复失败，请重试或返回详情页";
            }
        }
        catch (OperationCanceledException) when (!CanAcceptPlayerEvent(operationInstance)
            || playbackLifecycleCancellation?.IsCancellationRequested == true) { }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Quality switch failed: {exception.GetType().Name}");
            if (CanAcceptPlayerEvent(operationInstance))
            {
                PlayerOptionsMessage = "画质切换失败，请重试";
            }
        }
        finally
        {
            if (IsCurrentPlaybackInstance(operationInstance)) { IsSwitchingQuality = false; }
        }
    }

    private async Task<bool> ReloadQualityAsync(PlaybackInfo info, PlaybackReloadState state, IntPtr host,
        DetailNavigationParameter? back, string? logo)
    {
        LoadCore(new PlayerNavigationParameter(info, back ?? new DetailNavigationParameter(info.ItemId), logo, isQueuedPlayback), playerAlreadyStopped: true, reloadState: state);
        var instance = currentPlaybackInstanceId;
        var token = playbackLifecycleCancellation!.Token;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        qualityLoadCompletion = completion;
        failedPlayerStopTask = Task.CompletedTask;
        try
        {
            await AttachVideoHostAsync(host).ConfigureAwait(true);
            if (!CanContinuePlaybackOperation(instance, token) || HasError)
            {
                await failedPlayerStopTask.ConfigureAwait(true);
                return false;
            }
            var loaded = await completion.Task.WaitAsync(TimeSpan.FromSeconds(40), token).ConfigureAwait(true);
            if (localSubtitleRestoreTask is { } restoreSubtitles) await restoreSubtitles.WaitAsync(token).ConfigureAwait(true);
            await failedPlayerStopTask.ConfigureAwait(true);
            return loaded && CanContinuePlaybackOperation(instance, token) && !HasError && isPlayerLoaded;
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            if (ReferenceEquals(qualityLoadCompletion, completion)) { qualityLoadCompletion = null; }
        }
    }

    private PlaybackReloadState CaptureReloadState() => new(IsPaused, Volume, IsMuted, IsFullscreen,
        subtitleDelaySeconds, subtitleScale, subtitlePosition,
        activeAudioStreamIndex ?? AudioTracks.FirstOrDefault(track => track.IsSelected)?.MediaStreamIndex,
        SubtitleTracks.FirstOrDefault(track => track.IsSelected) is { IsOffOption: true } ? -1
            : activeSubtitleStreamIndex ?? SubtitleTracks.FirstOrDefault(track => track.IsSelected)?.MediaStreamIndex,
        IsAutoPlayCancelledForCurrentItem, selectedPlaybackSpeed,
        SubtitleTracks.FirstOrDefault(track => track.IsSelected)?.LocalFilePath);

    private sealed record PlaybackReloadState(bool IsPaused, int Volume, bool IsMuted, bool IsFullscreen,
        double SubtitleDelay, double SubtitleScale, double SubtitlePosition,
        int? AudioStreamIndex, int? SubtitleStreamIndex, bool AutoPlayCancelled, double PlaybackSpeed,
        string? LocalSubtitlePath = null);
}

public sealed record PlayerQualityOption(PlaybackQuality Quality, string Label, bool IsSelected);
