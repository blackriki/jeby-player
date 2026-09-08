using System.Windows.Input;
using EmbyPlayer.Core.Playback;

namespace EmbyPlayer.UI.ViewModels;

public sealed partial class PlayerViewModel
{
    private RecoverySnapshot? recoverySnapshot;
    private bool isRetryingPlayback;

    public ICommand RetryPlaybackCommand { get; private set; } = null!;
    public bool IsRetryingPlayback => isRetryingPlayback;
    public bool CanRetryPlayback => HasError && !isRetryingPlayback && !IsLoading
        && !IsSwitchingQuality && !IsAdvancingQueue && CanAcceptPlayerEvent(currentPlaybackInstanceId)
        && PlaybackInfo is not null && videoHostHandle != IntPtr.Zero
        && playbackService is not null && currentSessionService?.CurrentSession is not null;
    public string RecoveryStatusText => IsRetryingPlayback ? "正在重新连接并恢复播放…"
        : "重试将从中断位置继续，保留播放设置";

    private void InitializeRecoveryCommands() => RetryPlaybackCommand =
        new AsyncRelayCommand(RetryPlaybackAsync, () => CanRetryPlayback);

    private void NotifyRecoveryCommandState()
    {
        (RetryPlaybackCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRetryPlayback));
        OnPropertyChanged(nameof(IsRetryingPlayback));
        OnPropertyChanged(nameof(RecoveryStatusText));
    }

    private void CaptureRecoverySnapshot()
    {
        recoverySnapshot ??= new RecoverySnapshot(
            Math.Max(0, currentPosition?.Ticks ?? PlaybackInfo?.StartPositionTicks ?? 0),
            !isInitialPlayerConfigurationComplete && pendingReloadState is not null
                ? pendingReloadState : CaptureReloadState());
    }

    public async Task RetryPlaybackAsync()
    {
        if (!CanRetryPlayback || PlaybackInfo is not { } originalInfo) { return; }
        CaptureRecoverySnapshot();
        var snapshot = recoverySnapshot!;
        var instance = currentPlaybackInstanceId;
        var token = playbackLifecycleCancellation?.Token ?? CancellationToken.None;
        var session = currentSessionService!.CurrentSession!;
        isRetryingPlayback = true;
        NotifyRecoveryCommandState();
        ShowControlsOverlay();
        try
        {
            // Wait for native teardown before negotiating a replacement; never loop automatically.
            await failedPlayerStopTask.ConfigureAwait(true);
            if (!CanContinuePlaybackOperation(instance, token)) { return; }
            var result = await playbackService!.PreparePlaybackAsync(session,
                new PlaybackStartRequest(originalInfo.ItemId, originalInfo.Title, snapshot.PositionTicks,
                    originalInfo.MediaType, originalInfo.ProductionYear, originalInfo.Quality,
                    originalInfo.MediaSource.Id, snapshot.State.AudioStreamIndex,
                    snapshot.State.SubtitleStreamIndex), token).ConfigureAwait(true);
            if (!CanContinuePlaybackOperation(instance, token)) { return; }
            if (result.Error == PlaybackLoadError.Unauthorized)
            {
                await HandleExpiredSessionAsync(instance).ConfigureAwait(true);
                return;
            }
            if (!result.IsSuccess || result.PlaybackInfo is null)
            {
                if (result.Error == PlaybackLoadError.Cancelled) { return; }
                ErrorMessage = result.Error switch
                {
                    PlaybackLoadError.Forbidden => "服务器不允许播放此媒体，请检查播放权限或返回",
                    PlaybackLoadError.NotFound => "此媒体已不可用，请返回选择其他作品",
                    PlaybackLoadError.NoPlayableMediaSource => "此媒体暂无可播放版本，请返回选择其他作品",
                    _ => "暂时无法连接播放，请检查网络和服务器后重试"
                };
                return;
            }
            var stop = await StopCurrentPlaybackAsync(instance, "playback-retry").ConfigureAwait(true);
            if (!CanContinuePlaybackOperation(instance, token)) { return; }
            if (!stop.IsSuccess)
            {
                ErrorMessage = "播放器尚未停止，请稍后重试";
                return;
            }
            playbackReportScheduler.Stop(instance);
            await ReportStoppedOnceAsync(instance, snapshot.State.IsPaused).ConfigureAwait(true);
            if (!CanContinuePlaybackOperation(instance, token)) { return; }
            var load = ReloadQualityAsync(result.PlaybackInfo with { StartPositionTicks = snapshot.PositionTicks },
                snapshot.State, videoHostHandle, backToDetailParameter, LogoUrl);
            instance = currentPlaybackInstanceId;
            var loaded = await load.ConfigureAwait(true);
            if (!CanAcceptPlayerEvent(instance)) { return; }
            if (loaded)
            {
                PlayerOptionsMessage = WithLocalSubtitleRestoreStatus("已从中断位置恢复播放");
            }
            else
            {
                recoverySnapshot = snapshot;
                ErrorMessage = "仍无法播放，请检查网络和服务器后重试";
                IsLoading = false;
                await StopFailedPlaybackAsync(instance).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (!CanAcceptPlayerEvent(instance)
            || playbackLifecycleCancellation?.IsCancellationRequested != false) { }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"Playback retry failed: {exception.GetType().Name}");
            if (CanAcceptPlayerEvent(instance))
            {
                recoverySnapshot = snapshot;
                IsLoading = false;
                ErrorMessage = "恢复播放失败，请稍后重试";
            }
        }
        finally
        {
            isRetryingPlayback = false;
            NotifyRecoveryCommandState();
        }
    }

    private sealed record RecoverySnapshot(long PositionTicks, PlaybackReloadState State);
}
