using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Playback;
using EmbyPlayer.Core.Series;
using EmbyPlayer.Core.Settings;
using EmbyPlayer.Player;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.Pages;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows.Input;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed partial class PlayerViewModelTests
{
    [TestMethod]
    public async Task Load_WithVideoHost_CallsPlayerServiceLoad()
    {
        var context = CreateContext();
        var parameter = CreateNavigationParameter(CreatePlaybackInfo());

        context.ViewModel.Load(parameter);
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));

        Assert.AreEqual(1, context.PlayerService.LoadCallCount);
        Assert.AreEqual("item-1", context.PlayerService.LastLoadRequest!.PlaybackInfo.ItemId);
        Assert.AreEqual(new IntPtr(1234), context.PlayerService.LastLoadRequest.VideoHostHandle);
        Assert.IsFalse(context.ViewModel.IsPlaying);
        Assert.IsTrue(context.ViewModel.IsLoading);
        Assert.AreEqual("正在加载", context.ViewModel.StatusText);
    }

    [TestMethod]
    public void Load_ExposesNavigationLogo()
    {
        var context = CreateContext();
        var logoUrl = "http://media.local:8096/Items/item-1/Images/Logo";

        context.ViewModel.Load(new PlayerNavigationParameter(
            CreatePlaybackInfo(),
            new DetailNavigationParameter("item-1", AppPage.Home),
            logoUrl));

        Assert.AreEqual(logoUrl, context.ViewModel.LogoUrl);
    }

    [TestMethod]
    public void Load_ReplayingSameItemRetainsNavigationLogo()
    {
        var context = CreateContext();
        var logoUrl = "http://media.local:8096/Items/item-1/Images/Logo";
        var parameter = new PlayerNavigationParameter(
            CreatePlaybackInfo(),
            new DetailNavigationParameter("item-1", AppPage.Home),
            logoUrl);

        context.ViewModel.Load(parameter);
        context.ViewModel.Load(parameter);

        Assert.AreEqual(logoUrl, context.ViewModel.LogoUrl);
    }

    [TestMethod]
    public async Task Load_WithResumePosition_PassesStartPositionTicksToPlayerService()
    {
        var context = CreateContext();
        var startPositionTicks = TimeSpan.FromMinutes(12).Ticks;

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo(startPositionTicks: startPositionTicks)));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));

        Assert.AreEqual(startPositionTicks, context.PlayerService.LastLoadRequest!.PlaybackInfo.StartPositionTicks);
    }

    [TestMethod]
    public async Task Load_NewMediaClearsOldVideoHostAndWaitsForNewAttach()
    {
        var context = CreateContext();

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1111));
        Assert.AreEqual(1, context.PlayerService.LoadCallCount);

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await Task.Yield();

        Assert.AreEqual(1, context.PlayerService.LoadCallCount);

        await context.ViewModel.AttachVideoHostAsync(new IntPtr(2222));

        Assert.AreEqual(2, context.PlayerService.LoadCallCount);
        Assert.AreEqual(new IntPtr(2222), context.PlayerService.LastLoadRequest!.VideoHostHandle);
        Assert.AreNotEqual(0, context.PlayerService.LastLoadRequest.PlaybackInstanceId);
    }

    [TestMethod]
    public async Task Load_SecondMediaStopsPreviousPlaybackBeforeLoadingNewHost()
    {
        var context = CreateContext();

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1111));
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(2222));

        var lastLoadIndex = context.PlayerService.Calls.FindLastIndex(call => call.StartsWith("load:", StringComparison.Ordinal));
        var previousStopIndex = context.PlayerService.Calls.FindLastIndex(lastLoadIndex - 1, call => call == "stop");
        Assert.IsTrue(previousStopIndex >= 0);
        Assert.IsTrue(previousStopIndex < lastLoadIndex);
    }

    [TestMethod]
    public async Task StatusChanged_FromOldPlaybackInstance_DoesNotUpdateCurrentUi()
    {
        var context = CreateContext();

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1111));
        var firstInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(2222));
        var secondInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;

        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            firstInstanceId,
            PlayerPlaybackState.Playing));

        Assert.IsFalse(context.ViewModel.IsPlaying);
        Assert.IsTrue(context.ViewModel.IsLoading);

        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            secondInstanceId,
            PlayerPlaybackState.Playing));

        Assert.IsTrue(context.ViewModel.IsPlaying);
        Assert.IsFalse(context.ViewModel.IsLoading);
    }

    [TestMethod]
    public async Task ProgressChanged_UpdatesTimeAndDurationText()
    {
        var context = CreateContext();

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
            TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(35),
            TimeSpan.FromHours(1) + TimeSpan.FromMinutes(45) + TimeSpan.FromSeconds(20),
            null,
            false));

        Assert.AreEqual("12:35", context.ViewModel.CurrentTimeText);
        Assert.AreEqual("01:45:20", context.ViewModel.DurationText);
    }

    [TestMethod]
    public async Task ProgressChanged_CalculatesProgressPercentAndClampsValue()
    {
        var context = CreateContext();

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo(runTimeTicks: 0)));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
            TimeSpan.FromSeconds(90),
            TimeSpan.FromSeconds(100),
            null,
            false));

        Assert.AreEqual(90, context.ViewModel.ProgressPercent, 0.01);

        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            context.PlayerService.LastLoadRequest.PlaybackInstanceId,
            TimeSpan.FromSeconds(200),
            TimeSpan.FromSeconds(100),
            150,
            false));

        Assert.AreEqual(100, context.ViewModel.ProgressPercent, 0.01);
    }

    [TestMethod]
    public async Task ProgressChanged_WithUnknownDuration_DoesNotCrash()
    {
        var context = CreateContext();

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo(runTimeTicks: 0)));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
            null,
            TimeSpan.Zero,
            double.NaN,
            false));

        Assert.AreEqual("00:00", context.ViewModel.CurrentTimeText);
        Assert.AreEqual("--:--", context.ViewModel.DurationText);
        Assert.AreEqual(0, context.ViewModel.ProgressPercent, 0.01);
    }

    [TestMethod]
    public async Task ProgressChanged_FromOldPlaybackInstance_DoesNotUpdateCurrentUi()
    {
        var context = CreateContext();

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1111));
        var firstInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(2222));

        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            firstInstanceId,
            TimeSpan.FromMinutes(42),
            TimeSpan.FromHours(2),
            35,
            false));

        Assert.AreEqual("00:00", context.ViewModel.CurrentTimeText);
        Assert.AreNotEqual(35, context.ViewModel.ProgressPercent);
    }

    [TestMethod]
    public async Task SeekDrag_DoesNotCallSeekUntilCompleted()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);

        context.ViewModel.BeginSeekDrag();
        context.ViewModel.UpdateSeekDrag(50);

        Assert.AreEqual(0, context.PlayerService.SeekCallCount);
        Assert.AreEqual("45:00", context.ViewModel.CurrentTimeText);
    }

    [TestMethod]
    public async Task SeekDrag_CompletedCallsSeekOnceWithCalculatedTarget()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);

        context.ViewModel.BeginSeekDrag();
        context.ViewModel.UpdateSeekDrag(50);
        await context.ViewModel.CompleteSeekDragAsync(50);

        Assert.AreEqual(1, context.PlayerService.SeekCallCount);
        Assert.AreEqual(TimeSpan.FromMinutes(45), context.PlayerService.LastSeekPosition);
        Assert.AreEqual(context.PlayerService.LastLoadRequest!.PlaybackInstanceId, context.PlayerService.LastSeekPlaybackInstanceId);
    }

    [TestMethod]
    public async Task SeekDrag_CanceledRestoresReportedProgressWithoutSeeking()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var reportedProgress = context.ViewModel.ProgressPercent;

        context.ViewModel.BeginSeekDrag();
        context.ViewModel.UpdateSeekDrag(70);
        context.ViewModel.CancelSeekDrag();

        Assert.AreEqual(reportedProgress, context.ViewModel.SeekPercent, 0.01);
        Assert.AreEqual(0, context.PlayerService.SeekCallCount);
        Assert.IsFalse(context.ViewModel.IsSeeking);
    }

    [TestMethod]
    public async Task SeekDrag_ProgressChangedWhileDragging_DoesNotOverwriteUserValue()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;

        context.ViewModel.BeginSeekDrag();
        context.ViewModel.UpdateSeekDrag(70);
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(90),
            5,
            false));

        Assert.AreEqual(70, context.ViewModel.SeekPercent, 0.01);
        Assert.AreEqual("01:03:00", context.ViewModel.CurrentTimeText);
    }

    [TestMethod]
    public async Task SeekDrag_ClampsTargetPercent()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);

        context.ViewModel.BeginSeekDrag();
        await context.ViewModel.CompleteSeekDragAsync(-20);

        Assert.AreEqual(TimeSpan.Zero, context.PlayerService.LastSeekPosition);

        context.ViewModel.BeginSeekDrag();
        await context.ViewModel.CompleteSeekDragAsync(140);

        Assert.AreEqual(TimeSpan.FromMinutes(90), context.PlayerService.LastSeekPosition);
    }

    [TestMethod]
    public async Task SeekDrag_UnknownDurationDisablesSeek()
    {
        var context = CreateContext();
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo(runTimeTicks: 0)));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context);

        Assert.IsFalse(context.ViewModel.CanSeek);

        context.ViewModel.BeginSeekDrag();
        context.ViewModel.UpdateSeekDrag(50);
        await context.ViewModel.CompleteSeekDragAsync(50);

        Assert.AreEqual(0, context.PlayerService.SeekCallCount);
    }

    [TestMethod]
    public async Task SeekFailure_ShowsChineseStatusWithoutFatalError()
    {
        var context = CreateContext();
        context.PlayerService.SeekAsyncHandler = (_, _, _) =>
            Task.FromResult(PlayerOperationResult.Failure(PlayerError.SeekFailed));
        await StartLoadedPlayerAsync(context);

        context.ViewModel.BeginSeekDrag();
        await context.ViewModel.CompleteSeekDragAsync(25);

        Assert.IsFalse(context.ViewModel.HasError);
        Assert.IsFalse(context.ViewModel.IsSeeking);
        Assert.AreEqual("跳转失败，请稍后重试", context.ViewModel.StatusText);
    }

    [TestMethod]
    public async Task Seek_InPausedState_KeepsPausedState()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        await ((AsyncRelayCommand)context.ViewModel.PauseCommand).ExecuteAsync();

        context.ViewModel.BeginSeekDrag();
        await context.ViewModel.CompleteSeekDragAsync(30);

        Assert.IsTrue(context.ViewModel.IsPaused);
        Assert.IsFalse(context.ViewModel.IsPlaying);
    }

    [TestMethod]
    public async Task SeekToPercentAsync_InPausedState_KeepsPausedState()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        await ((AsyncRelayCommand)context.ViewModel.PauseCommand).ExecuteAsync();

        await context.ViewModel.SeekToPercentAsync(30);

        Assert.IsTrue(context.ViewModel.IsPaused);
        Assert.IsFalse(context.ViewModel.IsPlaying);

        await ((AsyncRelayCommand)context.ViewModel.TogglePlayPauseCommand).ExecuteAsync();

        Assert.IsFalse(context.ViewModel.IsPaused);
        Assert.IsTrue(context.ViewModel.IsPlaying);
        Assert.AreEqual(1, context.PlayerService.PlayCallCount);
    }

    [TestMethod]
    public async Task SeekToPercentAsync_InPausedState_IgnoresLatePlaybackRestart()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        await ((AsyncRelayCommand)context.ViewModel.PauseCommand).ExecuteAsync();
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;

        await context.ViewModel.SeekToPercentAsync(30);
        RaisePlaying(context, playbackInstanceId: playbackInstanceId);

        Assert.IsTrue(context.ViewModel.IsPaused);
        Assert.IsFalse(context.ViewModel.IsPlaying);
        Assert.AreEqual("播放", context.ViewModel.PlayPauseButtonText);
    }

    [TestMethod]
    public async Task Seek_InPlayingState_KeepsPlayingState()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);

        context.ViewModel.BeginSeekDrag();
        await context.ViewModel.CompleteSeekDragAsync(30);

        Assert.IsTrue(context.ViewModel.IsPlaying);
        Assert.IsFalse(context.ViewModel.IsPaused);
    }

    [TestMethod]
    public async Task Seek_AfterBack_DoesNotCallPlayerService()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);

        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();
        context.ViewModel.BeginSeekDrag();
        await context.ViewModel.CompleteSeekDragAsync(50);

        Assert.AreEqual(0, context.PlayerService.SeekCallCount);
    }

    [TestMethod]
    public async Task Load_AfterMpvPlayingEvent_ShowsPlaying()
    {
        var context = CreateContext();
        var parameter = CreateNavigationParameter(CreatePlaybackInfo());

        context.ViewModel.Load(parameter);
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context);

        Assert.IsTrue(context.ViewModel.IsPlaying);
        Assert.IsFalse(context.ViewModel.IsLoading);
        Assert.AreEqual("播放中", context.ViewModel.StatusText);
    }

    [TestMethod]
    public async Task PlayCommand_CallsPlayerServicePlay()
    {
        var context = CreateContext();
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context);

        await ((AsyncRelayCommand)context.ViewModel.PlayCommand).ExecuteAsync();

        Assert.AreEqual(1, context.PlayerService.PlayCallCount);
        Assert.IsTrue(context.ViewModel.IsPlaying);
    }

    [TestMethod]
    public async Task TogglePlayPauseCommand_PausesWhenPlayingAndPlaysWhenPaused()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);

        Assert.AreEqual("暂停", context.ViewModel.PlayPauseButtonText);
        await ((AsyncRelayCommand)context.ViewModel.TogglePlayPauseCommand).ExecuteAsync();

        Assert.AreEqual(1, context.PlayerService.PauseCallCount);
        Assert.IsTrue(context.ViewModel.IsPaused);
        Assert.AreEqual("播放", context.ViewModel.PlayPauseButtonText);

        await ((AsyncRelayCommand)context.ViewModel.TogglePlayPauseCommand).ExecuteAsync();

        Assert.AreEqual(1, context.PlayerService.PlayCallCount);
        Assert.IsTrue(context.ViewModel.IsPlaying);
    }

    [TestMethod]
    public async Task ShortcutSpace_TogglesPlayPause()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);

        var handled = await context.ViewModel.HandleShortcutKeyAsync(Key.Space);

        Assert.IsTrue(handled);
        Assert.AreEqual(1, context.PlayerService.PauseCallCount);
    }

    [TestMethod]
    public async Task ShortcutLeftRight_UsesExistingSeekPath()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(90),
            null,
            false));

        await context.ViewModel.HandleShortcutKeyAsync(Key.Right);

        Assert.AreEqual(1, context.PlayerService.SeekCallCount);
        Assert.AreEqual(TimeSpan.FromSeconds(40), context.PlayerService.LastSeekPosition);
        Assert.AreEqual(1, context.PlaybackReportService.ProgressCallCount);

        await context.ViewModel.HandleShortcutKeyAsync(Key.Left);

        Assert.AreEqual(2, context.PlayerService.SeekCallCount);
        Assert.AreEqual(TimeSpan.FromSeconds(30), context.PlayerService.LastSeekPosition);
    }

    [DataTestMethod]
    [DataRow(Key.Left)]
    [DataRow(Key.Right)]
    public async Task ShortcutLeftRight_FromHiddenKeepsControlsVisibleWhileInFlight(Key key)
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        context.ViewModel.HideControlsOverlayIfAllowed();
        Assert.IsFalse(context.ViewModel.IsControlsOverlayVisible);
        var seekCompletion = new TaskCompletionSource<PlayerOperationResult>();
        context.PlayerService.SeekAsyncHandler = (_, _, _) => seekCompletion.Task;

        var shortcutTask = context.ViewModel.HandleShortcutKeyAsync(key);
        context.ViewModel.HideControlsOverlayIfAllowed();

        Assert.IsTrue(context.ViewModel.IsSeeking);
        Assert.IsTrue(context.ViewModel.IsControlsOverlayVisible);

        seekCompletion.SetResult(PlayerOperationResult.Success());
        Assert.IsTrue(await shortcutTask);
    }

    [TestMethod]
    public async Task ShortcutLeftRight_UsesConfiguredSeekSeconds()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with { SeekSeconds = 25 };
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromSeconds(40),
            TimeSpan.FromMinutes(90),
            null,
            false));

        await context.ViewModel.HandleShortcutKeyAsync(Key.Right);

        Assert.AreEqual(TimeSpan.FromSeconds(65), context.PlayerService.LastSeekPosition);

        await context.ViewModel.HandleShortcutKeyAsync(Key.Left);

        Assert.AreEqual(TimeSpan.FromSeconds(40), context.PlayerService.LastSeekPosition);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(5)]
    [DataRow(10)]
    public async Task Load_ExposesConfiguredControlsHideSeconds(int controlsHideSeconds)
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        {
            ControlsHideSeconds = controlsHideSeconds
        };

        await StartLoadedPlayerAsync(context);

        Assert.AreEqual(controlsHideSeconds, context.ViewModel.ControlsHideSeconds);
        Assert.AreEqual(
            TimeSpan.FromSeconds(controlsHideSeconds),
            PlayerPage.GetControlsAutoHideInterval(context.ViewModel.ControlsHideSeconds));
    }

    [TestMethod]
    public async Task ShortcutUpDown_AdjustsVolumeAndClamps()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);

        await context.ViewModel.HandleShortcutKeyAsync(Key.Down);
        Assert.AreEqual(95, context.PlayerService.LastVolume);
        Assert.AreEqual(95, context.ViewModel.Volume);

        for (var i = 0; i < 30; i++)
        {
            await context.ViewModel.HandleShortcutKeyAsync(Key.Down);
        }

        Assert.AreEqual(0, context.PlayerService.LastVolume);
        Assert.AreEqual(0, context.ViewModel.Volume);

        for (var i = 0; i < 30; i++)
        {
            await context.ViewModel.HandleShortcutKeyAsync(Key.Up);
        }

        Assert.AreEqual(100, context.PlayerService.LastVolume);
        Assert.AreEqual(100, context.ViewModel.Volume);
    }

    [TestMethod]
    public async Task SetVolumeAsync_UsesPlayerServiceAndClamps()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);

        await context.ViewModel.SetVolumeAsync(42.4);

        Assert.AreEqual(42, context.PlayerService.LastVolume);
        Assert.AreEqual(42, context.ViewModel.Volume);
        Assert.AreEqual("42%", context.ViewModel.VolumePercentText);

        await context.ViewModel.SetVolumeAsync(150);

        Assert.AreEqual(100, context.PlayerService.LastVolume);
        Assert.AreEqual(100, context.ViewModel.Volume);
    }

    [TestMethod]
    public async Task Load_AppliesDefaultVolume()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with { DefaultVolume = 66 };

        await StartLoadedPlayerAsync(context);

        Assert.AreEqual(66, context.PlayerService.LastVolume);
        Assert.AreEqual(66, context.ViewModel.Volume);
    }

    [TestMethod]
    public async Task Load_WhenRememberLastVolumeUsesLastVolume()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        {
            DefaultVolume = 66,
            RememberLastVolume = true,
            LastVolume = 33
        };

        await StartLoadedPlayerAsync(context);

        Assert.AreEqual(33, context.PlayerService.LastVolume);
        Assert.AreEqual(33, context.ViewModel.Volume);
    }

    [TestMethod]
    public async Task Load_WhenRememberLastVolumeDisabledUsesDefaultVolume()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        {
            DefaultVolume = 66,
            RememberLastVolume = false,
            LastVolume = 33
        };

        await StartLoadedPlayerAsync(context);

        Assert.AreEqual(66, context.PlayerService.LastVolume);
        Assert.AreEqual(66, context.ViewModel.Volume);
    }

    [TestMethod]
    public async Task Load_WhenSettingsReadFailsUsesDefaults()
    {
        var context = CreateContext();
        context.AppSettingsService.GetPlayerPreferencesAsyncHandler = _ =>
            throw new IOException("settings unavailable");

        await StartLoadedPlayerAsync(context);

        Assert.AreEqual(PlayerPreferences.Default.DefaultVolume, context.PlayerService.LastVolume);
        Assert.AreEqual(PlayerPreferences.Default.DefaultVolume, context.ViewModel.Volume);
    }

    [TestMethod]
    public async Task Load_SettingsFromOldInstanceDoesNotAffectCurrentPlayback()
    {
        var context = CreateContext();
        var firstPreferences = new TaskCompletionSource<PlayerPreferences>();
        var secondPreferences = new TaskCompletionSource<PlayerPreferences>();
        var getCallCount = 0;
        context.AppSettingsService.GetPlayerPreferencesAsyncHandler = _ =>
        {
            getCallCount++;
            return getCallCount == 1 ? firstPreferences.Task : secondPreferences.Task;
        };

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        var firstAttachTask = context.ViewModel.AttachVideoHostAsync(new IntPtr(1111));
        await Task.Yield();
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        var secondAttachTask = context.ViewModel.AttachVideoHostAsync(new IntPtr(2222));
        secondPreferences.SetResult(PlayerPreferences.Default with { DefaultVolume = 55 });
        await secondAttachTask;
        firstPreferences.SetResult(PlayerPreferences.Default with { DefaultVolume = 22 });
        await firstAttachTask;

        Assert.AreEqual(55, context.ViewModel.Volume);
        Assert.AreEqual(55, context.PlayerService.LastVolume);
    }

    [TestMethod]
    public async Task Load_SettingsAfterReleaseDoesNotStartPlayback()
    {
        var context = CreateContext();
        var preferences = new TaskCompletionSource<PlayerPreferences>();
        context.AppSettingsService.GetPlayerPreferencesAsyncHandler = _ => preferences.Task;

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        var attachTask = context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        await Task.Yield();
        await context.ViewModel.ReleasePlayerAsync();
        preferences.SetResult(PlayerPreferences.Default with { DefaultVolume = 22 });
        await attachTask;

        Assert.AreEqual(0, context.PlayerService.LoadCallCount);
    }

    [TestMethod]
    public async Task SetVolumeAsync_WhenRememberLastVolumeSavesLastVolume()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with { RememberLastVolume = true };
        await StartLoadedPlayerAsync(context);

        await context.ViewModel.SetVolumeAsync(42);

        Assert.AreEqual(42, context.AppSettingsService.PlayerPreferences.LastVolume);
    }

    [TestMethod]
    public async Task SetVolumeAsync_PreservesPreferencesChangedAfterPlayerLoad()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with { RememberLastVolume = true };
        await StartLoadedPlayerAsync(context);
        context.AppSettingsService.PlayerPreferences = context.AppSettingsService.PlayerPreferences with
        {
            DefaultVolume = 64,
            SeekSeconds = 30
        };

        await context.ViewModel.SetVolumeAsync(42);

        Assert.AreEqual(64, context.AppSettingsService.PlayerPreferences.DefaultVolume);
        Assert.AreEqual(30, context.AppSettingsService.PlayerPreferences.SeekSeconds);
        Assert.AreEqual(42, context.AppSettingsService.PlayerPreferences.LastVolume);
    }

    [TestMethod]
    public async Task SetVolumeAsync_WhenRememberLastVolumeDisabledDoesNotSaveLastVolume()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        {
            RememberLastVolume = false,
            LastVolume = 77
        };
        await StartLoadedPlayerAsync(context);

        await context.ViewModel.SetVolumeAsync(42);

        Assert.AreEqual(77, context.AppSettingsService.PlayerPreferences.LastVolume);
    }

    [TestMethod]
    public async Task ToggleMuteAsync_DoesNotPersistLastVolumeAsZero()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        {
            RememberLastVolume = true,
            LastVolume = 77
        };
        await StartLoadedPlayerAsync(context);

        await context.ViewModel.ToggleMuteAsync();

        Assert.AreEqual(77, context.AppSettingsService.PlayerPreferences.LastVolume);
    }

    [TestMethod]
    public async Task ShortcutM_TogglesMute()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);

        await context.ViewModel.HandleShortcutKeyAsync(Key.M);

        Assert.AreEqual(2, context.PlayerService.SetMuteCallCount);
        Assert.IsTrue(context.PlayerService.LastMute);
        Assert.IsTrue(context.ViewModel.IsMuted);

        await context.ViewModel.HandleShortcutKeyAsync(Key.M);

        Assert.AreEqual(3, context.PlayerService.SetMuteCallCount);
        Assert.IsFalse(context.PlayerService.LastMute);
        Assert.IsFalse(context.ViewModel.IsMuted);
    }

    [TestMethod]
    public async Task ShortcutEscape_StopsAndReturnsDetail()
    {
        var context = CreateContext();
        context.NavigationService.NavigateTo(AppPage.Player);
        await StartLoadedPlayerAsync(context);

        var handled = await context.ViewModel.HandleShortcutKeyAsync(Key.Escape);

        Assert.IsTrue(handled);
        Assert.AreEqual(AppPage.Detail, context.NavigationService.CurrentPage);
        Assert.IsTrue(context.PlayerService.StopCallCount > 0);
    }

    [TestMethod]
    public async Task Shortcuts_AfterBack_DoNotControlOldPlayback()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);

        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();
        var handled = await context.ViewModel.HandleShortcutKeyAsync(Key.Space);

        Assert.IsFalse(handled);
        Assert.AreEqual(0, context.PlayerService.PlayCallCount);
        Assert.AreEqual(0, context.PlayerService.PauseCallCount);
    }

    [TestMethod]
    public async Task VolumeAndMuteFailures_ShowChineseStatus()
    {
        var context = CreateContext();
        context.PlayerService.SetVolumeAsyncHandler = (_, _) =>
            Task.FromResult(PlayerOperationResult.Failure(PlayerError.VolumeFailed));
        context.PlayerService.SetMuteAsyncHandler = (_, _) =>
            Task.FromResult(PlayerOperationResult.Failure(PlayerError.MuteFailed));
        await StartLoadedPlayerAsync(context);

        await context.ViewModel.AdjustVolumeAsync(-5);
        Assert.AreEqual("音量调整失败", context.ViewModel.StatusText);

        await context.ViewModel.ToggleMuteAsync();
        Assert.AreEqual("静音切换失败", context.ViewModel.StatusText);
    }

    [TestMethod]
    public async Task AudioTrackMenu_ShowsAvailableTrackCountAndCurrentTrack()
    {
        var context = CreateContext();
        var playbackInfo = CreatePlaybackInfo(
            audioTracks: new[]
            {
                new PlaybackTrack(1, "jpn", "aac", "日语 AAC", true),
                new PlaybackTrack(2, "chi", "ac3", "中文 AC3", false)
            });

        context.ViewModel.Load(CreateNavigationParameter(playbackInfo));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));

        Assert.AreEqual("音轨 2", context.ViewModel.AudioMenuButtonText);
        Assert.AreEqual(2, context.ViewModel.AudioTracks.Count);
        Assert.IsTrue(context.ViewModel.AudioTracks[0].IsSelected);
        StringAssert.Contains(context.ViewModel.CurrentAudioTrackText, "日语 AAC");
    }

    [TestMethod]
    public async Task SubtitleMenu_ShowsAvailableSubtitleCountAndOffOption()
    {
        var context = CreateContext();
        var playbackInfo = CreatePlaybackInfo(
            subtitles: new[]
            {
                new PlaybackSubtitle(3, "chi", "srt", "中文 SRT", true, false, null),
                new PlaybackSubtitle(4, "eng", "ass", "English ASS", false, true, "External")
            });

        context.ViewModel.Load(CreateNavigationParameter(playbackInfo));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));

        Assert.AreEqual("字幕 2", context.ViewModel.SubtitleMenuButtonText);
        Assert.AreEqual(3, context.ViewModel.SubtitleTracks.Count);
        Assert.AreEqual("关闭字幕", context.ViewModel.SubtitleTracks[0].DisplayText);
        Assert.IsTrue(context.ViewModel.SubtitleTracks[0].IsSelected);
        StringAssert.Contains(context.ViewModel.CurrentSubtitleText, "关闭字幕");
    }

    [TestMethod]
    public async Task SelectAudioTrackAsync_CallsPlayerServiceAndUpdatesSelection()
    {
        var context = CreateContext();
        var playbackInfo = CreatePlaybackInfo(
            audioTracks: new[]
            {
                new PlaybackTrack(1, "jpn", "aac", "日语 AAC", true),
                new PlaybackTrack(2, "chi", "ac3", "中文 AC3", false)
            });
        context.ViewModel.Load(CreateNavigationParameter(playbackInfo));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(8, "audio", "jpn", "日语 AAC", "aac", false, true, true, false, 1),
            new PlayerTrackInfo(9, "audio", "chi", "中文 AC3", "ac3", false, false, false, false, 2)
        });

        await context.ViewModel.SelectAudioTrackAsync(context.ViewModel.AudioTracks[1]);

        Assert.AreEqual(1, context.PlayerService.SelectAudioTrackCallCount);
        Assert.AreEqual(9, context.PlayerService.LastAudioTrackIndex);
        Assert.AreEqual(context.PlayerService.LastLoadRequest!.PlaybackInstanceId, context.PlayerService.LastAudioTrackPlaybackInstanceId);
        Assert.IsFalse(context.ViewModel.AudioTracks[0].IsSelected);
        Assert.IsTrue(context.ViewModel.AudioTracks[1].IsSelected);
        StringAssert.Contains(context.ViewModel.CurrentAudioTrackText, "中文 AC3");
        Assert.AreEqual(1, context.PlaybackReportService.ProgressCallCount);
        Assert.AreEqual(2, context.PlaybackReportService.LastProgressRequest!.AudioStreamIndex);
    }

    [TestMethod]
    public async Task SelectSubtitleTrackAsync_CallsPlayerServiceAndUpdatesSelection()
    {
        var context = CreateContext();
        var playbackInfo = CreatePlaybackInfo(
            subtitles: new[]
            {
                new PlaybackSubtitle(3, "chi", "srt", "中文 SRT", false, false, null)
            });
        context.ViewModel.Load(CreateNavigationParameter(playbackInfo));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(11, "sub", "chi", "中文 SRT", "srt", false, false, false, false, 3)
        });

        await context.ViewModel.SelectSubtitleTrackAsync(context.ViewModel.SubtitleTracks[1]);

        Assert.AreEqual(1, context.PlayerService.SelectSubtitleTrackCallCount);
        Assert.AreEqual(11, context.PlayerService.LastSubtitleTrackIndex);
        Assert.AreEqual(context.PlayerService.LastLoadRequest!.PlaybackInstanceId, context.PlayerService.LastSubtitleTrackPlaybackInstanceId);
        Assert.AreEqual(1, context.ViewModel.SubtitleTracks.Count(track => track.IsSelected));
        Assert.IsTrue(context.ViewModel.SubtitleTracks[1].IsSelected);
        Assert.AreEqual(1, context.PlaybackReportService.ProgressCallCount);
        Assert.AreEqual(3, context.PlaybackReportService.LastProgressRequest!.SubtitleStreamIndex);
    }

    [TestMethod]
    public async Task SelectSubtitleTrackAsync_CurrentSelection_DoesNotToggleOff()
    {
        var context = CreateContext();
        var playbackInfo = CreatePlaybackInfo(
            subtitles: new[]
            {
                new PlaybackSubtitle(3, "chi", "srt", "Chinese SRT", true, false, null)
            });
        context.ViewModel.Load(CreateNavigationParameter(playbackInfo));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context, [new PlayerTrackInfo(3, "sub", "chi", "Chinese SRT", "srt", false, true, true, false, 3)]);

        await context.ViewModel.SelectSubtitleTrackAsync(context.ViewModel.SubtitleTracks[1]);

        Assert.AreEqual(0, context.PlayerService.SelectSubtitleTrackCallCount);
        Assert.AreEqual(1, context.ViewModel.SubtitleTracks.Count(track => track.IsSelected));
        Assert.IsTrue(context.ViewModel.SubtitleTracks[1].IsSelected);
    }

    [TestMethod]
    public async Task MpvSubtitleTracks_WithChineseSubtitle_AutoSelectsChineseTrack()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        {
            DefaultSubtitleLanguage = "\u4e2d\u6587"
        };
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));

        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(1, "audio", "jpn", "Japanese AAC", "aac", false, true, true, false),
            new PlayerTrackInfo(7, "sub", "eng", "English SRT", "srt", false, false, false, false),
            new PlayerTrackInfo(8, "sub", "chi", "Chinese ASS", "ass", false, false, false, false)
        });
        await Task.Delay(50);

        Assert.AreEqual(1, context.PlayerService.SelectSubtitleTrackCallCount);
        Assert.AreEqual(8, context.PlayerService.LastSubtitleTrackIndex);
        Assert.AreEqual(1, context.ViewModel.SubtitleTracks.Count(track => track.IsSelected));
        Assert.IsTrue(context.ViewModel.SubtitleTracks.Single(track => track.MpvTrackId == 8).IsSelected);
    }

    [TestMethod]
    public async Task MpvSubtitleTracks_UsesConfiguredDefaultSubtitleLanguage()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        {
            DefaultSubtitleLanguage = "eng"
        };
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));

        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(1, "audio", "jpn", "Japanese AAC", "aac", false, true, true, false),
            new PlayerTrackInfo(7, "sub", "eng", "English SRT", "srt", false, false, false, false),
            new PlayerTrackInfo(8, "sub", "chi", "Chinese ASS", "ass", false, false, false, false)
        });
        await Task.Delay(50);

        Assert.AreEqual(1, context.PlayerService.SelectSubtitleTrackCallCount);
        Assert.AreEqual(7, context.PlayerService.LastSubtitleTrackIndex);
    }

    [TestMethod]
    public async Task MpvAudioTracks_UsesConfiguredDefaultAudioLanguage()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        {
            DefaultAudioLanguage = "chi"
        };
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));

        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(1, "audio", "jpn", "Japanese AAC", "aac", false, true, true, false),
            new PlayerTrackInfo(2, "audio", "chi", "Chinese AC3", "ac3", false, false, false, false)
        });
        await Task.Delay(50);

        Assert.AreEqual(1, context.PlayerService.SelectAudioTrackCallCount);
        Assert.AreEqual(2, context.PlayerService.LastAudioTrackIndex);
    }

    [TestMethod]
    public async Task MpvAudioTracks_AutomaticSelectionFailureDoesNotReportProgress()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        {
            DefaultAudioLanguage = "chi"
        };
        var selectionAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.PlayerService.SelectAudioTrackAsyncHandler = (_, _, _) =>
        {
            selectionAttempted.TrySetResult();
            return Task.FromResult(PlayerOperationResult.Failure(PlayerError.AudioTrackFailed));
        };
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));

        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(1, "audio", "jpn", "Japanese AAC", "aac", false, true, true, false),
            new PlayerTrackInfo(2, "audio", "chi", "Chinese AC3", "ac3", false, false, false, false)
        });
        await selectionAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, context.PlayerService.SelectAudioTrackCallCount);
        Assert.AreEqual(0, context.PlaybackReportService.ProgressCallCount);
    }

    [TestMethod]
    public async Task MpvSubtitleTracks_AutomaticLanguagePreferenceDoesNotForceSelection()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        {
            DefaultSubtitleLanguage = "\u81ea\u52a8"
        };
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));

        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(1, "audio", "jpn", "Japanese AAC", "aac", false, true, true, false),
            new PlayerTrackInfo(8, "sub", "chi", "Chinese ASS", "ass", false, false, false, false)
        });
        await Task.Delay(50);

        Assert.AreEqual(0, context.PlayerService.SelectSubtitleTrackCallCount);
    }

    [TestMethod]
    public async Task MpvAudioTracks_AutomaticLanguagePreferenceDoesNotForceSelection()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        {
            DefaultAudioLanguage = "\u81ea\u52a8"
        };
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));

        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(1, "audio", "jpn", "Japanese AAC", "aac", false, true, true, false),
            new PlayerTrackInfo(2, "audio", "chi", "Chinese AC3", "ac3", false, false, false, false)
        });
        await Task.Delay(50);

        Assert.AreEqual(0, context.PlayerService.SelectAudioTrackCallCount);
    }

    [TestMethod]
    public async Task ExternalChineseSubtitle_NotInMpvTrackList_AutoSelectsExternalSubtitle()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        {
            DefaultSubtitleLanguage = "\u4e2d\u6587"
        };
        var playbackInfo = CreatePlaybackInfo(subtitles: new[]
        {
            new PlaybackSubtitle(
                9,
                "chi",
                "ass",
                "Chinese ASS",
                false,
                true,
                "External",
                "http://media.local/Videos/item-1/source-1/Subtitles/9/Stream.ass")
        });
        context.ViewModel.Load(CreateNavigationParameter(playbackInfo));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));

        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(1, "audio", "jpn", "Japanese AAC", "aac", false, true, true, false)
        });
        await Task.Delay(50);

        Assert.AreEqual(1, context.PlayerService.SelectExternalSubtitleCallCount);
        Assert.AreEqual(9, context.PlayerService.LastExternalSubtitleStreamIndex);
        Assert.IsTrue(context.ViewModel.SubtitleTracks.Single(track => track.Index == 9).IsSelected);
    }

    [TestMethod]
    public async Task ExternalSubtitle_TrackRefreshKeepsOneSelectedItemAndReportIndex()
    {
        var context = CreateContext();
        var playbackInfo = CreatePlaybackInfo(subtitles: new[]
        {
            new PlaybackSubtitle(
                9,
                "chi",
                "ass",
                "External Chinese",
                false,
                true,
                "External",
                "http://media.local/Videos/item-1/source-1/Subtitles/9/Stream.ass")
        });
        context.ViewModel.Load(CreateNavigationParameter(playbackInfo));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(1, "audio", "jpn", "Japanese AAC", "aac", false, true, true, false)
        });
        var selectionReport = new TaskCompletionSource<PlaybackReportProgressRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.PlaybackReportService.ReportProgressAsyncHandler = (_, request, _) =>
        {
            selectionReport.TrySetResult(request);
            return Task.FromResult(PlaybackReportResult.Success());
        };

        await context.ViewModel.SelectSubtitleTrackAsync(
            context.ViewModel.SubtitleTracks.Single(track => track.MediaStreamIndex == 9));
        await selectionReport.Task.WaitAsync(TimeSpan.FromSeconds(2));
        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(1, "audio", "jpn", "Japanese AAC", "aac", false, true, true, false),
            new PlayerTrackInfo(12, "sub", "chi", "External Chinese", "ass", true, true, false, false, 9)
        });

        var matchingSubtitles = context.ViewModel.SubtitleTracks
            .Where(track => track.MediaStreamIndex == 9)
            .ToArray();
        Assert.AreEqual(1, matchingSubtitles.Length);
        Assert.AreEqual(12, matchingSubtitles[0].MpvTrackId);
        Assert.IsTrue(matchingSubtitles[0].IsSelected);

        var periodicReport = new TaskCompletionSource<PlaybackReportProgressRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.PlaybackReportService.ReportProgressAsyncHandler = (_, request, _) =>
        {
            periodicReport.TrySetResult(request);
            return Task.FromResult(PlaybackReportResult.Success());
        };
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlaybackReportScheduler.RaiseTick(playbackInstanceId);
        var progress = await periodicReport.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(9, progress.SubtitleStreamIndex);

        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();
        Assert.AreEqual(9, context.PlaybackReportService.LastStoppedRequest!.SubtitleStreamIndex);
    }

    [TestMethod]
    public async Task MpvSubtitleTracks_ManualChineseSelection_IsNotOverriddenByStaleRefresh()
    {
        var context = CreateContext();
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(1, "audio", "jpn", "Japanese AAC", "aac", false, true, true, false),
            new PlayerTrackInfo(7, "sub", "eng", "English SRT", "srt", false, false, false, false),
            new PlayerTrackInfo(8, "sub", "chi", "Chinese ASS", "ass", false, false, false, false)
        });
        await Task.Delay(50);

        await context.ViewModel.SelectSubtitleTrackAsync(context.ViewModel.SubtitleTracks[0]);
        await context.ViewModel.SelectSubtitleTrackAsync(context.ViewModel.SubtitleTracks.Single(track => track.MpvTrackId == 8));
        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(1, "audio", "jpn", "Japanese AAC", "aac", false, true, true, false),
            new PlayerTrackInfo(7, "sub", "eng", "English SRT", "srt", false, false, false, false),
            new PlayerTrackInfo(8, "sub", "chi", "Chinese ASS", "ass", false, false, false, false)
        });

        Assert.AreEqual(1, context.ViewModel.SubtitleTracks.Count(track => track.IsSelected));
        Assert.IsTrue(context.ViewModel.SubtitleTracks.Single(track => track.MpvTrackId == 8).IsSelected);
    }

    [TestMethod]
    public async Task MpvSubtitleTracks_ManualSelectionIsNotOverriddenByDefaultLanguage()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        {
            DefaultSubtitleLanguage = "chi"
        };
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(1, "audio", "jpn", "Japanese AAC", "aac", false, true, true, false),
            new PlayerTrackInfo(7, "sub", "eng", "English SRT", "srt", false, false, false, false),
            new PlayerTrackInfo(8, "sub", "chi", "Chinese ASS", "ass", false, false, false, false)
        });
        await Task.Delay(50);

        await context.ViewModel.SelectSubtitleTrackAsync(context.ViewModel.SubtitleTracks[0]);
        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(1, "audio", "jpn", "Japanese AAC", "aac", false, true, true, false),
            new PlayerTrackInfo(7, "sub", "eng", "English SRT", "srt", false, false, false, false),
            new PlayerTrackInfo(8, "sub", "chi", "Chinese ASS", "ass", false, false, false, false)
        });
        await Task.Delay(50);

        Assert.IsTrue(context.ViewModel.SubtitleTracks[0].IsSelected);
    }

    [TestMethod]
    public async Task MpvAudioTracks_ManualSelectionIsNotOverriddenByDefaultLanguage()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        {
            DefaultAudioLanguage = "chi"
        };
        var playbackInfo = CreatePlaybackInfo(audioTracks: new[]
        {
            new PlaybackTrack(1, "jpn", "aac", "Japanese AAC", true),
            new PlaybackTrack(2, "chi", "ac3", "Chinese AC3", false)
        });
        context.ViewModel.Load(CreateNavigationParameter(playbackInfo));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(1, "audio", "jpn", "Japanese AAC", "aac", false, true, true, false),
            new PlayerTrackInfo(2, "audio", "chi", "Chinese AC3", "ac3", false, false, false, false)
        });
        await Task.Delay(50);

        await context.ViewModel.SelectAudioTrackAsync(context.ViewModel.AudioTracks[0]);
        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(1, "audio", "jpn", "Japanese AAC", "aac", false, true, true, false),
            new PlayerTrackInfo(2, "audio", "chi", "Chinese AC3", "ac3", false, false, false, false)
        });
        await Task.Delay(50);

        Assert.IsTrue(context.ViewModel.AudioTracks[0].IsSelected);
    }

    [TestMethod]
    public async Task DisableSubtitleAsync_CallsPlayerServiceAndSelectsOffOption()
    {
        var context = CreateContext();
        var playbackInfo = CreatePlaybackInfo(
            subtitles: new[]
            {
                new PlaybackSubtitle(3, "chi", "srt", "中文 SRT", true, false, null)
            });
        context.ViewModel.Load(CreateNavigationParameter(playbackInfo));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context, [new PlayerTrackInfo(3, "sub", "chi", "Chinese SRT", "srt", false, true, true, false, 3)]);

        await context.ViewModel.SelectSubtitleTrackAsync(context.ViewModel.SubtitleTracks[0]);

        Assert.AreEqual(1, context.PlayerService.DisableSubtitleCallCount);
        Assert.AreEqual(context.PlayerService.LastLoadRequest!.PlaybackInstanceId, context.PlayerService.LastDisableSubtitlePlaybackInstanceId);
        Assert.AreEqual(1, context.ViewModel.SubtitleTracks.Count(track => track.IsSelected));
        Assert.IsTrue(context.ViewModel.SubtitleTracks[0].IsSelected);
        StringAssert.Contains(context.ViewModel.CurrentSubtitleText, "关闭字幕");
        Assert.AreEqual(1, context.PlaybackReportService.ProgressCallCount);
        Assert.IsNull(context.PlaybackReportService.LastProgressRequest!.SubtitleStreamIndex);
    }

    [TestMethod]
    public async Task SelectAudioTrackAsync_CurrentSelection_DoesNotToggleOff()
    {
        var context = CreateContext();
        var playbackInfo = CreatePlaybackInfo(
            audioTracks: new[]
            {
                new PlaybackTrack(1, "jpn", "aac", "Japanese AAC", true),
                new PlaybackTrack(2, "chi", "ac3", "Chinese AC3", false)
            });
        context.ViewModel.Load(CreateNavigationParameter(playbackInfo));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context);

        await context.ViewModel.SelectAudioTrackAsync(context.ViewModel.AudioTracks[0]);

        Assert.AreEqual(0, context.PlayerService.SelectAudioTrackCallCount);
        Assert.AreEqual(1, context.ViewModel.AudioTracks.Count(track => track.IsSelected));
        Assert.IsTrue(context.ViewModel.AudioTracks[0].IsSelected);
    }

    [TestMethod]
    public async Task TrackSwitchFailures_ShowChineseStatus()
    {
        var context = CreateContext();
        context.PlayerService.SelectAudioTrackAsyncHandler = (_, _, _) =>
            Task.FromResult(PlayerOperationResult.Failure(PlayerError.AudioTrackFailed));
        context.PlayerService.SelectSubtitleTrackAsyncHandler = (_, _, _) =>
            Task.FromResult(PlayerOperationResult.Failure(PlayerError.SubtitleTrackFailed));
        var playbackInfo = CreatePlaybackInfo(
            audioTracks: new[]
            {
                new PlaybackTrack(1, "jpn", "aac", "Japanese AAC", true),
                new PlaybackTrack(2, "chi", "ac3", "Chinese AC3", false)
            },
            subtitles: new[] { new PlaybackSubtitle(3, "chi", "srt", "中文 SRT", false, false, null) });
        context.ViewModel.Load(CreateNavigationParameter(playbackInfo));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(8, "audio", "jpn", "Japanese AAC", "aac", false, true, true, false, 1),
            new PlayerTrackInfo(9, "audio", "chi", "Chinese AC3", "ac3", false, false, false, false, 2),
            new PlayerTrackInfo(11, "sub", "chi", "中文 SRT", "srt", false, false, false, false, 3)
        });

        await context.ViewModel.SelectAudioTrackAsync(context.ViewModel.AudioTracks[1]);
        Assert.AreEqual("音轨切换失败", context.ViewModel.StatusText);

        await context.ViewModel.SelectSubtitleTrackAsync(context.ViewModel.SubtitleTracks[1]);
        Assert.AreEqual("字幕切换失败", context.ViewModel.StatusText);
        Assert.AreEqual(0, context.PlaybackReportService.ProgressCallCount);
    }

    [TestMethod]
    public async Task TrackSwitchSuccess_WithoutSessionDoesNotReportProgress()
    {
        var context = CreateContext();
        var playbackInfo = CreatePlaybackInfo(audioTracks: new[]
        {
            new PlaybackTrack(1, "jpn", "aac", "Japanese AAC", true),
            new PlaybackTrack(2, "chi", "ac3", "Chinese AC3", false)
        });
        context.ViewModel.Load(CreateNavigationParameter(playbackInfo));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(8, "audio", "jpn", "Japanese AAC", "aac", false, true, true, false, 1),
            new PlayerTrackInfo(9, "audio", "chi", "Chinese AC3", "ac3", false, false, false, false, 2)
        });
        context.CurrentSessionService.ClearSession();

        await context.ViewModel.SelectAudioTrackAsync(context.ViewModel.AudioTracks[1]);

        Assert.AreEqual(1, context.PlayerService.SelectAudioTrackCallCount);
        Assert.AreEqual(0, context.PlaybackReportService.ProgressCallCount);
    }

    [TestMethod]
    public async Task TrackSwitch_AfterBack_DoesNotCallPlayerService()
    {
        var context = CreateContext();
        var playbackInfo = CreatePlaybackInfo(
            audioTracks: new[] { new PlaybackTrack(1, "jpn", "aac", "日语 AAC", true) },
            subtitles: new[] { new PlaybackSubtitle(3, "chi", "srt", "中文 SRT", false, false, null) });
        context.ViewModel.Load(CreateNavigationParameter(playbackInfo));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context);

        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();
        await context.ViewModel.SelectAudioTrackAsync(context.ViewModel.AudioTracks[0]);
        await context.ViewModel.SelectSubtitleTrackAsync(context.ViewModel.SubtitleTracks[1]);

        Assert.AreEqual(0, context.PlayerService.SelectAudioTrackCallCount);
        Assert.AreEqual(0, context.PlayerService.SelectSubtitleTrackCallCount);
    }

    [TestMethod]
    public async Task ControlsOverlay_MouseMoveShowsAndPlayingCanAutoHide()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        context.ViewModel.SetFullscreen(true);

        context.ViewModel.HideControlsOverlayIfAllowed();
        Assert.IsFalse(context.ViewModel.IsControlsOverlayVisible);

        context.ViewModel.ShowControlsOverlay();
        Assert.IsTrue(context.ViewModel.IsControlsOverlayVisible);
    }

    [TestMethod]
    public async Task ControlsOverlay_LoadDefaultsVisibleAndNewPlaybackShowsAgain()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        context.ViewModel.SetFullscreen(true);

        context.ViewModel.HideControlsOverlayIfAllowed();
        Assert.IsFalse(context.ViewModel.IsControlsOverlayVisible);

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));

        Assert.IsTrue(context.ViewModel.IsControlsOverlayVisible);
    }

    [TestMethod]
    public async Task ControlsOverlay_WindowedPlaybackCanAutoHide()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);

        context.ViewModel.HideControlsOverlayIfAllowed();

        Assert.IsFalse(context.ViewModel.IsControlsOverlayVisible);
    }

    [TestMethod]
    public async Task FullscreenState_UpdatesButtonTextAndTooltip()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);

        Assert.IsFalse(context.ViewModel.IsFullscreen);
        Assert.AreEqual("\u26f6", context.ViewModel.FullscreenButtonText);
        Assert.AreEqual("\u8fdb\u5165\u5168\u5c4f", context.ViewModel.FullscreenButtonTooltip);

        context.ViewModel.SetFullscreen(true);

        Assert.IsTrue(context.ViewModel.IsFullscreen);
        Assert.AreEqual("\u9000\u51fa", context.ViewModel.FullscreenButtonText);
        Assert.AreEqual("\u9000\u51fa\u5168\u5c4f", context.ViewModel.FullscreenButtonTooltip);
    }

    [TestMethod]
    public async Task ControlsOverlay_PausedStateKeepsVisible()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        await ((AsyncRelayCommand)context.ViewModel.PauseCommand).ExecuteAsync();

        context.ViewModel.HideControlsOverlayIfAllowed();

        Assert.IsTrue(context.ViewModel.IsControlsOverlayVisible);
    }

    [TestMethod]
    public async Task ControlsOverlay_CompletedStateKeepsVisible()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        context.ViewModel.HideControlsOverlayIfAllowed();

        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
            PlayerPlaybackState.Completed));
        context.ViewModel.HideControlsOverlayIfAllowed();

        Assert.IsTrue(context.ViewModel.IsControlsOverlayVisible);
    }

    [TestMethod]
    public async Task ControlsOverlay_MenuOpenKeepsVisible()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);

        context.ViewModel.SetTrackMenuOpen(true);
        context.ViewModel.HideControlsOverlayIfAllowed();

        Assert.IsTrue(context.ViewModel.IsControlsOverlayVisible);
    }

    [TestMethod]
    public async Task ControlsOverlay_SeekDragKeepsVisible()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        context.ViewModel.HideControlsOverlayIfAllowed();

        context.ViewModel.BeginSeekDrag();
        context.ViewModel.HideControlsOverlayIfAllowed();

        Assert.IsTrue(context.ViewModel.IsControlsOverlayVisible);
    }

    [TestMethod]
    public async Task ControlsOverlay_PlaybackFailureKeepsVisible()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);

        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
            PlayerPlaybackState.Failed,
            PlayerError.LoadFailed));
        await Task.Delay(50);
        context.ViewModel.HideControlsOverlayIfAllowed();

        Assert.IsTrue(context.ViewModel.IsControlsOverlayVisible);
    }

    [TestMethod]
    public async Task PauseCommand_CallsPlayerServicePause()
    {
        var context = CreateContext();
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context);

        await ((AsyncRelayCommand)context.ViewModel.PauseCommand).ExecuteAsync();

        Assert.AreEqual(1, context.PlayerService.PauseCallCount);
        Assert.IsTrue(context.ViewModel.IsPaused);
        Assert.AreEqual("已暂停", context.ViewModel.StatusText);
    }

    [TestMethod]
    public async Task BackCommand_StopsPlayerAndReturnsDetail()
    {
        var context = CreateContext();
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context);

        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();

        Assert.AreEqual(2, context.PlayerService.StopCallCount);
        Assert.AreEqual(AppPage.Detail, context.NavigationService.CurrentPage);
    }

    [TestMethod]
    public async Task BackCommand_AwaitsStopBeforeNavigatingDetail()
    {
        var context = CreateContext();
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context);
        context.NavigationService.NavigateTo(AppPage.Player);

        var stopCompletion = new TaskCompletionSource();
        context.PlayerService.StopAsyncHandler = async _ =>
        {
            await stopCompletion.Task;
            return PlayerOperationResult.Success();
        };

        var backTask = ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();
        await Task.Delay(50);

        Assert.AreEqual(AppPage.Player, context.NavigationService.CurrentPage);

        stopCompletion.SetResult();
        await backTask;

        Assert.AreEqual(AppPage.Detail, context.NavigationService.CurrentPage);
    }

    [TestMethod]
    public async Task BackCommand_UsesOneSeekedSnapshotForStoppedReportAndDetailReturn()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        await ((AsyncRelayCommand)context.ViewModel.PauseCommand).ExecuteAsync();
        await context.ViewModel.SeekToPercentAsync(50);
        var calls = new List<string>();
        object? navigationParameter = null;
        context.PlaybackReportService.ReportStoppedAsyncHandler = (_, _, _) =>
        {
            calls.Add("stopped-report");
            return Task.FromResult(PlaybackReportResult.Success());
        };
        context.PlayerService.StopAsyncHandler = _ =>
        {
            calls.Add("local-stop");
            return Task.FromResult(PlayerOperationResult.Success());
        };
        context.NavigationService.CurrentPageChanged += (_, args) =>
        {
            if (args.CurrentPage == AppPage.Detail)
            {
                calls.Add("detail-navigation");
                navigationParameter = args.Parameter;
            }
        };

        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();

        CollectionAssert.AreEqual(
            new[] { "stopped-report", "local-stop", "detail-navigation" },
            calls);
        var stopped = context.PlaybackReportService.LastStoppedRequest!;
        var detailParameter = (DetailNavigationParameter)navigationParameter!;
        var state = detailParameter.PlaybackState!;
        Assert.AreEqual(TimeSpan.FromMinutes(45).Ticks, stopped.PositionTicks);
        Assert.AreEqual(stopped.PositionTicks, state.PositionTicks);
        Assert.AreEqual(stopped.RunTimeTicks, state.RunTimeTicks);
        Assert.AreEqual(50d, state.PlayedPercentage!.Value, 0.01d);
        Assert.IsFalse(state.IsPlayed);
        Assert.IsTrue(state.IsSynchronized);
        Assert.AreEqual("item-1", state.ItemId);
    }

    [DataTestMethod]
    [DataRow(PlaybackReportError.ServerUnreachable)]
    [DataRow(PlaybackReportError.Forbidden)]
    public async Task BackCommand_StoppedReportFailureReturnsLocalSnapshotWithSyncWarning(
        PlaybackReportError reportError)
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(90),
            null,
            false));
        context.PlaybackReportService.ReportStoppedAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackReportResult.Failure(reportError));
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;

        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();

        Assert.AreEqual(AppPage.Detail, context.NavigationService.CurrentPage);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        var state = ((DetailNavigationParameter)navigationParameter!).PlaybackState!;
        Assert.AreEqual(TimeSpan.FromMinutes(30).Ticks, state.PositionTicks);
        Assert.IsFalse(state.IsSynchronized);
    }

    [TestMethod]
    public async Task BackCommand_StoppedReportExceptionReturnsLocalSnapshotWithSyncWarning()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(90),
            null,
            false));
        context.PlaybackReportService.ReportStoppedAsyncHandler = (_, _, _) =>
            throw new HttpRequestException("server unavailable");
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;

        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();

        Assert.AreEqual(AppPage.Detail, context.NavigationService.CurrentPage);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
        var state = ((DetailNavigationParameter)navigationParameter!).PlaybackState!;
        Assert.AreEqual(TimeSpan.FromMinutes(30).Ticks, state.PositionTicks);
        Assert.IsFalse(state.IsSynchronized);
    }

    [TestMethod]
    public async Task BackCommand_CompletionThresholdReturnsPlayedWithoutResume()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(82),
            TimeSpan.FromMinutes(90),
            null,
            false));
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;

        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();

        var state = ((DetailNavigationParameter)navigationParameter!).PlaybackState!;
        Assert.IsTrue(state.IsPlayed);
        Assert.AreEqual(100d, state.PlayedPercentage);
    }

    [TestMethod]
    public async Task BackCommand_IgnoresProgressEventsAfterRelease()
    {
        var context = CreateContext();
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        RaisePlaying(context);

        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1),
            50,
            false));

        Assert.AreEqual("00:00", context.ViewModel.CurrentTimeText);
        Assert.AreNotEqual(50, context.ViewModel.ProgressPercent);
    }

    [TestMethod]
    public async Task Load_WithoutPlayableUrl_ShowsChineseError()
    {
        var context = CreateContext();
        context.PlayerService.LoadAsyncHandler = (_, _) =>
            Task.FromResult(PlayerOperationResult.Failure(PlayerError.NoPlayableUrl));

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo(playbackPath: null)));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));

        Assert.IsTrue(context.ViewModel.HasError);
        Assert.AreEqual("未找到可播放地址", context.ViewModel.ErrorMessage);
        Assert.AreEqual("播放失败", context.ViewModel.StatusText);
    }

    [TestMethod]
    public async Task LoadFailure_ShowsChineseError()
    {
        var context = CreateContext();
        context.PlayerService.LoadAsyncHandler = (_, _) =>
            Task.FromResult(PlayerOperationResult.Failure(PlayerError.LoadFailed));

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));

        Assert.IsTrue(context.ViewModel.HasError);
        Assert.AreEqual("视频加载失败，请稍后重试", context.ViewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task LoadFailure_StopsPlayerToAvoidResidualAudio()
    {
        var context = CreateContext();
        context.PlayerService.LoadAsyncHandler = (_, _) =>
            Task.FromResult(PlayerOperationResult.Failure(PlayerError.LoadFailed));

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));

        Assert.AreEqual(2, context.PlayerService.StopCallCount);
    }

    [TestMethod]
    public async Task EndFileErrorEvent_ShowsChineseLoadFailure()
    {
        var context = CreateContext();

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
            PlayerPlaybackState.Failed,
            PlayerError.LoadFailed,
            "error"));

        Assert.IsFalse(context.ViewModel.IsPlaying);
        Assert.IsFalse(context.ViewModel.IsLoading);
        Assert.IsTrue(context.ViewModel.HasError);
        Assert.AreEqual("视频加载失败，请稍后重试", context.ViewModel.ErrorMessage);
        Assert.AreEqual("播放失败", context.ViewModel.StatusText);
    }

    [TestMethod]
    public async Task EndFileErrorEvent_StopsPlayerToAvoidResidualAudio()
    {
        var context = CreateContext();

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        var stopCountBeforeFailure = context.PlayerService.StopCallCount;

        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
            PlayerPlaybackState.Failed,
            PlayerError.LoadFailed,
            "error"));
        await Task.Delay(50);

        Assert.IsTrue(context.PlayerService.StopCallCount > stopCountBeforeFailure);
    }

    [TestMethod]
    public async Task CompletedEvent_ShowsPlaybackEnded()
    {
        var context = CreateContext();

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context);

        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
            PlayerPlaybackState.Completed));

        Assert.IsFalse(context.ViewModel.IsPlaying);
        Assert.IsFalse(context.ViewModel.IsPaused);
        Assert.AreEqual("播放结束", context.ViewModel.StatusText);
    }

    [TestMethod]
    public async Task ResumeFailureEvent_ShowsChineseErrorAndStopsPlayer()
    {
        var context = CreateContext();

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo(startPositionTicks: TimeSpan.FromMinutes(12).Ticks)));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        var stopCountBeforeFailure = context.PlayerService.StopCallCount;

        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
            PlayerPlaybackState.Failed,
            PlayerError.ResumeFailed));
        await Task.Delay(50);

        Assert.IsTrue(context.ViewModel.HasError);
        Assert.AreEqual("继续播放失败，请稍后重试", context.ViewModel.ErrorMessage);
        Assert.IsTrue(context.PlayerService.StopCallCount > stopCountBeforeFailure);
    }

    [TestMethod]
    public async Task DetachVideoHost_PreventsReusingOldHostForNextLoad()
    {
        var context = CreateContext();

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1111));
        context.ViewModel.DetachVideoHost(new IntPtr(1111));
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await Task.Yield();

        Assert.AreEqual(1, context.PlayerService.LoadCallCount);

        await context.ViewModel.AttachVideoHostAsync(new IntPtr(2222));

        Assert.AreEqual(2, context.PlayerService.LoadCallCount);
        Assert.AreEqual(new IntPtr(2222), context.PlayerService.LastLoadRequest!.VideoHostHandle);
    }

    [TestMethod]
    public async Task PlayingEvent_ReportsPlayingAndStartsTenSecondProgressScheduler()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);

        Assert.AreEqual(1, context.PlaybackReportService.PlayingCallCount);
        Assert.AreEqual("item-1", context.PlaybackReportService.LastStartRequest!.ItemId);
        Assert.AreEqual("media-source-1", context.PlaybackReportService.LastStartRequest.MediaSourceId);
        Assert.AreEqual("play-session-1", context.PlaybackReportService.LastStartRequest.PlaySessionId);
        Assert.AreEqual("DirectPlay", context.PlaybackReportService.LastStartRequest.PlayMethod);
        Assert.AreEqual(TimeSpan.FromSeconds(10), context.PlaybackReportScheduler.LastInterval);
    }

    [TestMethod]
    public async Task PlayingRaisedDuringLoad_WaitsForInitialConfigurationAndReportsAutoSelectedTrackOnce()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        {
            DefaultVolume = 63,
            DefaultAudioLanguage = "chi"
        };
        var playingCallCountBeforeConfiguration = -1;
        context.PlayerService.LoadAsyncHandler = (request, _) =>
        {
            context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
                request.PlaybackInstanceId,
                PlayerPlaybackState.Playing,
                tracks: new[]
                {
                    new PlayerTrackInfo(8, "audio", "jpn", "Japanese AAC", "aac", false, true, true, false, 3),
                    new PlayerTrackInfo(9, "audio", "chi", "Chinese AC3", "ac3", false, false, false, false, 4)
                }));
            playingCallCountBeforeConfiguration = context.PlaybackReportService.PlayingCallCount;
            return Task.FromResult(PlayerOperationResult.Success());
        };
        var playbackInfo = CreatePlaybackInfo(audioTracks: new[]
        {
            new PlaybackTrack(3, "jpn", "aac", "Japanese AAC", true),
            new PlaybackTrack(4, "chi", "ac3", "Chinese AC3", false)
        });

        context.ViewModel.Load(CreateNavigationParameter(playbackInfo));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));

        Assert.AreEqual(0, playingCallCountBeforeConfiguration);
        Assert.AreEqual(1, context.PlayerService.SelectAudioTrackCallCount);
        Assert.AreEqual(9, context.PlayerService.LastAudioTrackIndex);
        Assert.AreEqual(1, context.PlaybackReportService.PlayingCallCount);
        Assert.AreEqual(63, context.PlaybackReportService.LastStartRequest!.VolumeLevel);
        Assert.IsFalse(context.PlaybackReportService.LastStartRequest.IsMuted);
        Assert.AreEqual(4, context.PlaybackReportService.LastStartRequest.AudioStreamIndex);
        Assert.AreEqual(0, context.PlaybackReportService.ProgressCallCount);

        RaisePlaying(context);
        Assert.AreEqual(1, context.PlaybackReportService.PlayingCallCount);
    }

    [TestMethod]
    public async Task PlayingRaisedDuringLoad_WhenInitialVolumeFails_ReportsTrustedFallbackOnce()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with { DefaultVolume = 63 };
        context.PlayerService.SetVolumeAsyncHandler = (_, _) =>
            Task.FromResult(PlayerOperationResult.Failure(PlayerError.VolumeFailed));
        context.PlayerService.LoadAsyncHandler = (request, _) =>
        {
            context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
                request.PlaybackInstanceId,
                PlayerPlaybackState.Playing));
            return Task.FromResult(PlayerOperationResult.Success());
        };

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));

        Assert.AreEqual(1, context.PlaybackReportService.PlayingCallCount);
        Assert.AreEqual(100, context.PlaybackReportService.LastStartRequest!.VolumeLevel);
        Assert.IsFalse(context.PlaybackReportService.LastStartRequest.IsMuted);

        RaisePlaying(context);
        Assert.AreEqual(1, context.PlaybackReportService.PlayingCallCount);
    }

    [TestMethod]
    public async Task ConsecutivePlayback_WhenSecondInitialAudioStateFails_UsesNewSessionFallback()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with { DefaultVolume = 63 };
        await StartLoadedPlayerAsync(context);
        await context.ViewModel.SetVolumeAsync(42);
        await context.ViewModel.ToggleMuteAsync();
        Assert.AreEqual(42, context.ViewModel.Volume);
        Assert.IsTrue(context.ViewModel.IsMuted);

        context.PlayerService.SetVolumeAsyncHandler = (_, _) =>
            Task.FromResult(PlayerOperationResult.Failure(PlayerError.VolumeFailed));
        context.PlayerService.SetMuteAsyncHandler = (_, _) =>
            Task.FromResult(PlayerOperationResult.Failure(PlayerError.MuteFailed));
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo() with { ItemId = "item-2" }));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(5678));
        RaisePlaying(context);

        Assert.AreEqual(2, context.PlaybackReportService.PlayingCallCount);
        Assert.AreEqual(100, context.PlaybackReportService.LastStartRequest!.VolumeLevel);
        Assert.IsFalse(context.PlaybackReportService.LastStartRequest.IsMuted);
    }

    [TestMethod]
    public async Task AutomaticAudioSelection_CompletingAfterStartedReportsOneProgress()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        {
            DefaultAudioLanguage = "chi"
        };
        var selection = new TaskCompletionSource<PlayerOperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.PlayerService.SelectAudioTrackAsyncHandler = (_, _, _) => selection.Task;
        var playbackInfo = CreatePlaybackInfo(audioTracks: new[]
        {
            new PlaybackTrack(3, "jpn", "aac", "Japanese AAC", true),
            new PlaybackTrack(4, "chi", "ac3", "Chinese AC3", false)
        });
        context.ViewModel.Load(CreateNavigationParameter(playbackInfo));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));

        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(8, "audio", "jpn", "Japanese AAC", "aac", false, true, true, false, 3),
            new PlayerTrackInfo(9, "audio", "chi", "Chinese AC3", "ac3", false, false, false, false, 4)
        });
        Assert.AreEqual(1, context.PlaybackReportService.PlayingCallCount);
        Assert.AreEqual(3, context.PlaybackReportService.LastStartRequest!.AudioStreamIndex);
        Assert.AreEqual(0, context.PlaybackReportService.ProgressCallCount);

        var progressReported = new TaskCompletionSource<PlaybackReportProgressRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.PlaybackReportService.ReportProgressAsyncHandler = (_, request, _) =>
        {
            progressReported.TrySetResult(request);
            return Task.FromResult(PlaybackReportResult.Success());
        };
        selection.SetResult(PlayerOperationResult.Success());
        var progress = await progressReported.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, context.PlaybackReportService.ProgressCallCount);
        Assert.AreEqual(4, progress.AudioStreamIndex);
    }

    [TestMethod]
    public async Task UnmappedMpvIds_DoNotCollideWithMediaStreamIndexesOrHideExternalSubtitles()
    {
        var context = CreateContext();
        var playbackInfo = CreatePlaybackInfo(
            audioTracks: new[] { new PlaybackTrack(8, "jpn", "aac", "Mapped audio", true) },
            subtitles: new[]
            {
                new PlaybackSubtitle(
                    11,
                    "chi",
                    "ass",
                    "External Chinese",
                    false,
                    true,
                    "External",
                    "http://media.local/Videos/item-1/source-1/Subtitles/11/Stream.ass")
            });
        context.ViewModel.Load(CreateNavigationParameter(playbackInfo));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(3, "audio", "jpn", "Mapped audio", "aac", false, true, true, false, 8),
            new PlayerTrackInfo(8, "audio", "eng", "Unmapped audio", "aac", false, false, false, false),
            new PlayerTrackInfo(11, "sub", "eng", "Unmapped subtitle", "srt", false, false, false, false)
        });

        Assert.IsTrue(context.ViewModel.SubtitleTracks.Any(track =>
            !track.MpvTrackId.HasValue
            && track.MediaStreamIndex == 11
            && track.IsExternal));

        await context.ViewModel.SelectAudioTrackAsync(
            context.ViewModel.AudioTracks.Single(track => track.MpvTrackId == 8));

        Assert.AreEqual(8, context.PlayerService.LastAudioTrackIndex);
        Assert.IsNull(context.PlaybackReportService.LastProgressRequest!.AudioStreamIndex);

        await context.ViewModel.SelectSubtitleTrackAsync(
            context.ViewModel.SubtitleTracks.Single(track => track.MpvTrackId == 11));

        Assert.AreEqual(11, context.PlayerService.LastSubtitleTrackIndex);
        Assert.AreEqual(0, context.PlayerService.SelectExternalSubtitleCallCount);
        Assert.IsNull(context.PlaybackReportService.LastProgressRequest!.SubtitleStreamIndex);
    }

    [TestMethod]
    public async Task PlaybackReports_CaptureConfirmedPlayerStateWithoutVolumeSpam()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with { DefaultVolume = 63 };
        var playbackInfo = CreatePlaybackInfo(
            audioTracks: new[] { new PlaybackTrack(3, "jpn", "aac", "Japanese AAC", true) },
            subtitles: new[] { new PlaybackSubtitle(7, "chi", "ass", "Chinese ASS", true, false, null) });
        context.ViewModel.Load(CreateNavigationParameter(playbackInfo));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));

        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(8, "audio", "jpn", "Japanese AAC", "aac", false, true, true, false, 3),
            new PlayerTrackInfo(11, "sub", "chi", "Chinese ASS", "ass", false, true, true, false, 7)
        });

        var started = context.PlaybackReportService.LastStartRequest!;
        Assert.IsFalse(started.IsMuted);
        Assert.AreEqual(63, started.VolumeLevel);
        Assert.AreEqual(3, started.AudioStreamIndex);
        Assert.AreEqual(7, started.SubtitleStreamIndex);

        await context.ViewModel.SetVolumeAsync(42);
        await context.ViewModel.ToggleMuteAsync();
        Assert.AreEqual(0, context.PlaybackReportService.ProgressCallCount);

        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlaybackReportScheduler.RaiseTick(playbackInstanceId);
        await Task.Yield();

        var progress = context.PlaybackReportService.LastProgressRequest!;
        Assert.IsTrue(progress.IsMuted);
        Assert.AreEqual(42, progress.VolumeLevel);
        Assert.AreEqual(3, progress.AudioStreamIndex);
        Assert.AreEqual(7, progress.SubtitleStreamIndex);

        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();

        var stopped = context.PlaybackReportService.LastStoppedRequest!;
        Assert.IsTrue(stopped.IsMuted);
        Assert.AreEqual(42, stopped.VolumeLevel);
        Assert.AreEqual(3, stopped.AudioStreamIndex);
        Assert.AreEqual(7, stopped.SubtitleStreamIndex);
    }

    [TestMethod]
    public async Task PlayingReport_DoesNotUseUnmappedMpvTrackIdsAsStreamIndexes()
    {
        var context = CreateContext();
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));

        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(8, "audio", "jpn", "Japanese AAC", "aac", false, true, true, false),
            new PlayerTrackInfo(11, "sub", "chi", "Chinese ASS", "ass", false, true, true, false)
        });

        Assert.IsNull(context.PlaybackReportService.LastStartRequest!.AudioStreamIndex);
        Assert.IsNull(context.PlaybackReportService.LastStartRequest.SubtitleStreamIndex);
    }

    [TestMethod]
    public async Task ProgressEvents_DoNotReportUntilSchedulerTick()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;

        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(90),
            null,
            false));
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMinutes(90),
            null,
            false));

        Assert.AreEqual(0, context.PlaybackReportService.ProgressCallCount);

        context.PlaybackReportScheduler.RaiseTick(playbackInstanceId);
        await Task.Yield();

        Assert.AreEqual(1, context.PlaybackReportService.ProgressCallCount);
        Assert.AreEqual(TimeSpan.FromSeconds(2).Ticks, context.PlaybackReportService.LastProgressRequest!.PositionTicks);
    }

    [TestMethod]
    public async Task Progress_ReachingNinetyPercentNotifiesOnceAndKeepsPeriodicReports()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        var schedulerStopsBeforeThreshold = context.PlaybackReportScheduler.StopCallCount;

        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(81),
            TimeSpan.FromMinutes(90),
            null,
            false));
        await Task.Yield();

        Assert.AreEqual(1, context.PlaybackReportService.ProgressCallCount);
        Assert.AreEqual(TimeSpan.FromMinutes(81).Ticks, context.PlaybackReportService.LastProgressRequest!.PositionTicks);
        Assert.AreEqual(0, context.PlaybackReportService.StoppedCallCount);
        Assert.IsTrue(context.ViewModel.IsPlaying);

        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(82),
            TimeSpan.FromMinutes(90),
            null,
            false));
        context.PlaybackReportScheduler.RaiseTick(playbackInstanceId);
        await Task.Yield();

        Assert.AreEqual(2, context.PlaybackReportService.ProgressCallCount);
        Assert.AreEqual(TimeSpan.FromMinutes(82).Ticks, context.PlaybackReportService.LastProgressRequest!.PositionTicks);
        Assert.AreEqual(schedulerStopsBeforeThreshold, context.PlaybackReportScheduler.StopCallCount);

        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;
        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();

        var state = ((DetailNavigationParameter)navigationParameter!).PlaybackState!;
        Assert.IsTrue(state.IsPlayed);
        Assert.AreEqual(100d, state.PlayedPercentage);
        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
    }

    [DataTestMethod]
    [DataRow("Movie", 600, 480, false)]
    [DataRow("Movie", 600, 481, true)]
    [DataRow("episode", 600, 481, true)]
    [DataRow("Movie", 240, 121, true)]
    [DataRow("Episode", 120, 30, false)]
    [DataRow("Movie", 60, 10, false)]
    [DataRow("Video", 600, 481, false)]
    [DataRow(null, 600, 481, false)]
    public async Task Progress_RemainingTimeRuleUsesMovieOrEpisodeAndExcludesShortClips(
        string? mediaType,
        int durationSeconds,
        int positionSeconds,
        bool expectedPlayed)
    {
        var context = CreateContext();
        var duration = TimeSpan.FromSeconds(durationSeconds);
        await StartLoadedPlayerAsync(context, CreatePlaybackInfo(runTimeTicks: duration.Ticks) with
        {
            MediaType = mediaType
        });
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        var position = TimeSpan.FromSeconds(positionSeconds);
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            position,
            duration,
            null,
            false));
        await Task.Yield();

        Assert.AreEqual(expectedPlayed ? 1 : 0, context.PlaybackReportService.ProgressCallCount);
        Assert.AreEqual(0, context.PlaybackReportService.StoppedCallCount);
        Assert.IsTrue(context.ViewModel.IsPlaying);

        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;
        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();

        var state = ((DetailNavigationParameter)navigationParameter!).PlaybackState!;
        Assert.AreEqual(expectedPlayed, state.IsPlayed);
        Assert.AreEqual(position.Ticks, state.PositionTicks);
        Assert.AreEqual(position.Ticks, context.PlaybackReportService.LastStoppedRequest!.PositionTicks);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Progress_ThresholdReportFailureRecoversOnNextPeriodicTick(bool throwException)
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        var schedulerStopsBeforeThreshold = context.PlaybackReportScheduler.StopCallCount;
        context.PlaybackReportService.ReportProgressAsyncHandler = (_, _, _) => throwException
            ? throw new HttpRequestException("report unavailable")
            : Task.FromResult(PlaybackReportResult.Failure(PlaybackReportError.ServerError));
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(81),
            TimeSpan.FromMinutes(90),
            null,
            false));
        await Task.Yield();

        Assert.AreEqual(1, context.PlaybackReportService.ProgressCallCount);
        Assert.AreEqual("播放记录同步失败", context.ViewModel.StatusText);
        Assert.AreEqual(schedulerStopsBeforeThreshold, context.PlaybackReportScheduler.StopCallCount);

        context.PlaybackReportService.ReportProgressAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackReportResult.Success());
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(82),
            TimeSpan.FromMinutes(90),
            null,
            false));
        context.PlaybackReportScheduler.RaiseTick(playbackInstanceId);
        await Task.Yield();

        Assert.AreEqual(2, context.PlaybackReportService.ProgressCallCount);
        Assert.AreEqual(TimeSpan.FromMinutes(82).Ticks, context.PlaybackReportService.LastProgressRequest!.PositionTicks);
        Assert.AreEqual(0, context.PlaybackReportService.StoppedCallCount);
        Assert.IsTrue(context.ViewModel.IsPlaying);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
    }

    [TestMethod]
    public async Task Progress_AboveThresholdWhilePausedStillReturnsWatchedAtTheActualPosition()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            playbackInstanceId,
            PlayerPlaybackState.Paused));
        var progressReportsBeforePausedSnapshot = context.PlaybackReportService.ProgressCallCount;

        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(86),
            TimeSpan.FromMinutes(90),
            null,
            true));
        await Task.Yield();

        Assert.AreEqual(progressReportsBeforePausedSnapshot, context.PlaybackReportService.ProgressCallCount);

        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;
        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();

        var state = ((DetailNavigationParameter)navigationParameter!).PlaybackState!;
        Assert.IsTrue(state.IsPlayed);
        Assert.AreEqual(TimeSpan.FromMinutes(86).Ticks, state.PositionTicks);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SeekAcrossThresholdUsesFinalPositionForWatchedState(bool seekBack)
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;

        await context.ViewModel.SeekToPercentAsync(95);
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(86),
            TimeSpan.FromMinutes(90),
            null,
            false));
        await Task.Yield();

        Assert.AreEqual(0, context.PlaybackReportService.StoppedCallCount);
        Assert.IsTrue(context.ViewModel.IsPlaying);
        if (seekBack)
        {
            await context.ViewModel.SeekToPercentAsync(50);
        }

        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;
        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();

        var state = ((DetailNavigationParameter)navigationParameter!).PlaybackState!;
        Assert.AreEqual(!seekBack, state.IsPlayed);
        Assert.AreEqual(TimeSpan.FromMinutes(seekBack ? 45 : 86).Ticks, state.PositionTicks);
        Assert.AreEqual(state.PositionTicks, context.PlaybackReportService.LastStoppedRequest!.PositionTicks);
    }

    [TestMethod]
    public async Task PauseCommand_ReportsPausedProgressImmediately()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);

        await ((AsyncRelayCommand)context.ViewModel.PauseCommand).ExecuteAsync();

        Assert.AreEqual(1, context.PlaybackReportService.ProgressCallCount);
        Assert.IsTrue(context.PlaybackReportService.LastProgressRequest!.IsPaused);
    }

    [TestMethod]
    public async Task PlayCommand_ReportsResumeProgressImmediately()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        await ((AsyncRelayCommand)context.ViewModel.PauseCommand).ExecuteAsync();

        await ((AsyncRelayCommand)context.ViewModel.PlayCommand).ExecuteAsync();

        Assert.AreEqual(2, context.PlaybackReportService.ProgressCallCount);
        Assert.IsFalse(context.PlaybackReportService.LastProgressRequest!.IsPaused);
    }

    [TestMethod]
    public async Task SeekCompleted_ReportsProgressImmediately()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);

        context.ViewModel.BeginSeekDrag();
        await context.ViewModel.CompleteSeekDragAsync(50);

        Assert.AreEqual(1, context.PlaybackReportService.ProgressCallCount);
        Assert.AreEqual(TimeSpan.FromMinutes(45).Ticks, context.PlaybackReportService.LastProgressRequest!.PositionTicks);
    }

    [TestMethod]
    public async Task SeekToPercentAsync_CallsSeekAndReportsProgress()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);

        await context.ViewModel.SeekToPercentAsync(25);

        Assert.AreEqual(1, context.PlayerService.SeekCallCount);
        Assert.AreEqual(TimeSpan.FromMinutes(22.5), context.PlayerService.LastSeekPosition);
        Assert.AreEqual(1, context.PlaybackReportService.ProgressCallCount);
    }

    [TestMethod]
    public async Task SeekToPercentAsync_ImmediatelyShowsTargetWhileSeekIsPending()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var seekCompletion = new TaskCompletionSource<PlayerOperationResult>();
        context.PlayerService.SeekAsyncHandler = (_, _, _) => seekCompletion.Task;

        var seekTask = context.ViewModel.SeekToPercentAsync(50);
        await Task.Yield();

        Assert.AreEqual("45:00", context.ViewModel.CurrentTimeText);
        Assert.AreEqual(50, context.ViewModel.SeekPercent, 0.01);
        Assert.AreEqual("正在跳转...", context.ViewModel.StatusText);
        Assert.AreEqual(1, context.PlayerService.SeekCallCount);

        seekCompletion.SetResult(PlayerOperationResult.Success());
        await seekTask;
    }

    [TestMethod]
    public async Task SeekToPercentAsync_IgnoresProgressEventsWhileSeekIsPending()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        var seekCompletion = new TaskCompletionSource<PlayerOperationResult>();
        context.PlayerService.SeekAsyncHandler = (_, _, _) => seekCompletion.Task;

        var seekTask = context.ViewModel.SeekToPercentAsync(50);
        await Task.Yield();
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(90),
            1,
            false));

        Assert.AreEqual("45:00", context.ViewModel.CurrentTimeText);
        Assert.AreEqual(50, context.ViewModel.SeekPercent, 0.01);

        seekCompletion.SetResult(PlayerOperationResult.Success());
        await seekTask;
    }

    [TestMethod]
    public async Task SeekToPercentAsync_AfterSuccessAcceptsProgressEventsAgain()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;

        await context.ViewModel.SeekToPercentAsync(50);
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(46),
            TimeSpan.FromMinutes(90),
            null,
            false));

        Assert.AreEqual("46:00", context.ViewModel.CurrentTimeText);
        Assert.AreEqual(51.11, context.ViewModel.ProgressPercent, 0.1);
    }

    [TestMethod]
    public async Task SeekToPercentAsync_FailureRestoresLastRealProgress()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(90),
            null,
            false));
        context.PlayerService.SeekAsyncHandler = (_, _, _) =>
            Task.FromResult(PlayerOperationResult.Failure(PlayerError.SeekFailed));

        await context.ViewModel.SeekToPercentAsync(50);

        Assert.AreEqual("05:00", context.ViewModel.CurrentTimeText);
        Assert.AreEqual(5.55, context.ViewModel.SeekPercent, 0.1);
        Assert.AreEqual("跳转失败，请稍后重试", context.ViewModel.StatusText);
        Assert.AreEqual(0, context.PlaybackReportService.ProgressCallCount);
    }

    [TestMethod]
    public async Task SeekToPercentAsync_UnknownDurationDoesNotSeek()
    {
        var context = CreateContext();
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo(runTimeTicks: 0)));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context);

        await context.ViewModel.SeekToPercentAsync(50);

        Assert.AreEqual(0, context.PlayerService.SeekCallCount);
    }

    [TestMethod]
    public async Task BackCommand_ReportsStoppedAndStopsProgressScheduler()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;

        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();

        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
        Assert.AreEqual("item-1", context.PlaybackReportService.LastStoppedRequest!.ItemId);
        Assert.IsTrue(context.PlaybackReportScheduler.StopCallCount > 0);
        Assert.AreEqual(playbackInstanceId, context.PlaybackReportScheduler.LastStoppedPlaybackInstanceId);
    }

    [TestMethod]
    public async Task ApplicationExit_ReportsStoppedBeforeReleasingLocalPlayer()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var calls = new List<string>();
        context.PlaybackReportService.ReportStoppedAsyncHandler = (_, _, _) =>
        {
            calls.Add("stopped-report");
            return Task.FromResult(PlaybackReportResult.Success());
        };
        context.PlayerService.StopAsyncHandler = _ =>
        {
            calls.Add("local-stop");
            return Task.FromResult(PlayerOperationResult.Success());
        };

        await context.ViewModel.PrepareForApplicationExitAsync();

        CollectionAssert.AreEqual(new[] { "stopped-report", "local-stop" }, calls);
        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
    }

    [TestMethod]
    public async Task ApplicationExit_AfterBackDoesNotReportStoppedAgain()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);

        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();
        await context.ViewModel.PrepareForApplicationExitAsync();

        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
    }

    [TestMethod]
    public async Task CompletedEvent_ReportsFinalRuntimeAndStopsExactlyOnce()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(90),
            null,
            false));

        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            playbackInstanceId,
            PlayerPlaybackState.Completed));
        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            playbackInstanceId,
            PlayerPlaybackState.Completed));
        await Task.Yield();

        Assert.AreEqual(1, context.PlaybackReportService.ProgressCallCount);
        Assert.AreEqual(TimeSpan.FromMinutes(90).Ticks, context.PlaybackReportService.LastProgressRequest!.PositionTicks);
        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
        Assert.AreEqual(TimeSpan.FromMinutes(90).Ticks, context.PlaybackReportService.LastStoppedRequest!.PositionTicks);
        Assert.AreEqual(playbackInstanceId, context.PlaybackReportScheduler.LastStoppedPlaybackInstanceId);
    }

    [TestMethod]
    public async Task ReportPositionTicks_AreClampedToKnownRuntime()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;

        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(100),
            TimeSpan.FromMinutes(90),
            null,
            false));
        context.PlaybackReportScheduler.RaiseTick(playbackInstanceId);
        await Task.Yield();

        Assert.AreEqual(TimeSpan.FromMinutes(90).Ticks, context.PlaybackReportService.LastProgressRequest!.PositionTicks);
    }

    [TestMethod]
    public async Task ReportUnauthorized_ClearsSessionStopsPlaybackAndNavigatesLogin()
    {
        var context = CreateContext();
        context.PlaybackReportService.ReportProgressAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackReportResult.Failure(PlaybackReportError.Unauthorized));
        await StartLoadedPlayerAsync(context);

        await ((AsyncRelayCommand)context.ViewModel.PauseCommand).ExecuteAsync();

        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.IsNull(context.CurrentSessionService.CurrentSession);
        Assert.AreEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.IsTrue(context.PlayerService.StopCallCount > 0);
    }

    [TestMethod]
    public async Task ReportUnauthorized_PersistentClearFailureStillStopsAndNavigatesLoginOnce()
    {
        var context = CreateContext();
        context.AuthSessionStore.ClearAsyncHandler = _ => throw new IOException("credential cleanup failed");
        context.PlaybackReportService.ReportProgressAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackReportResult.Failure(PlaybackReportError.Unauthorized));
        var loginNavigationCount = 0;
        context.NavigationService.CurrentPageChanged += (_, args) =>
        {
            if (args.CurrentPage == AppPage.Login)
            {
                loginNavigationCount++;
            }
        };
        await StartLoadedPlayerAsync(context);

        await ((AsyncRelayCommand)context.ViewModel.PauseCommand).ExecuteAsync();
        await ((AsyncRelayCommand)context.ViewModel.PauseCommand).ExecuteAsync();

        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
        Assert.IsNull(context.CurrentSessionService.CurrentSession);
        Assert.AreEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual(1, loginNavigationCount);
        Assert.IsTrue(context.PlayerService.StopCallCount > 0);
        Assert.AreEqual("登录状态已失效，请重新登录", context.LastLoginError);
    }

    [TestMethod]
    public async Task ReportUnauthorized_PersistentClearFailureDoesNotBlockLaterPlaybackInstance()
    {
        var context = CreateContext();
        context.AuthSessionStore.ClearAsyncHandler = _ => throw new IOException("credential cleanup failed");
        context.PlaybackReportService.ReportProgressAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackReportResult.Failure(PlaybackReportError.Unauthorized));
        await StartLoadedPlayerAsync(context);
        await ((AsyncRelayCommand)context.ViewModel.PauseCommand).ExecuteAsync();
        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);

        context.CurrentSessionService.SetSession(context.Session);
        await StartLoadedPlayerAsync(
            context,
            CreatePlaybackInfo(playbackPath: "http://media.local/Videos/item-2/stream.mkv") with
            {
                ItemId = "item-2",
                Title = "Second Item"
            });
        await ((AsyncRelayCommand)context.ViewModel.PauseCommand).ExecuteAsync();

        Assert.AreEqual(2, context.AuthSessionStore.ClearCallCount);
        Assert.IsNull(context.CurrentSessionService.CurrentSession);
        Assert.AreEqual(AppPage.Login, context.NavigationService.CurrentPage);
    }

    [TestMethod]
    public async Task ReportForbidden_KeepsSessionAndLocalPlayback()
    {
        var context = CreateContext();
        context.PlaybackReportService.ReportProgressAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackReportResult.Failure(PlaybackReportError.Forbidden));
        await StartLoadedPlayerAsync(context);
        var stopCallCount = context.PlayerService.StopCallCount;

        await ((AsyncRelayCommand)context.ViewModel.PauseCommand).ExecuteAsync();

        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
        Assert.AreNotEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual(stopCallCount, context.PlayerService.StopCallCount);
        Assert.IsTrue(context.ViewModel.IsPaused);
        Assert.AreEqual("播放记录同步失败", context.ViewModel.StatusText);
    }

    [TestMethod]
    public async Task ReportPlayingException_ShowsWarningAndStillStartsProgressScheduler()
    {
        var context = CreateContext();
        context.PlaybackReportService.ReportPlayingAsyncHandler = (_, _, _) =>
            throw new IOException("settings unavailable");

        await StartLoadedPlayerAsync(context);

        Assert.AreEqual(1, context.PlaybackReportScheduler.StartCallCount);
        Assert.AreEqual("播放记录同步失败", context.ViewModel.StatusText);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
        Assert.AreNotEqual(AppPage.Login, context.NavigationService.CurrentPage);
    }

    [TestMethod]
    public async Task ReportProgressException_IsObservedAndKeepsLocalPlayback()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        context.PlaybackReportService.ReportProgressAsyncHandler = (_, _, _) =>
            throw new UnauthorizedAccessException("settings denied");

        await ((AsyncRelayCommand)context.ViewModel.PauseCommand).ExecuteAsync();

        Assert.AreEqual("播放记录同步失败", context.ViewModel.StatusText);
        Assert.IsTrue(context.ViewModel.IsPaused);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
        Assert.AreNotEqual(AppPage.Login, context.NavigationService.CurrentPage);
    }

    [TestMethod]
    public async Task BackCommand_WhenStoppedReportUnauthorized_StaysOnLogin()
    {
        var context = CreateContext();
        context.PlaybackReportService.ReportStoppedAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackReportResult.Failure(PlaybackReportError.Unauthorized));
        await StartLoadedPlayerAsync(context);

        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();

        Assert.AreEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual(1, context.AuthSessionStore.ClearCallCount);
    }

    [DataTestMethod]
    [DataRow(PlaybackReportError.ServerUnreachable)]
    [DataRow(PlaybackReportError.ServerTimeout)]
    [DataRow(PlaybackReportError.ServerError)]
    public async Task ReportTransportFailure_DoesNotStopLocalPlayback(PlaybackReportError error)
    {
        var context = CreateContext();
        context.PlaybackReportService.ReportProgressAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackReportResult.Failure(error));
        await StartLoadedPlayerAsync(context);
        var stopCallCount = context.PlayerService.StopCallCount;

        await ((AsyncRelayCommand)context.ViewModel.PauseCommand).ExecuteAsync();

        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
        Assert.AreNotEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual(stopCallCount, context.PlayerService.StopCallCount);
        Assert.IsTrue(context.ViewModel.IsPaused);
        Assert.AreEqual("播放记录同步失败", context.ViewModel.StatusText);
    }

    [TestMethod]
    public async Task ReportCancelled_DoesNotShowFailureOrStopLocalPlayback()
    {
        var context = CreateContext();
        context.PlaybackReportService.ReportProgressAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackReportResult.Failure(PlaybackReportError.Cancelled));
        await StartLoadedPlayerAsync(context);
        var stopCallCount = context.PlayerService.StopCallCount;

        await ((AsyncRelayCommand)context.ViewModel.PauseCommand).ExecuteAsync();

        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
        Assert.AreEqual(stopCallCount, context.PlayerService.StopCallCount);
        Assert.AreEqual("已暂停", context.ViewModel.StatusText);
    }

    [TestMethod]
    public async Task RepeatedProgressReportFailure_ShowsWarningOncePerPlaybackInstance()
    {
        var context = CreateContext();
        context.PlaybackReportService.ReportProgressAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackReportResult.Failure(PlaybackReportError.Forbidden));
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        var warningChangeCount = 0;
        context.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PlayerViewModel.StatusText)
                && context.ViewModel.StatusText == "播放记录同步失败")
            {
                warningChangeCount++;
            }
        };

        context.PlaybackReportScheduler.RaiseTick(playbackInstanceId);
        await Task.Yield();
        context.PlaybackReportScheduler.RaiseTick(playbackInstanceId);
        await Task.Yield();

        Assert.AreEqual(2, context.PlaybackReportService.ProgressCallCount);
        Assert.AreEqual(1, warningChangeCount);
    }

    [TestMethod]
    public async Task BackCommand_WaitsForInFlightProgressAndDropsQueuedProgressBeforeStopped()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        var progressStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProgress = new TaskCompletionSource<PlaybackReportResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var networkCalls = new List<string>();
        context.PlaybackReportService.ReportProgressAsyncHandler = (_, _, _) =>
        {
            networkCalls.Add("progress");
            progressStarted.TrySetResult();
            return releaseProgress.Task;
        };
        context.PlaybackReportService.ReportStoppedAsyncHandler = (_, _, _) =>
        {
            networkCalls.Add("stopped");
            return Task.FromResult(PlaybackReportResult.Success());
        };

        context.PlaybackReportScheduler.RaiseTick(playbackInstanceId);
        await progressStarted.Task;
        context.PlaybackReportScheduler.RaiseTick(playbackInstanceId);
        var backTask = ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();
        await Task.Yield();

        Assert.AreEqual(0, context.PlaybackReportService.StoppedCallCount);
        releaseProgress.SetResult(PlaybackReportResult.Success());
        await backTask;

        CollectionAssert.AreEqual(new[] { "progress", "stopped" }, networkCalls);
        Assert.AreEqual(1, context.PlaybackReportService.ProgressCallCount);
        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
    }

    [TestMethod]
    public async Task BackCommand_WaitsForInFlightPlayingBeforeStopped()
    {
        var context = CreateContext();
        var playingStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePlaying = new TaskCompletionSource<PlaybackReportResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var networkCalls = new List<string>();
        context.PlaybackReportService.ReportPlayingAsyncHandler = (_, _, _) =>
        {
            networkCalls.Add("playing");
            playingStarted.TrySetResult();
            return releasePlaying.Task;
        };
        context.PlaybackReportService.ReportStoppedAsyncHandler = (_, _, _) =>
        {
            networkCalls.Add("stopped");
            return Task.FromResult(PlaybackReportResult.Success());
        };
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context);
        await playingStarted.Task;

        var backTask = ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();
        await Task.Yield();

        Assert.AreEqual(0, context.PlaybackReportService.StoppedCallCount);
        releasePlaying.SetResult(PlaybackReportResult.Success());
        await backTask;

        CollectionAssert.AreEqual(new[] { "playing", "stopped" }, networkCalls);
        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
        Assert.AreEqual(0, context.PlaybackReportScheduler.StartCallCount);
    }

    [TestMethod]
    public async Task BackCommand_AwaitsExistingStoppedReportAndKeepsItsSyncResult()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        var stoppedStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStopped = new TaskCompletionSource<PlaybackReportResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.PlaybackReportService.ReportStoppedAsyncHandler = (_, _, _) =>
        {
            stoppedStarted.TrySetResult();
            return releaseStopped.Task;
        };
        var lastPolledPosition = TimeSpan.FromMinutes(10);
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            lastPolledPosition,
            TimeSpan.FromMinutes(90),
            null,
            false));
        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            playbackInstanceId,
            PlayerPlaybackState.Completed));
        await stoppedStarted.Task;
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(60),
            TimeSpan.FromMinutes(90),
            null,
            false));
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;

        var backTask = ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();
        await Task.Yield();

        Assert.IsFalse(backTask.IsCompleted);
        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
        releaseStopped.SetResult(PlaybackReportResult.Failure(PlaybackReportError.ServerError));
        await backTask;

        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
        var state = ((DetailNavigationParameter)navigationParameter!).PlaybackState!;
        Assert.AreEqual(TimeSpan.FromMinutes(90).Ticks, context.PlaybackReportService.LastStoppedRequest?.PositionTicks);
        Assert.AreEqual(context.PlaybackReportService.LastStoppedRequest?.PositionTicks, state.PositionTicks);
        Assert.IsFalse(state.IsSynchronized);
        Assert.IsTrue(state.IsPlayed);
    }

    [TestMethod]
    public async Task OldPlaybackInstance_DoesNotContinueReporting()
    {
        var context = CreateContext();
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1111));
        var firstInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        RaisePlaying(context);

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(2222));
        RaisePlaying(context);

        context.PlaybackReportScheduler.RaiseTick(firstInstanceId);
        await Task.Yield();

        Assert.AreEqual(0, context.PlaybackReportService.ProgressCallCount);
    }

    [TestMethod]
    public async Task NextEpisode_EnteringLastFortyFiveSecondsShowsPrompt()
    {
        var context = CreateContextWithNextEpisode();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;

        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(89) + TimeSpan.FromSeconds(20),
            TimeSpan.FromMinutes(90),
            null,
            false));

        Assert.IsTrue(context.ViewModel.HasNextEpisode);
        Assert.IsTrue(context.ViewModel.IsNextEpisodePromptVisible);
        Assert.AreEqual("S1:E2", context.ViewModel.NextEpisodeNumberText);
        Assert.AreEqual("即将播放下一集", context.ViewModel.NextEpisodeStatusText);
    }

    [TestMethod]
    public async Task NextEpisode_SeekingAwayAndBackUpdatesPromptVisibility()
    {
        var context = CreateContextWithNextEpisode();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(89) + TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(90),
            null,
            false));

        await context.ViewModel.SeekToPercentAsync(50);
        Assert.IsFalse(context.ViewModel.IsNextEpisodePromptVisible);

        await context.ViewModel.SeekToPercentAsync(99.5);
        Assert.IsTrue(context.ViewModel.IsNextEpisodePromptVisible);
    }

    [TestMethod]
    public async Task NextEpisode_ShortEpisodeUsesLastFifteenPercent()
    {
        var context = CreateContextWithNextEpisode();
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo(runTimeTicks: TimeSpan.FromMinutes(4).Ticks)));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;

        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(24),
            TimeSpan.FromMinutes(4),
            null,
            false));

        Assert.IsTrue(context.ViewModel.IsNextEpisodePromptVisible);
    }

    [TestMethod]
    public async Task NoNextEpisode_NeverShowsPrompt()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
            TimeSpan.FromMinutes(89) + TimeSpan.FromSeconds(50),
            TimeSpan.FromMinutes(90),
            null,
            false));

        Assert.IsFalse(context.ViewModel.HasNextEpisode);
        Assert.IsFalse(context.ViewModel.IsNextEpisodePromptVisible);
    }

    [TestMethod]
    public async Task SkipIntro_WhenPositionIsInsideMarkerRangeSeeksToIntroEnd()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context, CreatePlaybackInfo(markers: new[]
        {
            new PlaybackMarker(PlaybackMarkerType.IntroStart, TimeSpan.FromSeconds(15).Ticks),
            new PlaybackMarker(PlaybackMarkerType.IntroEnd, TimeSpan.FromMinutes(1.5).Ticks)
        }));
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;

        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(90),
            null,
            false));

        Assert.IsTrue(context.ViewModel.IsSkipSegmentVisible);
        Assert.AreEqual("跳过片头", context.ViewModel.SkipSegmentButtonText);
        await ((AsyncRelayCommand)context.ViewModel.SkipSegmentCommand).ExecuteAsync();
        Assert.AreEqual(1, context.PlayerService.SeekCallCount);
        Assert.AreEqual(TimeSpan.FromMinutes(1.5), context.PlayerService.LastSeekPosition);
        Assert.IsFalse(context.ViewModel.IsSkipSegmentVisible);
        Assert.AreEqual(1, context.PlaybackReportService.ProgressCallCount);
    }

    [TestMethod]
    public async Task SkipIntro_PreservesPausedStateAndBlocksRepeatedClick()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context, CreatePlaybackInfo(markers: new[]
        {
            new PlaybackMarker(PlaybackMarkerType.IntroStart, TimeSpan.FromSeconds(10).Ticks),
            new PlaybackMarker(PlaybackMarkerType.IntroEnd, TimeSpan.FromSeconds(80).Ticks)
        }));
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromSeconds(20),
            TimeSpan.FromMinutes(90),
            null,
            false));
        await ((AsyncRelayCommand)context.ViewModel.PauseCommand).ExecuteAsync();
        var seekCompletion = new TaskCompletionSource<PlayerOperationResult>();
        context.PlayerService.SeekAsyncHandler = (_, _, _) => seekCompletion.Task;

        var firstClick = ((AsyncRelayCommand)context.ViewModel.SkipSegmentCommand).ExecuteAsync();
        var secondClick = ((AsyncRelayCommand)context.ViewModel.SkipSegmentCommand).ExecuteAsync();

        Assert.AreEqual(1, context.PlayerService.SeekCallCount);
        seekCompletion.SetResult(PlayerOperationResult.Success());
        await Task.WhenAll(firstClick, secondClick);
        Assert.IsTrue(context.ViewModel.IsPaused);
        Assert.IsFalse(context.ViewModel.IsPlaying);
    }

    [TestMethod]
    public async Task SkipIntro_MissingOrderedEndMarkerDoesNotShowAction()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context, CreatePlaybackInfo(markers: new[]
        {
            new PlaybackMarker(PlaybackMarkerType.IntroEnd, TimeSpan.FromSeconds(10).Ticks),
            new PlaybackMarker(PlaybackMarkerType.IntroStart, TimeSpan.FromSeconds(20).Ticks)
        }));

        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(90),
            null,
            false));

        Assert.IsFalse(context.ViewModel.IsSkipSegmentVisible);
        Assert.IsFalse(context.ViewModel.SkipSegmentCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task CreditsMarker_WithNextEpisodeShowsExistingNextEpisodeCardEarly()
    {
        var context = CreateContextWithNextEpisode();
        await StartLoadedPlayerAsync(context, CreatePlaybackInfo(markers: new[]
        {
            new PlaybackMarker(PlaybackMarkerType.CreditsStart, TimeSpan.FromMinutes(82).Ticks)
        }));

        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
            TimeSpan.FromMinutes(82),
            TimeSpan.FromMinutes(90),
            null,
            false));

        Assert.IsTrue(context.ViewModel.IsNextEpisodePromptVisible);
        Assert.IsFalse(context.ViewModel.IsSkipSegmentVisible);
        Assert.AreEqual("即将播放下一集", context.ViewModel.NextEpisodeStatusText);
    }

    [TestMethod]
    public async Task CreditsMarker_WithoutNextEpisodeDoesNotShowTailAction()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context, CreatePlaybackInfo(markers: new[]
        {
            new PlaybackMarker(PlaybackMarkerType.CreditsStart, TimeSpan.FromMinutes(82).Ticks)
        }));

        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
            TimeSpan.FromMinutes(82),
            TimeSpan.FromMinutes(90),
            null,
            false));

        Assert.IsFalse(context.ViewModel.IsSkipSegmentVisible);
        Assert.IsFalse(context.ViewModel.SkipSegmentCommand.CanExecute(null));
        Assert.IsFalse(context.ViewModel.IsNextEpisodePromptVisible);
        Assert.AreEqual(0, context.PlayerService.SeekCallCount);
    }

    [TestMethod]
    public async Task CreditsMarker_NextEpisodeLookupFailureDoesNotShowTailAction()
    {
        var context = CreateContext();
        context.NextEpisodeService.GetNextEpisodeAsyncHandler = (_, _, _) =>
            Task.FromResult(NextEpisodeResult.Failure(NextEpisodeError.ServerTimeout));
        await StartLoadedPlayerAsync(context, CreatePlaybackInfo(markers: new[]
        {
            new PlaybackMarker(PlaybackMarkerType.CreditsStart, TimeSpan.FromMinutes(82).Ticks)
        }));

        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
            TimeSpan.FromMinutes(82),
            TimeSpan.FromMinutes(90),
            null,
            false));

        Assert.IsFalse(context.ViewModel.IsSkipSegmentVisible);
        Assert.IsFalse(context.ViewModel.IsNextEpisodePromptVisible);
    }

    [TestMethod]
    public async Task NoCreditsMarker_KeepsExistingLastFortyFiveSecondsPromptRule()
    {
        var context = CreateContextWithNextEpisode();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;

        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(82),
            TimeSpan.FromMinutes(90),
            null,
            false));
        Assert.IsFalse(context.ViewModel.IsNextEpisodePromptVisible);

        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(89) + TimeSpan.FromSeconds(20),
            TimeSpan.FromMinutes(90),
            null,
            false));
        Assert.IsTrue(context.ViewModel.IsNextEpisodePromptVisible);
    }

    [TestMethod]
    public async Task PlayNextEpisode_ReportsStopsPreparesAndLoadsNewInstance()
    {
        var context = CreateContextWithNextEpisode();
        await StartLoadedPlayerAsync(context);
        var firstInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        var stopCountBefore = context.PlayerService.StopCallCount;
        var callOrder = new List<string>();
        context.PlaybackPreparationService.PreparePlaybackAsyncHandler = (_, request, _) =>
        {
            callOrder.Add("prepare");
            Assert.AreEqual(stopCountBefore, context.PlayerService.StopCallCount);
            Assert.AreEqual(0, context.PlaybackReportService.StoppedCallCount);
            return Task.FromResult(PlaybackLoadResult.Success(CreatePlaybackInfoForRequest(request)));
        };
        context.PlayerService.StopAsyncHandler = _ =>
        {
            callOrder.Add("stop");
            return Task.FromResult(PlayerOperationResult.Success());
        };
        context.PlaybackReportService.ReportStoppedAsyncHandler = (_, _, _) =>
        {
            callOrder.Add("report");
            return Task.FromResult(PlaybackReportResult.Success());
        };
        context.PlayerService.LoadAsyncHandler = (_, _) =>
        {
            callOrder.Add("load");
            return Task.FromResult(PlayerOperationResult.Success());
        };

        await ((AsyncRelayCommand)context.ViewModel.PlayNextEpisodeCommand).ExecuteAsync();

        CollectionAssert.AreEqual(
            new[] { "prepare", "stop", "report", "load" },
            callOrder);
        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
        Assert.IsTrue(context.PlayerService.StopCallCount > stopCountBefore);
        Assert.AreEqual("episode-2", context.PlaybackPreparationService.LastRequest?.ItemId);
        Assert.AreEqual("episode-2", context.PlayerService.LastLoadRequest?.PlaybackInfo.ItemId);
        Assert.AreEqual(
            "http://media.local:8096/Items/series-1/Images/Logo",
            context.ViewModel.LogoUrl);
        Assert.IsTrue(context.PlayerService.LastLoadRequest?.PlaybackInstanceId > firstInstanceId);

        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;
        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();

        Assert.IsInstanceOfType<DetailNavigationParameter>(navigationParameter);
        var detailParameter = (DetailNavigationParameter)navigationParameter!;
        Assert.AreEqual("series-1", detailParameter.ItemId);
        Assert.AreEqual(AppPage.Home, detailParameter.ReturnPage);
        Assert.AreEqual("season-1", detailParameter.SelectedSeasonId);
        Assert.AreEqual("episode-2", detailParameter.FocusedEpisodeId);
        Assert.AreEqual("episode-2", detailParameter.FallbackItemId);
    }

    [TestMethod]
    public async Task PlayNextEpisode_CrossSeasonUpdatesSeriesReturnContext()
    {
        var context = CreateContext();
        context.NextEpisodeService.GetNextEpisodeAsyncHandler = (_, _, _) => Task.FromResult(
            NextEpisodeResult.Success(new NextEpisodeInfo(
                "season-2-episode-1",
                "新一季",
                2,
                1,
                TimeSpan.FromMinutes(45).Ticks,
                SeriesId: "series-1",
                SeasonId: "season-2")));
        await StartLoadedPlayerAsync(context);

        await ((AsyncRelayCommand)context.ViewModel.PlayNextEpisodeCommand).ExecuteAsync();
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;
        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();

        Assert.IsInstanceOfType<DetailNavigationParameter>(navigationParameter);
        var detailParameter = (DetailNavigationParameter)navigationParameter!;
        Assert.AreEqual("series-1", detailParameter.ItemId);
        Assert.AreEqual("season-2", detailParameter.SelectedSeasonId);
        Assert.AreEqual("season-2-episode-1", detailParameter.FocusedEpisodeId);
        Assert.AreEqual("season-2-episode-1", detailParameter.FallbackItemId);
    }

    [TestMethod]
    public async Task PlayNextEpisode_FromEpisodeDetailRemovesSameSeriesSelfBackTarget()
    {
        var context = CreateContextWithNextEpisode();
        var playbackInfo = CreatePlaybackInfo();
        context.ViewModel.Load(new PlayerNavigationParameter(
            playbackInfo,
            new DetailNavigationParameter(
                "episode-1",
                AppPage.Home,
                new DetailBackTarget(AppPage.Detail, "series-1", AppPage.Home, "season-1"))));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context);

        await ((AsyncRelayCommand)context.ViewModel.PlayNextEpisodeCommand).ExecuteAsync();
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;
        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();

        Assert.IsInstanceOfType<DetailNavigationParameter>(navigationParameter);
        var detailParameter = (DetailNavigationParameter)navigationParameter!;
        Assert.AreEqual("series-1", detailParameter.ItemId);
        Assert.IsNull(detailParameter.BackTarget);
        Assert.AreEqual("season-1", detailParameter.SelectedSeasonId);
        Assert.AreEqual("episode-2", detailParameter.FocusedEpisodeId);
    }

    [TestMethod]
    public async Task PlayNextEpisode_PreservesDifferentDetailBackTarget()
    {
        var context = CreateContextWithNextEpisode();
        var expectedBackTarget = new DetailBackTarget(
            AppPage.Detail,
            "series-2",
            AppPage.Library,
            "season-3");
        context.ViewModel.Load(new PlayerNavigationParameter(
            CreatePlaybackInfo(),
            new DetailNavigationParameter(
                "episode-1",
                AppPage.Library,
                expectedBackTarget)));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context);

        await ((AsyncRelayCommand)context.ViewModel.PlayNextEpisodeCommand).ExecuteAsync();
        object? navigationParameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => navigationParameter = args.Parameter;
        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();

        Assert.IsInstanceOfType<DetailNavigationParameter>(navigationParameter);
        var detailParameter = (DetailNavigationParameter)navigationParameter!;
        Assert.AreEqual("series-1", detailParameter.ItemId);
        Assert.AreEqual(expectedBackTarget, detailParameter.BackTarget);
    }

    [TestMethod]
    public async Task PlayNextEpisode_WhilePreparingIgnoresRepeatedClick()
    {
        var context = CreateContextWithNextEpisode();
        var pending = new TaskCompletionSource<PlaybackLoadResult>();
        context.PlaybackPreparationService.PreparePlaybackAsyncHandler = (_, _, _) => pending.Task;
        await StartLoadedPlayerAsync(context);

        var firstClick = ((AsyncRelayCommand)context.ViewModel.PlayNextEpisodeCommand).ExecuteAsync();
        var secondClick = ((AsyncRelayCommand)context.ViewModel.PlayNextEpisodeCommand).ExecuteAsync();
        Assert.AreEqual(1, context.PlaybackPreparationService.PrepareCallCount);

        pending.SetResult(PlaybackLoadResult.Failure(PlaybackLoadError.ServerError));
        await Task.WhenAll(firstClick, secondClick);
    }

    [TestMethod]
    public async Task PlayNextEpisode_PreparationFailureRestoresInteractiveState()
    {
        var context = CreateContextWithNextEpisode();
        context.PlaybackPreparationService.PreparePlaybackAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackLoadResult.Failure(PlaybackLoadError.ServerUnreachable));
        await StartLoadedPlayerAsync(context);
        var stopCountBefore = context.PlayerService.StopCallCount;

        await ((AsyncRelayCommand)context.ViewModel.PlayNextEpisodeCommand).ExecuteAsync();

        Assert.IsFalse(context.ViewModel.IsPreparingNextEpisode);
        Assert.IsNull(context.ViewModel.ErrorMessage);
        Assert.AreEqual("下一集播放准备失败，请稍后重试", context.ViewModel.NextEpisodeStatusText);
        Assert.AreEqual(stopCountBefore, context.PlayerService.StopCallCount);
        Assert.AreEqual(0, context.PlaybackReportService.StoppedCallCount);
        Assert.IsTrue(context.ViewModel.IsPlaying);
        Assert.IsTrue(context.ViewModel.IsNextEpisodePromptVisible);
        Assert.IsTrue(context.ViewModel.PlayNextEpisodeCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task PlayNextEpisode_ForbiddenKeepsSessionAndCurrentPlaybackInteractive()
    {
        var context = CreateContextWithNextEpisode();
        context.PlaybackPreparationService.PreparePlaybackAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackLoadResult.Failure(PlaybackLoadError.Forbidden));
        await StartLoadedPlayerAsync(context);
        var stopCountBefore = context.PlayerService.StopCallCount;

        await ((AsyncRelayCommand)context.ViewModel.PlayNextEpisodeCommand).ExecuteAsync();

        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
        Assert.AreNotEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual(stopCountBefore, context.PlayerService.StopCallCount);
        Assert.IsTrue(context.ViewModel.IsPlaying);
        Assert.AreEqual("没有权限播放下一集", context.ViewModel.NextEpisodeStatusText);
    }

    [TestMethod]
    public async Task PlayNextEpisode_StopFailureKeepsCurrentPlaybackInteractive()
    {
        var context = CreateContextWithNextEpisode();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        var loadCallCount = context.PlayerService.LoadCallCount;
        var stopCallCount = context.PlayerService.StopCallCount;
        context.PlayerService.StopAsyncHandler = _ =>
            Task.FromResult(PlayerOperationResult.Failure(PlayerError.PlaybackFailed));

        await ((AsyncRelayCommand)context.ViewModel.PlayNextEpisodeCommand).ExecuteAsync();

        Assert.AreEqual(loadCallCount, context.PlayerService.LoadCallCount);
        Assert.AreEqual(stopCallCount + 1, context.PlayerService.StopCallCount);
        Assert.AreEqual(playbackInstanceId, context.PlayerService.LastLoadRequest.PlaybackInstanceId);
        Assert.AreEqual("item-1", context.PlayerService.LastLoadRequest.PlaybackInfo.ItemId);
        Assert.AreEqual(0, context.PlaybackReportService.StoppedCallCount);
        Assert.IsTrue(context.ViewModel.IsPlaying);
        Assert.IsTrue(context.ViewModel.IsNextEpisodePromptVisible);
        Assert.IsTrue(context.ViewModel.PlayNextEpisodeCommand.CanExecute(null));
        Assert.AreEqual("无法切换下一集，请稍后重试", context.ViewModel.NextEpisodeStatusText);
    }

    [TestMethod]
    public async Task PlayNextEpisode_StoppedReportFailureStillLoadsWithoutRetry()
    {
        var context = CreateContextWithNextEpisode();
        await StartLoadedPlayerAsync(context);
        context.PlaybackReportService.ReportStoppedAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackReportResult.Failure(PlaybackReportError.ServerError));

        await ((AsyncRelayCommand)context.ViewModel.PlayNextEpisodeCommand).ExecuteAsync();

        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
        Assert.AreEqual("episode-2", context.PlayerService.LastLoadRequest?.PlaybackInfo.ItemId);
        Assert.AreEqual("播放记录同步失败", context.ViewModel.StatusText);
    }

    [TestMethod]
    public async Task PlayNextEpisode_StoppedReportExceptionStillLoadsWithoutRetry()
    {
        var context = CreateContextWithNextEpisode();
        await StartLoadedPlayerAsync(context);
        context.PlaybackReportService.ReportStoppedAsyncHandler = (_, _, _) =>
            throw new InvalidOperationException("report unavailable");

        await ((AsyncRelayCommand)context.ViewModel.PlayNextEpisodeCommand).ExecuteAsync();

        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
        Assert.AreEqual("episode-2", context.PlayerService.LastLoadRequest?.PlaybackInfo.ItemId);
        Assert.AreEqual("播放记录同步失败", context.ViewModel.StatusText);
    }

    [TestMethod]
    public async Task PlayNextEpisode_StoppedReportPendingAndCompletionReportsAtMostOnce()
    {
        var context = CreateContextWithNextEpisode();
        var reportStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingReport = new TaskCompletionSource<PlaybackReportResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await StartLoadedPlayerAsync(context);
        var oldInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlaybackReportService.ReportStoppedAsyncHandler = (_, _, _) =>
        {
            reportStarted.TrySetResult();
            return pendingReport.Task;
        };

        var playNextTask = ((AsyncRelayCommand)context.ViewModel.PlayNextEpisodeCommand).ExecuteAsync();
        await reportStarted.Task;
        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            oldInstanceId,
            PlayerPlaybackState.Completed));
        await Task.Yield();

        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
        pendingReport.SetResult(PlaybackReportResult.Success());
        await playNextTask;

        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
        Assert.AreEqual("episode-2", context.PlayerService.LastLoadRequest?.PlaybackInfo.ItemId);
    }

    [TestMethod]
    public async Task PlayNextEpisode_OldStoppedReportUnauthorizedDoesNotAffectNewInstance()
    {
        var context = CreateContextWithNextEpisode();
        var reportStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingReport = new TaskCompletionSource<PlaybackReportResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await StartLoadedPlayerAsync(context);
        context.PlaybackReportService.ReportStoppedAsyncHandler = (_, _, _) =>
        {
            reportStarted.TrySetResult();
            return pendingReport.Task;
        };

        var playNextTask = ((AsyncRelayCommand)context.ViewModel.PlayNextEpisodeCommand).ExecuteAsync();
        await reportStarted.Task;
        var replacement = CreatePlaybackInfo() with
        {
            ItemId = "replacement-item",
            Title = "Replacement Item",
            PlaySessionId = "replacement-session"
        };
        context.ViewModel.Load(CreateNavigationParameter(replacement));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(5678));
        RaisePlaying(context);

        pendingReport.SetResult(PlaybackReportResult.Failure(PlaybackReportError.Unauthorized));
        await playNextTask;

        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.IsNotNull(context.CurrentSessionService.CurrentSession);
        Assert.AreNotEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual("replacement-item", context.PlayerService.LastLoadRequest?.PlaybackInfo.ItemId);
    }

    [TestMethod]
    public async Task PlayNextEpisode_ManualPreparationOverlappingCompletionReportsStoppedOnce()
    {
        var context = CreateContextWithNextEpisode();
        var pending = new TaskCompletionSource<PlaybackLoadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.PlaybackPreparationService.PreparePlaybackAsyncHandler = (_, _, _) => pending.Task;
        await StartLoadedPlayerAsync(context);
        var oldInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;

        var playNextTask = ((AsyncRelayCommand)context.ViewModel.PlayNextEpisodeCommand).ExecuteAsync();
        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            oldInstanceId,
            PlayerPlaybackState.Completed));
        await Task.Yield();
        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);

        pending.SetResult(PlaybackLoadResult.Success(CreatePlaybackInfoForRequest(
            context.PlaybackPreparationService.LastRequest!)));
        await playNextTask;

        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
        Assert.AreEqual("episode-2", context.PlayerService.LastLoadRequest?.PlaybackInfo.ItemId);
        Assert.IsTrue(context.PlayerService.LastLoadRequest?.PlaybackInstanceId > oldInstanceId);
    }

    [TestMethod]
    public async Task ReleasePlayer_CancelsNextEpisodePreparationWithoutStoppingForReplacement()
    {
        var context = CreateContextWithNextEpisode();
        var pending = new TaskCompletionSource<PlaybackLoadResult>();
        context.PlaybackPreparationService.PreparePlaybackAsyncHandler = (_, _, _) => pending.Task;
        await StartLoadedPlayerAsync(context);
        var stopCountBefore = context.PlayerService.StopCallCount;

        var prepareTask = ((AsyncRelayCommand)context.ViewModel.PlayNextEpisodeCommand).ExecuteAsync();
        var releaseTask = context.ViewModel.ReleasePlayerAsync();
        Assert.IsTrue(context.PlaybackPreparationService.LastCancellationToken.IsCancellationRequested);

        pending.SetResult(PlaybackLoadResult.Success(CreatePlaybackInfo()));
        await Task.WhenAll(prepareTask, releaseTask);

        Assert.AreEqual(stopCountBefore + 1, context.PlayerService.StopCallCount);
        Assert.AreEqual(0, context.PlaybackReportService.StoppedCallCount);
        Assert.AreEqual("item-1", context.PlayerService.LastLoadRequest?.PlaybackInfo.ItemId);
    }

    [TestMethod]
    public async Task PlayNextEpisode_OldInstanceProgressDoesNotUpdateNewPlayback()
    {
        var context = CreateContextWithNextEpisode();
        await StartLoadedPlayerAsync(context);
        var oldInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        await ((AsyncRelayCommand)context.ViewModel.PlayNextEpisodeCommand).ExecuteAsync();

        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            oldInstanceId,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(60),
            50,
            false));

        Assert.AreEqual("00:00", context.ViewModel.CurrentTimeText);
        Assert.AreNotEqual(50, context.ViewModel.ProgressPercent);
    }

    [TestMethod]
    public async Task AutoPlayNextEpisode_LastTenSecondsShowsMediaTimeCountdown()
    {
        var context = CreateContextWithNextEpisode();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;

        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(89) + TimeSpan.FromSeconds(51),
            TimeSpan.FromMinutes(90),
            null,
            false));

        Assert.IsTrue(context.ViewModel.IsAutoPlayCountdownVisible);
        Assert.AreEqual("9 秒后播放下一集", context.ViewModel.AutoPlayCountdownText);
        Assert.AreEqual("9 秒后播放下一集", context.ViewModel.NextEpisodeStatusText);
        Assert.AreEqual(10d, context.ViewModel.AutoPlayCountdownProgress, 0.01);
        await ((AsyncRelayCommand)context.ViewModel.PauseCommand).ExecuteAsync();
        await Task.Delay(20);
        Assert.AreEqual("9 秒后播放下一集", context.ViewModel.AutoPlayCountdownText);
    }

    [TestMethod]
    public async Task AutoPlayNextEpisode_DisabledDoesNotShowCountdownOrSwitchOnCompletion()
    {
        var context = CreateContextWithNextEpisode();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        {
            AutoPlayNextEpisode = false
        };
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(89) + TimeSpan.FromSeconds(55),
            TimeSpan.FromMinutes(90),
            null,
            false));

        Assert.IsFalse(context.ViewModel.IsAutoPlayCountdownVisible);
        Assert.IsTrue(context.ViewModel.IsNextEpisodePromptVisible);
        Assert.IsTrue(context.ViewModel.CancelAutoPlayNextEpisodeCommand.CanExecute(null));
        context.ViewModel.CancelAutoPlayNextEpisodeCommand.Execute(null);
        Assert.IsFalse(context.ViewModel.IsNextEpisodePromptVisible);
        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            playbackInstanceId,
            PlayerPlaybackState.Completed));
        await Task.Yield();

        Assert.AreEqual(0, context.PlaybackPreparationService.PrepareCallCount);
    }

    [TestMethod]
    public async Task AutoPlayNextEpisode_SeekAwayAndBackUpdatesCountdown()
    {
        var context = CreateContextWithNextEpisode();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(89) + TimeSpan.FromSeconds(55),
            TimeSpan.FromMinutes(90),
            null,
            false));
        Assert.IsTrue(context.ViewModel.IsAutoPlayCountdownVisible);

        await context.ViewModel.SeekToPercentAsync(50);
        Assert.IsFalse(context.ViewModel.IsAutoPlayCountdownVisible);

        await context.ViewModel.SeekToPercentAsync(99.9);
        Assert.IsTrue(context.ViewModel.IsAutoPlayCountdownVisible);
    }

    [TestMethod]
    public async Task AutoPlayNextEpisode_CancelCurrentItemPreventsAutomaticSwitchButKeepsManualCommand()
    {
        var context = CreateContextWithNextEpisode();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(89) + TimeSpan.FromSeconds(55),
            TimeSpan.FromMinutes(90),
            null,
            false));

        context.ViewModel.CancelAutoPlayNextEpisodeCommand.Execute(null);
        Assert.IsFalse(context.ViewModel.IsNextEpisodePromptVisible);
        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            playbackInstanceId,
            PlayerPlaybackState.Completed));
        await Task.Yield();

        Assert.IsTrue(context.ViewModel.IsAutoPlayCancelledForCurrentItem);
        Assert.AreEqual(0, context.PlaybackPreparationService.PrepareCallCount);
        Assert.IsTrue(context.ViewModel.PlayNextEpisodeCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task NextEpisodeCard_CanCloseBeforeCountdownAndDoesNotReappear()
    {
        var context = CreateContextWithNextEpisode();
        await StartLoadedPlayerAsync(context, CreatePlaybackInfo(markers: new[]
        {
            new PlaybackMarker(PlaybackMarkerType.CreditsStart, TimeSpan.FromMinutes(82).Ticks)
        }));
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(82),
            TimeSpan.FromMinutes(90),
            null,
            false));

        Assert.IsTrue(context.ViewModel.IsNextEpisodePromptVisible);
        Assert.IsFalse(context.ViewModel.IsAutoPlayCountdownVisible);
        Assert.IsTrue(context.ViewModel.CancelAutoPlayNextEpisodeCommand.CanExecute(null));

        context.ViewModel.CancelAutoPlayNextEpisodeCommand.Execute(null);
        Assert.IsFalse(context.ViewModel.IsNextEpisodePromptVisible);

        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(89),
            TimeSpan.FromMinutes(90),
            null,
            false));
        Assert.IsFalse(context.ViewModel.IsNextEpisodePromptVisible);
    }

    [TestMethod]
    public async Task ReleasePlayer_CancelsNextEpisodeLookupAndIgnoresLateUnauthorized()
    {
        var context = CreateContext();
        var pending = new TaskCompletionSource<NextEpisodeResult>();
        context.NextEpisodeService.GetNextEpisodeAsyncHandler = (_, _, _) => pending.Task;
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));

        var releaseTask = context.ViewModel.ReleasePlayerAsync();
        Assert.IsTrue(context.NextEpisodeService.LastCancellationToken.IsCancellationRequested);
        pending.SetResult(NextEpisodeResult.Failure(NextEpisodeError.Unauthorized));
        await releaseTask;
        await Task.Yield();

        Assert.AreNotEqual(AppPage.Login, context.NavigationService.CurrentPage);
        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
    }

    [TestMethod]
    public async Task NextEpisodeLookup_ForbiddenDoesNotClearSession()
    {
        var context = CreateContext();
        context.NextEpisodeService.GetNextEpisodeAsyncHandler = (_, _, _) =>
            Task.FromResult(NextEpisodeResult.Failure(NextEpisodeError.Forbidden));

        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        await Task.Yield();

        Assert.AreEqual(0, context.AuthSessionStore.ClearCallCount);
        Assert.AreSame(context.Session, context.CurrentSessionService.CurrentSession);
        Assert.AreNotEqual(AppPage.Login, context.NavigationService.CurrentPage);
    }

    [TestMethod]
    public async Task LoadingNewMediaCancelsOldNextEpisodeLookupAndIgnoresLateResult()
    {
        var context = CreateContext();
        var firstPending = new TaskCompletionSource<NextEpisodeResult>();
        var call = 0;
        context.NextEpisodeService.GetNextEpisodeAsyncHandler = (_, _, _) =>
        {
            call++;
            return call == 1
                ? firstPending.Task
                : Task.FromResult(NextEpisodeResult.Success(null));
        };
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo()));
        var firstToken = context.NextEpisodeService.LastCancellationToken;

        var secondPlaybackInfo = CreatePlaybackInfo(
            playbackPath: "http://media.local/Videos/item-2/stream.mkv") with
        {
            ItemId = "item-2",
            Title = "Second Item"
        };
        context.ViewModel.Load(CreateNavigationParameter(secondPlaybackInfo));
        Assert.IsTrue(firstToken.IsCancellationRequested);
        firstPending.SetResult(NextEpisodeResult.Success(new NextEpisodeInfo(
            "stale-episode",
            "过期单集",
            1,
            99,
            TimeSpan.FromMinutes(45).Ticks)));
        await Task.Yield();

        Assert.IsFalse(context.ViewModel.HasNextEpisode);
    }

    [TestMethod]
    public async Task AutoPlayNextEpisode_NaturalCompletionSwitchesExactlyOnceAndResetsCancellation()
    {
        var context = CreateContextWithNextEpisode();
        await StartLoadedPlayerAsync(context);
        var oldInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            oldInstanceId,
            TimeSpan.FromMinutes(81),
            TimeSpan.FromMinutes(90),
            null,
            false));
        await Task.Yield();

        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            oldInstanceId,
            PlayerPlaybackState.Completed));
        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            oldInstanceId,
            PlayerPlaybackState.Completed));
        await Task.Yield();

        Assert.AreEqual(1, context.PlaybackPreparationService.PrepareCallCount);
        Assert.IsTrue(context.PlayerService.LastLoadRequest!.PlaybackInstanceId > oldInstanceId);
        Assert.IsFalse(context.ViewModel.IsAutoPlayCancelledForCurrentItem);
        Assert.AreEqual(2, context.PlaybackReportService.ProgressCallCount);
        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
        Assert.AreEqual(TimeSpan.FromMinutes(90).Ticks, context.PlaybackReportService.LastStoppedRequest!.PositionTicks);
    }

    [TestMethod]
    public async Task AutoPlayNextEpisode_NaturalCompletionPreparationFailureReportsStoppedOnce()
    {
        var context = CreateContextWithNextEpisode();
        context.PlaybackPreparationService.PreparePlaybackAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackLoadResult.Failure(PlaybackLoadError.ServerError));
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;

        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            playbackInstanceId,
            PlayerPlaybackState.Completed));
        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            playbackInstanceId,
            PlayerPlaybackState.Completed));
        await Task.Yield();

        Assert.AreEqual(1, context.PlaybackPreparationService.PrepareCallCount);
        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
        Assert.AreEqual("item-1", context.PlaybackReportService.LastStoppedRequest?.ItemId);
        Assert.AreEqual(playbackInstanceId, context.PlayerService.LastLoadRequest.PlaybackInstanceId);
        Assert.AreEqual("下一集播放准备失败，请稍后重试", context.ViewModel.NextEpisodeStatusText);
    }

    [TestMethod]
    public async Task AutoPlayNextEpisode_NewEpisodeResetsCurrentItemCancellation()
    {
        var context = CreateContextWithNextEpisode();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            TimeSpan.FromMinutes(89) + TimeSpan.FromSeconds(55),
            TimeSpan.FromMinutes(90),
            null,
            false));
        context.ViewModel.CancelAutoPlayNextEpisodeCommand.Execute(null);
        Assert.IsTrue(context.ViewModel.IsAutoPlayCancelledForCurrentItem);

        await ((AsyncRelayCommand)context.ViewModel.PlayNextEpisodeCommand).ExecuteAsync();

        Assert.IsFalse(context.ViewModel.IsAutoPlayCancelledForCurrentItem);
    }

    [TestMethod]
    public async Task AutoPlayNextEpisode_UserBackDoesNotSwitch()
    {
        var context = CreateContextWithNextEpisode();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        var stoppedStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStopped = new TaskCompletionSource<PlaybackReportResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.PlaybackReportService.ReportStoppedAsyncHandler = (_, _, _) =>
        {
            stoppedStarted.TrySetResult();
            return releaseStopped.Task;
        };

        var backTask = ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();
        await stoppedStarted.Task;
        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            playbackInstanceId,
            PlayerPlaybackState.Completed));
        await Task.Yield();

        Assert.AreEqual(0, context.PlaybackPreparationService.PrepareCallCount);
        Assert.AreEqual(0, context.PlaybackReportService.ProgressCallCount);

        releaseStopped.SetResult(PlaybackReportResult.Success());
        await backTask;
        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
    }

    [TestMethod]
    public async Task AutoPlayNextEpisode_BackDuringFinalProgressDoesNotPrepareOrSwitch()
    {
        var context = CreateContextWithNextEpisode();
        await StartLoadedPlayerAsync(context);
        var playbackInstanceId = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        var initialLoadCount = context.PlayerService.LoadCallCount;
        var finalProgressStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinalProgress = new TaskCompletionSource<PlaybackReportResult>();
        var stoppedStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStopped = new TaskCompletionSource<PlaybackReportResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.PlaybackReportService.ReportProgressAsyncHandler = (_, _, _) =>
        {
            finalProgressStarted.TrySetResult();
            return releaseFinalProgress.Task;
        };
        context.PlaybackReportService.ReportStoppedAsyncHandler = (_, _, _) =>
        {
            stoppedStarted.TrySetResult();
            return releaseStopped.Task;
        };

        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            playbackInstanceId,
            PlayerPlaybackState.Completed));
        await finalProgressStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var backTask = ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();
        Assert.AreEqual(0, context.PlaybackReportService.StoppedCallCount);
        releaseFinalProgress.SetResult(PlaybackReportResult.Success());
        await stoppedStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Yield();

        Assert.AreEqual(0, context.PlaybackPreparationService.PrepareCallCount);
        Assert.AreEqual(initialLoadCount, context.PlayerService.LoadCallCount);
        Assert.AreEqual(1, context.PlaybackReportService.ProgressCallCount);
        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);

        releaseStopped.SetResult(PlaybackReportResult.Success());
        await backTask;
        Assert.AreEqual(AppPage.Detail, context.NavigationService.CurrentPage);
        Assert.AreEqual(0, context.PlaybackPreparationService.PrepareCallCount);
        Assert.AreEqual(initialLoadCount, context.PlayerService.LoadCallCount);
        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
    }

    [TestMethod]
    public async Task AutoPlayNextEpisode_LoadFailureDoesNotSwitch()
    {
        var context = CreateContextWithNextEpisode();
        await StartLoadedPlayerAsync(context);
        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
            PlayerPlaybackState.Failed,
            PlayerError.PlaybackFailed));
        await Task.Yield();

        Assert.AreEqual(0, context.PlaybackPreparationService.PrepareCallCount);
    }

    private static PlayerViewModelTestContext CreateContextWithNextEpisode()
    {
        var context = CreateContext();
        context.NextEpisodeService.GetNextEpisodeAsyncHandler = (_, _, _) => Task.FromResult(
            NextEpisodeResult.Success(new NextEpisodeInfo(
                "episode-2",
                "第二集",
                1,
                2,
                TimeSpan.FromMinutes(45).Ticks,
                "http://media.local:8096/Items/series-1/Images/Logo",
                SeriesId: "series-1",
                SeasonId: "season-1")));
        return context;
    }

    private static PlayerViewModelTestContext CreateContext(EmbyPlayer.Core.PlaybackQueue.IPlaybackQueueService? queue = null)
    {
        return new PlayerViewModelTestContext(queue);
    }

    private static void RaisePlaying(PlayerViewModelTestContext context)
    {
        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
            PlayerPlaybackState.Playing));
    }

    private static void RaisePlaying(PlayerViewModelTestContext context, long playbackInstanceId)
    {
        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            playbackInstanceId,
            PlayerPlaybackState.Playing));
    }

    private static void RaisePlaying(
        PlayerViewModelTestContext context,
        IReadOnlyList<PlayerTrackInfo> tracks)
    {
        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
            PlayerPlaybackState.Playing,
            tracks: tracks));
    }

    private static async Task StartLoadedPlayerAsync(PlayerViewModelTestContext context)
    {
        await StartLoadedPlayerAsync(context, CreatePlaybackInfo());
    }

    private static async Task StartLoadedPlayerAsync(
        PlayerViewModelTestContext context,
        PlaybackInfo playbackInfo)
    {
        context.ViewModel.Load(CreateNavigationParameter(playbackInfo));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context);
    }

    private static PlayerNavigationParameter CreateNavigationParameter(PlaybackInfo playbackInfo)
    {
        return new PlayerNavigationParameter(
            playbackInfo,
            new DetailNavigationParameter("item-1", AppPage.Home));
    }

    private static PlaybackInfo CreatePlaybackInfo(
        string? playbackPath = "http://media.local/Videos/item-1/stream.mkv",
        long startPositionTicks = 0,
        long? runTimeTicks = null,
        IReadOnlyList<PlaybackTrack>? audioTracks = null,
        IReadOnlyList<PlaybackSubtitle>? subtitles = null,
        IReadOnlyList<PlaybackMarker>? markers = null)
    {
        return new PlaybackInfo(
            "item-1",
            "Playback Item",
            "play-session-1",
            new PlaybackMediaSource(
                "media-source-1",
                "mkv",
                SupportsDirectPlay: true,
                SupportsDirectStream: true,
                SupportsTranscoding: false,
                new Dictionary<string, string>
                {
                    ["X-Test-Header"] = "header-value"
                }),
            playbackPath,
            RequiresTranscoding: false,
            RequiresTokenInUrl: false,
            runTimeTicks ?? TimeSpan.FromMinutes(90).Ticks,
            startPositionTicks,
            audioTracks ?? Array.Empty<PlaybackTrack>(),
            subtitles ?? Array.Empty<PlaybackSubtitle>(),
            Markers: markers);
    }

    private static PlaybackInfo CreatePlaybackInfoForRequest(PlaybackStartRequest request)
    {
        return CreatePlaybackInfo(startPositionTicks: request.StartPositionTicks) with
        {
            ItemId = request.ItemId,
            Title = request.Title,
            MediaType = request.MediaType
        };
    }

    private sealed class PlayerViewModelTestContext
    {
        public PlayerViewModelTestContext(EmbyPlayer.Core.PlaybackQueue.IPlaybackQueueService? queue = null)
        {
            NavigationService = new NavigationService();
            PlayerService = new TestPlayerService();
            PlaybackReportService = new TestPlaybackReportService();
            PlaybackReportScheduler = new TestPlaybackReportScheduler();
            CurrentSessionService = new CurrentSessionService();
            AuthSessionStore = new TestAuthSessionStore();
            AppSettingsService = new TestAppSettingsService();
            NextEpisodeService = new TestNextEpisodeService();
            PlaybackPreparationService = new TestPlaybackService();
            Session = CreateSession();
            CurrentSessionService.SetSession(Session);
            ViewModel = new PlayerViewModel(
                NavigationService,
                PlayerService,
                PlaybackReportService,
                CurrentSessionService,
                AuthSessionStore,
                message => LastLoginError = message,
                PlaybackReportScheduler,
                AppSettingsService,
                NextEpisodeService,
                PlaybackPreparationService,
                queue);
        }

        public NavigationService NavigationService { get; }

        public TestPlayerService PlayerService { get; }

        public TestPlaybackReportService PlaybackReportService { get; }

        public TestPlaybackReportScheduler PlaybackReportScheduler { get; }

        public CurrentSessionService CurrentSessionService { get; }

        public TestAuthSessionStore AuthSessionStore { get; }

        public TestAppSettingsService AppSettingsService { get; }

        public TestNextEpisodeService NextEpisodeService { get; }

        public TestPlaybackService PlaybackPreparationService { get; }

        public AuthSession Session { get; }

        public string? LastLoginError { get; private set; }

        public PlayerViewModel ViewModel { get; }
    }

    private static AuthSession CreateSession()
    {
        return new AuthSession(
            "http://media.local:8096",
            "test-access-token",
            "user-1",
            "Test User",
            "server-1");
    }
}
