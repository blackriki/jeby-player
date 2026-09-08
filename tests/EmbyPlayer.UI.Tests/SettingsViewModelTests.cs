using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Library;
using EmbyPlayer.Core.Settings;
using EmbyPlayer.UI.Diagnostics;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class SettingsViewModelTests
{
    [TestMethod]
    public async Task DefaultSubtitles_SaveFailureRetryAndRestoreDefaultsKeepThePreferenceTransaction()
    {
        var context = CreateContext(PlayerPreferences.Default);
        await context.ViewModel.LoadAsync();
        context.ViewModel.DefaultSubtitlesEnabled = false;
        context.ViewModel.DefaultSubtitleLanguage = "eng";
        context.SettingsService.UpdatePlayerPreferencesAsyncHandler = (_, _) => throw new IOException("test disk failure");
        await ((AsyncRelayCommand)context.ViewModel.SaveCommand).ExecuteAsync();
        Assert.IsTrue(context.ViewModel.IsSaveFailed && context.ViewModel.IsDirty);
        Assert.IsFalse(context.ViewModel.DefaultSubtitlesEnabled);
        Assert.IsTrue(context.SettingsService.PlayerPreferences.DefaultSubtitlesEnabled);
        context.SettingsService.UpdatePlayerPreferencesAsyncHandler = null;
        await ((AsyncRelayCommand)context.ViewModel.RetrySaveCommand).ExecuteAsync();
        Assert.IsFalse(context.SettingsService.PlayerPreferences.DefaultSubtitlesEnabled);
        await context.ViewModel.LoadAsync();
        Assert.IsFalse(context.ViewModel.DefaultSubtitlesEnabled);
        Assert.AreEqual("eng", context.ViewModel.DefaultSubtitleLanguage);
        context.ViewModel.RestoreDefaultsCommand.Execute(null);
        context.ViewModel.ConfirmRestoreDefaultsCommand.Execute(null);
        Assert.IsTrue(context.ViewModel.DefaultSubtitlesEnabled && context.ViewModel.IsDirty);
        Assert.IsFalse(context.SettingsService.PlayerPreferences.DefaultSubtitlesEnabled);
    }

    [TestMethod]
    public async Task Shortcuts_EditSaveReloadAndRestoreDefaultsUseTheExistingPreferencesTransaction()
    {
        var context = CreateContext(PlayerPreferences.Default);
        await context.ViewModel.LoadAsync();
        Assert.IsTrue(context.ViewModel.Shortcuts.TryAssign(PlayerShortcutAction.ToggleMute, "Ctrl+K"));
        Assert.IsTrue(context.ViewModel.IsDirty);
        Assert.AreEqual("M", context.SettingsService.PlayerPreferences.EffectiveShortcuts.ToggleMute);
        await ((AsyncRelayCommand)context.ViewModel.SaveCommand).ExecuteAsync();
        Assert.AreEqual("Ctrl+K", context.SettingsService.PlayerPreferences.EffectiveShortcuts.ToggleMute);
        Assert.IsFalse(context.ViewModel.IsDirty);
        await context.ViewModel.LoadAsync();
        Assert.AreEqual("Ctrl+K", context.ViewModel.Shortcuts.Bindings.ToggleMute);
        context.ViewModel.Shortcuts.RestoreDefaultsCommand.Execute(null);
        Assert.IsTrue(context.ViewModel.IsDirty);
        Assert.AreEqual("Ctrl+K", context.SettingsService.PlayerPreferences.EffectiveShortcuts.ToggleMute);
        await ((AsyncRelayCommand)context.ViewModel.SaveCommand).ExecuteAsync();
        Assert.AreEqual(PlayerShortcutBindings.Default, context.SettingsService.PlayerPreferences.EffectiveShortcuts);
    }

    [TestMethod]
    public async Task Shortcuts_SaveFailureRetainsDraftAndRetryPersistsIt()
    {
        var context = CreateContext(PlayerPreferences.Default);
        await context.ViewModel.LoadAsync();
        context.ViewModel.Shortcuts.TryAssign(PlayerShortcutAction.NextAudioTrack, "Ctrl+Shift+A");
        context.SettingsService.UpdatePlayerPreferencesAsyncHandler = (_, _) => throw new IOException("test disk failure");
        await ((AsyncRelayCommand)context.ViewModel.SaveCommand).ExecuteAsync();
        Assert.IsTrue(context.ViewModel.IsSaveFailed);
        Assert.IsTrue(context.ViewModel.IsDirty);
        Assert.AreEqual("Ctrl+Shift+A", context.ViewModel.Shortcuts.Bindings.NextAudioTrack);
        Assert.AreEqual("A", context.SettingsService.PlayerPreferences.EffectiveShortcuts.NextAudioTrack);
        context.SettingsService.UpdatePlayerPreferencesAsyncHandler = null;
        await ((AsyncRelayCommand)context.ViewModel.RetrySaveCommand).ExecuteAsync();
        Assert.AreEqual("Ctrl+Shift+A", context.SettingsService.PlayerPreferences.EffectiveShortcuts.NextAudioTrack);
        Assert.IsFalse(context.ViewModel.IsDirty);
    }

    [TestMethod]
    public void NavigateHomeCommand_UsesExistingNavigationFlow()
    {
        var context = CreateContext(PlayerPreferences.Default);
        context.NavigationService.NavigateTo(AppPage.Settings);

        context.ViewModel.NavigateHomeCommand.Execute(null);

        Assert.AreEqual(AppPage.Home, context.NavigationService.CurrentPage);
    }

    [TestMethod]
    public async Task LoadAsync_ReadsRealPreferencesAndStartsClean()
    {
        var preferences = new PlayerPreferences(72, 15, 5, "zh-CN", "en", true, 46, false);
        var context = CreateContext(preferences);

        await context.ViewModel.LoadAsync();

        Assert.AreEqual(72, context.ViewModel.DefaultVolume);
        Assert.AreEqual(15, context.ViewModel.SeekSeconds);
        Assert.AreEqual(5, context.ViewModel.ControlsHideSeconds);
        Assert.AreEqual("zh-CN", context.ViewModel.DefaultSubtitleLanguage);
        Assert.AreEqual("en", context.ViewModel.DefaultAudioLanguage);
        Assert.IsTrue(context.ViewModel.RememberLastVolume);
        Assert.IsFalse(context.ViewModel.AutoPlayNextEpisode);
        Assert.IsFalse(context.ViewModel.IsDirty);
        Assert.AreEqual(preferences, context.ViewModel.SavedSnapshot);
    }

    [TestMethod]
    public async Task DraftChangeAndRevert_RecomputesDirtyState()
    {
        var context = CreateContext(PlayerPreferences.Default with { DefaultVolume = 74 });
        await context.ViewModel.LoadAsync();

        context.ViewModel.DefaultVolume = 52;
        Assert.IsTrue(context.ViewModel.IsDirty);

        context.ViewModel.DefaultVolume = 74;
        Assert.IsFalse(context.ViewModel.IsDirty);
    }

    [TestMethod]
    public async Task SaveCommand_SavesAllFieldsPreservesLatestLastVolumeAndUpdatesSnapshot()
    {
        var context = CreateContext(PlayerPreferences.Default with { LastVolume = 31 });
        await context.ViewModel.LoadAsync();
        context.SettingsService.PlayerPreferences = context.SettingsService.PlayerPreferences with { LastVolume = 63 };

        context.ViewModel.AutoPlayNextEpisode = false;
        context.ViewModel.RememberLastVolume = true;
        context.ViewModel.DefaultVolume = 67;
        context.ViewModel.SeekSeconds = 30;
        context.ViewModel.ControlsHideSeconds = 8;
        context.ViewModel.DefaultSubtitleLanguage = "zh-CN";
        context.ViewModel.DefaultAudioLanguage = "en";

        await ((AsyncRelayCommand)context.ViewModel.SaveCommand).ExecuteAsync();

        var saved = context.SettingsService.PlayerPreferences;
        Assert.AreEqual(67, saved.DefaultVolume);
        Assert.AreEqual(30, saved.SeekSeconds);
        Assert.AreEqual(8, saved.ControlsHideSeconds);
        Assert.AreEqual("zh-CN", saved.DefaultSubtitleLanguage);
        Assert.AreEqual("en", saved.DefaultAudioLanguage);
        Assert.IsTrue(saved.RememberLastVolume);
        Assert.AreEqual(63, saved.LastVolume);
        Assert.IsFalse(saved.AutoPlayNextEpisode);
        Assert.AreEqual(saved, context.ViewModel.SavedSnapshot);
        Assert.IsFalse(context.ViewModel.IsDirty);
    }

    [TestMethod]
    public async Task SaveCommand_AutomaticLanguagesPersistAsEmptyValues()
    {
        var context = CreateContext(PlayerPreferences.Default with
        {
            DefaultSubtitleLanguage = "en",
            DefaultAudioLanguage = "ja"
        });
        await context.ViewModel.LoadAsync();

        context.ViewModel.DefaultSubtitleLanguage = string.Empty;
        context.ViewModel.DefaultAudioLanguage = string.Empty;
        await ((AsyncRelayCommand)context.ViewModel.SaveCommand).ExecuteAsync();

        Assert.AreEqual(string.Empty, context.SettingsService.PlayerPreferences.DefaultSubtitleLanguage);
        Assert.AreEqual(string.Empty, context.SettingsService.PlayerPreferences.DefaultAudioLanguage);
    }

    [TestMethod]
    public async Task SaveFailure_KeepsDraftDirtyAndAllowsRetry()
    {
        var context = CreateContext(PlayerPreferences.Default);
        await context.ViewModel.LoadAsync();
        context.ViewModel.DefaultVolume = 64;
        context.SettingsService.UpdatePlayerPreferencesAsyncHandler = (_, _) =>
            throw new IOException("disk failure");

        await ((AsyncRelayCommand)context.ViewModel.SaveCommand).ExecuteAsync();

        Assert.AreEqual(64, context.ViewModel.DefaultVolume);
        Assert.IsTrue(context.ViewModel.IsDirty);
        Assert.IsTrue(context.ViewModel.IsSaveFailed);
        Assert.AreEqual("设置保存失败，请稍后重试。", context.ViewModel.SaveErrorMessage);
        Assert.IsTrue(context.ViewModel.RetrySaveCommand.CanExecute(null));
        Assert.IsFalse(context.ViewModel.SaveErrorMessage!.Contains("disk failure", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SaveCommand_DoesNotRunConcurrently()
    {
        var context = CreateContext(PlayerPreferences.Default);
        await context.ViewModel.LoadAsync();
        context.ViewModel.DefaultVolume = 64;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.SettingsService.UpdatePlayerPreferencesAsyncHandler = async (update, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return update(context.SettingsService.PlayerPreferences);
        };

        var firstSave = ((AsyncRelayCommand)context.ViewModel.SaveCommand).ExecuteAsync();
        await entered.Task;
        await ((AsyncRelayCommand)context.ViewModel.SaveCommand).ExecuteAsync();

        Assert.AreEqual(1, context.SettingsService.UpdatePlayerPreferencesCallCount);
        release.TrySetResult();
        await firstSave;
    }

    [TestMethod]
    public async Task RestoreDefaults_ChangesDraftOnlyAndRequiresSave()
    {
        var context = CreateContext(PlayerPreferences.Default with { DefaultVolume = 58, SeekSeconds = 30 });
        await context.ViewModel.LoadAsync();

        context.ViewModel.RestoreDefaultsCommand.Execute(null);
        Assert.IsTrue(context.ViewModel.IsRestoreDefaultsDialogOpen);

        context.ViewModel.ConfirmRestoreDefaultsCommand.Execute(null);

        Assert.AreEqual(PlayerPreferences.Default.DefaultVolume, context.ViewModel.DefaultVolume);
        Assert.AreEqual(PlayerPreferences.Default.SeekSeconds, context.ViewModel.SeekSeconds);
        Assert.IsTrue(context.ViewModel.IsDirty);
        Assert.AreEqual(0, context.SettingsService.UpdatePlayerPreferencesCallCount);
        Assert.AreEqual(58, context.SettingsService.PlayerPreferences.DefaultVolume);
    }

    [TestMethod]
    public async Task DirtyNavigation_IsBlockedUntilContinueDiscardOrSave()
    {
        var context = CreateContext(PlayerPreferences.Default);
        context.NavigationService.NavigateTo(AppPage.Settings);
        await context.ViewModel.LoadAsync();
        context.ViewModel.DefaultVolume = 64;

        context.NavigationService.NavigateTo(AppPage.Home);

        Assert.AreEqual(AppPage.Settings, context.NavigationService.CurrentPage);
        Assert.IsTrue(context.ViewModel.IsUnsavedLeaveDialogOpen);

        context.ViewModel.ContinueEditingCommand.Execute(null);
        Assert.AreEqual(AppPage.Settings, context.NavigationService.CurrentPage);
        Assert.IsTrue(context.ViewModel.IsDirty);

        context.NavigationService.NavigateTo(AppPage.Home);
        context.ViewModel.DiscardChangesCommand.Execute(null);
        Assert.AreEqual(AppPage.Home, context.NavigationService.CurrentPage);
        Assert.IsFalse(context.ViewModel.IsDirty);
        Assert.AreEqual(PlayerPreferences.Default.DefaultVolume, context.ViewModel.DefaultVolume);
    }

    [TestMethod]
    public async Task SaveAndLeave_NavigatesOnlyAfterSuccessfulSave()
    {
        var context = CreateContext(PlayerPreferences.Default);
        context.NavigationService.NavigateTo(AppPage.Settings);
        await context.ViewModel.LoadAsync();
        context.ViewModel.DefaultVolume = 64;
        context.NavigationService.NavigateTo(AppPage.Search, "query");

        await ((AsyncRelayCommand)context.ViewModel.SaveAndLeaveCommand).ExecuteAsync();

        Assert.AreEqual(AppPage.Search, context.NavigationService.CurrentPage);
        Assert.IsFalse(context.ViewModel.IsDirty);
        Assert.AreEqual(64, context.SettingsService.PlayerPreferences.DefaultVolume);
    }

    [TestMethod]
    public async Task SaveAndLeave_FailureStaysOnSettingsAndKeepsDraft()
    {
        var context = CreateContext(PlayerPreferences.Default);
        context.NavigationService.NavigateTo(AppPage.Settings);
        await context.ViewModel.LoadAsync();
        context.ViewModel.DefaultVolume = 64;
        context.SettingsService.UpdatePlayerPreferencesAsyncHandler = (_, _) =>
            throw new IOException("disk failure");
        context.NavigationService.NavigateTo(AppPage.Home);

        await ((AsyncRelayCommand)context.ViewModel.SaveAndLeaveCommand).ExecuteAsync();

        Assert.AreEqual(AppPage.Settings, context.NavigationService.CurrentPage);
        Assert.AreEqual(64, context.ViewModel.DefaultVolume);
        Assert.IsTrue(context.ViewModel.IsDirty);
        Assert.IsTrue(context.ViewModel.IsUnsavedLeaveDialogOpen);
    }

    [TestMethod]
    public async Task RequestWindowClose_WithDirtyDraftUsesSameLeaveGuard()
    {
        var context = CreateContext(PlayerPreferences.Default);
        context.NavigationService.NavigateTo(AppPage.Settings);
        await context.ViewModel.LoadAsync();
        context.ViewModel.DefaultVolume = 64;
        var closeApproved = false;
        context.ViewModel.WindowCloseApproved += (_, _) => closeApproved = true;

        Assert.IsFalse(context.ViewModel.RequestWindowClose());
        Assert.IsTrue(context.ViewModel.IsUnsavedLeaveDialogOpen);

        context.ViewModel.DiscardChangesCommand.Execute(null);
        Assert.IsTrue(closeApproved);
    }

    [TestMethod]
    public async Task DraftValues_AreClampedToExistingPreferenceRanges()
    {
        var context = CreateContext(PlayerPreferences.Default);
        await context.ViewModel.LoadAsync();

        context.ViewModel.DefaultVolume = 120;
        context.ViewModel.SeekSeconds = -5;
        context.ViewModel.ControlsHideSeconds = 99;

        Assert.AreEqual(100, context.ViewModel.DefaultVolume);
        Assert.AreEqual(5, context.ViewModel.SeekSeconds);
        Assert.AreEqual(10, context.ViewModel.ControlsHideSeconds);
    }

    [TestMethod]
    public void NavigationService_CancelledNavigationDoesNotRaiseChangedEvent()
    {
        var navigationService = new NavigationService();
        var changedCount = 0;
        navigationService.Navigating += (_, args) => args.Cancel = true;
        navigationService.CurrentPageChanged += (_, _) => changedCount++;

        navigationService.NavigateTo(AppPage.Home);

        Assert.AreEqual(AppPage.ServerConnection, navigationService.CurrentPage);
        Assert.AreEqual(0, changedCount);
    }

    [TestMethod]
    public async Task ScanMediaLibraryCommand_SubmitsCurrentSessionAndShowsSuccess()
    {
        var context = CreateContext(PlayerPreferences.Default);

        await ((AsyncRelayCommand)context.ViewModel.ScanMediaLibraryCommand).ExecuteAsync();

        Assert.AreEqual(1, context.MediaLibraryScanService.RequestCallCount);
        Assert.AreSame(context.SessionService.CurrentSession, context.MediaLibraryScanService.LastSession);
        Assert.AreEqual("扫描请求已提交，服务器将在后台扫描媒体文件。", context.ViewModel.LibraryScanMessage);
        Assert.IsFalse(context.ViewModel.IsLibraryScanRunning);
    }

    [TestMethod]
    public async Task ScanMediaLibraryCommand_DoesNotRunConcurrently()
    {
        var context = CreateContext(PlayerPreferences.Default);
        var pending = new TaskCompletionSource<MediaLibraryScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.MediaLibraryScanService.RequestScanAsyncHandler = (_, _) => pending.Task;

        var first = ((AsyncRelayCommand)context.ViewModel.ScanMediaLibraryCommand).ExecuteAsync();
        await WaitForAsync(() => context.ViewModel.IsLibraryScanRunning);
        await ((AsyncRelayCommand)context.ViewModel.ScanMediaLibraryCommand).ExecuteAsync();

        Assert.AreEqual(1, context.MediaLibraryScanService.RequestCallCount);
        pending.SetResult(MediaLibraryScanResult.Success());
        await first;
    }

    [TestMethod]
    public async Task ScanMediaLibraryCommand_ForbiddenShowsAdministratorMessage()
    {
        var context = CreateContext(PlayerPreferences.Default);
        context.MediaLibraryScanService.RequestScanAsyncHandler = (_, _) =>
            Task.FromResult(MediaLibraryScanResult.Failure(MediaLibraryScanResult.FailureReason.Forbidden));

        await ((AsyncRelayCommand)context.ViewModel.ScanMediaLibraryCommand).ExecuteAsync();

        Assert.AreEqual("需要 Emby 服务器管理员权限才能扫描媒体库。", context.ViewModel.LibraryScanMessage);
    }

    [TestMethod]
    public async Task ScanMediaLibraryCommand_UnauthorizedClearsSessionAndNavigatesToLogin()
    {
        var context = CreateContext(PlayerPreferences.Default);
        context.MediaLibraryScanService.RequestScanAsyncHandler = (_, _) =>
            Task.FromResult(MediaLibraryScanResult.Failure(MediaLibraryScanResult.FailureReason.Unauthorized));

        await ((AsyncRelayCommand)context.ViewModel.ScanMediaLibraryCommand).ExecuteAsync();

        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.IsNull(context.SessionService.CurrentSession);
        Assert.AreEqual(AppPage.Login, context.NavigationService.CurrentPage);
        CollectionAssert.Contains(context.LoginErrors, "登录状态已失效，请重新登录");
    }

    [TestMethod]
    public async Task ScanMediaLibraryCommand_UnauthorizedBypassesDirtyAccountDialogWhenPersistentClearFails()
    {
        var context = CreateContext(PlayerPreferences.Default);
        context.NavigationService.NavigateTo(AppPage.Settings);
        await context.ViewModel.LoadAsync();
        context.ViewModel.DefaultVolume = 64;
        var pending = new TaskCompletionSource<MediaLibraryScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.MediaLibraryScanService.RequestScanAsyncHandler = (_, _) => pending.Task;
        context.AuthSessionStore.ClearAsyncHandler = _ => throw new IOException("credential cleanup failed");

        var scanTask = ((AsyncRelayCommand)context.ViewModel.ScanMediaLibraryCommand).ExecuteAsync();
        await WaitForAsync(() => context.ViewModel.IsLibraryScanRunning);
        context.ViewModel.RequestLogoutCommand.Execute(null);
        Assert.IsTrue(context.ViewModel.IsLogoutDialogOpen);

        pending.SetResult(
            MediaLibraryScanResult.Failure(MediaLibraryScanResult.FailureReason.Unauthorized));
        await scanTask;

        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.IsNull(context.SessionService.CurrentSession);
        Assert.AreEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.IsFalse(context.ViewModel.IsAccountActionDialogOpen);
        Assert.IsTrue(context.ViewModel.IsDirty);
        Assert.IsFalse(context.ViewModel.IsLibraryScanRunning);
        CollectionAssert.Contains(context.LoginErrors, "登录状态已失效，请重新登录");
    }

    [TestMethod]
    public async Task AccountActionRequest_RequiresConfirmationAndCancelHasNoSideEffects()
    {
        var context = CreateContext(PlayerPreferences.Default);
        context.NavigationService.NavigateTo(AppPage.Settings);
        await context.ViewModel.LoadAsync();

        context.ViewModel.RequestLogoutCommand.Execute(null);

        Assert.IsTrue(context.ViewModel.IsLogoutDialogOpen);
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.IsNotNull(context.SessionService.CurrentSession);

        context.ViewModel.CancelDialogCommand.Execute(null);

        Assert.IsFalse(context.ViewModel.IsAccountActionDialogOpen);
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreEqual(AppPage.Settings, context.NavigationService.CurrentPage);
    }

    [TestMethod]
    public async Task ConfirmLogout_WithDirtyDraftDiscardsDraftPreservesServerAndNavigatesToLogin()
    {
        var context = CreateContext(PlayerPreferences.Default);
        context.NavigationService.NavigateTo(AppPage.Settings);
        await context.ViewModel.LoadAsync();
        context.ViewModel.DefaultVolume = 64;

        context.ViewModel.RequestLogoutCommand.Execute(null);

        StringAssert.Contains(context.ViewModel.DialogDetail, "未保存的设置更改会被放弃");
        await ((AsyncRelayCommand)context.ViewModel.ConfirmAccountActionCommand).ExecuteAsync();

        Assert.AreEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.AreEqual(0, context.SettingsService.ClearLastServerBaseCallCount);
        Assert.AreEqual("http://media.local:8096", context.SettingsService.LastServerBase);
        Assert.IsNull(context.SessionService.CurrentSession);
        Assert.IsFalse(context.ViewModel.IsDirty);
        Assert.AreEqual(PlayerPreferences.Default.DefaultVolume, context.ViewModel.DefaultVolume);
    }

    [TestMethod]
    public async Task ConfirmSwitchServer_ClearsServerAndNavigatesToConnection()
    {
        var context = CreateContext(PlayerPreferences.Default);
        context.NavigationService.NavigateTo(AppPage.Settings);
        await context.ViewModel.LoadAsync();

        context.ViewModel.RequestSwitchServerCommand.Execute(null);
        await ((AsyncRelayCommand)context.ViewModel.ConfirmAccountActionCommand).ExecuteAsync();

        Assert.AreEqual(AppPage.ServerConnection, context.NavigationService.CurrentPage);
        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.AreEqual(1, context.SettingsService.ClearLastServerBaseCallCount);
        Assert.IsNull(context.SettingsService.LastServerBase);
        Assert.IsNull(context.SessionService.CurrentSession);
    }

    [TestMethod]
    public async Task ConfirmSwitchServer_AddressClearFailureNavigatesWithRetainedAddressWarning()
    {
        var context = CreateContext(PlayerPreferences.Default);
        context.NavigationService.NavigateTo(AppPage.Settings);
        await context.ViewModel.LoadAsync();
        context.ViewModel.DefaultVolume = 64;
        context.SettingsService.ClearLastServerBaseAsyncHandler = _ =>
            throw new IOException("settings failure");
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;

        context.ViewModel.RequestSwitchServerCommand.Execute(null);
        await ((AsyncRelayCommand)context.ViewModel.ConfirmAccountActionCommand).ExecuteAsync();

        Assert.AreEqual(AppPage.ServerConnection, context.NavigationService.CurrentPage);
        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.AreEqual(1, context.SettingsService.ClearLastServerBaseCallCount);
        Assert.IsNull(context.SessionService.CurrentSession);
        Assert.AreEqual("http://media.local:8096", context.SettingsService.LastServerBase);
        Assert.IsFalse(context.ViewModel.IsDirty);
        Assert.IsFalse(context.ViewModel.IsAccountActionDialogOpen);
        var parameter = navigationParameter as ServerConnectionNavigationParameter;
        Assert.IsNotNull(parameter);
        Assert.AreEqual(
            ServerConnectionNavigationParameter.SwitchServerPartialFailureMessage,
            parameter.Message);
        Assert.IsNull(context.ViewModel.AccountActionErrorMessage);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ConfirmSwitchServer_InvalidatesLateMediaScanResult(bool scanSucceeds)
    {
        var context = CreateContext(PlayerPreferences.Default);
        context.NavigationService.NavigateTo(AppPage.Settings);
        await context.ViewModel.LoadAsync();
        var pending = new TaskCompletionSource<MediaLibraryScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.MediaLibraryScanService.RequestScanAsyncHandler = (_, _) => pending.Task;

        var scanTask = ((AsyncRelayCommand)context.ViewModel.ScanMediaLibraryCommand).ExecuteAsync();
        await WaitForAsync(() => context.ViewModel.IsLibraryScanRunning);
        context.ViewModel.RequestSwitchServerCommand.Execute(null);
        await ((AsyncRelayCommand)context.ViewModel.ConfirmAccountActionCommand).ExecuteAsync();

        pending.SetResult(scanSucceeds
            ? MediaLibraryScanResult.Success()
            : MediaLibraryScanResult.Failure(MediaLibraryScanResult.FailureReason.Unauthorized));
        await scanTask;

        Assert.AreEqual(AppPage.ServerConnection, context.NavigationService.CurrentPage);
        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.IsNull(context.ViewModel.LibraryScanMessage);
        Assert.IsFalse(context.ViewModel.IsLibraryScanRunning);
        Assert.AreEqual(0, context.LoginErrors.Count);
    }

    [TestMethod]
    public async Task ConfirmAccountAction_FailureStaysOnSettingsAndKeepsDraft()
    {
        var context = CreateContext(PlayerPreferences.Default);
        context.NavigationService.NavigateTo(AppPage.Settings);
        await context.ViewModel.LoadAsync();
        context.ViewModel.DefaultVolume = 64;
        context.AuthSessionStore.ClearAsyncHandler = _ => throw new IOException("credential failure");

        context.ViewModel.RequestLogoutCommand.Execute(null);
        await ((AsyncRelayCommand)context.ViewModel.ConfirmAccountActionCommand).ExecuteAsync();

        Assert.AreEqual(AppPage.Settings, context.NavigationService.CurrentPage);
        Assert.IsTrue(context.ViewModel.IsAccountActionDialogOpen);
        Assert.AreEqual(64, context.ViewModel.DefaultVolume);
        Assert.IsTrue(context.ViewModel.IsDirty);
        Assert.AreEqual("退出登录失败，请稍后重试。", context.ViewModel.AccountActionErrorMessage);
        Assert.IsFalse(context.ViewModel.AccountActionErrorMessage!.Contains("credential failure", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ConfirmLogout_PartialAuthenticationCleanupNavigatesToLoginWithWarning()
    {
        var context = CreateContext(PlayerPreferences.Default);
        context.NavigationService.NavigateTo(AppPage.Settings);
        await context.ViewModel.LoadAsync();
        context.ViewModel.DefaultVolume = 64;
        context.AuthSessionStore.ClearAsyncHandler = _ =>
            throw new AuthSessionClearPartialFailureException(new IOException("metadata failure"));

        context.ViewModel.RequestLogoutCommand.Execute(null);
        await ((AsyncRelayCommand)context.ViewModel.ConfirmAccountActionCommand).ExecuteAsync();

        Assert.AreEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.IsNull(context.SessionService.CurrentSession);
        Assert.IsFalse(context.ViewModel.IsAccountActionDialogOpen);
        Assert.IsFalse(context.ViewModel.IsDirty);
        CollectionAssert.Contains(
            context.LoginErrors,
            "安全令牌已清除，但本地账户信息未能完全清理，请稍后重试。");
    }

    [TestMethod]
    public async Task ConfirmAccountAction_DoesNotRunConcurrently()
    {
        var context = CreateContext(PlayerPreferences.Default);
        context.NavigationService.NavigateTo(AppPage.Settings);
        await context.ViewModel.LoadAsync();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.AuthSessionStore.ClearAsyncHandler = _ => pending.Task;
        context.ViewModel.RequestLogoutCommand.Execute(null);

        var first = ((AsyncRelayCommand)context.ViewModel.ConfirmAccountActionCommand).ExecuteAsync();
        await WaitForAsync(() => context.ViewModel.IsAccountActionRunning);
        await ((AsyncRelayCommand)context.ViewModel.ConfirmAccountActionCommand).ExecuteAsync();

        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.IsFalse(context.ViewModel.IsDialogCancelEnabled);
        pending.SetResult();
        await first;
    }

    [TestMethod]
    public async Task ExportDiagnosticLogs_WhenDestinationSelectionIsCancelled_DoesNotExport()
    {
        var context = CreateContext(PlayerPreferences.Default);
        context.DiagnosticExportDestinationPicker.DestinationPath = null;

        await ((AsyncRelayCommand)context.ViewModel.ExportDiagnosticLogsCommand).ExecuteAsync();

        Assert.AreEqual(1, context.DiagnosticExportDestinationPicker.CallCount);
        Assert.AreEqual(0, context.DiagnosticExportService.CallCount);
        Assert.AreEqual("已取消导出。", context.ViewModel.DiagnosticExportMessage);
        Assert.IsFalse(context.ViewModel.IsDiagnosticExportRunning);
    }

    [TestMethod]
    public async Task ExportDiagnosticLogs_WhenNoLogsExist_ShowsManifestOnlyResult()
    {
        var context = CreateContext(PlayerPreferences.Default);
        context.DiagnosticExportDestinationPicker.DestinationPath = "diagnostics.zip";
        context.DiagnosticExportService.ExportAsyncHandler = (_, _) =>
            Task.FromResult(new DiagnosticExportResult(Array.Empty<string>()));

        await ((AsyncRelayCommand)context.ViewModel.ExportDiagnosticLogsCommand).ExecuteAsync();

        Assert.AreEqual("diagnostics.zip", context.DiagnosticExportService.LastDestinationPath);
        Assert.AreEqual(
            "诊断包已导出；当前没有可用日志，包内仅包含说明文件。",
            context.ViewModel.DiagnosticExportMessage);
    }

    [TestMethod]
    public async Task ExportDiagnosticLogs_WhileRunning_DisablesCommandAndReportsSuccess()
    {
        var context = CreateContext(PlayerPreferences.Default);
        context.DiagnosticExportDestinationPicker.DestinationPath = "diagnostics.zip";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<DiagnosticExportResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.DiagnosticExportService.ExportAsyncHandler = (_, _) =>
        {
            entered.TrySetResult();
            return completion.Task;
        };

        var export = ((AsyncRelayCommand)context.ViewModel.ExportDiagnosticLogsCommand).ExecuteAsync();
        await entered.Task;

        Assert.IsTrue(context.ViewModel.IsDiagnosticExportRunning);
        Assert.AreEqual("正在导出…", context.ViewModel.DiagnosticExportButtonText);
        Assert.IsFalse(context.ViewModel.ExportDiagnosticLogsCommand.CanExecute(null));

        completion.SetResult(new DiagnosticExportResult(new[] { "mpv.log", "playback.log" }));
        await export;

        Assert.IsFalse(context.ViewModel.IsDiagnosticExportRunning);
        Assert.AreEqual("导出日志", context.ViewModel.DiagnosticExportButtonText);
        Assert.IsTrue(context.ViewModel.ExportDiagnosticLogsCommand.CanExecute(null));
        Assert.AreEqual("诊断包已导出，共包含 2 个日志文件。", context.ViewModel.DiagnosticExportMessage);
    }

    [TestMethod]
    public async Task ExportDiagnosticLogs_WhenIoFails_ShowsSafeRetryableMessage()
    {
        var context = CreateContext(PlayerPreferences.Default);
        context.DiagnosticExportDestinationPicker.DestinationPath = "diagnostics.zip";
        context.DiagnosticExportService.ExportAsyncHandler = (_, _) =>
            throw new IOException("C:\\Users\\person\\secret-path");

        await ((AsyncRelayCommand)context.ViewModel.ExportDiagnosticLogsCommand).ExecuteAsync();

        Assert.AreEqual(
            "导出失败，请确认目标文件未被占用并且有写入权限。",
            context.ViewModel.DiagnosticExportMessage);
        Assert.IsFalse(context.ViewModel.DiagnosticExportMessage!.Contains("person", StringComparison.Ordinal));
        Assert.IsFalse(context.ViewModel.IsDiagnosticExportRunning);
    }

    private static SettingsTestContext CreateContext(PlayerPreferences preferences)
    {
        var navigationService = new NavigationService();
        var settingsService = new TestAppSettingsService
        {
            LastServerBase = "http://media.local:8096",
            DeviceId = "stable-device-id",
            PlayerPreferences = preferences
        };
        var sessionService = new CurrentSessionService();
        var mediaLibraryScanService = new TestMediaLibraryScanService();
        var authSessionStore = new TestAuthSessionStore();
        var diagnosticExportService = new TestDiagnosticExportService();
        var diagnosticExportDestinationPicker = new TestDiagnosticExportDestinationPicker();
        var loginErrors = new List<string>();
        sessionService.SetSession(new AuthSession(
            "http://media.local:8096",
            "test-token",
            "user-1",
            "Riki",
            "server-1"));
        var accountSessionService = new AccountSessionService(
            authSessionStore,
            sessionService,
            settingsService);
        var viewModel = new SettingsViewModel(
            navigationService,
            settingsService,
            sessionService,
            mediaLibraryScanService,
            authSessionStore,
            accountSessionService,
            loginErrors.Add,
            diagnosticExportService,
            diagnosticExportDestinationPicker);
        return new SettingsTestContext(
            navigationService,
            settingsService,
            sessionService,
            mediaLibraryScanService,
            authSessionStore,
            loginErrors,
            diagnosticExportService,
            diagnosticExportDestinationPicker,
            viewModel);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            if (timeout.IsCancellationRequested)
            {
                Assert.Fail("Timed out waiting for asynchronous operation.");
            }

            await Task.Delay(10);
        }
    }

    private sealed record SettingsTestContext(
        NavigationService NavigationService,
        TestAppSettingsService SettingsService,
        CurrentSessionService SessionService,
        TestMediaLibraryScanService MediaLibraryScanService,
        TestAuthSessionStore AuthSessionStore,
        List<string> LoginErrors,
        TestDiagnosticExportService DiagnosticExportService,
        TestDiagnosticExportDestinationPicker DiagnosticExportDestinationPicker,
        SettingsViewModel ViewModel);

    private sealed class TestDiagnosticExportService : IDiagnosticExportService
    {
        public Func<string, CancellationToken, Task<DiagnosticExportResult>>? ExportAsyncHandler { get; set; }

        public int CallCount { get; private set; }

        public string? LastDestinationPath { get; private set; }

        public Task<DiagnosticExportResult> ExportAsync(
            string destinationPath,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastDestinationPath = destinationPath;
            return ExportAsyncHandler?.Invoke(destinationPath, cancellationToken)
                ?? Task.FromResult(new DiagnosticExportResult(new[] { "mpv.log" }));
        }
    }

    private sealed class TestDiagnosticExportDestinationPicker : IDiagnosticExportDestinationPicker
    {
        public string? DestinationPath { get; set; } = "diagnostics.zip";

        public int CallCount { get; private set; }

        public string? PickDestinationPath()
        {
            CallCount++;
            return DestinationPath;
        }
    }
}
