using System.Windows.Input;
using EmbyPlayer.Core.PlaybackQueue;
using EmbyPlayer.Core.WatchLater;

namespace EmbyPlayer.UI.ViewModels;

public sealed partial class MediaDetailViewModel
{
    private IWatchLaterStore? watchLaterStore;
    private IPlaybackQueueService? playbackQueue;
    private bool isSavedForLater;
    private bool isWatchLaterBusy;
    private bool isWatchLaterKnown;
    private string? personalMediaMessage;

    public ICommand ToggleWatchLaterCommand { get; private set; } = null!;
    public ICommand RefreshWatchLaterCommand { get; private set; } = null!;
    public ICommand AddToQueueCommand { get; private set; } = null!;
    public bool HasPersonalMediaActions => watchLaterStore is not null || playbackQueue is not null;
    public bool IsSavedForLater => isSavedForLater;
    public string WatchLaterButtonText => isSavedForLater ? "移出稍后观看" : "稍后观看";
    public string? PersonalMediaMessage
    {
        get => personalMediaMessage;
        private set { personalMediaMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsWatchLaterReadError)); }
    }
    public bool IsWatchLaterReadError => !isWatchLaterKnown && PersonalMediaMessage is not null;

    private void InitializePersonalMedia(IWatchLaterStore? store, IPlaybackQueueService? queue)
    {
        watchLaterStore = store;
        playbackQueue = queue;
        ToggleWatchLaterCommand = new AsyncRelayCommand(ToggleWatchLaterAsync, CanToggleWatchLater);
        RefreshWatchLaterCommand = new AsyncRelayCommand(RefreshWatchLaterAsync,
            () => watchLaterStore is not null && !isWatchLaterBusy && Detail is not null);
        AddToQueueCommand = new RelayCommand(_ => AddToQueue(), _ => !IsLoading && !IsPreparingPlayback
            && playbackQueue?.Snapshot.HasSession == true && CreateQueueItem() is not null);
    }

    private void ResetPersonalMediaState()
    {
        isWatchLaterKnown = false;
        isWatchLaterBusy = false;
        isSavedForLater = false;
        PersonalMediaMessage = null;
        OnPropertyChanged(nameof(IsSavedForLater));
        OnPropertyChanged(nameof(WatchLaterButtonText));
    }

    private void NotifyPersonalMediaCommands()
    {
        (ToggleWatchLaterCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (RefreshWatchLaterCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (AddToQueueCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task RefreshWatchLaterAsync()
    {
        if (watchLaterStore is null || Detail is not { } item || currentSessionService.CurrentSession is not { } session) return;
        var generation = mediaLoadGeneration;
        var token = mediaLoadCancellation?.Token ?? CancellationToken.None;
        isWatchLaterBusy = true;
        NotifyPersonalMediaCommands();
        try
        {
            var saved = await watchLaterStore.IsSavedAsync(session, item.Id, token).ConfigureAwait(true);
            if (!IsCurrentMediaLoad(generation, token) || !ReferenceEquals(session, currentSessionService.CurrentSession)) return;
            isSavedForLater = saved;
            isWatchLaterKnown = true;
            PersonalMediaMessage = null;
            OnPropertyChanged(nameof(IsSavedForLater));
            OnPropertyChanged(nameof(WatchLaterButtonText));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Watch later read failed: {exception.GetType().Name}");
            if (IsCurrentMediaLoad(generation, token)) PersonalMediaMessage = "稍后观看读取失败，请刷新状态；损坏的列表可在稍后观看页恢复";
        }
        finally
        {
            if (IsCurrentMediaLoad(generation, token)) { isWatchLaterBusy = false; NotifyPersonalMediaCommands(); }
        }
    }

    private bool CanToggleWatchLater() => watchLaterStore is not null && isWatchLaterKnown
        && !isWatchLaterBusy && !IsLoading && Detail is not null;

    private async Task ToggleWatchLaterAsync()
    {
        if (!CanToggleWatchLater() || Detail is not { } item || currentSessionService.CurrentSession is not { } session) return;
        var generation = mediaLoadGeneration;
        var token = mediaLoadCancellation?.Token ?? CancellationToken.None;
        var saved = !isSavedForLater;
        isWatchLaterBusy = true;
        NotifyPersonalMediaCommands();
        try
        {
            if (saved) await watchLaterStore!.AddAsync(session, new(item.Id, item.Title, item.PosterUrl, item.Type, item.Year), token).ConfigureAwait(true);
            else await watchLaterStore!.RemoveAsync(session, item.Id, token).ConfigureAwait(true);
            if (!IsCurrentMediaLoad(generation, token) || !ReferenceEquals(session, currentSessionService.CurrentSession)) return;
            isSavedForLater = saved;
            PersonalMediaMessage = saved ? "已加入稍后观看" : "已移出稍后观看";
            OnPropertyChanged(nameof(IsSavedForLater));
            OnPropertyChanged(nameof(WatchLaterButtonText));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Watch later update failed: {exception.GetType().Name}");
            if (IsCurrentMediaLoad(generation, token)) PersonalMediaMessage = "稍后观看保存失败，请重试";
        }
        finally
        {
            if (IsCurrentMediaLoad(generation, token)) { isWatchLaterBusy = false; NotifyPersonalMediaCommands(); }
        }
    }

    private PlaybackQueueItem? CreateQueueItem()
    {
        if (Detail is not { } item) return null;
        if (IsSeriesDetail)
        {
            var episode = SelectedEpisode ?? GetSeriesPlaybackTarget()?.Episode;
            return episode is null ? null : new(episode.Id, $"{item.Title} · {episode.Title}", "Episode", item.Year, item.PosterUrl);
        }
        return item.Type is "Movie" or "Episode" ? new(item.Id, item.Title, item.Type, item.Year, item.PosterUrl) : null;
    }

    private void AddToQueue()
    {
        if (!AddToQueueCommand.CanExecute(null) || CreateQueueItem() is not { } item) return;
        PersonalMediaMessage = playbackQueue!.Add(item) ? $"已加入待播：{item.Title}" : "这个作品已在播放队列中";
    }
}
