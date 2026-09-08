using EmbyPlayer.Core.Servers;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class ServerConnectionViewModelTests
{
    [TestMethod]
    public void ConnectCommand_EmptyServerUrl_CannotExecute()
    {
        var viewModel = CreateViewModel();

        viewModel.ServerUrl = string.Empty;

        Assert.IsFalse(viewModel.ConnectCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task LoadLastServerBaseAsync_LoadsSavedServerUrl()
    {
        var settingsService = new TestAppSettingsService
        {
            LastServerBase = "http://media.local:8096"
        };
        var viewModel = CreateViewModel(settingsService: settingsService);

        await viewModel.LoadLastServerBaseAsync(CancellationToken.None);

        Assert.AreEqual("http://media.local:8096", viewModel.ServerUrl);
    }

    [TestMethod]
    public async Task LoadLastServerBaseAsync_ReentryClearsStaleAddressAndErrorWhenNoServerIsSaved()
    {
        var settingsService = new TestAppSettingsService();
        var viewModel = CreateViewModel(settingsService: settingsService);
        viewModel.ServerUrl = "unreachable.local";
        await viewModel.ConnectAsync();
        Assert.IsTrue(viewModel.HasError);

        await viewModel.LoadLastServerBaseAsync(CancellationToken.None);

        Assert.AreEqual(string.Empty, viewModel.ServerUrl);
        Assert.IsNull(viewModel.ErrorMessage);
        Assert.IsFalse(viewModel.CanConnect);
    }

    [TestMethod]
    public async Task LoadLastServerBaseAsync_UserInputDuringLoadWinsOverLateSavedValue()
    {
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settingsService = new TestAppSettingsService
        {
            GetLastServerBaseAsyncHandler = _ => pending.Task
        };
        var viewModel = CreateViewModel(settingsService: settingsService);

        var load = viewModel.LoadLastServerBaseAsync(CancellationToken.None);
        viewModel.ServerUrl = "http://typed.local:8096";
        pending.SetResult("http://saved.local:8096");
        await load;

        Assert.AreEqual("http://typed.local:8096", viewModel.ServerUrl);
    }

    [TestMethod]
    public async Task LoadLastServerBaseAsync_ConcurrentCallsShareOneSettingsLoad()
    {
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCount = 0;
        var settingsService = new TestAppSettingsService
        {
            GetLastServerBaseAsyncHandler = _ =>
            {
                loadCount++;
                return pending.Task;
            }
        };
        var viewModel = CreateViewModel(settingsService: settingsService);

        var firstLoad = viewModel.LoadLastServerBaseAsync(CancellationToken.None);
        var secondLoad = viewModel.LoadLastServerBaseAsync(CancellationToken.None);
        pending.SetResult("http://saved.local:8096");
        await Task.WhenAll(firstLoad, secondLoad);

        Assert.AreEqual(1, loadCount);
        Assert.AreEqual(1, settingsService.GetRecentServerBasesCallCount);
        Assert.AreEqual("http://saved.local:8096", viewModel.ServerUrl);
    }

    [TestMethod]
    public async Task LoadLastServerBaseAsync_UserInputBetweenConcurrentCallsIsNotClearedOrOverwritten()
    {
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCount = 0;
        var settingsService = new TestAppSettingsService
        {
            GetLastServerBaseAsyncHandler = _ =>
            {
                loadCount++;
                return pending.Task;
            }
        };
        var viewModel = CreateViewModel(settingsService: settingsService);

        var firstLoad = viewModel.LoadLastServerBaseAsync(CancellationToken.None);
        viewModel.ServerUrl = "http://typed.local:8096";
        var secondLoad = viewModel.LoadLastServerBaseAsync(CancellationToken.None);
        pending.SetResult("http://saved.local:8096");
        await Task.WhenAll(firstLoad, secondLoad);

        Assert.AreEqual(1, loadCount);
        Assert.AreEqual("http://typed.local:8096", viewModel.ServerUrl);
    }

    [TestMethod]
    public async Task LoadLastServerBaseAsync_AfterPreviousLoadCompletesStartsFreshReentry()
    {
        var settingsService = new TestAppSettingsService
        {
            LastServerBase = "http://first.local:8096"
        };
        var viewModel = CreateViewModel(settingsService: settingsService);
        await viewModel.LoadLastServerBaseAsync(CancellationToken.None);
        viewModel.ServerUrl = "http://typed.local:8096";
        settingsService.LastServerBase = "http://second.local:8096";

        await viewModel.LoadLastServerBaseAsync(CancellationToken.None);

        Assert.AreEqual("http://second.local:8096", viewModel.ServerUrl);
    }

    [TestMethod]
    public async Task LoadLastServerBaseAsync_StatusMessageKeepsRetainedAddressEditable()
    {
        var settingsService = new TestAppSettingsService
        {
            LastServerBase = "http://media.local:8096"
        };
        var viewModel = CreateViewModel(settingsService: settingsService);

        await viewModel.LoadLastServerBaseAsync(
            CancellationToken.None,
            ServerConnectionNavigationParameter.SwitchServerPartialFailureMessage);

        Assert.AreEqual("http://media.local:8096", viewModel.ServerUrl);
        Assert.AreEqual(
            ServerConnectionNavigationParameter.SwitchServerPartialFailureMessage,
            viewModel.ErrorMessage);
        Assert.IsTrue(viewModel.CanConnect);

        viewModel.ServerUrl = "http://replacement.local:8096";

        Assert.AreEqual("http://replacement.local:8096", viewModel.ServerUrl);
    }

    [TestMethod]
    public async Task ConnectAsync_Success_SavesLastServerBaseAndNavigatesToLogin()
    {
        var navigationService = new NavigationService();
        var settingsService = new TestAppSettingsService();
        var serverConnectionService = new TestServerConnectionService
        {
            ConnectAsyncHandler = (_, _) => Task.FromResult(
                ServerConnectionResult.Success(new ServerConnectionInfo("http://media.local:8096")))
        };
        var viewModel = CreateViewModel(navigationService, serverConnectionService, settingsService);

        viewModel.ServerUrl = "media.local:8096";
        await viewModel.ConnectAsync();

        Assert.AreEqual("http://media.local:8096", settingsService.SavedLastServerBase);
        Assert.AreEqual(1, settingsService.SaveCallCount);
        Assert.AreEqual(AppPage.Login, navigationService.CurrentPage);
        Assert.IsNull(viewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task ConnectAsync_Failure_StaysOnServerConnectionAndShowsChineseError()
    {
        var navigationService = new NavigationService();
        var settingsService = new TestAppSettingsService();
        var serverConnectionService = new TestServerConnectionService
        {
            ConnectAsyncHandler = (_, _) => Task.FromResult(
                ServerConnectionResult.Failure(ServerConnectionError.ServerUnreachable))
        };
        var viewModel = CreateViewModel(navigationService, serverConnectionService, settingsService);

        viewModel.ServerUrl = "media.local:8096";
        await viewModel.ConnectAsync();

        Assert.AreEqual(AppPage.ServerConnection, navigationService.CurrentPage);
        Assert.AreEqual(0, settingsService.SaveCallCount);
        StringAssert.Contains(viewModel.ErrorMessage!, "无法连接服务器");
    }

    [TestMethod]
    public async Task ConnectAsync_UnsupportedScheme_ShowsChineseError()
    {
        var serverConnectionService = new TestServerConnectionService
        {
            ConnectAsyncHandler = (_, _) => Task.FromResult(
                ServerConnectionResult.Failure(ServerConnectionError.UnsupportedScheme))
        };
        var viewModel = CreateViewModel(serverConnectionService: serverConnectionService);

        viewModel.ServerUrl = "ftp://media.local";
        await viewModel.ConnectAsync();

        Assert.AreEqual("仅支持 http:// 或 https:// 地址。", viewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task ConnectAsync_WhileConnecting_DisablesInputAndCommand()
    {
        var connectionStarted = new TaskCompletionSource();
        var connectionResult = new TaskCompletionSource<ServerConnectionResult>();
        var serverConnectionService = new TestServerConnectionService
        {
            ConnectAsyncHandler = async (_, _) =>
            {
                connectionStarted.SetResult();
                return await connectionResult.Task;
            }
        };
        var viewModel = CreateViewModel(serverConnectionService: serverConnectionService);

        viewModel.ServerUrl = "media.local:8096";
        var connectTask = viewModel.ConnectAsync();
        await connectionStarted.Task;

        Assert.IsTrue(viewModel.IsConnecting);
        Assert.IsFalse(viewModel.IsInputEnabled);
        Assert.IsFalse(viewModel.ConnectCommand.CanExecute(null));
        Assert.IsFalse(viewModel.SelectRecentServerCommand.CanExecute("http://recent.local"));
        Assert.IsFalse(viewModel.RemoveRecentServerCommand.CanExecute("http://recent.local"));

        connectionResult.SetResult(
            ServerConnectionResult.Failure(ServerConnectionError.ServerUnreachable));
        await connectTask;
    }

    [TestMethod]
    public async Task LoadLastServerBaseAsync_ShowsHistoryWithoutSelectingAddressAfterServerWasCleared()
    {
        var settings = new TestAppSettingsService
        {
            RecentServerBases = new[] { "http://recent.local:8096", "https://older.local/emby" }
        };
        var viewModel = CreateViewModel(settingsService: settings);
        await viewModel.LoadLastServerBaseAsync(CancellationToken.None);

        CollectionAssert.AreEqual(settings.RecentServerBases.ToArray(), viewModel.RecentServerBases.ToArray());
        Assert.AreEqual(string.Empty, viewModel.ServerUrl);
        Assert.IsFalse(viewModel.CanConnect);
        Assert.IsFalse(viewModel.IsRecentServersEmpty);
        Assert.IsTrue(viewModel.CanManageRecentServers);
    }

    [TestMethod]
    public async Task LoadLastServerBaseAsync_PendingHistoryKeepsTypingButDisablesMutatingActions()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<string>>();
        var settings = new TestAppSettingsService { GetRecentServerBasesAsyncHandler = _ => pending.Task };
        var viewModel = CreateViewModel(settingsService: settings);
        var loading = viewModel.LoadLastServerBaseAsync(CancellationToken.None);
        viewModel.ServerUrl = "http://typed.local";
        var sharedLoading = viewModel.LoadLastServerBaseAsync(CancellationToken.None);

        Assert.IsTrue(viewModel.IsLoadingRecentServers);
        Assert.IsTrue(viewModel.IsInputEnabled);
        Assert.IsFalse(viewModel.CanConnect);
        Assert.IsFalse(viewModel.SelectRecentServerCommand.CanExecute("http://recent.local"));
        Assert.IsFalse(viewModel.RemoveRecentServerCommand.CanExecute("http://recent.local"));
        pending.SetResult(new[] { "http://recent.local" });
        await Task.WhenAll(loading, sharedLoading);

        Assert.AreEqual(1, settings.GetRecentServerBasesCallCount);
        Assert.AreEqual("http://typed.local", viewModel.ServerUrl);
        Assert.IsTrue(viewModel.CanConnect);
        Assert.IsFalse(viewModel.IsLoadingRecentServers);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RetryRecentServersCommand_PreservesUserInputAndPartialCleanupWarning(bool accessDenied)
    {
        var settings = new TestAppSettingsService
        {
            GetRecentServerBasesAsyncHandler = _ => throw (accessDenied
                ? new UnauthorizedAccessException("Read denied") : new IOException("Read failed"))
        };
        var viewModel = CreateViewModel(settingsService: settings);
        var warning = ServerConnectionNavigationParameter.SwitchServerPartialFailureMessage;
        await viewModel.LoadLastServerBaseAsync(CancellationToken.None, warning);
        viewModel.ServerUrl = "http://typed.local";

        Assert.IsTrue(viewModel.IsRecentServersLoadErrorVisible);
        Assert.IsTrue(viewModel.RetryRecentServersCommand.CanExecute(null));
        Assert.AreEqual(warning, viewModel.ErrorMessage);
        settings.GetRecentServerBasesAsyncHandler = _ => Task.FromResult<IReadOnlyList<string>>(
            new[] { "http://recent.local" });
        await ((AsyncRelayCommand)viewModel.RetryRecentServersCommand).ExecuteAsync();

        Assert.AreEqual("http://typed.local", viewModel.ServerUrl);
        Assert.AreEqual(warning, viewModel.ErrorMessage);
        Assert.IsFalse(viewModel.HasRecentServersError);
        Assert.AreEqual(1, viewModel.RecentServerBases.Count);
    }

    [TestMethod]
    public async Task SelectRecentServerCommand_OnlyFillsInputUntilUserConnectsAndKeepsWarning()
    {
        const string address = "http://recent.local:8096";
        var settings = new TestAppSettingsService { RecentServerBases = new[] { address } };
        var connection = new TestServerConnectionService();
        var navigation = new NavigationService();
        var viewModel = CreateViewModel(navigation, connection, settings);
        var warning = ServerConnectionNavigationParameter.AuthenticationCleanupPartialFailureMessage;
        await viewModel.LoadLastServerBaseAsync(CancellationToken.None, warning);

        viewModel.SelectRecentServerCommand.Execute(address);

        Assert.AreEqual(address, viewModel.ServerUrl);
        Assert.AreEqual(warning, viewModel.ErrorMessage);
        Assert.AreEqual(0, connection.ConnectCallCount);
        Assert.AreEqual(0, settings.SaveCallCount);
        Assert.AreEqual(AppPage.ServerConnection, navigation.CurrentPage);
        await ((AsyncRelayCommand)viewModel.ConnectCommand).ExecuteAsync();
        Assert.AreEqual(1, connection.ConnectCallCount);
        Assert.AreEqual(address, connection.LastServerUrl);
    }

    [TestMethod]
    public async Task RemoveRecentServerAsync_PendingThenSuccessfulRemovalPreservesCurrentInputAndSavedServer()
    {
        const string address = "http://current.local";
        var pending = new TaskCompletionSource();
        var settings = new TestAppSettingsService
        {
            LastServerBase = address,
            RecentServerBases = new[] { address, "http://other.local" },
            RemoveRecentServerBaseAsyncHandler = (_, _) => pending.Task
        };
        var viewModel = CreateViewModel(settingsService: settings);
        await viewModel.LoadLastServerBaseAsync(CancellationToken.None);
        var removing = viewModel.RemoveRecentServerAsync(address);

        Assert.IsTrue(viewModel.IsRemovingRecentServer);
        Assert.IsFalse(viewModel.CanConnect);
        Assert.IsTrue(viewModel.IsInputEnabled);
        Assert.IsFalse(viewModel.SelectRecentServerCommand.CanExecute(address));
        Assert.IsFalse(viewModel.RemoveRecentServerCommand.CanExecute(address));
        Assert.AreEqual(2, viewModel.RecentServerBases.Count);
        pending.SetResult();
        await removing;

        Assert.AreEqual(address, viewModel.ServerUrl);
        Assert.AreEqual(address, settings.LastServerBase);
        Assert.AreEqual(address, settings.LastRemovedServerBase);
        CollectionAssert.AreEqual(new[] { "http://other.local" }, viewModel.RecentServerBases.ToArray());
        Assert.IsFalse(viewModel.IsRemovingRecentServer);
        Assert.IsTrue(viewModel.CanConnect);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemoveRecentServerAsync_StorageFailureKeepsRecordAndAllowsRetry(bool accessDenied)
    {
        const string address = "http://recent.local";
        var settings = new TestAppSettingsService
        {
            RecentServerBases = new[] { address },
            RemoveRecentServerBaseAsyncHandler = (_, _) => throw (accessDenied
                ? new UnauthorizedAccessException("Write denied") : new IOException("Write failed"))
        };
        var viewModel = CreateViewModel(settingsService: settings);
        await viewModel.LoadLastServerBaseAsync(CancellationToken.None);
        viewModel.ServerUrl = address;
        await ((AsyncRelayCommand)viewModel.RemoveRecentServerCommand).ExecuteAsync(address);

        Assert.IsTrue(viewModel.HasRecentServersError);
        Assert.IsFalse(viewModel.IsRecentServersLoadErrorVisible);
        Assert.AreEqual(address, viewModel.RecentServerBases.Single());
        Assert.IsTrue(viewModel.RemoveRecentServerCommand.CanExecute(address));
        settings.RemoveRecentServerBaseAsyncHandler = null;
        await ((AsyncRelayCommand)viewModel.RemoveRecentServerCommand).ExecuteAsync(address);

        Assert.IsTrue(viewModel.IsRecentServersEmpty);
        Assert.IsFalse(viewModel.HasRecentServersError);
        Assert.AreEqual(address, viewModel.ServerUrl);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConnectCommand_SaveFailureStaysEditableAndRetryCanFinish(bool accessDenied)
    {
        var navigation = new NavigationService();
        var settings = new TestAppSettingsService
        {
            SaveLastServerBaseAsyncHandler = (_, _) => throw (accessDenied
                ? new UnauthorizedAccessException("Write denied") : new IOException("Write failed"))
        };
        var connection = new TestServerConnectionService
        {
            ConnectAsyncHandler = (_, _) => Task.FromResult(ServerConnectionResult.Success(
                new ServerConnectionInfo("http://recent.local:8096")))
        };
        var viewModel = CreateViewModel(navigation, connection, settings);
        viewModel.ServerUrl = "recent.local:8096";
        await ((AsyncRelayCommand)viewModel.ConnectCommand).ExecuteAsync();

        Assert.AreEqual(AppPage.ServerConnection, navigation.CurrentPage);
        StringAssert.Contains(viewModel.ErrorMessage!, "无法保存地址");
        Assert.IsFalse(viewModel.IsConnecting);
        Assert.IsTrue(viewModel.IsInputEnabled);
        Assert.IsTrue(viewModel.ConnectCommand.CanExecute(null));
        Assert.IsNull(settings.SavedLastServerBase);
        settings.SaveLastServerBaseAsyncHandler = null;
        await ((AsyncRelayCommand)viewModel.ConnectCommand).ExecuteAsync();

        Assert.AreEqual(AppPage.Login, navigation.CurrentPage);
        Assert.AreEqual("http://recent.local:8096", settings.RecentServerBases.Single());
        Assert.AreEqual(2, settings.SaveCallCount);
    }

    private static ServerConnectionViewModel CreateViewModel(
        NavigationService? navigationService = null,
        TestServerConnectionService? serverConnectionService = null,
        TestAppSettingsService? settingsService = null)
    {
        return new ServerConnectionViewModel(
            navigationService ?? new NavigationService(),
            serverConnectionService ?? new TestServerConnectionService(),
            settingsService ?? new TestAppSettingsService());
    }
}
