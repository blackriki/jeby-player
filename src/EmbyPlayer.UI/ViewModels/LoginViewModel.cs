using System.IO;
using System.Windows.Input;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Settings;
using EmbyPlayer.UI.Navigation;

namespace EmbyPlayer.UI.ViewModels;

public sealed class LoginViewModel : ViewModelBase
{
    private readonly IAccountSessionService accountSessionService;
    private readonly IAppSettingsService appSettingsService;
    private readonly IAuthSessionStore authSessionStore;
    private readonly IAuthenticationService authenticationService;
    private readonly ICurrentSessionService currentSessionService;
    private readonly INavigationService navigationService;
    private string? errorMessage;
    private int errorRevision;
    private int initializationGeneration;
    private Task? initializationTask;
    private bool isLoggingIn;
    private bool isSwitchingServer;
    private string password = string.Empty;
    private string serverBase = string.Empty;
    private string userName = string.Empty;

    public LoginViewModel(
        INavigationService navigationService,
        IAuthenticationService authenticationService,
        ICurrentSessionService currentSessionService,
        IAuthSessionStore authSessionStore,
        IAppSettingsService appSettingsService,
        IAccountSessionService accountSessionService)
    {
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        this.authenticationService = authenticationService ?? throw new ArgumentNullException(nameof(authenticationService));
        this.currentSessionService = currentSessionService ?? throw new ArgumentNullException(nameof(currentSessionService));
        this.authSessionStore = authSessionStore ?? throw new ArgumentNullException(nameof(authSessionStore));
        this.appSettingsService = appSettingsService ?? throw new ArgumentNullException(nameof(appSettingsService));
        this.accountSessionService = accountSessionService ?? throw new ArgumentNullException(nameof(accountSessionService));

        LoginCommand = new AsyncRelayCommand(LoginAsync, () => CanLogin);
        SwitchServerCommand = new AsyncRelayCommand(SwitchServerAsync, () => !IsBusy);
    }

    public string ServerBase
    {
        get => serverBase;
        private set
        {
            if (serverBase == value)
            {
                return;
            }

            serverBase = value;
            OnPropertyChanged();
            NotifyCommandCanExecuteChanged();
        }
    }

    public string UserName
    {
        get => userName;
        set
        {
            if (userName == value)
            {
                return;
            }

            userName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanLogin));
            NotifyCommandCanExecuteChanged();
        }
    }

    public string Password
    {
        get => password;
        set
        {
            if (password == value)
            {
                return;
            }

            password = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanLogin));
            NotifyCommandCanExecuteChanged();
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
            errorRevision++;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool IsLoggingIn
    {
        get => isLoggingIn;
        private set
        {
            if (isLoggingIn == value)
            {
                return;
            }

            isLoggingIn = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsInputEnabled));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(LoginButtonText));
            OnPropertyChanged(nameof(CanLogin));
            NotifyCommandCanExecuteChanged();
        }
    }

    public bool IsSwitchingServer
    {
        get => isSwitchingServer;
        private set
        {
            if (isSwitchingServer == value)
            {
                return;
            }

            isSwitchingServer = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsInputEnabled));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(SwitchServerButtonText));
            OnPropertyChanged(nameof(CanLogin));
            NotifyCommandCanExecuteChanged();
        }
    }

    public bool IsBusy => IsLoggingIn || IsSwitchingServer;

    public bool IsInputEnabled => !IsBusy;

    public bool CanLogin => !IsBusy && !string.IsNullOrWhiteSpace(UserName);

    public string LoginButtonText => IsLoggingIn ? "正在登录..." : "登录";

    public string SwitchServerButtonText => IsSwitchingServer ? "正在切换..." : "切换服务器";

    public ICommand LoginCommand { get; }

    public ICommand SwitchServerCommand { get; }

    public void ShowError(string message)
    {
        ErrorMessage = string.IsNullOrWhiteSpace(message) ? null : message;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (initializationTask is { } activeInitialization)
        {
            await activeInitialization.WaitAsync(cancellationToken).ConfigureAwait(true);
            return;
        }

        var generation = ++initializationGeneration;
        UserName = string.Empty;
        Password = string.Empty;
        ServerBase = string.Empty;
        ErrorMessage = null;
        var startingErrorRevision = errorRevision;

        var task = InitializeCoreAsync(generation, startingErrorRevision, cancellationToken);
        initializationTask = task;
        try
        {
            await task.ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(initializationTask, task))
            {
                initializationTask = null;
            }
        }
    }

    private async Task InitializeCoreAsync(
        int generation,
        int startingErrorRevision,
        CancellationToken cancellationToken)
    {
        string? lastServerBase;
        try
        {
            lastServerBase = await appSettingsService
                .GetLastServerBaseAsync(cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            if (CanApplyInitialization(generation, startingErrorRevision))
            {
                ErrorMessage = "请先连接服务器";
            }

            return;
        }

        if (generation != initializationGeneration)
        {
            return;
        }

        ServerBase = lastServerBase ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ServerBase)
            && errorRevision == startingErrorRevision)
        {
            ErrorMessage = "请先连接服务器";
        }
    }

    private bool CanApplyInitialization(int generation, int startingErrorRevision)
    {
        return generation == initializationGeneration
            && errorRevision == startingErrorRevision;
    }

    public async Task LoginAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ServerBase))
        {
            ErrorMessage = "请先连接服务器";
            return;
        }

        if (string.IsNullOrWhiteSpace(UserName))
        {
            ErrorMessage = "请输入用户名";
            return;
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "请输入密码";
            return;
        }

        IsLoggingIn = true;
        ErrorMessage = null;

        try
        {
            var result = await authenticationService
                .AuthenticateAsync(ServerBase, UserName, Password, CancellationToken.None)
                .ConfigureAwait(true);

            if (result.IsSuccess && result.Session is not null)
            {
                Password = string.Empty;
                try
                {
                    await authSessionStore
                        .SaveAsync(result.Session, CancellationToken.None)
                        .ConfigureAwait(true);
                }
                catch
                {
                    ErrorMessage = "登录状态保存失败，请稍后重试。";
                    return;
                }

                currentSessionService.SetSession(result.Session);
                navigationService.NavigateTo(AppPage.Home);
                return;
            }

            ErrorMessage = GetErrorMessage(result.Error);
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    public async Task SwitchServerAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsSwitchingServer = true;
        ErrorMessage = null;
        try
        {
            await accountSessionService
                .SwitchServerAsync(CancellationToken.None)
                .ConfigureAwait(true);
            UserName = string.Empty;
            Password = string.Empty;
            ServerBase = string.Empty;
            navigationService.NavigateTo(AppPage.ServerConnection);
        }
        catch (AuthSessionClearPartialFailureException exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"Switch server cleared the secure token, but metadata cleanup failed: {exception.InnerException}");
            UserName = string.Empty;
            Password = string.Empty;
            ServerBase = string.Empty;
            navigationService.NavigateTo(
                AppPage.ServerConnection,
                new ServerConnectionNavigationParameter(
                    ServerConnectionNavigationParameter.AuthenticationCleanupPartialFailureMessage));
        }
        catch (SwitchServerPartialFailureException exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"Switch server cleared the session but retained the saved address: {exception.InnerException}");
            UserName = string.Empty;
            Password = string.Empty;
            ServerBase = string.Empty;
            navigationService.NavigateTo(
                AppPage.ServerConnection,
                new ServerConnectionNavigationParameter(
                    ServerConnectionNavigationParameter.SwitchServerPartialFailureMessage));
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"Switch server cleanup failed: {exception}");
            ErrorMessage = "切换服务器失败，请稍后重试。";
        }
        finally
        {
            IsSwitchingServer = false;
        }
    }

    private void NotifyCommandCanExecuteChanged()
    {
        if (LoginCommand is AsyncRelayCommand asyncCommand)
        {
            asyncCommand.NotifyCanExecuteChanged();
        }

        if (SwitchServerCommand is AsyncRelayCommand switchServerCommand)
        {
            switchServerCommand.NotifyCanExecuteChanged();
        }
    }

    private static string GetErrorMessage(AuthenticationError error)
    {
        return error switch
        {
            AuthenticationError.MissingServer => "请先连接服务器",
            AuthenticationError.InvalidCredentials => "用户名或密码错误",
            AuthenticationError.ServerUnreachable => "无法连接服务器，请检查地址或网络",
            AuthenticationError.ServerTimeout => "登录超时，请稍后重试",
            AuthenticationError.Cancelled => "登录已取消",
            _ => "登录失败，请稍后重试"
        };
    }
}
