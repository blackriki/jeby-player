using EmbyPlayer.Core.Playback;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Concurrent;

namespace EmbyPlayer.Player.Tests;

[TestClass]
public sealed partial class MpvPlayerServiceLifecycleTests
{
    [TestMethod]
    public async Task StopAsync_StopsEventLoopBeforeDestroyHandle()
    {
        var nativeApi = new FakeMpvNativeApi();
        var service = CreateService(nativeApi);

        await service.LoadAsync(CreateRequest(), CancellationToken.None);
        Assert.IsTrue(nativeApi.WaitUntilEventLoopEntered());
        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, nativeApi.DestroyCallCount);
        AssertSequence(nativeApi.Calls, "wakeup", "wait-exit", "destroy");
    }

    [TestMethod]
    public async Task StopAsync_CalledMultipleTimes_DestroysHandleOnce()
    {
        var nativeApi = new FakeMpvNativeApi();
        var service = CreateService(nativeApi);

        await service.LoadAsync(CreateRequest(), CancellationToken.None);
        Assert.IsTrue(nativeApi.WaitUntilEventLoopEntered());
        await service.StopAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, nativeApi.DestroyCallCount);
    }

    [TestMethod]
    public async Task DisposeAsync_CalledMultipleTimes_DestroysHandleOnce()
    {
        var nativeApi = new FakeMpvNativeApi();
        var service = CreateService(nativeApi);

        await service.LoadAsync(CreateRequest(), CancellationToken.None);
        Assert.IsTrue(nativeApi.WaitUntilEventLoopEntered());
        await service.DisposeAsync();
        await service.DisposeAsync();

        Assert.AreEqual(1, nativeApi.DestroyCallCount);
    }

    [TestMethod]
    public async Task LoadAsync_InitializationAndLoadFailures_CanRetry()
    {
        var nativeApi = new FakeMpvNativeApi();
        nativeApi.InitializeResults.Enqueue(-1);
        nativeApi.LoadFileResults.Enqueue(-1);
        nativeApi.LoadFileResults.Enqueue(0);
        var service = CreateService(nativeApi);

        var initializationFailure = await service.LoadAsync(CreateRequest(), CancellationToken.None);
        var loadFailure = await service.LoadAsync(CreateRequest(), CancellationToken.None);
        var retry = await service.LoadAsync(CreateRequest(), CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(PlayerError.InitializationFailed, initializationFailure.Error);
        Assert.AreEqual(PlayerError.LoadFailed, loadFailure.Error);
        Assert.IsTrue(retry.IsSuccess);
        Assert.AreEqual(3, nativeApi.DestroyCallCount);
    }

    [TestMethod]
    public async Task LoadTimeoutAndFileLoaded_OnlyFirstOutcomeWins()
    {
        var loadedDelay = new ControlledLoadTimeout();
        var loadedNativeApi = new FakeMpvNativeApi();
        var loadedService = new MpvPlayerService(
            loadedNativeApi,
            new FakePlayerDiagnostics(),
            loadedDelay.DelayAsync);
        var playing = new TaskCompletionSource<PlayerStatusChangedEventArgs>();
        var loadedFailureCount = 0;
        loadedService.StatusChanged += (_, e) =>
        {
            if (e.State == PlayerPlaybackState.Playing)
            {
                playing.TrySetResult(e);
            }
            else if (e.State == PlayerPlaybackState.Failed)
            {
                Interlocked.Increment(ref loadedFailureCount);
            }
        };

        await loadedService.LoadAsync(CreateRequest(), CancellationToken.None);
        var loadedTimeout = await loadedDelay.NextAsync();
        loadedNativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.FileLoaded, 0));
        loadedNativeApi.NotifyEventAvailable();
        await playing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        loadedTimeout.Complete();
        await loadedTimeout.Returned.WaitAsync(TimeSpan.FromSeconds(2));
        await loadedService.StopAsync(CancellationToken.None);

        Assert.AreEqual(TimeSpan.FromSeconds(30), loadedTimeout.Duration);
        Assert.AreEqual(0, loadedFailureCount);

        var timeoutDelay = new ControlledLoadTimeout();
        var timeoutNativeApi = new FakeMpvNativeApi();
        var timeoutService = new MpvPlayerService(
            timeoutNativeApi,
            new FakePlayerDiagnostics(),
            timeoutDelay.DelayAsync);
        var failed = new TaskCompletionSource<PlayerStatusChangedEventArgs>();
        var timeoutFailureCount = 0;
        timeoutService.StatusChanged += (_, e) =>
        {
            if (e.State == PlayerPlaybackState.Failed)
            {
                Interlocked.Increment(ref timeoutFailureCount);
                failed.TrySetResult(e);
            }
        };

        await timeoutService.LoadAsync(CreateRequest(), CancellationToken.None);
        var pendingTimeout = await timeoutDelay.NextAsync();
        pendingTimeout.Complete();
        var failure = await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        timeoutNativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.FileLoaded, 0));
        timeoutNativeApi.NotifyEventAvailable();
        await timeoutService.StopAsync(CancellationToken.None);

        Assert.AreEqual(PlayerError.LoadFailed, failure.Error);
        Assert.AreEqual(1, timeoutFailureCount);
    }

    [TestMethod]
    public async Task StopAndReload_CancelSupersededLoadTimeouts()
    {
        var loadTimeout = new ControlledLoadTimeout();
        var nativeApi = new FakeMpvNativeApi();
        var service = new MpvPlayerService(
            nativeApi,
            new FakePlayerDiagnostics(),
            loadTimeout.DelayAsync);
        var playing = new TaskCompletionSource<PlayerStatusChangedEventArgs>();
        var failureCount = 0;
        service.StatusChanged += (_, e) =>
        {
            if (e.State == PlayerPlaybackState.Playing)
            {
                playing.TrySetResult(e);
            }
            else if (e.State == PlayerPlaybackState.Failed)
            {
                Interlocked.Increment(ref failureCount);
            }
        };

        await service.LoadAsync(CreateRequest(playbackInstanceId: 41), CancellationToken.None);
        var firstTimeout = await loadTimeout.NextAsync();
        var reload = await service.LoadAsync(CreateRequest(playbackInstanceId: 42), CancellationToken.None);
        var secondTimeout = await loadTimeout.NextAsync();
        await firstTimeout.Cancelled.WaitAsync(TimeSpan.FromSeconds(2));
        nativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.FileLoaded, 0));
        nativeApi.NotifyEventAvailable();
        await playing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);
        await secondTimeout.Cancelled.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(reload.IsSuccess);
        Assert.AreEqual(0, failureCount);
    }

    [TestMethod]
    public async Task EventLoop_EndFileError_RaisesFailedState()
    {
        var nativeApi = new FakeMpvNativeApi();
        nativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.EndFile, -13, 4, -13));
        var diagnostics = new FakePlayerDiagnostics();
        var service = new MpvPlayerService(nativeApi, diagnostics);
        var status = new TaskCompletionSource<PlayerStatusChangedEventArgs>();
        service.StatusChanged += (_, e) =>
        {
            if (e.State == PlayerPlaybackState.Failed)
            {
                status.TrySetResult(e);
            }
        };

        var request = CreateRequest();
        await service.LoadAsync(request, CancellationToken.None);
        var eventArgs = await status.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(request.PlaybackInstanceId, eventArgs.PlaybackInstanceId);
        Assert.AreEqual(PlayerError.LoadFailed, eventArgs.Error);
        Assert.AreEqual("error", eventArgs.EndReason);
        Assert.IsTrue(diagnostics.Messages.Any(message =>
            message.Contains(
                "playback-stage event=end-file reason=error trackCount=0",
                StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task EventLoop_FileLoadedWithoutVideoTrack_DoesNotFailImmediately()
    {
        var nativeApi = new FakeMpvNativeApi
        {
            HasVideoTrack = false,
            HasVideoOutParams = false
        };
        nativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.FileLoaded, 0));
        var service = CreateService(nativeApi);
        var status = new TaskCompletionSource<PlayerStatusChangedEventArgs>();
        var failed = false;
        service.StatusChanged += (_, e) =>
        {
            if (e.State == PlayerPlaybackState.Failed)
            {
                failed = true;
            }

            if (e.State == PlayerPlaybackState.Playing)
            {
                status.TrySetResult(e);
            }
        };

        var request = CreateRequest();
        await service.LoadAsync(request, CancellationToken.None);
        var eventArgs = await status.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.IsFalse(failed);
        Assert.AreEqual(request.PlaybackInstanceId, eventArgs.PlaybackInstanceId);
        Assert.AreEqual(true, eventArgs.HasAudioTrack);
        Assert.AreEqual(false, eventArgs.HasVideoTrack);
    }

    [TestMethod]
    public async Task EventLoop_FileLoaded_RaisesMpvTrackListAndSanitizedSubtitleDiagnostics()
    {
        var nativeApi = new FakeMpvNativeApi();
        nativeApi.Tracks.Add(new FakeMpvTrack(
            "audio",
            1,
            Codec: "aac",
            Language: "jpn",
            Title: "Japanese AAC",
            IsDefault: true)
        {
            IsSelected = true
        });
        nativeApi.Tracks.Add(new FakeMpvTrack(
            "sub",
            7,
            Codec: "ass",
            Language: "chi",
            Title: "Chinese ASS",
            IsExternal: true));
        nativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.FileLoaded, 0));
        var diagnostics = new FakePlayerDiagnostics();
        var service = new MpvPlayerService(nativeApi, diagnostics);
        var status = new TaskCompletionSource<PlayerStatusChangedEventArgs>();
        service.StatusChanged += (_, e) =>
        {
            if (e.State == PlayerPlaybackState.Playing)
            {
                status.TrySetResult(e);
            }
        };

        var request = CreateRequest();
        await service.LoadAsync(request, CancellationToken.None);
        var eventArgs = await status.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(2, eventArgs.Tracks.Count);
        Assert.AreEqual(7, eventArgs.Tracks.Single(track => track.Type == "sub").Id);
        Assert.AreEqual("chi", eventArgs.Tracks.Single(track => track.Type == "sub").Language);
        Assert.IsTrue(diagnostics.Messages.Any(message => message.Contains("subtitle-tracks count=1", StringComparison.Ordinal)));
        Assert.IsTrue(diagnostics.Messages.Any(message =>
            message.Contains(
                "playback-stage event=file-loaded trackCount=2",
                StringComparison.Ordinal)));
        Assert.IsFalse(diagnostics.Messages.Any(message => message.Contains("http://", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task EventLoop_FileLoaded_MapsMpvIdsToEmbyMediaStreamIndexes()
    {
        var nativeApi = new FakeMpvNativeApi();
        nativeApi.Tracks.Add(new FakeMpvTrack("audio", Id: 8, FfIndex: 3) { IsSelected = true });
        nativeApi.Tracks.Add(new FakeMpvTrack("sub", Id: 11, DemuxIndex: 4) { IsSelected = true });
        nativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.FileLoaded, 0));
        var service = CreateService(nativeApi);
        var status = new TaskCompletionSource<PlayerStatusChangedEventArgs>();
        service.StatusChanged += (_, e) =>
        {
            if (e.State == PlayerPlaybackState.Playing)
            {
                status.TrySetResult(e);
            }
        };
        var request = CreateRequest(
            audioTracks: new[] { new PlaybackTrack(3, "jpn", "aac", "Japanese AAC", true) },
            subtitles: new[] { new PlaybackSubtitle(4, "chi", "ass", "Chinese ASS", false, false, null) });

        await service.LoadAsync(request, CancellationToken.None);
        var eventArgs = await status.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        var audio = eventArgs.Tracks.Single(track => track.Type == "audio");
        var subtitle = eventArgs.Tracks.Single(track => track.Type == "sub");
        Assert.AreEqual(8, audio.Id);
        Assert.AreEqual(3, audio.MediaStreamIndex);
        Assert.AreEqual(11, subtitle.Id);
        Assert.AreEqual(4, subtitle.MediaStreamIndex);
    }

    [TestMethod]
    public async Task EventLoop_FileLoaded_DoesNotInferMediaStreamIndexFromMpvIdOrOrdinal()
    {
        var nativeApi = new FakeMpvNativeApi();
        nativeApi.Tracks.Add(new FakeMpvTrack("audio", Id: 3, FfIndex: 8) { IsSelected = true });
        nativeApi.Tracks.Add(new FakeMpvTrack("audio", Id: 8));
        nativeApi.Tracks.Add(new FakeMpvTrack("sub", Id: 11));
        nativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.FileLoaded, 0));
        var service = CreateService(nativeApi);
        var status = new TaskCompletionSource<PlayerStatusChangedEventArgs>();
        service.StatusChanged += (_, e) =>
        {
            if (e.State == PlayerPlaybackState.Playing)
            {
                status.TrySetResult(e);
            }
        };
        var request = CreateRequest(
            audioTracks: new[] { new PlaybackTrack(8, "jpn", "aac", "Japanese AAC", true) },
            subtitles: new[] { new PlaybackSubtitle(11, "chi", "ass", "External Chinese", false, true, "External") });

        await service.LoadAsync(request, CancellationToken.None);
        var eventArgs = await status.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(8, eventArgs.Tracks.Single(track => track.Id == 3).MediaStreamIndex);
        Assert.IsNull(eventArgs.Tracks.Single(track => track.Id == 8).MediaStreamIndex);
        Assert.IsNull(eventArgs.Tracks.Single(track => track.Id == 11).MediaStreamIndex);
    }

    [TestMethod]
    public async Task EventLoop_FileLoaded_StartsProgressLoopAndRaisesProgress()
    {
        var nativeApi = new FakeMpvNativeApi
        {
            TimePositionSeconds = 75,
            DurationSeconds = 600,
            PercentPosition = 12.5,
            IsPaused = false
        };
        nativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.FileLoaded, 0));
        var service = CreateService(nativeApi);
        var progress = new TaskCompletionSource<PlayerProgressChangedEventArgs>();
        service.ProgressChanged += (_, e) => progress.TrySetResult(e);

        var request = CreateRequest();
        await service.LoadAsync(request, CancellationToken.None);
        var eventArgs = await progress.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(request.PlaybackInstanceId, eventArgs.PlaybackInstanceId);
        Assert.AreEqual(TimeSpan.FromSeconds(75), eventArgs.Position);
        Assert.AreEqual(TimeSpan.FromSeconds(600), eventArgs.Duration);
        Assert.AreEqual(12.5, eventArgs.Percent);
        Assert.AreEqual(false, eventArgs.IsPaused);
    }

    [TestMethod]
    public async Task StopAsync_StopsProgressLoopBeforeDestroyHandle()
    {
        var nativeApi = new FakeMpvNativeApi();
        var diagnostics = new FakePlayerDiagnostics();
        nativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.FileLoaded, 0));
        var service = new MpvPlayerService(nativeApi, diagnostics);
        var progress = new TaskCompletionSource<PlayerProgressChangedEventArgs>();
        service.ProgressChanged += (_, e) => progress.TrySetResult(e);

        await service.LoadAsync(CreateRequest(), CancellationToken.None);
        await progress.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        AssertSequence(diagnostics.Messages, "progress-loop-stopped", "handle-destroyed");
    }

    [TestMethod]
    public async Task LoadAsync_WithResumePosition_LoadsFileWithoutStartOption()
    {
        var nativeApi = new FakeMpvNativeApi();
        var service = CreateService(nativeApi);

        await service.LoadAsync(CreateRequest(startPositionTicks: TimeSpan.FromMinutes(12).Ticks), CancellationToken.None);
        Assert.IsTrue(nativeApi.WaitUntilEventLoopEntered());
        await service.StopAsync(CancellationToken.None);

        var loadFileCommand = nativeApi.CommandArguments.First(args => args[0] == "loadfile");
        Assert.AreEqual(3, loadFileCommand.Length);
        Assert.IsFalse(loadFileCommand.Any(arg => arg.StartsWith("start=", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task EventLoop_FileLoaded_AppliesPendingSeekAfterLoad()
    {
        var nativeApi = new FakeMpvNativeApi();
        var service = CreateService(nativeApi);
        var status = new TaskCompletionSource<PlayerStatusChangedEventArgs>();
        service.StatusChanged += (_, e) =>
        {
            if (e.State == PlayerPlaybackState.Playing)
            {
                status.TrySetResult(e);
            }
        };

        await service.LoadAsync(CreateRequest(startPositionTicks: TimeSpan.FromMinutes(12).Ticks), CancellationToken.None);
        Assert.IsTrue(nativeApi.WaitUntilEventLoopEntered());
        nativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.FileLoaded, 0));
        nativeApi.NotifyEventAvailable();
        await status.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        var seekCommand = nativeApi.CommandArguments.First(args => args[0] == "seek");
        Assert.AreEqual("720", seekCommand[1]);
        Assert.AreEqual("absolute+exact", seekCommand[2]);
        AssertSequence(nativeApi.Calls, "loadfile", "seek");
    }

    [TestMethod]
    public async Task EventLoop_NonPositiveStartPosition_DoesNotSeek()
    {
        var nativeApi = new FakeMpvNativeApi();
        var service = CreateService(nativeApi);
        var status = new TaskCompletionSource<PlayerStatusChangedEventArgs>();
        service.StatusChanged += (_, e) =>
        {
            if (e.State == PlayerPlaybackState.Playing)
            {
                status.TrySetResult(e);
            }
        };

        await service.LoadAsync(CreateRequest(startPositionTicks: -100), CancellationToken.None);
        Assert.IsTrue(nativeApi.WaitUntilEventLoopEntered());
        nativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.FileLoaded, 0));
        nativeApi.NotifyEventAvailable();
        await status.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.IsFalse(nativeApi.CommandArguments.Any(args => args[0] == "seek"));
    }

    [TestMethod]
    public async Task EventLoop_SeekFailure_RaisesResumeFailedStateWithoutEscaping()
    {
        var nativeApi = new FakeMpvNativeApi
        {
            SeekResult = -1
        };
        var service = CreateService(nativeApi);
        var status = new TaskCompletionSource<PlayerStatusChangedEventArgs>();
        service.StatusChanged += (_, e) =>
        {
            if (e.State == PlayerPlaybackState.Failed)
            {
                status.TrySetResult(e);
            }
        };

        var request = CreateRequest(startPositionTicks: TimeSpan.FromMinutes(12).Ticks);
        await service.LoadAsync(request, CancellationToken.None);
        Assert.IsTrue(nativeApi.WaitUntilEventLoopEntered());
        nativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.FileLoaded, 0));
        nativeApi.NotifyEventAvailable();
        var eventArgs = await status.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(request.PlaybackInstanceId, eventArgs.PlaybackInstanceId);
        Assert.AreEqual(PlayerError.ResumeFailed, eventArgs.Error);
    }

    [TestMethod]
    public async Task SeekAsync_UsesAbsoluteExactSeekForCurrentInstance()
    {
        var nativeApi = new FakeMpvNativeApi();
        var service = CreateService(nativeApi);
        var request = CreateRequest();

        await service.LoadAsync(request, CancellationToken.None);
        nativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.FileLoaded, 0));
        nativeApi.NotifyEventAvailable();
        await Task.Delay(50);
        var result = await service.SeekAsync(
            request.PlaybackInstanceId,
            TimeSpan.FromSeconds(123.456),
            CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        var seekCommand = nativeApi.CommandArguments.Last(args => args[0] == "seek");
        CollectionAssert.AreEqual(
            new[] { "seek", "123.456", "absolute+exact" },
            seekCommand);
    }

    [TestMethod]
    public async Task SeekAsync_BeforeFileLoadedDoesNotCallMpvSeek()
    {
        var nativeApi = new FakeMpvNativeApi();
        var service = CreateService(nativeApi);
        var request = CreateRequest();

        await service.LoadAsync(request, CancellationToken.None);
        var result = await service.SeekAsync(
            request.PlaybackInstanceId,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsFalse(nativeApi.CommandArguments.Any(args => args[0] == "seek"));
    }

    [TestMethod]
    public async Task SeekAsync_WithOldPlaybackInstance_DoesNotCallMpv()
    {
        var nativeApi = new FakeMpvNativeApi();
        var service = CreateService(nativeApi);
        var request = CreateRequest();

        await service.LoadAsync(request, CancellationToken.None);
        var seekCountBefore = nativeApi.CommandArguments.Count(args => args[0] == "seek");

        var result = await service.SeekAsync(
            request.PlaybackInstanceId - 1,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(PlayerError.SeekFailed, result.Error);
        Assert.AreEqual(seekCountBefore, nativeApi.CommandArguments.Count(args => args[0] == "seek"));
    }

    [TestMethod]
    public async Task SeekAsync_AfterStop_DoesNotAccessDestroyedHandle()
    {
        var nativeApi = new FakeMpvNativeApi();
        var service = CreateService(nativeApi);
        var request = CreateRequest();

        await service.LoadAsync(request, CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        var seekCountBefore = nativeApi.CommandArguments.Count(args => args[0] == "seek");

        var result = await service.SeekAsync(
            request.PlaybackInstanceId,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(PlayerError.SeekFailed, result.Error);
        Assert.AreEqual(seekCountBefore, nativeApi.CommandArguments.Count(args => args[0] == "seek"));
    }

    [TestMethod]
    public async Task SeekAsync_CommandFailureReturnsSeekFailed()
    {
        var nativeApi = new FakeMpvNativeApi
        {
            SeekResult = -1
        };
        var service = CreateService(nativeApi);
        var request = CreateRequest();

        await service.LoadAsync(request, CancellationToken.None);
        nativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.FileLoaded, 0));
        nativeApi.NotifyEventAvailable();
        await Task.Delay(50);
        var result = await service.SeekAsync(
            request.PlaybackInstanceId,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(PlayerError.SeekFailed, result.Error);
    }

    [TestMethod]
    public async Task SetVolumeAsync_ClampsAndSetsMpvVolumeProperty()
    {
        var nativeApi = new FakeMpvNativeApi();
        var service = CreateService(nativeApi);

        await service.LoadAsync(CreateRequest(), CancellationToken.None);
        var highResult = await service.SetVolumeAsync(140, CancellationToken.None);
        var lowResult = await service.SetVolumeAsync(-5, CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.IsTrue(highResult.IsSuccess);
        Assert.IsTrue(lowResult.IsSuccess);
        Assert.IsTrue(nativeApi.PropertySets.Contains("volume=100"));
        Assert.IsTrue(nativeApi.PropertySets.Contains("volume=0"));
    }

    [TestMethod]
    public async Task SetMuteAsync_SetsMpvMuteProperty()
    {
        var nativeApi = new FakeMpvNativeApi();
        var service = CreateService(nativeApi);

        await service.LoadAsync(CreateRequest(), CancellationToken.None);
        var muteResult = await service.SetMuteAsync(true, CancellationToken.None);
        var unmuteResult = await service.SetMuteAsync(false, CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.IsTrue(muteResult.IsSuccess);
        Assert.IsTrue(unmuteResult.IsSuccess);
        Assert.IsTrue(nativeApi.PropertySets.Contains("mute=yes"));
        Assert.IsTrue(nativeApi.PropertySets.Contains("mute=no"));
    }

    [TestMethod]
    public async Task SelectAudioTrackAsync_UsesProvidedMpvAudioTrackId()
    {
        var nativeApi = new FakeMpvNativeApi();
        nativeApi.Tracks.Add(new FakeMpvTrack("audio", Id: 8, FfIndex: 3));
        var service = CreateService(nativeApi);
        var request = CreateRequest(audioTracks: new[] { new PlaybackTrack(3, "jpn", "aac", "日语 AAC", true) });

        await service.LoadAsync(request, CancellationToken.None);
        var result = await service.SelectAudioTrackAsync(request.PlaybackInstanceId, 8, CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(nativeApi.PropertySets.Contains("aid=8"));
    }

    [TestMethod]
    public async Task SelectAudioTrackAsync_DoesNotTreatEmbyStreamIndexAsMpvId()
    {
        var nativeApi = new FakeMpvNativeApi();
        nativeApi.Tracks.Add(new FakeMpvTrack("audio", Id: 3, FfIndex: 9));
        nativeApi.Tracks.Add(new FakeMpvTrack("audio", Id: 8, FfIndex: 3));
        var service = CreateService(nativeApi);
        var request = CreateRequest(audioTracks: new[] { new PlaybackTrack(3, "jpn", "aac", "Japanese AAC", true) });

        await service.LoadAsync(request, CancellationToken.None);
        var result = await service.SelectAudioTrackAsync(request.PlaybackInstanceId, 8, CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(nativeApi.PropertySets.Contains("aid=8"));
        Assert.IsFalse(nativeApi.PropertySets.Contains("aid=3"));
    }

    [TestMethod]
    public async Task SelectSubtitleTrackAsync_UsesProvidedMpvSubtitleTrackIdAndShowsSubtitles()
    {
        var nativeApi = new FakeMpvNativeApi();
        nativeApi.Tracks.Add(new FakeMpvTrack("sub", Id: 11, FfIndex: 4));
        var service = CreateService(nativeApi);
        var request = CreateRequest(subtitles: new[] { new PlaybackSubtitle(4, "chi", "ass", "中文 ASS", false, false, null) });

        await service.LoadAsync(request, CancellationToken.None);
        var result = await service.SelectSubtitleTrackAsync(request.PlaybackInstanceId, 11, CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(nativeApi.PropertySets.Contains("sid=11"));
        Assert.IsTrue(nativeApi.PropertySets.Contains("sub-visibility=yes"));
    }

    [TestMethod]
    public async Task DisableSubtitleAsync_SetsMpvSubtitleTrackToNo()
    {
        var nativeApi = new FakeMpvNativeApi();
        var service = CreateService(nativeApi);
        var request = CreateRequest();

        await service.LoadAsync(request, CancellationToken.None);
        var result = await service.DisableSubtitleAsync(request.PlaybackInstanceId, CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(nativeApi.PropertySets.Contains("sid=no"));
        Assert.IsTrue(nativeApi.PropertySets.Contains("sub-visibility=no"));
    }

    [TestMethod]
    public async Task SelectSubtitleTrackAsync_UnmappedTrackReturnsFailure()
    {
        var nativeApi = new FakeMpvNativeApi();
        nativeApi.Tracks.Add(new FakeMpvTrack("sub", Id: 11, FfIndex: 4));
        var service = CreateService(nativeApi);
        var request = CreateRequest(subtitles: new[]
        {
            new PlaybackSubtitle(4, "chi", "ass", "中文 ASS", false, false, null),
            new PlaybackSubtitle(5, "eng", "srt", "English SRT", false, false, null)
        });

        await service.LoadAsync(request, CancellationToken.None);
        var result = await service.SelectSubtitleTrackAsync(request.PlaybackInstanceId, 5, CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(PlayerError.SubtitleTrackFailed, result.Error);
        Assert.IsFalse(nativeApi.PropertySets.Contains("sid=11"));
    }

    [TestMethod]
    public async Task SelectExternalSubtitleAsync_AddsSubtitleUrlAndShowsSubtitles()
    {
        var nativeApi = new FakeMpvNativeApi();
        var service = CreateService(nativeApi);
        var request = CreateRequest(subtitles: new[]
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

        await service.LoadAsync(request, CancellationToken.None);
        var result = await service.SelectExternalSubtitleAsync(request.PlaybackInstanceId, 9, CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(nativeApi.CommandArguments.Any(args => args.SequenceEqual(new[]
        {
            "sub-add",
            "http://media.local/Videos/item-1/source-1/Subtitles/9/Stream.ass",
            "select"
        })));
        Assert.IsTrue(nativeApi.PropertySets.Contains("sub-visibility=yes"));
    }

    [TestMethod]
    public async Task SelectExternalSubtitleAsync_WithoutUrlReturnsFailure()
    {
        var nativeApi = new FakeMpvNativeApi();
        var service = CreateService(nativeApi);
        var request = CreateRequest(subtitles: new[]
        {
            new PlaybackSubtitle(9, "chi", "ass", "Chinese ASS", false, true, "External")
        });

        await service.LoadAsync(request, CancellationToken.None);
        var result = await service.SelectExternalSubtitleAsync(request.PlaybackInstanceId, 9, CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsFalse(nativeApi.CommandArguments.Any(args => args.FirstOrDefault() == "sub-add"));
    }

    [TestMethod]
    public async Task SelectExternalSubtitleAsync_RefreshMapsAddedTrackAndNewMediaClearsMapping()
    {
        var nativeApi = new FakeMpvNativeApi
        {
            AddedExternalSubtitleFfIndex = 3
        };
        nativeApi.Tracks.Add(new FakeMpvTrack("sub", Id: 2, FfIndex: 3));
        var service = CreateService(nativeApi);
        var request = CreateRequest(subtitles: new[]
        {
            new PlaybackSubtitle(3, "eng", "srt", "English SRT", false, false, null),
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

        await service.LoadAsync(request, CancellationToken.None);
        var selection = await service.SelectExternalSubtitleAsync(
            request.PlaybackInstanceId,
            9,
            CancellationToken.None);
        var addedTrackId = nativeApi.Tracks.Single(track => track.IsExternal).Id;
        var refreshed = new TaskCompletionSource<PlayerStatusChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.StatusChanged += (_, e) =>
        {
            if (e.State == PlayerPlaybackState.Playing)
            {
                refreshed.TrySetResult(e);
            }
        };
        nativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.PlaybackRestart, 0));
        nativeApi.NotifyEventAvailable();
        var refreshedStatus = await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(selection.IsSuccess);
        Assert.AreEqual(3, refreshedStatus.Tracks.Single(track => track.Id == 2).MediaStreamIndex);
        var refreshedExternal = refreshedStatus.Tracks.Single(track => track.Id == addedTrackId);
        Assert.AreEqual(9, refreshedExternal.MediaStreamIndex);
        Assert.IsTrue(refreshedExternal.IsSelected);

        await service.StopAsync(CancellationToken.None);
        nativeApi.Tracks.Clear();
        nativeApi.Tracks.Add(new FakeMpvTrack("sub", addedTrackId, IsExternal: true)
        {
            IsSelected = true
        });
        var nextStatus = new TaskCompletionSource<PlayerStatusChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.StatusChanged += (_, e) =>
        {
            if (e.PlaybackInstanceId == request.PlaybackInstanceId + 1
                && e.State == PlayerPlaybackState.Playing)
            {
                nextStatus.TrySetResult(e);
            }
        };
        nativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.FileLoaded, 0));
        var nextRequest = CreateRequest(
            subtitles: new[]
            {
                new PlaybackSubtitle(13, "chi", "ass", "Other External", false, true, "External")
            },
            playbackInstanceId: request.PlaybackInstanceId + 1);

        await service.LoadAsync(nextRequest, CancellationToken.None);
        var nextPlaybackStatus = await nextStatus.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.IsNull(nextPlaybackStatus.Tracks.Single(track => track.Id == addedTrackId).MediaStreamIndex);
    }

    [TestMethod]
    public async Task SelectExternalSubtitleAsync_SubAddFailureDoesNotCreateMapping()
    {
        var nativeApi = new FakeMpvNativeApi
        {
            SubAddResult = -1
        };
        nativeApi.Tracks.Add(new FakeMpvTrack("sub", Id: 5, IsExternal: true)
        {
            IsSelected = true
        });
        var service = CreateService(nativeApi);
        var request = CreateRequest(subtitles: new[]
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

        await service.LoadAsync(request, CancellationToken.None);
        var selection = await service.SelectExternalSubtitleAsync(
            request.PlaybackInstanceId,
            9,
            CancellationToken.None);
        var refreshed = new TaskCompletionSource<PlayerStatusChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.StatusChanged += (_, e) =>
        {
            if (e.State == PlayerPlaybackState.Playing)
            {
                refreshed.TrySetResult(e);
            }
        };
        nativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.PlaybackRestart, 0));
        nativeApi.NotifyEventAvailable();
        var refreshedStatus = await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.IsFalse(selection.IsSuccess);
        Assert.IsNull(refreshedStatus.Tracks.Single(track => track.Id == 5).MediaStreamIndex);
    }

    [TestMethod]
    public async Task TrackSelection_WithOldPlaybackInstance_DoesNotCallMpv()
    {
        var nativeApi = new FakeMpvNativeApi();
        var service = CreateService(nativeApi);
        var request = CreateRequest();

        await service.LoadAsync(request, CancellationToken.None);
        var result = await service.SelectAudioTrackAsync(request.PlaybackInstanceId - 1, 3, CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(PlayerError.AudioTrackFailed, result.Error);
        Assert.IsFalse(nativeApi.PropertySets.Contains("aid=3"));
    }

    [TestMethod]
    public async Task TrackSelection_AfterStop_DoesNotAccessDestroyedHandle()
    {
        var nativeApi = new FakeMpvNativeApi();
        var service = CreateService(nativeApi);
        var request = CreateRequest();

        await service.LoadAsync(request, CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        var result = await service.SelectSubtitleTrackAsync(request.PlaybackInstanceId, 4, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(PlayerError.SubtitleTrackFailed, result.Error);
        Assert.IsFalse(nativeApi.PropertySets.Contains("sid=4"));
    }

    [TestMethod]
    public async Task EventLoop_EndFileEofThenShutdown_RaisesCompletionOnly()
    {
        var nativeApi = new FakeMpvNativeApi();
        nativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.FileLoaded, 0));
        nativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.EndFile, 0, 0, 0));
        nativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.Shutdown, 0));
        var shutdownSeen = new TaskCompletionSource<bool>();
        var diagnostics = new FakePlayerDiagnostics(message =>
        {
            if (message.Contains("event=shutdown", StringComparison.Ordinal))
            {
                shutdownSeen.TrySetResult(true);
            }
        });
        var service = new MpvPlayerService(nativeApi, diagnostics);
        var status = new TaskCompletionSource<PlayerStatusChangedEventArgs>();
        var failureCount = 0;
        service.StatusChanged += (_, e) =>
        {
            if (e.State == PlayerPlaybackState.Completed)
            {
                status.TrySetResult(e);
            }
            else if (e.State == PlayerPlaybackState.Failed)
            {
                Interlocked.Increment(ref failureCount);
            }
        };

        var request = CreateRequest();
        await service.LoadAsync(request, CancellationToken.None);
        var eventArgs = await status.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await shutdownSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(request.PlaybackInstanceId, eventArgs.PlaybackInstanceId);
        Assert.AreEqual(PlayerPlaybackState.Completed, eventArgs.State);
        Assert.AreEqual(PlayerError.None, eventArgs.Error);
        Assert.AreEqual(0, failureCount);
    }

    [TestMethod]
    public async Task EventLoop_RedirectThenFileLoaded_ContinuesLoading()
    {
        var nativeApi = new FakeMpvNativeApi();
        nativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.EndFile, 0, 5, 0));
        nativeApi.Events.Enqueue(new MpvEventSnapshot(MpvEventId.FileLoaded, 0));
        var service = CreateService(nativeApi);
        var playing = new TaskCompletionSource<PlayerStatusChangedEventArgs>();
        var failureCount = 0;
        service.StatusChanged += (_, e) =>
        {
            if (e.State == PlayerPlaybackState.Playing)
            {
                playing.TrySetResult(e);
            }
            else if (e.State == PlayerPlaybackState.Failed)
            {
                Interlocked.Increment(ref failureCount);
            }
        };

        await service.LoadAsync(CreateRequest(), CancellationToken.None);
        var eventArgs = await playing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(PlayerPlaybackState.Playing, eventArgs.State);
        Assert.AreEqual(0, failureCount);
    }

    [TestMethod]
    public async Task EventLoop_Exception_RaisesFailedStateWithoutEscaping()
    {
        var nativeApi = new FakeMpvNativeApi
        {
            ThrowOnWaitEvent = true
        };
        var service = CreateService(nativeApi);
        var status = new TaskCompletionSource<PlayerStatusChangedEventArgs>();
        service.StatusChanged += (_, e) =>
        {
            if (e.State == PlayerPlaybackState.Failed)
            {
                status.TrySetResult(e);
            }
        };

        await service.LoadAsync(CreateRequest(), CancellationToken.None);
        var eventArgs = await status.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(PlayerError.PlaybackFailed, eventArgs.Error);
    }

    [TestMethod]
    public async Task Diagnostics_DoNotWriteFullUrlOrToken()
    {
        var nativeApi = new FakeMpvNativeApi();
        var diagnostics = new FakePlayerDiagnostics();
        var service = new MpvPlayerService(nativeApi, diagnostics);

        await service.LoadAsync(CreateRequest(
            playbackPath: "http://server.local/Videos/item-1/stream.mkv?api_key=secret-token",
            token: "secret-token"), CancellationToken.None);
        Assert.IsTrue(nativeApi.WaitUntilEventLoopEntered());
        await service.StopAsync(CancellationToken.None);

        var log = string.Join(Environment.NewLine, diagnostics.Messages);
        Assert.IsFalse(log.Contains("http://server.local", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(log.Contains("secret-token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(log.Contains("api_key", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void DebugDiagnostics_Hash8IsStableAndDistinguishesIdentifiers()
    {
        var first = PlayerDebugDiagnostics.CreateHash8("item-alpha");

        Assert.AreEqual(8, first.Length);
        Assert.AreEqual(first, PlayerDebugDiagnostics.CreateHash8("item-alpha"));
        Assert.AreNotEqual(first, PlayerDebugDiagnostics.CreateHash8("item-beta"));
        Assert.AreEqual("none", PlayerDebugDiagnostics.CreateHash8(null));
    }

    [TestMethod]
    public void DebugDiagnostics_PathTemplateRemovesHostIdentifiersAndQueryValues()
    {
        const string itemId = "private-item-id";
        const string mediaSourceId = "private-source-id";
        var path = PlayerDebugDiagnostics.ParsePlaybackPath(
            $"https://private-server.example/emby/Videos/{itemId}/stream?static=true&MediaSourceId={mediaSourceId}&PlaySessionId=private-session",
            itemId,
            mediaSourceId);

        Assert.AreEqual("emby", path.ApiPrefix);
        Assert.AreEqual("/emby/Videos/{ItemId}/stream", path.PathTemplate);
        CollectionAssert.AreEqual(
            new[] { "MediaSourceId", "PlaySessionId", "static" },
            path.QueryParameterNames.ToArray());
        Assert.IsFalse(path.PathTemplate.Contains(itemId, StringComparison.Ordinal));
        Assert.IsFalse(path.PathTemplate.Contains("private-server", StringComparison.Ordinal));
        Assert.IsFalse(string.Join(',', path.QueryParameterNames).Contains("private-session", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DebugDiagnostics_ApiPrefixDistinguishesRootAndEmby()
    {
        Assert.AreEqual(
            "root",
            PlayerDebugDiagnostics.ParsePlaybackPath(
                "/Videos/item/stream?static=true",
                "item",
                "source").ApiPrefix);
        Assert.AreEqual(
            "emby",
            PlayerDebugDiagnostics.ParsePlaybackPath(
                "/emby/videos/item/master.m3u8",
                "item",
                "source").ApiPrefix);
    }

    [TestMethod]
    public void DebugDiagnostics_AttemptMessageContainsNoSensitiveValues()
    {
        const string itemId = "private-item-id";
        const string mediaSourceId = "private-source-id";
        const string secretToken = "private-access-token-value";
        var request = CreateRequest(
            playbackPath: $"https://private-server.example/Videos/{itemId}/stream?static=true&MediaSourceId={mediaSourceId}&api_key={secretToken}",
            token: secretToken,
            itemId: itemId,
            mediaSourceId: mediaSourceId);

        var message = PlayerDebugDiagnostics.CreatePlaybackAttemptMessage(request);

        StringAssert.Contains(message, "selectedSourceKind=StaticStream");
        StringAssert.Contains(message, "pathTemplate=/Videos/{ItemId}/stream");
        StringAssert.Contains(message, "headerNames=X-Emby-Token");
        Assert.IsFalse(message.Contains(itemId, StringComparison.Ordinal));
        Assert.IsFalse(message.Contains(mediaSourceId, StringComparison.Ordinal));
        Assert.IsFalse(message.Contains(secretToken, StringComparison.Ordinal));
        Assert.IsFalse(message.Contains("private-server", StringComparison.Ordinal));
        Assert.IsFalse(message.Contains("api_key=", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task DebugDiagnostics_DoNotChangeMpvLoadRequestOrHeaders()
    {
        var nativeApi = new FakeMpvNativeApi();
        var service = CreateService(nativeApi);
        var request = CreateRequest();

        await service.LoadAsync(request, CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        var loadCommand = nativeApi.CommandArguments.Single(args => args[0] == "loadfile");
        CollectionAssert.AreEqual(
            new[] { "loadfile", request.PlaybackInfo.PlaybackPath!, "replace" },
            loadCommand);
        Assert.IsTrue(nativeApi.Calls.Contains("set-option-http-header-fields"));
    }

    [TestMethod]
    public async Task LoadAsync_EscapesCommasInHttpHeaderListValues()
    {
        const string authorization =
            "Emby Client=\"Emby Windows Player\", Device=\"Windows\", DeviceId=\"device-1\", Version=\"1.0.0\"";
        var nativeApi = new FakeMpvNativeApi();
        var service = CreateService(nativeApi);
        var request = CreateRequest(requiredHeaders: new Dictionary<string, string>
        {
            ["X-Emby-Token"] = "test-access-token",
            ["X-Emby-Authorization"] = authorization,
            ["User-Agent"] = "Emby Windows Player/1.0"
        });

        await service.LoadAsync(request, CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        var headerOption = nativeApi.OptionSets.Single(value =>
            value.StartsWith("http-header-fields=", StringComparison.Ordinal));
        StringAssert.Contains(
            headerOption,
            "X-Emby-Authorization: Emby Client=\"Emby Windows Player\"\\, Device=\"Windows\"\\, DeviceId=\"device-1\"\\, Version=\"1.0.0\"");
        StringAssert.Contains(headerOption, "X-Emby-Token: test-access-token");
        StringAssert.Contains(headerOption, "User-Agent: Emby Windows Player/1.0");
        Assert.IsFalse(nativeApi.OptionSets.Any(value =>
            value.StartsWith("http-header-fields-append=", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void DebugDiagnostics_StopOriginIsWhitelisted()
    {
        StringAssert.EndsWith(
            PlayerDebugDiagnostics.CreateStopRequestedMessage(42, "navigation"),
            "stop-requested origin=navigation");
        StringAssert.EndsWith(
            PlayerDebugDiagnostics.CreateStopRequestedMessage(42, "unexpected-origin"),
            "stop-requested origin=unknown");
    }

    [TestMethod]
    [DataRow(SubtitleAdjustmentKind.DelaySeconds, -0.3, "sub-delay=-0.3")]
    [DataRow(SubtitleAdjustmentKind.DelaySeconds, 100.0, "sub-delay=60")]
    [DataRow(SubtitleAdjustmentKind.Scale, 1.4, "sub-scale=1.4")]
    [DataRow(SubtitleAdjustmentKind.Scale, 0.1, "sub-scale=0.5")]
    [DataRow(SubtitleAdjustmentKind.Position, 85.0, "sub-pos=85")]
    [DataRow(SubtitleAdjustmentKind.Position, 110.0, "sub-pos=100")]
    public async Task SubtitleAdjustment_UsesMpvPropertyAndBounds(SubtitleAdjustmentKind kind, double value, string expected)
    {
        var nativeApi = new FakeMpvNativeApi();
        await using var service = CreateService(nativeApi);
        await service.LoadAsync(CreateRequest(), CancellationToken.None);
        var result = await service.SetSubtitleAdjustmentAsync(42, kind, value, CancellationToken.None);
        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.Contains(nativeApi.PropertySets.ToArray(), expected);
    }

    [TestMethod]
    public async Task SubtitleAdjustment_RejectsStaleInstanceInvalidNumberAndStoppedPlayer()
    {
        var nativeApi = new FakeMpvNativeApi();
        await using var service = CreateService(nativeApi);
        await service.LoadAsync(CreateRequest(), CancellationToken.None);
        Assert.IsFalse((await service.SetSubtitleAdjustmentAsync(41, SubtitleAdjustmentKind.Scale, 2, CancellationToken.None)).IsSuccess);
        Assert.IsFalse((await service.SetSubtitleAdjustmentAsync(42, SubtitleAdjustmentKind.Scale, double.NaN, CancellationToken.None)).IsSuccess);
        Assert.IsFalse((await service.SetSubtitleAdjustmentAsync(42, (SubtitleAdjustmentKind)100, 2, CancellationToken.None)).IsSuccess);
        await service.StopAsync(CancellationToken.None);
        Assert.IsFalse((await service.SetSubtitleAdjustmentAsync(42, SubtitleAdjustmentKind.Scale, 2, CancellationToken.None)).IsSuccess);
        Assert.IsFalse(nativeApi.PropertySets.Any(value => value.StartsWith("sub-scale=")));
    }

    [TestMethod]
    public async Task SubtitleAdjustment_ReportsNativeFailureWithoutStoppingPlayback()
    {
        var nativeApi = new FakeMpvNativeApi { FailedProperty = "sub-delay" };
        await using var service = CreateService(nativeApi);
        await service.LoadAsync(CreateRequest(), CancellationToken.None);
        var result = await service.SetSubtitleAdjustmentAsync(42, SubtitleAdjustmentKind.DelaySeconds, 1, CancellationToken.None);
        Assert.AreEqual(PlayerError.SubtitleAdjustmentFailed, result.Error);
        Assert.AreEqual(0, nativeApi.DestroyCallCount);
    }

    [TestMethod]
    public async Task TechnicalInfo_ReportsNativeDimensionsAndRejectsStaleInstance()
    {
        var nativeApi = new FakeMpvNativeApi { VideoWidth = 1920, VideoHeight = 1080, VideoFormat = "h264" };
        await using var service = CreateService(nativeApi);
        await service.LoadAsync(CreateRequest(), CancellationToken.None);
        var info = await service.GetTechnicalInfoAsync(42, CancellationToken.None);
        Assert.AreEqual(new PlayerTechnicalInfo(1920, 1080, "h264"), info);
        Assert.IsNull(await service.GetTechnicalInfoAsync(43, CancellationToken.None));
        nativeApi.VideoWidth = double.NaN;
        nativeApi.VideoHeight = -1;
        var unknown = await service.GetTechnicalInfoAsync(42, CancellationToken.None);
        Assert.IsNull(unknown!.Width);
        Assert.IsNull(unknown.Height);
    }

    [TestMethod]
    public async Task TranscodedTimeline_ConvertsResumeSeekProgressAndFullDuration()
    {
        var native = new FakeMpvNativeApi { TimePositionSeconds = 20, DurationSeconds = 4800 };
        await using var service = CreateService(native);
        var request = CreateRequest(startPositionTicks: TimeSpan.FromSeconds(615).Ticks);
        request = request with { PlaybackInfo = request.PlaybackInfo with { RequiresTranscoding = true, StreamPositionOffsetTicks = TimeSpan.FromSeconds(600).Ticks } };
        var ready = new TaskCompletionSource<PlayerStatusChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new TaskCompletionSource<PlayerProgressChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StatusChanged += (_, e) => { if (e.State == PlayerPlaybackState.Playing) ready.TrySetResult(e); };
        service.ProgressChanged += (_, e) => progress.TrySetResult(e);
        await service.LoadAsync(request, CancellationToken.None);
        native.Events.Enqueue(new MpvEventSnapshot(MpvEventId.FileLoaded, 0));
        native.NotifyEventAvailable();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var snapshot = await progress.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(TimeSpan.FromSeconds(620), snapshot.Position);
        Assert.AreEqual(TimeSpan.FromMinutes(90), snapshot.Duration);
        Assert.AreEqual(620d / 5400 * 100, snapshot.Percent!.Value, 0.001);
        Assert.IsTrue(native.CommandArguments.Any(args => args.SequenceEqual(new[] { "seek", "15", "absolute+exact" })));
        Assert.IsTrue((await service.SeekAsync(42, TimeSpan.FromSeconds(660), CancellationToken.None)).IsSuccess);
        Assert.IsTrue(native.CommandArguments.Any(args => args.SequenceEqual(new[] { "seek", "60", "absolute+exact" })));
        Assert.IsFalse((await service.SeekAsync(42, TimeSpan.FromSeconds(300), CancellationToken.None)).IsSuccess);
    }

    [TestMethod]
    public async Task PausedLoad_InitializesPauseBeforeLoadingAndEmitsPaused()
    {
        var native = new FakeMpvNativeApi { IsPaused = true };
        await using var service = CreateService(native);
        var ready = new TaskCompletionSource<PlayerStatusChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StatusChanged += (_, e) => ready.TrySetResult(e);
        await service.LoadAsync(CreateRequest() with { StartPaused = true }, CancellationToken.None);
        native.Events.Enqueue(new MpvEventSnapshot(MpvEventId.FileLoaded, 0));
        native.NotifyEventAvailable();
        Assert.AreEqual(PlayerPlaybackState.Paused, (await ready.Task.WaitAsync(TimeSpan.FromSeconds(3))).State);
        CollectionAssert.Contains(native.OptionSets.ToArray(), "pause=yes");
    }

    [TestMethod]
    public async Task TranscodedAudio_UsesNegotiatedSourceIndexInsteadOfOutputFfIndex()
    {
        var native = new FakeMpvNativeApi();
        native.Tracks.Add(new FakeMpvTrack("audio", Id: 1, FfIndex: 1) { IsSelected = true });
        await using var service = CreateService(native);
        var request = CreateRequest(audioTracks: new[] {
            new PlaybackTrack(1, "jpn", "aac", "Japanese", true),
            new PlaybackTrack(3, "chi", "aac", "Chinese", false) });
        request = request with { PlaybackInfo = request.PlaybackInfo with { RequiresTranscoding = true, SelectedAudioStreamIndex = 3 } };
        var ready = new TaskCompletionSource<PlayerStatusChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StatusChanged += (_, e) => ready.TrySetResult(e);
        await service.LoadAsync(request, CancellationToken.None);
        native.Events.Enqueue(new MpvEventSnapshot(MpvEventId.FileLoaded, 0));
        native.NotifyEventAvailable();
        var status = await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(3, status.Tracks.Single(track => track.Type == "audio").MediaStreamIndex);
    }

    private static MpvPlayerService CreateService(FakeMpvNativeApi nativeApi)
    {
        return new MpvPlayerService(nativeApi, new FakePlayerDiagnostics());
    }

    private static PlayerLoadRequest CreateRequest(
        string playbackPath = "http://media.local/Videos/item-1/stream.mkv",
        string token = "test-access-token",
        long startPositionTicks = 0,
        IReadOnlyList<PlaybackTrack>? audioTracks = null,
        IReadOnlyList<PlaybackSubtitle>? subtitles = null,
        string itemId = "item-1",
        string mediaSourceId = "source-1",
        IReadOnlyDictionary<string, string>? requiredHeaders = null,
        long playbackInstanceId = 42)
    {
        var playbackInfo = new PlaybackInfo(
            itemId,
            "Playback Item",
            "play-session-1",
            new PlaybackMediaSource(
                mediaSourceId,
                "mkv",
                SupportsDirectPlay: true,
                SupportsDirectStream: true,
                SupportsTranscoding: false,
                requiredHeaders ?? new Dictionary<string, string>
                {
                    ["X-Emby-Token"] = token
                }),
            playbackPath,
            RequiresTranscoding: false,
            RequiresTokenInUrl: false,
            TimeSpan.FromMinutes(90).Ticks,
            startPositionTicks,
            audioTracks ?? Array.Empty<PlaybackTrack>(),
            subtitles ?? Array.Empty<PlaybackSubtitle>());
        return new PlayerLoadRequest(playbackInfo, new IntPtr(1234), playbackInstanceId);
    }

    private static void AssertSequence(IReadOnlyList<string> calls, params string[] expectedOrder)
    {
        var currentIndex = -1;
        foreach (var expected in expectedOrder)
        {
            var nextIndex = calls
                .Select((call, index) => new { call, index })
                .FirstOrDefault(item => item.index > currentIndex && item.call.Contains(expected, StringComparison.Ordinal))?.index;
            Assert.IsNotNull(nextIndex, $"Could not find call '{expected}' after index {currentIndex}.");
            currentIndex = nextIndex.Value;
        }
    }

    private sealed class FakeMpvNativeApi : IMpvNativeApi
    {
        private readonly ManualResetEventSlim eventLoopEntered = new(false);
        private readonly ManualResetEventSlim wakeupCalled = new(false);
        private readonly object callSync = new();

        public ConcurrentQueue<MpvEventSnapshot> Events { get; } = new();

        public ConcurrentQueue<int> InitializeResults { get; } = new();

        public ConcurrentQueue<int> LoadFileResults { get; } = new();

        public List<FakeMpvTrack> Tracks { get; } = new();

        public bool ThrowOnWaitEvent { get; set; }

        public int SeekResult { get; set; }

        public string? FailedProperty { get; set; }
        public double? VideoWidth { get; set; }
        public double? VideoHeight { get; set; }
        public string? VideoFormat { get; set; }

        public int SubAddResult { get; set; }

        public int? AddedExternalSubtitleFfIndex { get; set; }

        public bool HasAudioTrack { get; set; } = true;

        public bool HasVideoTrack { get; set; } = true;

        public bool HasVideoOutParams { get; set; } = true;

        public double? TimePositionSeconds { get; set; } = 30;

        public double? DurationSeconds { get; set; } = 300;

        public double? PercentPosition { get; set; } = 10;

        public bool? IsPaused { get; set; }

        public int DestroyCallCount { get; private set; }

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (callSync)
                {
                    return calls.ToArray();
                }
            }
        }

        public IReadOnlyList<string[]> CommandArguments
        {
            get
            {
                lock (callSync)
                {
                    return commandArguments.Select(args => args.ToArray()).ToArray();
                }
            }
        }

        public IReadOnlyList<string> PropertySets
        {
            get
            {
                lock (callSync)
                {
                    return propertySets.ToArray();
                }
            }
        }

        public IReadOnlyList<string> OptionSets
        {
            get
            {
                lock (callSync)
                {
                    return optionSets.ToArray();
                }
            }
        }

        private readonly List<string> calls = new();

        private readonly List<string[]> commandArguments = new();

        private readonly List<string> propertySets = new();

        private readonly List<string> optionSets = new();

        public IntPtr Create()
        {
            AddCall("create");
            return new IntPtr(4321);
        }

        public int Initialize(IntPtr context)
        {
            AddCall("initialize");
            return InitializeResults.TryDequeue(out var result) ? result : 0;
        }

        public int RequestLogMessages(IntPtr context, string minimumLevel)
        {
            AddCall("request-log");
            return 0;
        }

        public int SetOptionString(IntPtr context, string name, string value)
        {
            AddCall(name == "wid" ? "set-wid" : $"set-option-{name}");
            lock (callSync)
            {
                optionSets.Add($"{name}={value}");
            }

            return 0;
        }

        public int SetPropertyString(IntPtr context, string name, string value)
        {
            if (name == FailedProperty) { return -1; }
            AddCall($"set-property-{name}");
            lock (callSync)
            {
                propertySets.Add($"{name}={value}");
            }

            if (name == "aid")
            {
                SelectTrack("audio", value);
            }
            else if (name == "sid")
            {
                SelectTrack("sub", value);
            }

            return 0;
        }

        private void SelectTrack(string type, string value)
        {
            foreach (var track in Tracks.Where(track => track.Type == type))
            {
                track.IsSelected = int.TryParse(value, out var selectedId) && track.Id == selectedId;
            }
        }

        public int Command(IntPtr context, IReadOnlyList<string> args)
        {
            AddCall(args[0] == "stop" ? "stop-command" : args[0]);
            lock (callSync)
            {
                commandArguments.Add(args.ToArray());
            }

            if (args[0] == "sub-add")
            {
                if (SubAddResult < 0)
                {
                    return SubAddResult;
                }

                SelectTrack("sub", "no");
                Tracks.Add(new FakeMpvTrack(
                    "sub",
                    Tracks.Count == 0 ? 1 : Tracks.Max(track => track.Id) + 1,
                    FfIndex: AddedExternalSubtitleFfIndex,
                    IsExternal: true,
                    Codec: "ass",
                    Language: "chi",
                    Title: "External subtitle")
                {
                    IsSelected = true
                });
            }

            if (args[0] == "loadfile" && LoadFileResults.TryDequeue(out var loadResult))
            {
                return loadResult;
            }

            return args[0] == "seek" ? SeekResult : 0;
        }

        public MpvEventSnapshot WaitEvent(IntPtr context, double timeout)
        {
            AddCall("wait-enter");
            eventLoopEntered.Set();
            if (ThrowOnWaitEvent)
            {
                throw new InvalidOperationException("fake wait failure");
            }

            if (Events.TryDequeue(out var mpvEvent))
            {
                return mpvEvent;
            }

            wakeupCalled.Wait(TimeSpan.FromMilliseconds(200));
            AddCall("wait-exit");
            return new MpvEventSnapshot(MpvEventId.None, 0);
        }

        public string? GetPropertyString(IntPtr context, string name)
        {
            if (name == "video-format") { return VideoFormat; }
            if (name == "track-list/count")
            {
                return Tracks.Count.ToString();
            }

            if (TryGetTrackListProperty(name, out var trackProperty))
            {
                return trackProperty;
            }

            return name switch
            {
                "current-tracks/audio/id" => HasAudioTrack ? "1" : null,
                "current-tracks/video/id" => HasVideoTrack ? "2" : null,
                "video-out-params/w" => HasVideoOutParams ? "1920" : null,
                "aid" => GetSelectedTrackValue("audio") ?? GetLastPropertyValue("aid"),
                "sid" => GetSelectedTrackValue("sub") ?? GetLastPropertyValue("sid"),
                "sub-visibility" => GetLastPropertyValue("sub-visibility"),
                _ => null
            };
        }

        private string? GetLastPropertyValue(string propertyName)
        {
            lock (callSync)
            {
                var prefix = propertyName + "=";
                return propertySets
                    .LastOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal))?
                    .Substring(prefix.Length);
            }
        }

        private string? GetSelectedTrackValue(string trackType)
        {
            return Tracks.LastOrDefault(track => track.Type == trackType && track.IsSelected)?.Id.ToString();
        }

        private bool TryGetTrackListProperty(string name, out string? value)
        {
            value = null;
            var parts = name.Split('/');
            if (parts.Length != 3
                || parts[0] != "track-list"
                || !int.TryParse(parts[1], out var index)
                || index < 0
                || index >= Tracks.Count)
            {
                return false;
            }

            var track = Tracks[index];
            value = parts[2] switch
            {
                "type" => track.Type,
                "id" => track.Id.ToString(),
                "ff-index" => track.FfIndex?.ToString(),
                "demux-index" => track.DemuxIndex?.ToString(),
                "selected" => track.IsSelected ? "yes" : "no",
                "external" => track.IsExternal ? "yes" : "no",
                "codec" => track.Codec,
                "lang" => track.Language,
                "title" => track.Title,
                "default" => track.IsDefault ? "yes" : "no",
                "forced" => track.IsForced ? "yes" : "no",
                _ => null
            };
            return true;
        }

        public bool? GetFlagProperty(IntPtr context, string name)
        {
            return name == "pause" ? IsPaused : false;
        }

        public double? GetDoubleProperty(IntPtr context, string name)
        {
            AddCall(name switch
            {
                "time-pos" => "progress-read-time-pos",
                "duration" => "progress-read-duration",
                "percent-pos" => "progress-read-percent-pos",
                _ => $"progress-read-{name}"
            });

            return name switch
            {
                "time-pos" => TimePositionSeconds,
                "duration" => DurationSeconds,
                "percent-pos" => PercentPosition,
                "video-params/w" => VideoWidth,
                "video-params/h" => VideoHeight,
                _ => null
            };
        }

        public void Wakeup(IntPtr context)
        {
            AddCall("wakeup");
            wakeupCalled.Set();
        }

        public void NotifyEventAvailable()
        {
            wakeupCalled.Set();
        }

        public void TerminateDestroy(IntPtr context)
        {
            AddCall("destroy");
            DestroyCallCount++;
        }

        public bool WaitUntilEventLoopEntered()
        {
            return eventLoopEntered.Wait(TimeSpan.FromSeconds(2));
        }

        private void AddCall(string call)
        {
            lock (callSync)
            {
                calls.Add(call);
            }
        }
    }

    private sealed class FakePlayerDiagnostics : IPlayerDiagnostics
    {
        private readonly Action<string>? messageWritten;

        public FakePlayerDiagnostics(Action<string>? messageWritten = null)
        {
            this.messageWritten = messageWritten;
        }

        public List<string> Messages { get; } = new();

        public void Write(string message)
        {
            Messages.Add(message);
            messageWritten?.Invoke(message);
        }
    }

    private sealed class ControlledLoadTimeout
    {
        private readonly SemaphoreSlim available = new(0);
        private readonly ConcurrentQueue<PendingLoadTimeout> pending = new();

        public async Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            var timeout = new PendingLoadTimeout(duration, cancellationToken);
            pending.Enqueue(timeout);
            available.Release();
            await timeout.WaitAsync(cancellationToken);
        }

        public async Task<PendingLoadTimeout> NextAsync()
        {
            await available.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(pending.TryDequeue(out var timeout));
            return timeout;
        }
    }

    private sealed class PendingLoadTimeout
    {
        private readonly TaskCompletionSource<bool> cancelled = new();
        private readonly TaskCompletionSource<bool> release = new();
        private readonly TaskCompletionSource<bool> returned = new();

        public PendingLoadTimeout(TimeSpan duration, CancellationToken cancellationToken)
        {
            Duration = duration;
            cancellationToken.Register(() => cancelled.TrySetResult(true));
        }

        public Task Cancelled => cancelled.Task;

        public TimeSpan Duration { get; }

        public Task Returned => returned.Task;

        public void Complete()
        {
            release.TrySetResult(true);
        }

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            await release.Task.WaitAsync(cancellationToken);
            returned.TrySetResult(true);
        }
    }

    private sealed record FakeMpvTrack(
        string Type,
        int Id,
        int? FfIndex = null,
        int? DemuxIndex = null,
        bool IsExternal = false,
        string? Codec = null,
        string? Language = null,
        string? Title = null,
        bool IsDefault = false,
        bool IsForced = false)
    {
        public bool IsSelected { get; set; }
    }
}
