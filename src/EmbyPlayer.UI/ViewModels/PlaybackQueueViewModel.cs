using System.Windows.Input;
using System.Windows.Threading;
using EmbyPlayer.Core.PlaybackQueue;

namespace EmbyPlayer.UI.ViewModels;

public sealed class PlaybackQueueViewModel : ViewModelBase, IDisposable
{
    private readonly IPlaybackQueueService queue;
    private readonly Func<PlaybackQueueItem, CancellationToken, Task<string?>> playItemAsync;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private PlaybackQueueSnapshot snapshot;
    private CancellationTokenSource? playCancellation;
    private long playOperation;
    private bool transitionActive;
    private bool disposed;

    public PlaybackQueueViewModel(IPlaybackQueueService queue,
        Func<PlaybackQueueItem, CancellationToken, Task<string?>> playItemAsync, Action? goBack = null)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.playItemAsync = playItemAsync ?? throw new ArgumentNullException(nameof(playItemAsync));
        snapshot = queue.Snapshot;
        PlayCommand = new AsyncRelayCommand(value => PlayAsync(GetItem(value)), CanPlay);
        RemoveCommand = new RelayCommand(value => Remove(value as PlaybackQueueRowViewModel), CanEditRow);
        MoveUpCommand = new RelayCommand(value => MoveRelative(value as PlaybackQueueRowViewModel, -1),
            value => CanEditRow(value) && GetPendingIndex(((PlaybackQueueRowViewModel)value!).Item.ItemId) > 0);
        MoveDownCommand = new RelayCommand(value => MoveRelative(value as PlaybackQueueRowViewModel, 1),
            value => CanEditRow(value) && GetPendingIndex(((PlaybackQueueRowViewModel)value!).Item.ItemId) < snapshot.Pending.Count - 1);
        ClearPendingCommand = new RelayCommand(_ => { if (CanEdit && HasPending) queue.ClearPending(); }, _ => CanEdit && HasPending);
        BackCommand = new RelayCommand(_ => { Deactivate(); goBack?.Invoke(); }, _ => goBack is not null);
        queue.Changed += OnQueueChanged;
        ApplySnapshot();
    }

    public PlaybackQueueItem? CurrentItem => snapshot.Current;
    public IReadOnlyList<PlaybackQueueRowViewModel> PendingItems { get; private set; } = Array.Empty<PlaybackQueueRowViewModel>();
    public IReadOnlyList<int> PositionOptions { get; private set; } = Array.Empty<int>();
    public bool HasCurrent => CurrentItem is not null;
    public bool HasPending => PendingItems.Count > 0;
    public bool IsEmpty => !HasCurrent && !HasPending;
    public bool IsPendingEmpty => !HasPending && HasCurrent;
    public bool HasSession => snapshot.HasSession;
    public bool IsBusy { get; private set; }
    public bool CanEdit => HasSession && !IsBusy && !transitionActive && !disposed
        && snapshot.SessionVersion == queue.Snapshot.SessionVersion;
    public string? ErrorMessage { get; private set; }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string SummaryText => HasPending ? $"接下来播放 {PendingItems.Count} 项" : "暂无待播项目";
    public string EmptyMessage => HasSession ? "从影片或单集详情加入队列，在这里安排播放顺序。" : "登录后即可使用播放队列。";
    public bool AutoPlayEnabled
    {
        get => snapshot.AutoPlayEnabled;
        set { if (CanEdit) queue.SetAutoPlayEnabled(value); }
    }
    public ICommand PlayCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand ClearPendingCommand { get; }
    public ICommand BackCommand { get; }

    public async Task PlayAsync(PlaybackQueueItem? item)
    {
        if (!CanPlay(item)) return;
        var version = snapshot.SessionVersion;
        var operation = ++playOperation;
        var cancellation = new CancellationTokenSource();
        playCancellation = cancellation;
        IsBusy = true;
        ErrorMessage = null;
        NotifyState();
        try
        {
            var error = await playItemAsync(item!, cancellation.Token).ConfigureAwait(true);
            if (!IsCurrentOperation(operation, version, cancellation.Token)) return;
            ErrorMessage = error;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Queue playback failed: {exception.GetType().Name}");
            if (IsCurrentOperation(operation, version, cancellation.Token)) ErrorMessage = "暂时无法播放此项目，请重试";
        }
        finally
        {
            if (operation == playOperation && !disposed)
            {
                IsBusy = false;
                playCancellation = null;
                NotifyState();
            }
            cancellation.Dispose();
        }
    }

    public void MoveTo(PlaybackQueueRowViewModel? row, int destinationIndex)
    {
        if (CanEditRow(row)) queue.Move(row!.Item.ItemId, destinationIndex);
    }

    public void SetTransitionActive(bool active)
    {
        if (transitionActive == active) return;
        transitionActive = active;
        NotifyState();
    }

    public void Deactivate()
    {
        ++playOperation;
        playCancellation?.Cancel();
        playCancellation = null;
        IsBusy = false;
        ErrorMessage = null;
        NotifyState();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        queue.Changed -= OnQueueChanged;
        Deactivate();
    }

    private void OnQueueChanged(object? sender, EventArgs args)
    {
        if (disposed || dispatcher.HasShutdownStarted) return;
        if (dispatcher.CheckAccess()) ApplySnapshot();
        else dispatcher.BeginInvoke(new Action(() => { if (!disposed) ApplySnapshot(); }));
    }

    private void ApplySnapshot()
    {
        var next = queue.Snapshot;
        if (snapshot.SessionVersion != next.SessionVersion) Deactivate();
        snapshot = next;
        PendingItems = snapshot.Pending.Select((item, index) =>
            new PlaybackQueueRowViewModel(item, index + 1, snapshot.SessionVersion)).ToArray();
        PositionOptions = Enumerable.Range(1, PendingItems.Count).ToArray();
        OnPropertyChanged(nameof(CurrentItem));
        OnPropertyChanged(nameof(PendingItems));
        OnPropertyChanged(nameof(PositionOptions));
        NotifyState();
    }

    private void Remove(PlaybackQueueRowViewModel? row)
    {
        if (CanEditRow(row)) queue.Remove(row!.Item.ItemId);
    }

    private void MoveRelative(PlaybackQueueRowViewModel? row, int delta)
    {
        if (CanEditRow(row)) queue.Move(row!.Item.ItemId, GetPendingIndex(row.Item.ItemId) + delta);
    }

    private int GetPendingIndex(string itemId)
    {
        for (var index = 0; index < snapshot.Pending.Count; index++)
            if (snapshot.Pending[index].ItemId == itemId) return index;
        return -1;
    }

    private bool CanEditRow(object? value) => CanEdit && value is PlaybackQueueRowViewModel row
        && row.SessionVersion == snapshot.SessionVersion && GetPendingIndex(row.Item.ItemId) >= 0;

    private bool CanPlay(object? value)
    {
        var item = GetItem(value);
        return CanEdit && item is not null
            && (value is not PlaybackQueueRowViewModel row || row.SessionVersion == snapshot.SessionVersion)
            && (snapshot.Current?.ItemId == item.ItemId || GetPendingIndex(item.ItemId) >= 0);
    }

    private static PlaybackQueueItem? GetItem(object? value) => value switch
    {
        PlaybackQueueItem item => item,
        PlaybackQueueRowViewModel row => row.Item,
        _ => null
    };

    private bool IsCurrentOperation(long operation, long version, CancellationToken token)
        => !disposed && !token.IsCancellationRequested && operation == playOperation
            && queue.Snapshot.SessionVersion == version;

    private void NotifyState()
    {
        foreach (var property in new[] { nameof(IsBusy), nameof(CanEdit), nameof(HasCurrent), nameof(HasPending),
            nameof(IsEmpty), nameof(IsPendingEmpty), nameof(HasSession), nameof(AutoPlayEnabled), nameof(SummaryText),
            nameof(EmptyMessage), nameof(ErrorMessage), nameof(HasError) }) OnPropertyChanged(property);
        (PlayCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        foreach (var command in new[] { RemoveCommand, MoveUpCommand, MoveDownCommand, ClearPendingCommand })
            (command as RelayCommand)?.RaiseCanExecuteChanged();
    }
}

public sealed record PlaybackQueueRowViewModel(PlaybackQueueItem Item, int Position, long SessionVersion)
{
    public string Title => Item.Title;
    public string? ImageUrl => Item.ImageUrl;
    public string MetadataText => (Item.MediaType == "Episode" ? "单集" : "电影")
        + (Item.ProductionYear is > 0 ? $" · {Item.ProductionYear}" : string.Empty);
}
