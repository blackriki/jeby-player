using System.Windows.Input;
using EmbyPlayer.Core.Playback;
using EmbyPlayer.Core.PlaybackQueue;
using EmbyPlayer.UI.Navigation;

namespace EmbyPlayer.UI.ViewModels;

public sealed partial class PlayerViewModel
{
    private IPlaybackQueueService? playbackQueue;
    private long queueLoadSessionVersion;
    private bool isAdvancingQueue;
    private TaskCompletionSource<bool>? queueLoadCompletion;
    private long cancelledQueueInstanceId;
    private bool isQueuedPlayback;
    public PlaybackQueueViewModel? QueueViewModel { get; private set; }
    public bool HasPlaybackQueue => playbackQueue is not null;
    public int PendingQueueCount => playbackQueue?.Snapshot.Pending.Count ?? 0;
    public string QueueButtonText => PendingQueueCount > 0 ? $"队列 · {PendingQueueCount}" : "队列";
    public string QueueButtonToolTip => PendingQueueCount > 0
        ? $"{PendingQueueCount} 项待播 · 查看和调整播放顺序" : "播放队列 · 从详情页加入待播项目";
    public ICommand PlayNextQueuedCommand { get; private set; } = null!;
    public bool IsAdvancingQueue
    {
        get => isAdvancingQueue;
        private set
        {
            isAdvancingQueue = value;
            QueueViewModel?.SetTransitionActive(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSeek));
            (PlayNextQueuedCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            NotifyEnhancementCommandStates();
        }
    }

    private void InitializePlaybackQueue(IPlaybackQueueService? queue)
    {
        playbackQueue = queue;
        PlayNextQueuedCommand = new AsyncRelayCommand(AdvanceQueueAsync, () => CanAdvanceQueue());
        if (queue is null) return;
        QueueViewModel = new PlaybackQueueViewModel(queue, PlayQueueItemAsync, () => navigationService.NavigateTo(AppPage.Home));
        queue.Changed += (_, _) =>
        {
            void Notify()
            {
                OnPropertyChanged(nameof(PendingQueueCount));
                OnPropertyChanged(nameof(QueueButtonText));
                OnPropertyChanged(nameof(QueueButtonToolTip));
                (PlayNextQueuedCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            }
            if (synchronizationContext is null) Notify();
            else synchronizationContext.Post(_ => Notify(), null);
        };
    }

    private bool CanAdvanceQueue() => acceptsPlayerEvents && !IsAdvancingQueue && !IsSwitchingQuality && !IsPreparingNextEpisode
        && playbackQueue?.Snapshot is { HasSession: true, Pending.Count: > 0 };

    private async Task AdvanceQueueAsync()
    {
        if (!CanAdvanceQueue() || playbackQueue is null || !playbackQueue.TryGetNext(out var item) || item is null) return;
        var error = await PlayQueueItemAsync(item, CancellationToken.None).ConfigureAwait(true);
        if (error is not null && acceptsPlayerEvents) PlayerOptionsMessage = error;
    }

    private void CommitQueueCurrent()
    {
        if (playbackQueue is null || PlaybackInfo is not { } info || string.IsNullOrWhiteSpace(info.MediaType)) return;
        var existing = playbackQueue.Snapshot.Pending.FirstOrDefault(item => item.ItemId == info.ItemId)
            ?? (playbackQueue.Snapshot.Current?.ItemId == info.ItemId ? playbackQueue.Snapshot.Current : null);
        playbackQueue.SetCurrent(existing ?? new(info.ItemId, info.Title, info.MediaType, info.ProductionYear), queueLoadSessionVersion);
    }

    public async Task<string?> PlayQueueItemAsync(PlaybackQueueItem item, CancellationToken cancellationToken)
    {
        if (playbackQueue is null || playbackService is null || currentSessionService?.CurrentSession is not { } session)
            return "请先登录后播放队列";
        if (IsAdvancingQueue || IsSwitchingQuality || IsPreparingNextEpisode) return "正在切换播放，请稍候";
        var version = playbackQueue.Snapshot.SessionVersion;
        var fromPlayer = navigationService.CurrentPage == AppPage.Player && acceptsPlayerEvents;
        if (fromPlayer && (IsLoading || IsSeeking || isSeekDragging || Volatile.Read(ref trackOperationsInFlight) != 0 || isAdjustingSubtitles))
            return "正在调整播放，请稍候再切换待播项";
        using var preparingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            fromPlayer ? playbackLifecycleCancellation?.Token ?? CancellationToken.None : CancellationToken.None);
        var prepareToken = preparingCancellation.Token;
        var instance = currentPlaybackInstanceId;
        var operationInstance = instance;
        var loadedSuccessfully = false;
        var sourcePage = navigationService.CurrentPage;
        IsAdvancingQueue = true;
        try
        {
            var prepared = await playbackService.PreparePlaybackAsync(session,
                new(item.ItemId, item.Title, 0, item.MediaType, item.ProductionYear), prepareToken).ConfigureAwait(true);
            if (prepareToken.IsCancellationRequested || playbackQueue.Snapshot.SessionVersion != version
                || !ReferenceEquals(session, currentSessionService.CurrentSession)
                || (fromPlayer ? !acceptsPlayerEvents || !IsCurrentPlaybackInstance(instance) : navigationService.CurrentPage != sourcePage)) return null;
            if (prepared.Error == PlaybackLoadError.Unauthorized)
            {
                if (fromPlayer) await HandleExpiredSessionAsync(instance).ConfigureAwait(true);
                else
                {
                    if (authSessionStore is not null)
                        await ExpiredSessionRecovery.ClearAsync(authSessionStore, currentSessionService, "playback queue", session, prepareToken).ConfigureAwait(true);
                    if (prepareToken.IsCancellationRequested || playbackQueue.Snapshot.SessionVersion != version
                        || navigationService.CurrentPage != sourcePage
                        || (currentSessionService.CurrentSession is not null && !ReferenceEquals(session, currentSessionService.CurrentSession))) return null;
                    if (ReferenceEquals(session, currentSessionService.CurrentSession)) currentSessionService.ClearSession();
                    playbackQueue.SetSession(null, null);
                    showLoginError?.Invoke("登录状态已失效，请重新登录");
                    navigationService.NavigateTo(AppPage.Login);
                }
                return null;
            }
            if (!prepared.IsSuccess || prepared.PlaybackInfo is null)
                return prepared.Error == PlaybackLoadError.Forbidden ? "没有权限播放此待播项" : "待播项准备失败，请重试或手动移除";
            var info = prepared.PlaybackInfo;
            var back = new DetailNavigationParameter(item.ItemId, AppPage.PlaybackQueue);
            if (!fromPlayer)
            {
                navigationService.NavigateTo(AppPage.Player, new PlayerNavigationParameter(info, back, IsQueuedPlayback: true));
                operationInstance = currentPlaybackInstanceId;
                return null;
            }
            var host = videoHostHandle;
            var state = new PlaybackReloadState(false, Volume, IsMuted, IsFullscreen, 0, 1, 100, null, null, false, 1);
            var stopped = await StopCurrentPlaybackAsync(instance, "queue-next").ConfigureAwait(true);
            if (prepareToken.IsCancellationRequested || !acceptsPlayerEvents || !IsCurrentPlaybackInstance(instance)
                || playbackQueue.Snapshot.SessionVersion != version) return null;
            if (!stopped.IsSuccess) return "无法停止当前播放，请重试";
            playbackReportScheduler.Stop(instance);
            await ReportStoppedOnceAsync(instance, IsPaused).ConfigureAwait(true);
            if (prepareToken.IsCancellationRequested || !acceptsPlayerEvents || !IsCurrentPlaybackInstance(instance)
                || playbackQueue.Snapshot.SessionVersion != version) return null;
            LoadCore(new(info, back), playerAlreadyStopped: true, reloadState: state, queueTransition: true);
            operationInstance = currentPlaybackInstanceId;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            queueLoadCompletion = completion;
            failedPlayerStopTask = Task.CompletedTask;
            using var loadingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                playbackLifecycleCancellation?.Token ?? CancellationToken.None);
            await AttachVideoHostAsync(host).ConfigureAwait(true);
            if (!CanAcceptPlayerEvent(operationInstance)) return null;
            var ready = !HasError && await completion.Task.WaitAsync(TimeSpan.FromSeconds(40), loadingCancellation.Token).ConfigureAwait(true);
            await failedPlayerStopTask.ConfigureAwait(true);
            if (ready && !HasError && isPlayerLoaded)
            {
                loadedSuccessfully = true;
                return null;
            }
            if (CanAcceptPlayerEvent(operationInstance))
            {
                playbackReportScheduler.Stop(operationInstance);
                await ReportStoppedOnceAsync(operationInstance, false).ConfigureAwait(true);
            }
            return "待播项播放失败，可在队列中重试";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || !CanAcceptPlayerEvent(operationInstance)
            || preparingCancellation.IsCancellationRequested)
        {
            if (fromPlayer && operationInstance != instance && CanAcceptPlayerEvent(operationInstance))
            {
                cancelledQueueInstanceId = operationInstance;
                IsLoading = false;
                IsPlaying = false;
                IsPaused = false;
                ErrorMessage = "已取消待播加载，可在队列中重新播放";
                await StopFailedQueuePlaybackAsync(operationInstance).ConfigureAwait(true);
            }
            return null;
        }
        catch (TimeoutException)
        {
            if (CanAcceptPlayerEvent(operationInstance))
            {
                cancelledQueueInstanceId = operationInstance;
                IsLoading = false;
                ErrorMessage = "待播项加载超时，可在队列中重试";
                await StopFailedPlaybackAsync(operationInstance).ConfigureAwait(true);
                await ReportStoppedOnceAsync(operationInstance, false).ConfigureAwait(true);
            }
            return "待播项加载超时，可在队列中重试";
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Queue playback preparation: {exception.GetType().Name}");
            return "待播项播放失败，请重试";
        }
        finally
        {
            if (IsCurrentPlaybackInstance(operationInstance) || (!fromPlayer && currentPlaybackInstanceId == instance))
            {
                queueLoadCompletion = null;
                IsAdvancingQueue = false;
                if (loadedSuccessfully && operationInstance != instance && isCompleted && !HasError
                    && playbackQueue.Snapshot is { AutoPlayEnabled: true, Pending.Count: > 0 })
                {
                    // A short item can reach EOF before its ready continuation finishes.
                    await Task.Yield();
                    if (IsCurrentPlaybackInstance(operationInstance) && isCompleted) await AdvanceQueueAsync().ConfigureAwait(true);
                }
            }
        }
    }

    private async Task StopFailedQueuePlaybackAsync(long instance)
    {
        await StopFailedPlaybackAsync(instance).ConfigureAwait(true);
        if (!IsCurrentPlaybackInstance(instance)) return;
        playbackReportScheduler.Stop(instance);
        await ReportStoppedOnceAsync(instance, false).ConfigureAwait(true);
    }
}
