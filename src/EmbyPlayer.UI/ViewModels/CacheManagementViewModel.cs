using System.Diagnostics;
using System.Windows.Input;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Caching;

namespace EmbyPlayer.UI.ViewModels;

public sealed class CacheManagementViewModel : ViewModelBase
{
    private readonly ICacheManagementService? service;
    private readonly ICurrentSessionService sessions;
    private readonly IAuthSessionStore authStore;
    private readonly Action onSessionExpired;
    private CancellationTokenSource? cancellation;
    private int generation;
    private CacheAction pendingAction;
    private CacheAction lastAction;
    private CacheUsage? usage;
    private bool isBusy;
    private string? errorMessage;
    private string? statusMessage;

    public CacheManagementViewModel(ICacheManagementService? service, ICurrentSessionService sessions,
        IAuthSessionStore authStore, Action onSessionExpired)
    {
        this.service = service;
        this.sessions = sessions;
        this.authStore = authStore;
        this.onSessionExpired = onSessionExpired;
        RefreshUsageCommand = new AsyncRelayCommand(RefreshUsageAsync, () => CanOperate);
        ClearImagesCommand = new RelayCommand(_ => Request(CacheAction.Images), _ => CanOperate);
        ClearSearchIndexCommand = new RelayCommand(_ => Request(CacheAction.SearchIndex), _ => CanOperate);
        RebuildSearchIndexCommand = new RelayCommand(_ => Request(CacheAction.Rebuild),
            _ => CanOperate && sessions.CurrentSession is not null);
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => IsConfirmationOpen && !IsBusy);
        CancelConfirmationCommand = new RelayCommand(_ => { pendingAction = CacheAction.Usage; Notify(); });
        RetryCommand = new AsyncRelayCommand(() => ExecuteAsync(lastAction), () => CanOperate && HasError);
    }

    public bool IsAvailable => service is not null;
    public bool IsBusy => isBusy;
    public bool CanOperate => IsAvailable && !IsBusy && !IsConfirmationOpen;
    public bool HasError => errorMessage is not null;
    public bool HasStatus => statusMessage is not null;
    public string? ErrorMessage => errorMessage;
    public string? StatusMessage => statusMessage;
    public string ImageUsageText => usage is null ? "尚未读取" : $"内存占用 {FormatBytes(usage.ImageMemoryBytes)}";
    public string SearchIndexUsageText => usage is null ? "尚未读取" : $"磁盘占用 {FormatBytes(usage.SearchIndexDiskBytes)}";
    public bool IsConfirmationOpen => pendingAction != CacheAction.Usage;
    public string ConfirmationTitle => pendingAction switch
    {
        CacheAction.Images => "清理图片缓存？",
        CacheAction.SearchIndex => "清理搜索索引？",
        CacheAction.Rebuild => "重建搜索索引？",
        _ => string.Empty
    };
    public string ConfirmationMessage => pendingAction switch
    {
        CacheAction.Images => "将释放海报与背景图的内存缓存，图片会按需重新加载。",
        CacheAction.SearchIndex => "将清除这台电脑上的搜索索引，后续搜索会按需重新建立。登录状态与播放设置会保留。",
        CacheAction.Rebuild => "将清除本地索引并重新读取当前账户的媒体。登录状态与播放设置会保留。",
        _ => string.Empty
    };

    public ICommand RefreshUsageCommand { get; }
    public ICommand ClearImagesCommand { get; }
    public ICommand ClearSearchIndexCommand { get; }
    public ICommand RebuildSearchIndexCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand CancelConfirmationCommand { get; }
    public ICommand RetryCommand { get; }

    public Task RefreshUsageAsync() => CanOperate ? ExecuteAsync(CacheAction.Usage) : Task.CompletedTask;

    public void Cancel()
    {
        generation++;
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        pendingAction = CacheAction.Usage;
        isBusy = false;
        Notify();
    }

    private void Request(CacheAction action)
    {
        if (!CanOperate) return;
        pendingAction = action;
        Notify();
    }

    private Task ConfirmAsync()
    {
        var action = pendingAction;
        pendingAction = CacheAction.Usage;
        return ExecuteAsync(action);
    }

    private async Task ExecuteAsync(CacheAction action)
    {
        if (!CanOperate || service is null) return;
        var session = sessions.CurrentSession;
        if (action == CacheAction.Rebuild && session is null) return;
        lastAction = action;
        var requestGeneration = ++generation;
        using var requestCancellation = new CancellationTokenSource();
        cancellation = requestCancellation;
        var token = requestCancellation.Token;
        isBusy = true;
        errorMessage = null;
        statusMessage = null;
        Notify();
        try
        {
            switch (action)
            {
                case CacheAction.Images: await service.ClearImagesAsync(token).ConfigureAwait(true); break;
                case CacheAction.SearchIndex: await service.ClearSearchIndexAsync(token).ConfigureAwait(true); break;
                case CacheAction.Rebuild: await service.RebuildSearchIndexAsync(session!, token).ConfigureAwait(true); break;
            }
            var refreshedUsage = await service.GetUsageAsync(token).ConfigureAwait(true);
            if (!IsCurrent(requestGeneration, session, token)) return;
            usage = refreshedUsage;
            statusMessage = action switch
            {
                CacheAction.Images => "图片缓存已清理。",
                CacheAction.SearchIndex => "搜索索引已清理。",
                CacheAction.Rebuild => "搜索索引已重建。",
                _ => null
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Leaving settings or switching accounts cancels the current maintenance request.
        }
        catch (CacheOperationException exception)
        {
            Trace.TraceError("Cache maintenance failed: {0}", exception);
            if (!IsCurrent(requestGeneration, session, token)) return;
            if (exception.Error == CacheOperationError.Unauthorized)
            {
                await ExpiredSessionRecovery.ClearAsync(authStore, sessions, "cache rebuild", session, token).ConfigureAwait(true);
                if (requestGeneration == generation && !token.IsCancellationRequested && sessions.CurrentSession is null)
                    onSessionExpired();
                return;
            }
            errorMessage = exception.Error switch
            {
                CacheOperationError.Forbidden => "没有权限读取此账户的媒体，请检查服务器权限后重试。",
                CacheOperationError.ServerTimeout => "索引重建超时，请稍后重试。",
                CacheOperationError.ServerUnreachable => "无法连接服务器，请检查网络后重试。",
                CacheOperationError.InvalidResponse => "服务器返回的媒体数据无法识别，请稍后重试。",
                _ => "无法读写缓存，请检查本地存储或文件权限后重试。"
            };
        }
        finally
        {
            if (requestGeneration == generation)
            {
                isBusy = false;
                cancellation = null;
                Notify();
            }
        }
    }

    private bool IsCurrent(int requestGeneration, AuthSession? session, CancellationToken token) =>
        requestGeneration == generation && !token.IsCancellationRequested && ReferenceEquals(session, sessions.CurrentSession);

    private void Notify()
    {
        foreach (var name in new[] { nameof(IsBusy), nameof(CanOperate), nameof(HasError), nameof(HasStatus),
            nameof(ErrorMessage), nameof(StatusMessage), nameof(ImageUsageText), nameof(SearchIndexUsageText),
            nameof(IsConfirmationOpen), nameof(ConfirmationTitle), nameof(ConfirmationMessage) }) OnPropertyChanged(name);
        foreach (var command in new[] { RefreshUsageCommand, ClearImagesCommand, ClearSearchIndexCommand,
            RebuildSearchIndexCommand, ConfirmCommand, RetryCommand })
        {
            if (command is AsyncRelayCommand asyncCommand) asyncCommand.NotifyCanExecuteChanged();
            else if (command is RelayCommand relayCommand) relayCommand.RaiseCanExecuteChanged();
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KB";
        return $"{bytes / (1024d * 1024d):0.0} MB";
    }

    private enum CacheAction { Usage, Images, SearchIndex, Rebuild }
}
