using System.Diagnostics;
using System.Windows.Input;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.PlaybackQueue;
using EmbyPlayer.Core.WatchLater;
using EmbyPlayer.UI.Navigation;

namespace EmbyPlayer.UI.ViewModels;

public sealed partial class HomeViewModel
{
    private IWatchLaterStore? watchLaterStore;
    private IPlaybackQueueService? playbackQueue;
    private AuthSession? previewSession;
    private CancellationTokenSource? previewCancellation;
    private long previewGeneration;

    public IReadOnlyList<WatchLaterItem> WatchLaterPreview { get; private set; } = Array.Empty<WatchLaterItem>();
    public string? WatchLaterPreviewError { get; private set; }
    public bool HasWatchLaterPreviewError => WatchLaterPreviewError is not null;
    public bool HasWatchLaterSection => WatchLaterPreview.Count > 0 || HasWatchLaterPreviewError;
    public int PendingQueueCount => playbackQueue?.Snapshot.Pending.Count ?? 0;
    public bool HasPendingQueue => currentSessionService.CurrentSession is not null && PendingQueueCount > 0;
    public string PendingQueueText => $"待播 {PendingQueueCount} 项";
    public ICommand OpenWatchLaterItemCommand { get; private set; } = null!;
    public ICommand RetryWatchLaterPreviewCommand { get; private set; } = null!;

    private void InitializePersonalMedia(IWatchLaterStore? store, IPlaybackQueueService? queue)
    {
        watchLaterStore = store;
        playbackQueue = queue;
        OpenWatchLaterItemCommand = new RelayCommand(parameter =>
        {
            if (parameter is WatchLaterItem item && previewSession is not null
                && ReferenceEquals(previewSession, currentSessionService.CurrentSession)
                && WatchLaterPreview.Contains(item))
                navigationService.NavigateTo(AppPage.Detail, new DetailNavigationParameter(item.ItemId, AppPage.Home));
        });
        RetryWatchLaterPreviewCommand = new AsyncRelayCommand(LoadWatchLaterPreviewAsync);
        var context = SynchronizationContext.Current;
        if (queue is not null)
            queue.Changed += (_, _) =>
            {
                if (context is null || ReferenceEquals(context, SynchronizationContext.Current)) NotifyQueue();
                else context.Post(_ => NotifyQueue(), null);
            };
    }

    private void NotifyQueue()
    {
        OnPropertyChanged(nameof(PendingQueueCount));
        OnPropertyChanged(nameof(HasPendingQueue));
        OnPropertyChanged(nameof(PendingQueueText));
    }

    public void ResetPersonalMedia()
    {
        CancelWatchLaterPreviewLoad();
        previewSession = null;
        WatchLaterPreview = Array.Empty<WatchLaterItem>();
        WatchLaterPreviewError = null;
        NotifyWatchLaterPreview();
        NotifyQueue();
    }

    private void CancelWatchLaterPreviewLoad()
    {
        previewGeneration++;
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        previewCancellation = null;
    }

    private async Task LoadWatchLaterPreviewAsync()
    {
        CancelWatchLaterPreviewLoad();
        var session = currentSessionService.CurrentSession;
        if (!ReferenceEquals(previewSession, session))
        {
            WatchLaterPreview = Array.Empty<WatchLaterItem>();
            WatchLaterPreviewError = null;
            previewSession = session;
            NotifyWatchLaterPreview();
        }
        NotifyQueue();
        if (session is null || watchLaterStore is null) return;
        previewCancellation = new CancellationTokenSource();
        var token = previewCancellation.Token;
        var generation = previewGeneration;
        bool IsCurrent() => generation == previewGeneration
            && ReferenceEquals(session, currentSessionService.CurrentSession);
        try
        {
            var items = await watchLaterStore.LoadAsync(session, token);
            if (!IsCurrent()) return;
            WatchLaterPreview = items.Take(12).ToArray();
            WatchLaterPreviewError = null;
            NotifyWatchLaterPreview();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!IsCurrent()) return;
            Trace.TraceWarning($"Home watch-later preview could not be loaded: {exception}");
            WatchLaterPreviewError = "暂时无法读取稍后观看，请重试或进入查看全部。";
            NotifyWatchLaterPreview();
        }
        finally
        {
            if (generation == previewGeneration)
            {
                previewCancellation?.Dispose();
                previewCancellation = null;
            }
        }
    }

    private void NotifyWatchLaterPreview()
    {
        OnPropertyChanged(nameof(WatchLaterPreview));
        OnPropertyChanged(nameof(WatchLaterPreviewError));
        OnPropertyChanged(nameof(HasWatchLaterPreviewError));
        OnPropertyChanged(nameof(HasWatchLaterSection));
    }
}
