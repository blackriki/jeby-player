using EmbyPlayer.Core.Authentication;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class LoginViewModelTests
{
    [TestMethod]
    public void LoginCommand_RequiresUserName()
    {
        var viewModel = CreateViewModel();

        Assert.IsFalse(viewModel.LoginCommand.CanExecute(null));

        viewModel.UserName = "test-user";

        Assert.IsTrue(viewModel.LoginCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task InitializeAsync_LoadsCurrentServerAddress()
    {
        var settingsService = new TestAppSettingsService
        {
            LastServerBase = "http://media.local:8096"
        };
        var viewModel = CreateViewModel(settingsService: settingsService);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.AreEqual("http://media.local:8096", viewModel.ServerBase);
    }

    [TestMethod]
    public async Task InitializeAsync_WithoutServerAddress_ShowsChineseError()
    {
        var viewModel = CreateViewModel(settingsService: new TestAppSettingsService());

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.AreEqual("请先连接服务器", viewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task InitializeAsync_DoesNotBlockWhileLoadingServerAddress()
    {
        var loadCompletion = new TaskCompletionSource<string?>();
        var settingsService = new TestAppSettingsService
        {
            GetLastServerBaseAsyncHandler = _ => loadCompletion.Task
        };
        var viewModel = CreateViewModel(settingsService: settingsService);

        var initializeTask = viewModel.InitializeAsync(CancellationToken.None);

        Assert.IsFalse(initializeTask.IsCompleted);

        loadCompletion.SetResult("http://media.local:8096");
        await initializeTask;
        Assert.AreEqual("http://media.local:8096", viewModel.ServerBase);
    }

    [TestMethod]
    public async Task InitializeAsync_ReentryClearsStaleCredentialsAndError()
    {
        var viewModel = CreateViewModel();
        viewModel.UserName = "stale-user";
        viewModel.Password = "stale-password";
        viewModel.ShowError("stale error");

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.AreEqual(string.Empty, viewModel.UserName);
        Assert.AreEqual(string.Empty, viewModel.Password);
        Assert.IsNull(viewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task InitializeAsync_ReentryClearsOldServerAndOverlappingCallPreservesNewInput()
    {
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCount = 0;
        var settingsService = new TestAppSettingsService
        {
            LastServerBase = "http://old.local:8096"
        };
        var viewModel = CreateViewModel(settingsService: settingsService);
        await viewModel.InitializeAsync(CancellationToken.None);
        settingsService.GetLastServerBaseAsyncHandler = _ =>
        {
            loadCount++;
            return pending.Task;
        };

        var first = viewModel.InitializeAsync(CancellationToken.None);
        Assert.AreEqual(string.Empty, viewModel.ServerBase);
        viewModel.UserName = "new-user";
        viewModel.Password = "new-password";
        var overlapping = viewModel.InitializeAsync(CancellationToken.None);

        Assert.AreEqual("new-user", viewModel.UserName);
        Assert.AreEqual("new-password", viewModel.Password);
        pending.SetResult("http://new.local:8096");
        await Task.WhenAll(first, overlapping);

        Assert.AreEqual(1, loadCount);
        Assert.AreEqual("http://new.local:8096", viewModel.ServerBase);
        Assert.AreEqual("new-user", viewModel.UserName);
        Assert.AreEqual("new-password", viewModel.Password);
    }

    [TestMethod]
    public async Task InitializeAsync_LateFailureDoesNotOverwriteNavigationError()
    {
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settingsService = new TestAppSettingsService
        {
            GetLastServerBaseAsyncHandler = _ => pending.Task
        };
        var viewModel = CreateViewModel(settingsService: settingsService);

        var initialize = viewModel.InitializeAsync(CancellationToken.None);
        viewModel.ShowError("安全令牌已清除，但本地账户信息未能完全清理，请稍后重试。");
        pending.SetException(new IOException("settings failure"));
        await initialize;

        Assert.AreEqual(
            "安全令牌已清除，但本地账户信息未能完全清理，请稍后重试。",
            viewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task LoginAsync_EmptyUserName_ShowsChineseErrorAndDoesNotAuthenticate()
    {
        var authenticationService = new TestAuthenticationService();
        var viewModel = CreateViewModel(authenticationService: authenticationService);
        await LoadServerAddressAsync(viewModel);
        viewModel.Password = "secret";

        await viewModel.LoginAsync();

        Assert.AreEqual("请输入用户名", viewModel.ErrorMessage);
        Assert.AreEqual(0, authenticationService.AuthenticateCallCount);
    }

    [TestMethod]
    public async Task LoginAsync_EmptyPassword_ShowsChineseErrorAndDoesNotAuthenticate()
    {
        var authenticationService = new TestAuthenticationService();
        var viewModel = CreateViewModel(authenticationService: authenticationService);
        await LoadServerAddressAsync(viewModel);
        viewModel.UserName = "test-user";

        await viewModel.LoginAsync();

        Assert.AreEqual("请输入密码", viewModel.ErrorMessage);
        Assert.AreEqual(0, authenticationService.AuthenticateCallCount);
    }

    [TestMethod]
    public async Task LoginAsync_MissingServerAddress_ShowsChineseErrorAndDoesNotAuthenticate()
    {
        var authenticationService = new TestAuthenticationService();
        var viewModel = CreateViewModel(authenticationService: authenticationService);
        viewModel.UserName = "test-user";
        viewModel.Password = "secret";

        await viewModel.LoginAsync();

        Assert.AreEqual("请先连接服务器", viewModel.ErrorMessage);
        Assert.AreEqual(0, authenticationService.AuthenticateCallCount);
    }

    [TestMethod]
    public async Task LoginAsync_WhileLoggingIn_DisablesInputsAndCommand()
    {
        var authenticationStarted = new TaskCompletionSource();
        var authenticationResult = new TaskCompletionSource<AuthenticationResult>();
        var authenticationService = new TestAuthenticationService
        {
            AuthenticateAsyncHandler = (_, _, _, _) =>
            {
                authenticationStarted.SetResult();
                return authenticationResult.Task;
            }
        };
        var viewModel = CreateViewModel(authenticationService: authenticationService);
        await LoadServerAddressAsync(viewModel);
        viewModel.UserName = "test-user";
        viewModel.Password = "secret";

        var loginTask = viewModel.LoginAsync();
        await authenticationStarted.Task;

        Assert.IsTrue(viewModel.IsLoggingIn);
        Assert.IsFalse(viewModel.IsInputEnabled);
        Assert.IsFalse(viewModel.LoginCommand.CanExecute(null));
        Assert.AreEqual("正在登录...", viewModel.LoginButtonText);

        authenticationResult.SetResult(AuthenticationResult.Failure(AuthenticationError.InvalidCredentials));
        await loginTask;
    }

    [TestMethod]
    public async Task LoginAsync_Success_SavesRuntimeSessionAndNavigatesToHome()
    {
        var navigationService = new NavigationService();
        navigationService.NavigateTo(AppPage.Login);
        var currentSessionService = new CurrentSessionService();
        var authSessionStore = new TestAuthSessionStore();
        var session = new AuthSession(
            "http://media.local:8096",
            "test-access-token",
            "user-1",
            "测试用户",
            "server-1");
        var authenticationService = new TestAuthenticationService
        {
            AuthenticateAsyncHandler = (_, _, _, _) => Task.FromResult(AuthenticationResult.Success(session))
        };
        var viewModel = CreateViewModel(
            navigationService,
            authenticationService,
            currentSessionService,
            authSessionStore);
        await LoadServerAddressAsync(viewModel);
        viewModel.UserName = "test-user";
        viewModel.Password = "secret";

        await viewModel.LoginAsync();

        Assert.AreEqual(1, authSessionStore.SaveCallCount);
        Assert.AreSame(session, authSessionStore.SavedSession);
        Assert.AreSame(session, currentSessionService.CurrentSession);
        Assert.AreEqual(AppPage.Home, navigationService.CurrentPage);
        Assert.IsNull(viewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task LoginAsync_SaveSessionFailure_StaysOnLoginAndShowsChineseError()
    {
        var navigationService = new NavigationService();
        navigationService.NavigateTo(AppPage.Login);
        var currentSessionService = new CurrentSessionService();
        var session = new AuthSession(
            "http://media.local:8096",
            "test-access-token",
            "user-1",
            "测试用户",
            "server-1");
        var authenticationService = new TestAuthenticationService
        {
            AuthenticateAsyncHandler = (_, _, _, _) => Task.FromResult(AuthenticationResult.Success(session))
        };
        var authSessionStore = new TestAuthSessionStore
        {
            SaveAsyncHandler = (_, _) => throw new IOException("save failed")
        };
        var viewModel = CreateViewModel(
            navigationService,
            authenticationService,
            currentSessionService,
            authSessionStore);
        await LoadServerAddressAsync(viewModel);
        viewModel.UserName = "test-user";
        viewModel.Password = "secret";

        await viewModel.LoginAsync();

        Assert.IsNull(currentSessionService.CurrentSession);
        Assert.AreEqual(AppPage.Login, navigationService.CurrentPage);
        Assert.AreEqual("登录状态保存失败，请稍后重试。", viewModel.ErrorMessage);
        Assert.AreEqual(string.Empty, viewModel.Password);
    }

    [TestMethod]
    public async Task LoginAsync_Failure_StaysOnLoginAndShowsChineseError()
    {
        var navigationService = new NavigationService();
        navigationService.NavigateTo(AppPage.Login);
        var authenticationService = new TestAuthenticationService
        {
            AuthenticateAsyncHandler = (_, _, _, _) =>
                Task.FromResult(AuthenticationResult.Failure(AuthenticationError.InvalidCredentials))
        };
        var viewModel = CreateViewModel(
            navigationService,
            authenticationService);
        await LoadServerAddressAsync(viewModel);
        viewModel.UserName = "test-user";
        viewModel.Password = "wrong-password";

        await viewModel.LoginAsync();

        Assert.AreEqual(AppPage.Login, navigationService.CurrentPage);
        Assert.AreEqual("用户名或密码错误", viewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task LoginAsync_LoginFailed_StaysOnLoginAndShowsRetryableError()
    {
        var navigationService = new NavigationService();
        navigationService.NavigateTo(AppPage.Login);
        var authenticationService = new TestAuthenticationService
        {
            AuthenticateAsyncHandler = (_, _, _, _) =>
                Task.FromResult(AuthenticationResult.Failure(AuthenticationError.LoginFailed))
        };
        var viewModel = CreateViewModel(
            navigationService,
            authenticationService);
        await LoadServerAddressAsync(viewModel);
        viewModel.UserName = "test-user";
        viewModel.Password = "secret";

        await viewModel.LoginAsync();

        Assert.AreEqual(AppPage.Login, navigationService.CurrentPage);
        Assert.AreEqual("登录失败，请稍后重试", viewModel.ErrorMessage);
        Assert.IsFalse(viewModel.IsLoggingIn);
        Assert.IsTrue(viewModel.LoginCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task LoginAsync_NetworkFailure_ShowsChineseConnectionError()
    {
        var authenticationService = new TestAuthenticationService
        {
            AuthenticateAsyncHandler = (_, _, _, _) =>
                Task.FromResult(AuthenticationResult.Failure(AuthenticationError.ServerUnreachable))
        };
        var viewModel = CreateViewModel(authenticationService: authenticationService);
        await LoadServerAddressAsync(viewModel);
        viewModel.UserName = "test-user";
        viewModel.Password = "secret";

        await viewModel.LoginAsync();

        Assert.AreEqual("无法连接服务器，请检查地址或网络", viewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task LoginAsync_Timeout_ShowsChineseTimeoutError()
    {
        var authenticationService = new TestAuthenticationService
        {
            AuthenticateAsyncHandler = (_, _, _, _) =>
                Task.FromResult(AuthenticationResult.Failure(AuthenticationError.ServerTimeout))
        };
        var viewModel = CreateViewModel(authenticationService: authenticationService);
        await LoadServerAddressAsync(viewModel);
        viewModel.UserName = "test-user";
        viewModel.Password = "secret";

        await viewModel.LoginAsync();

        Assert.AreEqual("登录超时，请稍后重试", viewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task LoginAsync_Success_DoesNotSavePasswordToSettings()
    {
        var settingsService = new TestAppSettingsService
        {
            LastServerBase = "http://media.local:8096"
        };
        var session = new AuthSession(
            "http://media.local:8096",
            "test-access-token",
            "user-1",
            "测试用户",
            "server-1");
        var authenticationService = new TestAuthenticationService
        {
            AuthenticateAsyncHandler = (_, _, _, _) => Task.FromResult(AuthenticationResult.Success(session))
        };
        var viewModel = CreateViewModel(
            authenticationService: authenticationService,
            settingsService: settingsService);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.UserName = "test-user";
        viewModel.Password = "secret";

        await viewModel.LoginAsync();

        Assert.AreEqual(0, settingsService.SaveCallCount);
        Assert.IsNull(settingsService.SavedLastServerBase);
    }

    [TestMethod]
    public async Task LoginAsync_Success_DoesNotSavePasswordToSessionStore()
    {
        var authSessionStore = new TestAuthSessionStore();
        var session = new AuthSession(
            "http://media.local:8096",
            "test-access-token",
            "user-1",
            "测试用户",
            "server-1");
        var authenticationService = new TestAuthenticationService
        {
            AuthenticateAsyncHandler = (_, _, _, _) => Task.FromResult(AuthenticationResult.Success(session))
        };
        var viewModel = CreateViewModel(
            authenticationService: authenticationService,
            authSessionStore: authSessionStore);
        await LoadServerAddressAsync(viewModel);
        viewModel.UserName = "test-user";
        viewModel.Password = "secret";

        await viewModel.LoginAsync();

        Assert.IsNotNull(authSessionStore.SavedSession);
        Assert.IsFalse(authSessionStore.SavedSession.ServerBase.Contains("secret", StringComparison.Ordinal));
        Assert.IsFalse(authSessionStore.SavedSession.UserId.Contains("secret", StringComparison.Ordinal));
        Assert.IsFalse(authSessionStore.SavedSession.UserName.Contains("secret", StringComparison.Ordinal));
        Assert.IsFalse(authSessionStore.SavedSession.ServerId.Contains("secret", StringComparison.Ordinal));
        Assert.IsFalse(authSessionStore.SavedSession.AccessToken.Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SwitchServerAsync_ClearsSessionAndServerThenNavigatesToConnection()
    {
        var navigationService = new NavigationService();
        navigationService.NavigateTo(AppPage.Login);
        var currentSessionService = new CurrentSessionService();
        currentSessionService.SetSession(new AuthSession(
            "http://media.local:8096",
            "test-token",
            "user-1",
            "Test User",
            "server-1"));
        var authSessionStore = new TestAuthSessionStore();
        var settingsService = new TestAppSettingsService
        {
            LastServerBase = "http://media.local:8096"
        };
        var viewModel = CreateViewModel(
            navigationService,
            currentSessionService: currentSessionService,
            authSessionStore: authSessionStore,
            settingsService: settingsService);
        viewModel.UserName = "draft-user";
        viewModel.Password = "draft-password";

        await viewModel.SwitchServerAsync();

        Assert.AreEqual(1, authSessionStore.ClearCallCount);
        Assert.AreEqual(1, settingsService.ClearLastServerBaseCallCount);
        Assert.IsNull(currentSessionService.CurrentSession);
        Assert.IsNull(settingsService.LastServerBase);
        Assert.AreEqual(string.Empty, viewModel.UserName);
        Assert.AreEqual(string.Empty, viewModel.Password);
        Assert.AreEqual(AppPage.ServerConnection, navigationService.CurrentPage);
    }

    [TestMethod]
    public async Task SwitchServerAsync_FailureStaysOnLoginAndKeepsDraft()
    {
        var navigationService = new NavigationService();
        navigationService.NavigateTo(AppPage.Login);
        var authSessionStore = new TestAuthSessionStore
        {
            ClearAsyncHandler = _ => throw new IOException("credential failure")
        };
        var viewModel = CreateViewModel(
            navigationService,
            authSessionStore: authSessionStore);
        viewModel.UserName = "draft-user";
        viewModel.Password = "draft-password";

        await viewModel.SwitchServerAsync();

        Assert.AreEqual(AppPage.Login, navigationService.CurrentPage);
        Assert.AreEqual("draft-user", viewModel.UserName);
        Assert.AreEqual("draft-password", viewModel.Password);
        Assert.AreEqual("切换服务器失败，请稍后重试。", viewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task SwitchServerAsync_PartialAuthenticationCleanupNavigatesToSafeConnectionPage()
    {
        var navigationService = new NavigationService();
        navigationService.NavigateTo(AppPage.Login);
        var currentSessionService = new CurrentSessionService();
        currentSessionService.SetSession(new AuthSession(
            "http://media.local:8096",
            "test-token",
            "user-1",
            "Test User",
            "server-1"));
        var authSessionStore = new TestAuthSessionStore
        {
            ClearAsyncHandler = _ => throw new AuthSessionClearPartialFailureException(
                new IOException("metadata failure"))
        };
        var settingsService = new TestAppSettingsService
        {
            LastServerBase = "http://media.local:8096"
        };
        object? navigationParameter = null;
        navigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;
        var viewModel = CreateViewModel(
            navigationService,
            currentSessionService: currentSessionService,
            authSessionStore: authSessionStore,
            settingsService: settingsService);
        viewModel.UserName = "draft-user";
        viewModel.Password = "draft-password";

        await viewModel.SwitchServerAsync();

        Assert.IsNull(currentSessionService.CurrentSession);
        Assert.AreEqual(0, settingsService.ClearLastServerBaseCallCount);
        Assert.AreEqual("http://media.local:8096", settingsService.LastServerBase);
        Assert.AreEqual(string.Empty, viewModel.UserName);
        Assert.AreEqual(string.Empty, viewModel.Password);
        Assert.AreEqual(AppPage.ServerConnection, navigationService.CurrentPage);
        var parameter = navigationParameter as ServerConnectionNavigationParameter;
        Assert.IsNotNull(parameter);
        Assert.AreEqual(
            ServerConnectionNavigationParameter.AuthenticationCleanupPartialFailureMessage,
            parameter.Message);
    }

    [TestMethod]
    public async Task SwitchServerAsync_AddressClearFailureNavigatesWithRetainedAddressWarning()
    {
        var navigationService = new NavigationService();
        navigationService.NavigateTo(AppPage.Login);
        var currentSessionService = new CurrentSessionService();
        currentSessionService.SetSession(new AuthSession(
            "http://media.local:8096",
            "test-token",
            "user-1",
            "Test User",
            "server-1"));
        var authSessionStore = new TestAuthSessionStore();
        var settingsService = new TestAppSettingsService
        {
            LastServerBase = "http://media.local:8096",
            ClearLastServerBaseAsyncHandler = _ => throw new IOException("settings failure")
        };
        object? navigationParameter = null;
        navigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;
        var viewModel = CreateViewModel(
            navigationService,
            currentSessionService: currentSessionService,
            authSessionStore: authSessionStore,
            settingsService: settingsService);
        viewModel.UserName = "draft-user";
        viewModel.Password = "draft-password";

        await viewModel.SwitchServerAsync();

        Assert.AreEqual(1, authSessionStore.ClearCallCount);
        Assert.AreEqual(1, settingsService.ClearLastServerBaseCallCount);
        Assert.IsNull(currentSessionService.CurrentSession);
        Assert.AreEqual("http://media.local:8096", settingsService.LastServerBase);
        Assert.AreEqual(string.Empty, viewModel.UserName);
        Assert.AreEqual(string.Empty, viewModel.Password);
        Assert.AreEqual(AppPage.ServerConnection, navigationService.CurrentPage);
        var parameter = navigationParameter as ServerConnectionNavigationParameter;
        Assert.IsNotNull(parameter);
        Assert.AreEqual(
            ServerConnectionNavigationParameter.SwitchServerPartialFailureMessage,
            parameter.Message);
        Assert.IsNull(viewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task SwitchServerCommand_DoesNotRunConcurrently()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settingsService = new TestAppSettingsService
        {
            LastServerBase = "http://media.local:8096",
            ClearLastServerBaseAsyncHandler = _ => pending.Task
        };
        var authSessionStore = new TestAuthSessionStore();
        var viewModel = CreateViewModel(
            authSessionStore: authSessionStore,
            settingsService: settingsService);

        var first = ((AsyncRelayCommand)viewModel.SwitchServerCommand).ExecuteAsync();
        await WaitForAsync(() => viewModel.IsSwitchingServer);
        await ((AsyncRelayCommand)viewModel.SwitchServerCommand).ExecuteAsync();

        Assert.AreEqual(1, authSessionStore.ClearCallCount);
        Assert.AreEqual(1, settingsService.ClearLastServerBaseCallCount);
        pending.SetResult();
        await first;
    }

    [TestMethod]
    public async Task LoginPage_UsesSharedAccessibleButtonsForLoginAndSwitchServer()
    {
        var root = FindRepositoryRoot();
        var xaml = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Pages",
            "LoginPage.xaml"));

        StringAssert.Contains(xaml, "Command=\"{Binding LoginCommand}\"");
        StringAssert.Contains(xaml, "Style=\"{StaticResource AppPrimaryButtonStyle}\"");
        StringAssert.Contains(xaml, "IsDefault=\"True\"");
        StringAssert.Contains(xaml, "Command=\"{Binding SwitchServerCommand}\"");
        StringAssert.Contains(xaml, "Style=\"{StaticResource AppSecondaryButtonStyle}\"");
        StringAssert.Contains(xaml, "AutomationProperties.Name=\"切换服务器\"");
        StringAssert.Contains(xaml, "AutomationProperties.HelpText=\"清除当前账户登录状态和服务器地址，并返回服务器连接页面。\"");
        Assert.IsFalse(xaml.Contains("PrimaryLoginButtonStyle", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("DialogOverlay", StringComparison.Ordinal));
    }

    private static async Task LoadServerAddressAsync(LoginViewModel viewModel)
    {
        await viewModel.InitializeAsync(CancellationToken.None);
    }

    private static LoginViewModel CreateViewModel(
        NavigationService? navigationService = null,
        TestAuthenticationService? authenticationService = null,
        CurrentSessionService? currentSessionService = null,
        TestAuthSessionStore? authSessionStore = null,
        TestAppSettingsService? settingsService = null)
    {
        settingsService ??= new TestAppSettingsService
        {
            LastServerBase = "http://media.local:8096"
        };

        navigationService ??= new NavigationService();
        authenticationService ??= new TestAuthenticationService();
        currentSessionService ??= new CurrentSessionService();
        authSessionStore ??= new TestAuthSessionStore();
        var accountSessionService = new AccountSessionService(
            authSessionStore,
            currentSessionService,
            settingsService);

        return new LoginViewModel(
            navigationService,
            authenticationService,
            currentSessionService,
            authSessionStore,
            settingsService,
            accountSessionService);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            if (timeout.IsCancellationRequested)
            {
                Assert.Fail("Timed out waiting for asynchronous account operation.");
            }

            await Task.Delay(10);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EmbyPlayer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
