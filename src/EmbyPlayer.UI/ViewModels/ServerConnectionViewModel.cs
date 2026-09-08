using System.IO;
using System.Windows.Input;
using EmbyPlayer.Core.Servers;
using EmbyPlayer.Core.Settings;
using EmbyPlayer.UI.Navigation;

namespace EmbyPlayer.UI.ViewModels;

public sealed class ServerConnectionViewModel : ViewModelBase
{
    private readonly IAppSettingsService appSettingsService;
    private readonly INavigationService navigationService;
    private readonly IServerConnectionService serverConnectionService;
    private string? errorMessage;
    private bool isConnecting;
    private bool isApplyingLoadedServerUrl;
    private bool isLoadingSettings;
    private bool isLoadingRecentServers;
    private bool isRemovingRecentServer;
    private bool recentServersLoadFailed;
    private string? recentServersErrorMessage;
    private IReadOnlyList<string> recentServerBases = Array.Empty<string>();
    private Task? serverUrlLoadTask;
    private int serverUrlEditVersion;
    private string serverUrl = string.Empty;

    public ServerConnectionViewModel(
        INavigationService navigationService,
        IServerConnectionService serverConnectionService,
        IAppSettingsService appSettingsService)
    {
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        this.serverConnectionService = serverConnectionService ?? throw new ArgumentNullException(nameof(serverConnectionService));
        this.appSettingsService = appSettingsService ?? throw new ArgumentNullException(nameof(appSettingsService));

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => CanConnect);
        SelectRecentServerCommand = new RelayCommand(parameter =>
        {
            if (parameter is string address && CanManageRecentServers)
            {
                ServerUrl = address;
            }
        }, parameter => parameter is string && CanManageRecentServers);
        RemoveRecentServerCommand = new AsyncRelayCommand(
            parameter => RemoveRecentServerAsync(parameter as string),
            parameter => parameter is string && CanManageRecentServers);
        RetryRecentServersCommand = new AsyncRelayCommand(
            () => LoadRecentServersCoreAsync(CancellationToken.None),
            () => CanManageRecentServers && recentServersLoadFailed);
    }

    public string ServerUrl
    {
        get => serverUrl;
        set
        {
            if (serverUrl == value)
            {
                return;
            }

            serverUrl = value;
            if (!isApplyingLoadedServerUrl)
            {
                serverUrlEditVersion++;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(CanConnect));
            NotifyCommandsCanExecuteChanged();
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
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool IsConnecting
    {
        get => isConnecting;
        private set
        {
            if (isConnecting == value)
            {
                return;
            }

            isConnecting = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsInputEnabled));
            OnPropertyChanged(nameof(ConnectButtonText));
            OnPropertyChanged(nameof(CanConnect));
            OnRecentServerStateChanged();
        }
    }

    public bool IsInputEnabled => !IsConnecting;

    public bool CanConnect => !IsConnecting && !isLoadingSettings && !IsLoadingRecentServers
        && !IsRemovingRecentServer && !string.IsNullOrWhiteSpace(ServerUrl);

    public string ConnectButtonText => IsConnecting ? "正在连接..." : "连接";

    public ICommand ConnectCommand { get; }

    public IReadOnlyList<string> RecentServerBases => recentServerBases;

    public bool IsLoadingRecentServers => isLoadingRecentServers;

    public bool IsRemovingRecentServer => isRemovingRecentServer;

    public bool CanManageRecentServers => !IsConnecting && !isLoadingSettings
        && !IsLoadingRecentServers && !IsRemovingRecentServer;

    public string? RecentServersErrorMessage => recentServersErrorMessage;

    public bool HasRecentServersError => !string.IsNullOrWhiteSpace(RecentServersErrorMessage);

    public bool IsRecentServersLoadErrorVisible => HasRecentServersError && recentServersLoadFailed;

    public bool IsRecentServersEmpty => !IsLoadingRecentServers && !HasRecentServersError && RecentServerBases.Count == 0;

    public ICommand SelectRecentServerCommand { get; }

    public ICommand RemoveRecentServerCommand { get; }

    public ICommand RetryRecentServersCommand { get; }

    public async Task LoadLastServerBaseAsync(
        CancellationToken cancellationToken,
        string? statusMessage = null)
    {
        if (serverUrlLoadTask is { } activeLoad)
        {
            if (!string.IsNullOrWhiteSpace(statusMessage))
            {
                ErrorMessage = statusMessage;
            }

            await activeLoad.WaitAsync(cancellationToken).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(statusMessage))
            {
                ErrorMessage = statusMessage;
            }

            return;
        }

        var loadTask = LoadLastServerBaseCoreAsync(cancellationToken, statusMessage);
        serverUrlLoadTask = loadTask;
        try
        {
            await loadTask.ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(serverUrlLoadTask, loadTask))
            {
                serverUrlLoadTask = null;
            }
        }
    }

    private async Task LoadLastServerBaseCoreAsync(
        CancellationToken cancellationToken,
        string? statusMessage)
    {
        var editVersion = serverUrlEditVersion;
        ApplyLoadedServerUrl(string.Empty);
        ErrorMessage = statusMessage;
        isLoadingSettings = true;
        OnRecentServerStateChanged();
        var recentLoad = LoadRecentServersCoreAsync(cancellationToken);

        try
        {
            var lastServerBase = await appSettingsService
                .GetLastServerBaseAsync(cancellationToken)
                .ConfigureAwait(true);

            if (CanApplyLoadedServerUrl(editVersion))
            {
                ApplyLoadedServerUrl(lastServerBase ?? string.Empty);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceError("Failed to read saved server address: {0}", exception);
            if (CanApplyLoadedServerUrl(editVersion) && string.IsNullOrWhiteSpace(statusMessage))
            {
                ErrorMessage = "无法读取本地保存的服务器地址。";
            }
        }
        finally
        {
            try
            {
                await recentLoad.ConfigureAwait(true);
            }
            finally
            {
                isLoadingSettings = false;
                OnRecentServerStateChanged();
            }
        }
    }

    private async Task LoadRecentServersCoreAsync(CancellationToken cancellationToken)
    {
        isLoadingRecentServers = true;
        recentServersErrorMessage = null;
        recentServersLoadFailed = false;
        OnRecentServerStateChanged();
        try
        {
            recentServerBases = await appSettingsService.GetRecentServerBasesAsync(cancellationToken).ConfigureAwait(true);
            OnPropertyChanged(nameof(RecentServerBases));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceError("Failed to read recent server addresses: {0}", exception);
            recentServersErrorMessage = "无法读取最近服务器，请重试。";
            recentServersLoadFailed = true;
        }
        finally
        {
            isLoadingRecentServers = false;
            OnRecentServerStateChanged();
        }
    }

    public async Task RemoveRecentServerAsync(string? address)
    {
        if (!CanManageRecentServers || address is null || !RecentServerBases.Contains(address))
        {
            return;
        }

        isRemovingRecentServer = true;
        recentServersErrorMessage = null;
        recentServersLoadFailed = false;
        OnRecentServerStateChanged();
        try
        {
            await appSettingsService.RemoveRecentServerBaseAsync(address, CancellationToken.None).ConfigureAwait(true);
            recentServerBases = RecentServerBases.Where(item => !string.Equals(item, address, StringComparison.Ordinal)).ToArray();
            OnPropertyChanged(nameof(RecentServerBases));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceError("Failed to remove recent server address: {0}", exception);
            recentServersErrorMessage = "无法移除此地址，请再次点击它旁边的移除按钮重试。";
        }
        finally
        {
            isRemovingRecentServer = false;
            OnRecentServerStateChanged();
        }
    }

    public async Task ConnectAsync()
    {
        if (IsConnecting || isLoadingSettings || IsLoadingRecentServers || IsRemovingRecentServer)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ServerUrl))
        {
            ErrorMessage = "请输入服务器地址";
            return;
        }

        IsConnecting = true;
        ErrorMessage = null;

        try
        {
            var result = await serverConnectionService
                .ConnectAsync(ServerUrl, CancellationToken.None)
                .ConfigureAwait(true);

            if (result.IsSuccess && result.Server is not null)
            {
                try
                {
                    await appSettingsService
                        .SaveLastServerBaseAsync(result.Server.ServerBase, CancellationToken.None)
                        .ConfigureAwait(true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Trace.TraceError("Failed to save connected server address: {0}", exception);
                    ErrorMessage = "服务器连接成功，但无法保存地址，请检查本地存储后重试。";
                    return;
                }

                navigationService.NavigateTo(AppPage.Login);
                return;
            }

            ErrorMessage = GetErrorMessage(result.Error);
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private void NotifyCommandsCanExecuteChanged()
    {
        if (ConnectCommand is AsyncRelayCommand asyncCommand)
        {
            asyncCommand.NotifyCanExecuteChanged();
        }

        ((RelayCommand)SelectRecentServerCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)RemoveRecentServerCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)RetryRecentServersCommand).NotifyCanExecuteChanged();
    }

    private void OnRecentServerStateChanged()
    {
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(IsLoadingRecentServers));
        OnPropertyChanged(nameof(IsRemovingRecentServer));
        OnPropertyChanged(nameof(CanManageRecentServers));
        OnPropertyChanged(nameof(RecentServersErrorMessage));
        OnPropertyChanged(nameof(HasRecentServersError));
        OnPropertyChanged(nameof(IsRecentServersLoadErrorVisible));
        OnPropertyChanged(nameof(IsRecentServersEmpty));
        NotifyCommandsCanExecuteChanged();
    }

    private bool CanApplyLoadedServerUrl(int editVersion)
    {
        return editVersion == serverUrlEditVersion;
    }

    private void ApplyLoadedServerUrl(string value)
    {
        isApplyingLoadedServerUrl = true;
        try
        {
            ServerUrl = value;
        }
        finally
        {
            isApplyingLoadedServerUrl = false;
        }
    }

    private static string GetErrorMessage(ServerConnectionError error)
    {
        return error switch
        {
            ServerConnectionError.EmptyServerUrl => "请输入服务器地址",
            ServerConnectionError.InvalidUrl => "请输入有效的服务器地址",
            ServerConnectionError.UnsupportedScheme => "仅支持 http:// 或 https:// 地址。",
            ServerConnectionError.ServerTimeout => "服务器连接超时，请稍后重试",
            ServerConnectionError.ServerUnreachable => "无法连接服务器，请检查地址或网络。若未使用 https://，也可以尝试使用 https://。",
            ServerConnectionError.UnrecognizedServer => "无法识别 Emby Server，请检查服务器地址。",
            ServerConnectionError.Cancelled => "连接已取消",
            _ => "连接服务器失败，请稍后重试"
        };
    }
}
