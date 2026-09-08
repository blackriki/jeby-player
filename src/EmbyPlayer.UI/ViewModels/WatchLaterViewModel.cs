using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.WatchLater;
using EmbyPlayer.UI.Navigation;

namespace EmbyPlayer.UI.ViewModels;

public sealed class WatchLaterViewModel : ViewModelBase
{
    private readonly INavigationService navigationService;
    private readonly IWatchLaterStore store;
    private readonly ICurrentSessionService currentSessionService;
    private readonly Action<string> showLoginError;
    private AuthSession? loadedSession;
    private CancellationTokenSource? cancellation;
    private long generation;
    private bool hasLoaded;
    private Operation retryOperation;
    private WatchLaterItem? retryItem;

    public WatchLaterViewModel(INavigationService navigationService, IWatchLaterStore store,
        ICurrentSessionService currentSessionService, Action<string> showLoginError)
    {
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.currentSessionService = currentSessionService ?? throw new ArgumentNullException(nameof(currentSessionService));
        this.showLoginError = showLoginError ?? throw new ArgumentNullException(nameof(showLoginError));
        BackCommand = new RelayCommand(_ => navigationService.NavigateTo(AppPage.Home));
        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading);
        RetryCommand = new AsyncRelayCommand(() => RunAsync(retryOperation, retryItem), () => !IsLoading);
        RemoveCommand = new AsyncRelayCommand(parameter => RunAsync(Operation.Remove, (WatchLaterItem)parameter!),
            parameter => !IsLoading && parameter is WatchLaterItem);
        OpenMediaCommand = new RelayCommand(parameter =>
        {
            if (parameter is WatchLaterItem item && ReferenceEquals(loadedSession, currentSessionService.CurrentSession)
                && loadedSession is not null)
                navigationService.NavigateTo(AppPage.Detail, new DetailNavigationParameter(item.ItemId, AppPage.WatchLater));
        });
        ResetCorruptedCommand = new RelayCommand(_ =>
        {
            if (!IsCorrupted || IsLoading) return;
            IsResetConfirmationVisible = true;
            NotifyState();
        }, _ => IsCorrupted && !IsLoading);
        ConfirmResetCommand = new AsyncRelayCommand(() => RunAsync(Operation.Reset, null),
            () => IsResetConfirmationVisible && !IsLoading);
        CancelResetCommand = new RelayCommand(_ => { IsResetConfirmationVisible = false; NotifyState(); });
    }

    public IReadOnlyList<WatchLaterItem> Items { get; private set; } = Array.Empty<WatchLaterItem>();
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool IsCorrupted { get; private set; }
    public bool IsResetConfirmationVisible { get; private set; }
    public bool IsInitialLoading => IsLoading && !hasLoaded;
    public bool IsRefreshing => IsLoading && hasLoaded;
    public bool IsEmpty => hasLoaded && Items.Count == 0 && !IsLoading && ErrorMessage is null;
    public bool IsErrorVisible => !IsLoading && ErrorMessage is not null;
    public double ScrollOffset { get; set; }
    public ICommand BackCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand OpenMediaCommand { get; }
    public ICommand ResetCorruptedCommand { get; }
    public ICommand ConfirmResetCommand { get; }
    public ICommand CancelResetCommand { get; }

    public Task LoadAsync() => RunAsync(Operation.Load, null);

    private async Task RunAsync(Operation operation, WatchLaterItem? item)
    {
        var session = currentSessionService.CurrentSession;
        if (session is null)
        {
            ResetSession();
            navigationService.NavigateTo(AppPage.Login);
            showLoginError("请先登录");
            return;
        }
        Deactivate();
        if (!ReferenceEquals(loadedSession, session))
        {
            ClearContent();
            loadedSession = session;
            // A stale command from the previous account must never mutate this account's list.
            operation = Operation.Load;
            item = null;
        }
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var requestGeneration = generation;
        retryOperation = operation;
        retryItem = item;
        IsLoading = true;
        ErrorMessage = null;
        IsCorrupted = false;
        NotifyState();
        try
        {
            if (operation == Operation.Remove && item is not null)
            {
                await store.RemoveAsync(session, item.ItemId, token).ConfigureAwait(true);
                if (!IsCurrent(session, requestGeneration, token)) return;
                Items = Items.Where(value => !value.ItemId.Equals(item.ItemId, StringComparison.OrdinalIgnoreCase)).ToArray();
            }
            else
            {
                if (operation == Operation.Reset)
                {
                    await store.ResetCorruptedAsync(session, token).ConfigureAwait(true);
                    if (!IsCurrent(session, requestGeneration, token)) return;
                }
                var items = await store.LoadAsync(session, token).ConfigureAwait(true);
                if (!IsCurrent(session, requestGeneration, token)) return;
                Items = items;
                hasLoaded = true;
            }
            retryOperation = Operation.Load;
            retryItem = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Trace.TraceError("Watch-later operation failed: {0}", exception.GetType().Name);
            if (IsCurrent(session, requestGeneration, token))
            {
                IsCorrupted = exception is InvalidDataException;
                ErrorMessage = IsCorrupted ? "稍后观看列表已损坏。可以重试读取，或重置当前账号的列表。"
                    : operation == Operation.Remove ? "移除失败，列表尚未更新。请重试。"
                    : "无法读取或保存稍后观看列表，请检查本地存储后重试。";
            }
        }
        finally
        {
            if (IsCurrent(session, requestGeneration, token))
            {
                IsLoading = false;
                NotifyState();
            }
        }
    }

    public void Deactivate()
    {
        generation++;
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        IsLoading = false;
        IsResetConfirmationVisible = false;
        NotifyState();
    }

    public void ResetSession()
    {
        Deactivate();
        loadedSession = null;
        ClearContent();
        NotifyState();
    }

    private void ClearContent()
    {
        Items = Array.Empty<WatchLaterItem>();
        hasLoaded = false;
        ErrorMessage = null;
        IsCorrupted = false;
        retryOperation = Operation.Load;
        retryItem = null;
        ScrollOffset = 0;
        OnPropertyChanged(nameof(ScrollOffset));
    }

    private bool IsCurrent(AuthSession session, long requestGeneration, CancellationToken token)
        => generation == requestGeneration && !token.IsCancellationRequested && ReferenceEquals(session, loadedSession)
            && ReferenceEquals(session, currentSessionService.CurrentSession);

    private void NotifyState()
    {
        foreach (var name in new[] { nameof(Items), nameof(IsLoading), nameof(ErrorMessage), nameof(IsCorrupted),
            nameof(IsResetConfirmationVisible), nameof(IsInitialLoading), nameof(IsRefreshing), nameof(IsEmpty), nameof(IsErrorVisible) })
            OnPropertyChanged(name);
        ((AsyncRelayCommand)RefreshCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)RetryCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)RemoveCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)ConfirmResetCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ResetCorruptedCommand).RaiseCanExecuteChanged();
    }

    private enum Operation { Load, Remove, Reset }
}
