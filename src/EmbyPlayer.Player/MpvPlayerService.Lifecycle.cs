using EmbyPlayer.Core.Playback;
using System.Collections.Concurrent;
using System.Globalization;

namespace EmbyPlayer.Player;

public sealed partial class MpvPlayerService : IPlayerService
{
    private const int EndFileReasonEof = 0;
    private const int EndFileReasonError = 4;
    private const int EndFileReasonRedirect = 5;
    private static readonly TimeSpan EventLoopStopTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProgressPollInterval = TimeSpan.FromMilliseconds(500);
    private readonly IPlayerDiagnostics diagnostics;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly Func<TimeSpan, CancellationToken, Task> loadTimeoutDelay;
    private readonly IMpvNativeApi nativeApi;
    private readonly ConcurrentDictionary<int, int> externalSubtitleStreamIndexes = new();
    private PlaybackAttempt? activePlaybackAttempt;
    private CancellationTokenSource? eventLoopCancellation;
    private Task? eventLoopTask;
    private CancellationTokenSource? progressLoopCancellation;
    private Task? progressLoopTask;
    private IntPtr handle;
    private long activePlaybackInstanceId;
    private long pendingSeekPlaybackInstanceId;
    private double pendingSeekSeconds;
    private bool pendingSeekApplied = true;
    private bool isInitialized;
    private PlaybackInfo? currentPlaybackInfo;
    private MpvLifecycleState lifecycleState = MpvLifecycleState.NotInitialized;
    private int diagnosticTrackCount;

    public MpvPlayerService()
        : this(new LibMpvNativeApi(), new FilePlayerDiagnostics())
    {
    }

    internal MpvPlayerService(
        IMpvNativeApi nativeApi,
        IPlayerDiagnostics diagnostics,
        Func<TimeSpan, CancellationToken, Task>? loadTimeoutDelay = null)
    {
        this.nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
        this.diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        this.loadTimeoutDelay = loadTimeoutDelay ?? Task.Delay;
    }

    public event EventHandler<PlayerStatusChangedEventArgs>? StatusChanged;

    public event EventHandler<PlayerProgressChangedEventArgs>? ProgressChanged;

    public async Task<PlayerOperationResult> LoadAsync(
        PlayerLoadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.PlaybackInfo.PlaybackPath))
        {
            return PlayerOperationResult.Failure(PlayerError.NoPlayableUrl);
        }

        if (request.VideoHostHandle == IntPtr.Zero)
        {
            return PlayerOperationResult.Failure(PlayerError.InitializationFailed);
        }

        // Reserve lifecycle order on the caller before moving blocking native work to a worker.
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => LoadCoreAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { lifecycleGate.Release(); }
    }

    private async Task<PlayerOperationResult> LoadCoreAsync(PlayerLoadRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNotDisposed();
        await StopHandleCoreAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        activePlaybackInstanceId = request.PlaybackInstanceId;
        currentPlaybackInfo = request.PlaybackInfo;
        diagnosticTrackCount = 0;
#if DEBUG
        diagnostics.Write(PlayerDebugDiagnostics.CreatePlaybackAttemptMessage(request));
#endif
        pendingSeekSeconds = Math.Max(0, request.PlaybackInfo.StartPositionTicks - request.PlaybackInfo.StreamPositionOffsetTicks) / (double)TimeSpan.TicksPerSecond;
        pendingSeekPlaybackInstanceId = request.PlaybackInstanceId;
        pendingSeekApplied = pendingSeekSeconds <= 0;

        WriteDiagnostic(
            request.PlaybackInstanceId,
            "load-start "
            + $"hwndNonZero={request.VideoHostHandle != IntPtr.Zero} "
            + "widBeforeInitialize=True "
            + $"playbackMode={GetPlaybackMode(request)} "
            + $"playbackPathKind={GetPlaybackPathKind(request.PlaybackInfo.PlaybackPath)} "
            + $"pathPrefix={GetPlaybackPathPrefix(request.PlaybackInfo.PlaybackPath)} "
            + $"headersCount={request.PlaybackInfo.MediaSource.RequiredHttpHeaders.Count} "
            + $"headerNames={FormatHeaderNames(request.PlaybackInfo.MediaSource.RequiredHttpHeaders)} "
            + $"hasXEmbyTokenHeader={HasHeader(request, "X-Emby-Token")} "
            + $"hasStartPosition={pendingSeekSeconds > 0} "
            + $"container={FormatTrackValue(request.PlaybackInfo.MediaSource.Container)} "
            + $"audioCodecs={FormatTrackValue(JoinCodecs(request.PlaybackInfo.AudioTracks.Select(track => track.Codec)))} "
            + $"subtitleCodecs={FormatTrackValue(JoinCodecs(request.PlaybackInfo.Subtitles.Select(track => track.Codec)))} "
            + $"durationKnown={request.PlaybackInfo.RunTimeTicks is > 0} "
            + $"mediaSourceIdPresent={!string.IsNullOrWhiteSpace(request.PlaybackInfo.MediaSource.Id)} "
            + $"playSessionIdPresent={!string.IsNullOrWhiteSpace(request.PlaybackInfo.PlaySessionId)}");

        try
        {
            lifecycleState = MpvLifecycleState.Initializing;
            isInitialized = false;
            handle = nativeApi.Create();
            WriteDiagnostic(request.PlaybackInstanceId, $"mpv-create result={handle != IntPtr.Zero}");
            if (handle == IntPtr.Zero)
            {
                lifecycleState = MpvLifecycleState.Failed;
                return PlayerOperationResult.Failure(PlayerError.InitializationFailed);
            }

            _ = nativeApi.RequestLogMessages(handle, "warn");
            if (nativeApi.SetOptionString(handle, "terminal", "no") < 0
                || nativeApi.SetOptionString(handle, "msg-level", "all=no") < 0
                || nativeApi.SetOptionString(handle, "speed", "1") < 0
                || nativeApi.SetOptionString(handle, "audio-pitch-correction", "yes") < 0
                || nativeApi.SetOptionString(handle, "wid", request.VideoHostHandle.ToInt64().ToString(CultureInfo.InvariantCulture)) < 0
                || (request.StartPaused && nativeApi.SetOptionString(handle, "pause", "yes") < 0))
            {
                WriteDiagnostic(request.PlaybackInstanceId, "init-failed optionSet=False");
                lifecycleState = MpvLifecycleState.Failed;
                await StopHandleCoreAsync(CancellationToken.None).ConfigureAwait(false);
                return PlayerOperationResult.Failure(PlayerError.InitializationFailed);
            }

            if (nativeApi.Initialize(handle) < 0)
            {
                WriteDiagnostic(request.PlaybackInstanceId, "init-failed initialize=False");
                lifecycleState = MpvLifecycleState.Failed;
                await StopHandleCoreAsync(CancellationToken.None).ConfigureAwait(false);
                return PlayerOperationResult.Failure(PlayerError.InitializationFailed);
            }

            lifecycleState = MpvLifecycleState.Ready;
            isInitialized = true;
            cancellationToken.ThrowIfCancellationRequested();
            WriteDiagnostic(request.PlaybackInstanceId, "init-success");
            if (!TryApplyRequiredHeaders(request))
            {
                WriteDiagnostic(request.PlaybackInstanceId, "header-setup-failed");
                lifecycleState = MpvLifecycleState.Failed;
                await StopHandleCoreAsync(CancellationToken.None).ConfigureAwait(false);
                return PlayerOperationResult.Failure(PlayerError.HeaderSetupFailed);
            }

            lifecycleState = MpvLifecycleState.Loading;
            var playbackAttempt = new PlaybackAttempt(handle, request.PlaybackInstanceId);
            Volatile.Write(ref activePlaybackAttempt, playbackAttempt);
            StartEventLoop(handle, request.PlaybackInstanceId, playbackAttempt);
            var loadResult = LoadFile(request);
            WriteDiagnostic(request.PlaybackInstanceId, $"loadfile-called result={loadResult}");
            if (loadResult < 0)
            {
                playbackAttempt.TryTerminate();
                lifecycleState = MpvLifecycleState.Failed;
                await StopHandleCoreAsync(CancellationToken.None).ConfigureAwait(false);
                return PlayerOperationResult.Failure(PlayerError.LoadFailed);
            }

            _ = MonitorLoadTimeoutAsync(playbackAttempt, eventLoopCancellation!.Token);
            return PlayerOperationResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopHandleCoreAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (DllNotFoundException)
        {
            WriteDiagnostic(request.PlaybackInstanceId, "runtime-missing dllNotFound=True");
            lifecycleState = MpvLifecycleState.Failed;
            await StopHandleCoreAsync(CancellationToken.None).ConfigureAwait(false);
            return PlayerOperationResult.Failure(PlayerError.RuntimeMissing);
        }
        catch (BadImageFormatException)
        {
            WriteDiagnostic(request.PlaybackInstanceId, "runtime-missing badImage=True");
            lifecycleState = MpvLifecycleState.Failed;
            await StopHandleCoreAsync(CancellationToken.None).ConfigureAwait(false);
            return PlayerOperationResult.Failure(PlayerError.RuntimeMissing);
        }
        catch (EntryPointNotFoundException)
        {
            WriteDiagnostic(request.PlaybackInstanceId, "init-failed entryPointMissing=True");
            lifecycleState = MpvLifecycleState.Failed;
            await StopHandleCoreAsync(CancellationToken.None).ConfigureAwait(false);
            return PlayerOperationResult.Failure(PlayerError.InitializationFailed);
        }
        catch (Exception ex)
        {
            var error = isInitialized ? PlayerError.LoadFailed : PlayerError.InitializationFailed;
            WriteDiagnostic(request.PlaybackInstanceId, $"load-exception type={ex.GetType().Name}");
            lifecycleState = MpvLifecycleState.Failed;
            await StopHandleCoreAsync(CancellationToken.None).ConfigureAwait(false);
            return PlayerOperationResult.Failure(error);
        }
    }

    public async Task<PlayerOperationResult> PlayAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (handle == IntPtr.Zero || lifecycleState is MpvLifecycleState.Stopping or MpvLifecycleState.Disposed)
            {
                return PlayerOperationResult.Failure(PlayerError.PlaybackFailed);
            }

            if (nativeApi.SetPropertyString(handle, "pause", "no") < 0)
            {
                lifecycleState = MpvLifecycleState.Failed;
                return PlayerOperationResult.Failure(PlayerError.PlaybackFailed);
            }

            lifecycleState = MpvLifecycleState.Playing;
            return PlayerOperationResult.Success();
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task<PlayerOperationResult> PauseAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (handle == IntPtr.Zero || lifecycleState is MpvLifecycleState.Stopping or MpvLifecycleState.Disposed)
            {
                return PlayerOperationResult.Failure(PlayerError.PlaybackFailed);
            }

            if (nativeApi.SetPropertyString(handle, "pause", "yes") < 0)
            {
                lifecycleState = MpvLifecycleState.Failed;
                return PlayerOperationResult.Failure(PlayerError.PlaybackFailed);
            }

            lifecycleState = MpvLifecycleState.Paused;
            return PlayerOperationResult.Success();
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task<PlayerOperationResult> SeekAsync(
        long playbackInstanceId,
        TimeSpan position,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => SeekCore(playbackInstanceId, position, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { lifecycleGate.Release(); }
    }

    private PlayerOperationResult SeekCore(long playbackInstanceId, TimeSpan position, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (handle == IntPtr.Zero
                || playbackInstanceId == 0
                || playbackInstanceId != activePlaybackInstanceId
                || lifecycleState is MpvLifecycleState.Stopping or MpvLifecycleState.Disposed or MpvLifecycleState.Failed)
            {
                WriteDiagnostic(playbackInstanceId, $"seek-rejected state={lifecycleState} handleNonZero={handle != IntPtr.Zero}");
                return PlayerOperationResult.Failure(PlayerError.SeekFailed);
            }

            if (lifecycleState is not (MpvLifecycleState.Playing or MpvLifecycleState.Paused or MpvLifecycleState.Ready))
            {
                WriteDiagnostic(playbackInstanceId, $"seek-rejected state={lifecycleState}");
                return PlayerOperationResult.Failure(PlayerError.SeekFailed);
            }

            var streamOffset = Math.Max(0, currentPlaybackInfo?.StreamPositionOffsetTicks ?? 0);
            if (position.Ticks < streamOffset)
            {
                return PlayerOperationResult.Failure(PlayerError.SeekFailed);
            }
            var seekSeconds = Math.Max(0, position.TotalSeconds - streamOffset / (double)TimeSpan.TicksPerSecond)
                .ToString("0.###", CultureInfo.InvariantCulture);
            var result = nativeApi.Command(handle, new[] { "seek", seekSeconds, "absolute+exact" });
            WriteDiagnostic(playbackInstanceId, $"seek-called result={result}");
            return result < 0
                ? PlayerOperationResult.Failure(PlayerError.SeekFailed)
                : PlayerOperationResult.Success();
        }
        catch
        {
            WriteDiagnostic(playbackInstanceId, "seek-exception");
            return PlayerOperationResult.Failure(PlayerError.SeekFailed);
        }
    }

    public async Task<PlayerOperationResult> SetVolumeAsync(
        int volume,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (handle == IntPtr.Zero
                || lifecycleState is MpvLifecycleState.Stopping or MpvLifecycleState.Disposed or MpvLifecycleState.Failed)
            {
                return PlayerOperationResult.Failure(PlayerError.VolumeFailed);
            }

            var clampedVolume = Math.Clamp(volume, 0, 100);
            var result = nativeApi.SetPropertyString(
                handle,
                "volume",
                clampedVolume.ToString(CultureInfo.InvariantCulture));
            WriteDiagnostic(activePlaybackInstanceId, $"volume-set result={result} value={clampedVolume}");
            return result < 0
                ? PlayerOperationResult.Failure(PlayerError.VolumeFailed)
                : PlayerOperationResult.Success();
        }
        catch
        {
            WriteDiagnostic(activePlaybackInstanceId, "volume-set-exception");
            return PlayerOperationResult.Failure(PlayerError.VolumeFailed);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task<PlayerOperationResult> SetMuteAsync(
        bool isMuted,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (handle == IntPtr.Zero
                || lifecycleState is MpvLifecycleState.Stopping or MpvLifecycleState.Disposed or MpvLifecycleState.Failed)
            {
                return PlayerOperationResult.Failure(PlayerError.MuteFailed);
            }

            var result = nativeApi.SetPropertyString(handle, "mute", isMuted ? "yes" : "no");
            WriteDiagnostic(activePlaybackInstanceId, $"mute-set result={result} muted={isMuted}");
            return result < 0
                ? PlayerOperationResult.Failure(PlayerError.MuteFailed)
                : PlayerOperationResult.Success();
        }
        catch
        {
            WriteDiagnostic(activePlaybackInstanceId, "mute-set-exception");
            return PlayerOperationResult.Failure(PlayerError.MuteFailed);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public Task<PlayerOperationResult> SelectAudioTrackAsync(
        long playbackInstanceId,
        int mpvTrackId,
        CancellationToken cancellationToken)
    {
        return SetMpvTrackPropertyAsync(
            playbackInstanceId,
            "audio",
            "aid",
            mpvTrackId,
            PlayerError.AudioTrackFailed,
            "audio-track-set",
            cancellationToken);
    }

    public Task<PlayerOperationResult> SelectSubtitleTrackAsync(
        long playbackInstanceId,
        int mpvTrackId,
        CancellationToken cancellationToken)
    {
        return SetMpvTrackPropertyAsync(
            playbackInstanceId,
            "sub",
            "sid",
            mpvTrackId,
            PlayerError.SubtitleTrackFailed,
            "subtitle-track-set",
            cancellationToken);
    }

    public async Task<PlayerOperationResult> SelectExternalSubtitleAsync(
        long playbackInstanceId,
        int mediaStreamIndex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (handle == IntPtr.Zero
                || playbackInstanceId == 0
                || playbackInstanceId != activePlaybackInstanceId
                || lifecycleState is MpvLifecycleState.Stopping or MpvLifecycleState.Disposed or MpvLifecycleState.Failed)
            {
                return PlayerOperationResult.Failure(PlayerError.SubtitleTrackFailed);
            }

            return AddExternalSubtitle(
                playbackInstanceId,
                mediaStreamIndex,
                PlayerError.SubtitleTrackFailed,
                cancellationToken);
        }
        catch
        {
            WriteDiagnostic(playbackInstanceId, "external-subtitle-select-exception");
            return PlayerOperationResult.Failure(PlayerError.SubtitleTrackFailed);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public Task<PlayerOperationResult> DisableSubtitleAsync(
        long playbackInstanceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return SetTrackPropertiesAsync(
            playbackInstanceId,
            PlayerError.SubtitleTrackFailed,
            "subtitle-disable",
            cancellationToken,
            ("sid", "no"),
            ("sub-visibility", "no"));
    }

    private async Task<PlayerOperationResult> SetMpvTrackPropertyAsync(
        long playbackInstanceId,
        string mpvTrackType,
        string propertyName,
        int mpvTrackId,
        PlayerError failureError,
        string diagnosticName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (handle == IntPtr.Zero
                || playbackInstanceId == 0
                || playbackInstanceId != activePlaybackInstanceId
                || lifecycleState is MpvLifecycleState.Stopping or MpvLifecycleState.Disposed or MpvLifecycleState.Failed)
            {
                return PlayerOperationResult.Failure(failureError);
            }

            var mpvTrack = GetMpvTracks(handle, mpvTrackType)
                .FirstOrDefault(track => track.Id == mpvTrackId);
            if (mpvTrack is null)
            {
                WriteDiagnostic(playbackInstanceId, $"{diagnosticName}-track-not-found mpvTrackId={mpvTrackId}");
                return PlayerOperationResult.Failure(failureError);
            }

            var properties = mpvTrackType == "sub"
                ? new[] { (propertyName, mpvTrack.Id.ToString(CultureInfo.InvariantCulture)), ("sub-visibility", "yes") }
                : new[] { (propertyName, mpvTrack.Id.ToString(CultureInfo.InvariantCulture)) };

            var result = SetTrackPropertiesCore(
                playbackInstanceId,
                failureError,
                diagnosticName,
                properties);
            if (!result.IsSuccess)
            {
                return result;
            }

            return VerifyTrackSelection(
                playbackInstanceId,
                mpvTrackType,
                propertyName,
                mpvTrack.Id,
                failureError,
                diagnosticName);
        }
        catch
        {
            WriteDiagnostic(playbackInstanceId, $"{diagnosticName}-exception");
            return PlayerOperationResult.Failure(failureError);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task<PlayerOperationResult> SetTrackPropertiesAsync(
        long playbackInstanceId,
        PlayerError failureError,
        string diagnosticName,
        CancellationToken cancellationToken,
        params (string Name, string Value)[] properties)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (handle == IntPtr.Zero
                || playbackInstanceId == 0
                || playbackInstanceId != activePlaybackInstanceId
                || lifecycleState is MpvLifecycleState.Stopping or MpvLifecycleState.Disposed or MpvLifecycleState.Failed)
            {
                return PlayerOperationResult.Failure(failureError);
            }

            return SetTrackPropertiesCore(
                playbackInstanceId,
                failureError,
                diagnosticName,
                properties);
        }
        catch
        {
            WriteDiagnostic(playbackInstanceId, $"{diagnosticName}-exception");
            return PlayerOperationResult.Failure(failureError);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private PlayerOperationResult AddExternalSubtitle(
        long playbackInstanceId,
        int mediaStreamIndex,
        PlayerError failureError,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var subtitle = currentPlaybackInfo?.Subtitles.FirstOrDefault(item => item.Index == mediaStreamIndex);
        if (subtitle is null || !subtitle.IsExternal || string.IsNullOrWhiteSpace(subtitle.DeliveryUrl))
        {
            WriteDiagnostic(
                playbackInstanceId,
                $"external-subtitle-unavailable mediaStreamIndex={mediaStreamIndex} hasSubtitle={subtitle is not null} isExternal={subtitle?.IsExternal == true} hasUrl={subtitle?.DeliveryUrl is not null}");
            return PlayerOperationResult.Failure(failureError);
        }

        var addResult = nativeApi.Command(handle, new[] { "sub-add", subtitle.DeliveryUrl!, "select" });
        WriteDiagnostic(
            playbackInstanceId,
            $"external-subtitle-add result={addResult} mediaStreamIndex={mediaStreamIndex} codec={FormatTrackValue(subtitle.Codec)} language={FormatTrackValue(subtitle.Language)}");
        if (addResult < 0)
        {
            return PlayerOperationResult.Failure(failureError);
        }

        var visibilityResult = nativeApi.SetPropertyString(handle, "sub-visibility", "yes");
        WriteDiagnostic(playbackInstanceId, $"external-subtitle-visibility result={visibilityResult}");
        if (visibilityResult < 0)
        {
            return PlayerOperationResult.Failure(failureError);
        }

        var subtitleTracks = GetMpvTracks(handle, "sub");
        var selectedTrackId = GetIntProperty(handle, "sid");
        var selectedTrack = selectedTrackId.HasValue
            ? subtitleTracks.FirstOrDefault(track => track.Id == selectedTrackId.Value)
            : subtitleTracks.FirstOrDefault(track => track.IsSelected == true);
        WriteDiagnostic(
            playbackInstanceId,
            "external-subtitle-verify "
            + $"selectedTrackId={FormatTrackValue(selectedTrack?.Id.ToString(CultureInfo.InvariantCulture))} "
            + $"trackSelected={FormatNullableBool(selectedTrack?.IsSelected)} "
            + $"trackExternal={FormatNullableBool(selectedTrack?.IsExternal)} "
            + $"trackCodec={FormatTrackValue(selectedTrack?.Codec)} "
            + $"trackLanguage={FormatTrackValue(selectedTrack?.Language)}");

        if (selectedTrack is null)
        {
            return PlayerOperationResult.Failure(failureError);
        }

        externalSubtitleStreamIndexes[selectedTrack.Id] = mediaStreamIndex;
        return PlayerOperationResult.Success();
    }

    private PlayerOperationResult SetTrackPropertiesCore(
        long playbackInstanceId,
        PlayerError failureError,
        string diagnosticName,
        IReadOnlyList<(string Name, string Value)> properties)
    {
        foreach (var property in properties)
        {
            var result = nativeApi.SetPropertyString(handle, property.Name, property.Value);
            WriteDiagnostic(playbackInstanceId, $"{diagnosticName} result={result}");
            if (result < 0)
            {
                return PlayerOperationResult.Failure(failureError);
            }
        }

        return PlayerOperationResult.Success();
    }

    public async Task<PlayerOperationResult> StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopHandleCoreAsync(cancellationToken).ConfigureAwait(false);
            return PlayerOperationResult.Success();
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (lifecycleState == MpvLifecycleState.Disposed)
            {
                return;
            }

            PlayerDebugDiagnostics.WriteStopRequested(
                diagnostics,
                activePlaybackInstanceId,
                "dispose");
            await StopHandleCoreAsync(CancellationToken.None).ConfigureAwait(false);
            lifecycleState = MpvLifecycleState.Disposed;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private int LoadFile(PlayerLoadRequest request)
    {
        var args = new[] { "loadfile", request.PlaybackInfo.PlaybackPath!, "replace" };
        return nativeApi.Command(handle, args);
    }

    private bool TryApplyRequiredHeaders(PlayerLoadRequest request)
    {
        var headers = request.PlaybackInfo.MediaSource.RequiredHttpHeaders;
        var headerFields = new List<string>(headers.Count);

        foreach (var header in headers)
        {
            if (string.IsNullOrWhiteSpace(header.Key)
                || header.Key.Contains('\r', StringComparison.Ordinal)
                || header.Key.Contains('\n', StringComparison.Ordinal)
                || header.Value.Contains('\r', StringComparison.Ordinal)
                || header.Value.Contains('\n', StringComparison.Ordinal))
            {
                return false;
            }

            headerFields.Add(EscapeMpvStringListItem($"{header.Key}: {header.Value}"));
        }

        return nativeApi.SetOptionString(
            handle,
            "http-header-fields",
            string.Join(',', headerFields)) >= 0;
    }

    private static string EscapeMpvStringListItem(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal);
    }

    private async Task StopHandleCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref activePlaybackAttempt, null)?.TryTerminate();
        externalSubtitleStreamIndexes.Clear();
        localSubtitlePaths.Clear();

        if (handle == IntPtr.Zero)
        {
            ClearPendingSeek();
            if (lifecycleState != MpvLifecycleState.Disposed)
            {
                lifecycleState = MpvLifecycleState.NotInitialized;
            }

            return;
        }

        var handleToDestroy = handle;
        var loopTask = eventLoopTask;
        var loopCancellation = eventLoopCancellation;
        var progressTask = progressLoopTask;
        var progressCancellation = progressLoopCancellation;
        var shouldSendStopCommand = isInitialized;
        handle = IntPtr.Zero;
        isInitialized = false;
        eventLoopTask = null;
        eventLoopCancellation = null;
        progressLoopTask = null;
        progressLoopCancellation = null;
        lifecycleState = MpvLifecycleState.Stopping;
        WriteDiagnostic(activePlaybackInstanceId, "stop-start");

        if (shouldSendStopCommand)
        {
            try
            {
                await Task.Run(() => nativeApi.Command(handleToDestroy, new[] { "stop" })).ConfigureAwait(false);
                WriteDiagnostic(activePlaybackInstanceId, "stop-command-called");
            }
            catch (Exception ex)
            {
                WriteDiagnostic(activePlaybackInstanceId, $"stop-command-failed type={ex.GetType().Name}");
            }
        }

        loopCancellation?.Cancel();
        progressCancellation?.Cancel();
        try
        {
            nativeApi.Wakeup(handleToDestroy);
            WriteDiagnostic(activePlaybackInstanceId, "event-loop-wakeup-called");
        }
        catch (Exception ex)
        {
            WriteDiagnostic(activePlaybackInstanceId, $"event-loop-wakeup-failed type={ex.GetType().Name}");
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask
                    // Once detached, this handle must finish retirement even if its caller cancels.
                    .WaitAsync(EventLoopStopTimeout)
                    .ConfigureAwait(false);
                WriteDiagnostic(activePlaybackInstanceId, "event-loop-stopped");
            }
            catch (TimeoutException)
            {
                WriteDiagnostic(activePlaybackInstanceId, "event-loop-stop-timeout destroySkipped=True");
                lifecycleState = MpvLifecycleState.Failed;
                loopCancellation?.Dispose();
                return;
            }
        }

        if (progressTask is not null)
        {
            try
            {
                await progressTask
                    .WaitAsync(EventLoopStopTimeout)
                    .ConfigureAwait(false);
                WriteDiagnostic(activePlaybackInstanceId, "progress-loop-stopped");
            }
            catch (TimeoutException)
            {
                WriteDiagnostic(activePlaybackInstanceId, "progress-loop-stop-timeout destroySkipped=True");
                lifecycleState = MpvLifecycleState.Failed;
                loopCancellation?.Dispose();
                progressCancellation?.Dispose();
                return;
            }
        }

        try
        {
            await Task.Run(() => nativeApi.TerminateDestroy(handleToDestroy)).ConfigureAwait(false);
            WriteDiagnostic(activePlaybackInstanceId, "handle-destroyed");
        }
        catch (Exception ex)
        {
            WriteDiagnostic(activePlaybackInstanceId, $"handle-destroy-failed type={ex.GetType().Name}");
            lifecycleState = MpvLifecycleState.Failed;
            return;
        }
        finally
        {
            loopCancellation?.Dispose();
            progressCancellation?.Dispose();
        }

        if (lifecycleState != MpvLifecycleState.Disposed)
        {
            lifecycleState = MpvLifecycleState.NotInitialized;
        }

        currentPlaybackInfo = null;
        ClearPendingSeek();
    }

    private async Task MonitorLoadTimeoutAsync(
        PlaybackAttempt playbackAttempt,
        CancellationToken cancellationToken)
    {
        try
        {
            await loadTimeoutDelay(LoadTimeout, cancellationToken).ConfigureAwait(false);
            if (!playbackAttempt.TryTimeout())
            {
                return;
            }

            await lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(Volatile.Read(ref activePlaybackAttempt), playbackAttempt)
                    || handle != playbackAttempt.Handle)
                {
                    return;
                }

                WriteDiagnostic(playbackAttempt.PlaybackInstanceId, "load-timeout");
                await StopHandleCoreAsync(CancellationToken.None).ConfigureAwait(false);
                lifecycleState = MpvLifecycleState.Failed;
                RaiseStatusChanged(new PlayerStatusChangedEventArgs(
                    playbackAttempt.PlaybackInstanceId,
                    PlayerPlaybackState.Failed,
                    PlayerError.LoadFailed));
            }
            finally
            {
                lifecycleGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the attempt is stopped or replaced.
        }
    }

    private void StartEventLoop(
        IntPtr eventHandle,
        long playbackInstanceId,
        PlaybackAttempt playbackAttempt)
    {
        eventLoopCancellation = new CancellationTokenSource();
        var cancellationToken = eventLoopCancellation.Token;
        eventLoopTask = Task.Run(
            () => EventLoop(eventHandle, playbackInstanceId, playbackAttempt, cancellationToken),
            CancellationToken.None);
        WriteDiagnostic(playbackInstanceId, "event-loop-started");
    }

    private void EventLoop(
        IntPtr eventHandle,
        long playbackInstanceId,
        PlaybackAttempt playbackAttempt,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var mpvEvent = nativeApi.WaitEvent(eventHandle, 0.25);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (mpvEvent.EventId == MpvEventId.None)
                {
                    continue;
                }

                switch (mpvEvent.EventId)
                {
                    case MpvEventId.Shutdown:
                        WriteDiagnostic(playbackInstanceId, "event=shutdown");
                        if (playbackAttempt.TryTerminate())
                        {
                            CancelProgressLoop(playbackInstanceId, "shutdown");
                            lifecycleState = MpvLifecycleState.Failed;
                            RaiseStatusChanged(new PlayerStatusChangedEventArgs(
                                playbackInstanceId,
                                PlayerPlaybackState.Failed,
                                PlayerError.PlaybackFailed));
                        }

                        return;
                    case MpvEventId.LogMessage:
                        WriteDiagnostic(
                            playbackInstanceId,
                            $"event=log-message error={mpvEvent.Error} keywords={FormatTrackValue(mpvEvent.LogMessageKeywords)}");
                        break;
                    case MpvEventId.FileLoaded:
                    case MpvEventId.PlaybackRestart:
                        HandlePlaybackReadyEvent(eventHandle, playbackInstanceId, playbackAttempt, mpvEvent);
                        break;
                    case MpvEventId.AudioReconfig:
                    case MpvEventId.VideoReconfig:
                        WritePlaybackState(eventHandle, playbackInstanceId, GetEventName(mpvEvent.EventId), mpvEvent.Error);
                        break;
                    case MpvEventId.EndFile:
                        HandleEndFile(eventHandle, playbackInstanceId, playbackAttempt, mpvEvent);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                WriteDiagnostic(playbackInstanceId, $"event-loop-exception type={ex.GetType().Name}");
                if (!playbackAttempt.TryTerminate())
                {
                    return;
                }

                lifecycleState = MpvLifecycleState.Failed;
                RaiseStatusChanged(new PlayerStatusChangedEventArgs(
                    playbackInstanceId,
                    PlayerPlaybackState.Failed,
                    PlayerError.PlaybackFailed));
            }
        }
    }

    private void HandlePlaybackReadyEvent(
        IntPtr eventHandle,
        long playbackInstanceId,
        PlaybackAttempt playbackAttempt,
        MpvEventSnapshot mpvEvent)
    {
        if (!playbackAttempt.TryActivate())
        {
            return;
        }

        var hasAudioTrack = HasPropertyString(eventHandle, "current-tracks/audio/id");
        var hasVideoTrack = HasPropertyString(eventHandle, "current-tracks/video/id");
        var hasVideoOutParams = HasPropertyString(eventHandle, "video-out-params/w");
        var tracks = GetAllMpvTracks(eventHandle);
        diagnosticTrackCount = tracks.Count;
        WritePlaybackStageDiagnostic(
            playbackInstanceId,
            GetEventName(mpvEvent.EventId),
            tracks.Count,
            hasAudioTrack,
            hasVideoTrack,
            hasVideoOutParams,
            mpvEvent.Error);
        WriteSubtitleTracksDiagnostic(playbackInstanceId, tracks);
        WritePlaybackState(eventHandle, playbackInstanceId, GetEventName(mpvEvent.EventId), mpvEvent.Error);
        if (!TryApplyPendingSeek(eventHandle, playbackInstanceId))
        {
            playbackAttempt.TryTerminate();
            return;
        }

        var startsPaused = nativeApi.GetFlagProperty(eventHandle, "pause") == true;
        lifecycleState = startsPaused ? MpvLifecycleState.Paused : MpvLifecycleState.Playing;
        StartProgressLoop(eventHandle, playbackInstanceId);
        RaiseStatusChanged(new PlayerStatusChangedEventArgs(
            playbackInstanceId,
            startsPaused ? PlayerPlaybackState.Paused : PlayerPlaybackState.Playing,
            hasAudioTrack: hasAudioTrack,
            hasVideoTrack: hasVideoTrack,
            hasVideoOutParams: hasVideoOutParams,
            tracks: tracks));
    }

    private void HandleEndFile(
        IntPtr eventHandle,
        long playbackInstanceId,
        PlaybackAttempt playbackAttempt,
        MpvEventSnapshot mpvEvent)
    {
        if (mpvEvent.EndFileReason == EndFileReasonRedirect)
        {
            return;
        }

        if (!playbackAttempt.TryTerminate())
        {
            return;
        }

        var reasonText = GetEndFileReason(mpvEvent.EndFileReason);
        var endFileError = mpvEvent.EndFileError != 0 ? mpvEvent.EndFileError : mpvEvent.Error;
        WritePlaybackStageDiagnostic(
            playbackInstanceId,
            $"end-file reason={reasonText}",
            diagnosticTrackCount,
            HasPropertyString(eventHandle, "current-tracks/audio/id"),
            HasPropertyString(eventHandle, "current-tracks/video/id"),
            HasPropertyString(eventHandle, "video-out-params/w"),
            endFileError);
        WritePlaybackState(eventHandle, playbackInstanceId, $"end-file reason={reasonText}", endFileError);
        if (mpvEvent.EndFileReason == EndFileReasonError || endFileError < 0)
        {
            lifecycleState = MpvLifecycleState.Failed;
            CancelProgressLoop(playbackInstanceId, "failed");
            RaiseStatusChanged(new PlayerStatusChangedEventArgs(
                playbackInstanceId,
                PlayerPlaybackState.Failed,
                PlayerError.LoadFailed,
                reasonText,
                HasPropertyString(eventHandle, "current-tracks/audio/id"),
                HasPropertyString(eventHandle, "current-tracks/video/id"),
                HasPropertyString(eventHandle, "video-out-params/w")));
            return;
        }

        if (mpvEvent.EndFileReason == EndFileReasonEof)
        {
            CancelProgressLoop(playbackInstanceId, "completed");
            lifecycleState = MpvLifecycleState.Ready;
            RaiseProgressChanged(CreateProgressSnapshot(eventHandle, playbackInstanceId));
            RaiseStatusChanged(new PlayerStatusChangedEventArgs(
                playbackInstanceId,
                PlayerPlaybackState.Completed));
        }
    }

    private void StartProgressLoop(IntPtr progressHandle, long playbackInstanceId)
    {
        if (handle != progressHandle
            || lifecycleState is MpvLifecycleState.Stopping or MpvLifecycleState.Disposed or MpvLifecycleState.Failed)
        {
            return;
        }

        if (progressLoopTask is not null)
        {
            return;
        }

        progressLoopCancellation = new CancellationTokenSource();
        var cancellationToken = progressLoopCancellation.Token;
        progressLoopTask = Task.Run(
            () => ProgressLoopAsync(progressHandle, playbackInstanceId, cancellationToken),
            CancellationToken.None);
        WriteDiagnostic(playbackInstanceId, "progress-loop-started");
    }

    private async Task ProgressLoopAsync(
        IntPtr progressHandle,
        long playbackInstanceId,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && handle == progressHandle)
            {
                RaiseProgressChanged(CreateProgressSnapshot(progressHandle, playbackInstanceId));
                await Task.Delay(ProgressPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during Stop/Dispose.
        }
        catch (Exception ex)
        {
            WriteDiagnostic(playbackInstanceId, $"progress-loop-exception type={ex.GetType().Name}");
        }
    }

    private void CancelProgressLoop(long playbackInstanceId, string reason)
    {
        var cancellation = progressLoopCancellation;
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
            WriteDiagnostic(playbackInstanceId, $"progress-loop-cancelled reason={reason}");
        }
        catch (Exception ex)
        {
            WriteDiagnostic(playbackInstanceId, $"progress-loop-cancel-failed type={ex.GetType().Name}");
        }
    }

    private PlayerProgressChangedEventArgs CreateProgressSnapshot(
        IntPtr progressHandle,
        long playbackInstanceId)
    {
        var streamPosition = SecondsToTimeSpan(nativeApi.GetDoubleProperty(progressHandle, "time-pos"));
        var streamDuration = SecondsToTimeSpan(nativeApi.GetDoubleProperty(progressHandle, "duration"));
        var offsetTicks = Math.Max(0, currentPlaybackInfo?.StreamPositionOffsetTicks ?? 0);
        var position = streamPosition;
        var totalDuration = streamDuration;
        var percent = NormalizePercent(nativeApi.GetDoubleProperty(progressHandle, "percent-pos"));
        if (offsetTicks > 0)
        {
            position = streamPosition.HasValue && streamPosition.Value.Ticks <= TimeSpan.MaxValue.Ticks - offsetTicks
                ? TimeSpan.FromTicks(streamPosition.Value.Ticks + offsetTicks) : null;
            totalDuration = currentPlaybackInfo?.RunTimeTicks is > 0 and var ticks
                ? TimeSpan.FromTicks(ticks)
                : streamDuration.HasValue && streamDuration.Value.Ticks <= TimeSpan.MaxValue.Ticks - offsetTicks
                    ? TimeSpan.FromTicks(streamDuration.Value.Ticks + offsetTicks) : null;
            percent = position.HasValue && totalDuration is { Ticks: > 0 }
                ? NormalizePercent(position.Value.TotalSeconds / totalDuration.Value.TotalSeconds * 100) : null;
        }
        return new PlayerProgressChangedEventArgs(
            playbackInstanceId,
            position,
            totalDuration,
            percent,
            nativeApi.GetFlagProperty(progressHandle, "pause"));
    }

    private static TimeSpan? SecondsToTimeSpan(double? seconds)
    {
        if (!seconds.HasValue
            || double.IsNaN(seconds.Value)
            || double.IsInfinity(seconds.Value)
            || seconds.Value < 0)
        {
            return null;
        }

        return TimeSpan.FromSeconds(seconds.Value);
    }

    private static double? NormalizePercent(double? percent)
    {
        if (!percent.HasValue
            || double.IsNaN(percent.Value)
            || double.IsInfinity(percent.Value))
        {
            return null;
        }

        return Math.Clamp(percent.Value, 0d, 100d);
    }

    private bool TryApplyPendingSeek(IntPtr eventHandle, long playbackInstanceId)
    {
        if (pendingSeekApplied
            || pendingSeekPlaybackInstanceId != playbackInstanceId
            || pendingSeekSeconds <= 0)
        {
            return true;
        }

        pendingSeekApplied = true;
        try
        {
            var seekSeconds = pendingSeekSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            var result = nativeApi.Command(eventHandle, new[] { "seek", seekSeconds, "absolute+exact" });
            WriteDiagnostic(playbackInstanceId, $"seek-called result={result}");
            if (result >= 0)
            {
                return true;
            }

            lifecycleState = MpvLifecycleState.Failed;
            RaiseStatusChanged(new PlayerStatusChangedEventArgs(
                playbackInstanceId,
                PlayerPlaybackState.Failed,
                PlayerError.ResumeFailed));
            return false;
        }
        catch (Exception ex)
        {
            WriteDiagnostic(playbackInstanceId, $"seek-failed type={ex.GetType().Name}");
            lifecycleState = MpvLifecycleState.Failed;
            RaiseStatusChanged(new PlayerStatusChangedEventArgs(
                playbackInstanceId,
                PlayerPlaybackState.Failed,
                PlayerError.ResumeFailed));
            return false;
        }
    }

    private void WritePlaybackState(IntPtr eventHandle, long playbackInstanceId, string eventName, int error)
    {
        var hasAudio = HasPropertyString(eventHandle, "current-tracks/audio/id");
        var hasVideo = HasPropertyString(eventHandle, "current-tracks/video/id");
        var hasVideoOutParams = HasPropertyString(eventHandle, "video-out-params/w");
        var idleActive = nativeApi.GetFlagProperty(eventHandle, "idle-active");
        var coreIdle = nativeApi.GetFlagProperty(eventHandle, "core-idle");
        var pause = nativeApi.GetFlagProperty(eventHandle, "pause");
        WriteDiagnostic(
            playbackInstanceId,
            $"event={eventName} error={error} "
            + $"hasAudioTrack={hasAudio} hasVideoTrack={hasVideo} hasVideoOutParams={hasVideoOutParams} "
            + $"idleActive={FormatNullableBool(idleActive)} coreIdle={FormatNullableBool(coreIdle)} pause={FormatNullableBool(pause)}");
    }

    private bool HasPropertyString(IntPtr eventHandle, string name)
    {
        return !string.IsNullOrWhiteSpace(nativeApi.GetPropertyString(eventHandle, name));
    }

    private PlayerOperationResult VerifyTrackSelection(
        long playbackInstanceId,
        string mpvTrackType,
        string propertyName,
        int expectedMpvTrackId,
        PlayerError failureError,
        string diagnosticName)
    {
        var currentTrackValue = nativeApi.GetPropertyString(handle, propertyName);
        var subVisibilityValue = mpvTrackType == "sub"
            ? nativeApi.GetPropertyString(handle, "sub-visibility")
            : null;
        var selectedTrack = GetMpvTracks(handle, mpvTrackType)
            .FirstOrDefault(track => track.Id == expectedMpvTrackId);
        WriteDiagnostic(
            playbackInstanceId,
            $"{diagnosticName}-verify "
            + $"expectedTrackId={expectedMpvTrackId} "
            + $"{propertyName}Matches={StringEqualsInvariant(currentTrackValue, expectedMpvTrackId.ToString(CultureInfo.InvariantCulture))} "
            + $"subVisibility={FormatTrackValue(subVisibilityValue)} "
            + $"trackSelected={FormatNullableBool(selectedTrack?.IsSelected)} "
            + $"trackExternal={FormatNullableBool(selectedTrack?.IsExternal)} "
            + $"trackCodec={FormatTrackValue(selectedTrack?.Codec)} "
            + $"trackLanguage={FormatTrackValue(selectedTrack?.Language)}");

        if (!string.IsNullOrWhiteSpace(currentTrackValue)
            && !StringEqualsInvariant(currentTrackValue, expectedMpvTrackId.ToString(CultureInfo.InvariantCulture)))
        {
            return PlayerOperationResult.Failure(failureError);
        }

        if (mpvTrackType == "sub"
            && !string.IsNullOrWhiteSpace(subVisibilityValue)
            && !StringEqualsInvariant(subVisibilityValue, "yes")
            && !StringEqualsInvariant(subVisibilityValue, "true"))
        {
            return PlayerOperationResult.Failure(failureError);
        }

        if (selectedTrack?.IsSelected == false)
        {
            return PlayerOperationResult.Failure(failureError);
        }

        return PlayerOperationResult.Success();
    }

    private int[] GetEmbyTrackIndexes(string mpvTrackType)
    {
        if (currentPlaybackInfo is null)
        {
            return Array.Empty<int>();
        }

        return mpvTrackType == "sub"
            ? currentPlaybackInfo.Subtitles.Select(subtitle => subtitle.Index).ToArray()
            : currentPlaybackInfo.AudioTracks.Select(track => track.Index).ToArray();
    }

    private IReadOnlyList<MpvTrackInfo> GetMpvTracks(IntPtr trackHandle, string mpvTrackType)
    {
        var countText = nativeApi.GetPropertyString(trackHandle, "track-list/count");
        if (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            || count <= 0)
        {
            return Array.Empty<MpvTrackInfo>();
        }

        var tracks = new List<MpvTrackInfo>();
        for (var index = 0; index < count; index++)
        {
            var type = nativeApi.GetPropertyString(trackHandle, $"track-list/{index}/type");
            if (!string.Equals(type, mpvTrackType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = GetIntProperty(trackHandle, $"track-list/{index}/id");
            if (!id.HasValue)
            {
                continue;
            }

            tracks.Add(new MpvTrackInfo(
                id.Value,
                GetIntProperty(trackHandle, $"track-list/{index}/ff-index"),
                GetIntProperty(trackHandle, $"track-list/{index}/demux-index"),
                GetBoolProperty(trackHandle, $"track-list/{index}/selected"),
                GetBoolProperty(trackHandle, $"track-list/{index}/external"),
                nativeApi.GetPropertyString(trackHandle, $"track-list/{index}/codec"),
                nativeApi.GetPropertyString(trackHandle, $"track-list/{index}/lang"),
                nativeApi.GetPropertyString(trackHandle, $"track-list/{index}/title"),
                GetBoolProperty(trackHandle, $"track-list/{index}/default"),
                GetBoolProperty(trackHandle, $"track-list/{index}/forced")));
        }

        return tracks;
    }

    private IReadOnlyList<PlayerTrackInfo> GetAllMpvTracks(IntPtr trackHandle)
    {
        var tracks = new List<PlayerTrackInfo>();
        AddPlayerTrackInfo(tracks, "audio", GetMpvTracks(trackHandle, "audio"));
        AddPlayerTrackInfo(tracks, "sub", GetMpvTracks(trackHandle, "sub"));
        return tracks;
    }

    private void AddPlayerTrackInfo(
        ICollection<PlayerTrackInfo> destination,
        string trackType,
        IReadOnlyList<MpvTrackInfo> tracks)
    {
        foreach (var track in tracks)
        {
            destination.Add(new PlayerTrackInfo(
                track.Id,
                trackType,
                track.Language,
                track.Title,
                track.Codec,
                track.IsExternal,
                track.IsSelected,
                track.IsDefault,
                track.IsForced,
                ResolveEmbyStreamIndex(trackType, track, tracks.Count),
                trackType == "sub" && localSubtitlePaths.TryGetValue(track.Id, out var localPath) ? localPath : null));
        }
    }

    private int? ResolveEmbyStreamIndex(
        string mpvTrackType,
        MpvTrackInfo track,
        int outputTrackCount)
    {
        if (StringEqualsInvariant(mpvTrackType, "sub") && localSubtitlePaths.ContainsKey(track.Id)) return null;
        if (StringEqualsInvariant(mpvTrackType, "audio") && outputTrackCount == 1
            && currentPlaybackInfo?.RequiresTranscoding == true
            && currentPlaybackInfo.SelectedAudioStreamIndex is int selectedAudio
            && currentPlaybackInfo.AudioTracks.Any(item => item.Index == selectedAudio))
        {
            return selectedAudio;
        }
        if (StringEqualsInvariant(mpvTrackType, "sub")
            && track.IsExternal == true
            && externalSubtitleStreamIndexes.TryGetValue(track.Id, out var externalStreamIndex))
        {
            return externalStreamIndex;
        }

        var embyIndexes = GetEmbyTrackIndexes(mpvTrackType);
        foreach (var embyIndex in embyIndexes)
        {
            if (track.FfIndex == embyIndex || track.DemuxIndex == embyIndex)
            {
                return embyIndex;
            }
        }

        return null;
    }

    private void WriteSubtitleTracksDiagnostic(long playbackInstanceId, IReadOnlyList<PlayerTrackInfo> tracks)
    {
        var subtitleTracks = tracks
            .Where(track => StringEqualsInvariant(track.Type, "sub"))
            .ToArray();
        if (subtitleTracks.Length == 0)
        {
            WriteDiagnostic(playbackInstanceId, "subtitle-tracks count=0");
            return;
        }

        WriteDiagnostic(
            playbackInstanceId,
            "subtitle-tracks count="
            + subtitleTracks.Length.ToString(CultureInfo.InvariantCulture)
            + " "
            + string.Join(
                ";",
                subtitleTracks.Select(track =>
                    $"id={track.Id} selected={FormatNullableBool(track.IsSelected)} "
                    + $"lang={FormatTrackValue(track.Language)} title={FormatTrackValue(track.Title)} "
                    + $"codec={FormatTrackValue(track.Codec)} external={FormatNullableBool(track.IsExternal)} "
                    + $"default={FormatNullableBool(track.IsDefault)} forced={FormatNullableBool(track.IsForced)}")));
    }

    private int? GetIntProperty(IntPtr propertyHandle, string propertyName)
    {
        var value = nativeApi.GetPropertyString(propertyHandle, propertyName);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private bool? GetBoolProperty(IntPtr propertyHandle, string propertyName)
    {
        var value = nativeApi.GetPropertyString(propertyHandle, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (StringEqualsInvariant(value, "yes")
            || StringEqualsInvariant(value, "true")
            || value == "1")
        {
            return true;
        }

        if (StringEqualsInvariant(value, "no")
            || StringEqualsInvariant(value, "false")
            || value == "0")
        {
            return false;
        }

        return null;
    }

    private void RaiseStatusChanged(PlayerStatusChangedEventArgs eventArgs)
    {
        StatusChanged?.Invoke(this, eventArgs);
    }

    private void RaiseProgressChanged(PlayerProgressChangedEventArgs eventArgs)
    {
        ProgressChanged?.Invoke(this, eventArgs);
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(lifecycleState == MpvLifecycleState.Disposed, this);
    }

    private static bool HasHeader(PlayerLoadRequest request, string headerName)
    {
        return request.PlaybackInfo.MediaSource.RequiredHttpHeaders.Keys.Any(
            key => string.Equals(key, headerName, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetPlaybackMode(PlayerLoadRequest request)
    {
        return request.PlaybackInfo.RequiresTranscoding ? "TranscodingUrl" : "DirectStreamUrl";
    }

    private static string GetPlaybackPathKind(string? playbackPath)
    {
        if (string.IsNullOrWhiteSpace(playbackPath))
        {
            return "Missing";
        }

        return Uri.TryCreate(playbackPath, UriKind.Absolute, out _) ? "Absolute" : "Relative";
    }

    private static string GetPlaybackPathPrefix(string? playbackPath)
    {
        if (string.IsNullOrWhiteSpace(playbackPath))
        {
            return "missing";
        }

        var path = playbackPath;
        if (Uri.TryCreate(playbackPath, UriKind.Absolute, out var uri))
        {
            path = uri.AbsolutePath;
        }

        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "/";
        }

        if (parts.Length >= 2 && string.Equals(parts[0], "emby", StringComparison.OrdinalIgnoreCase))
        {
            return $"/{parts[0]}/{parts[1]}";
        }

        return $"/{parts[0]}";
    }

    private static string FormatHeaderNames(IReadOnlyDictionary<string, string> headers)
    {
        return headers.Count == 0
            ? "none"
            : string.Join(
                ",",
                headers.Keys
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
    }

    private static string GetEventName(MpvEventId eventId)
    {
        return eventId switch
        {
            MpvEventId.FileLoaded => "file-loaded",
            MpvEventId.PlaybackRestart => "playback-restart",
            MpvEventId.AudioReconfig => "audio-reconfig",
            MpvEventId.VideoReconfig => "video-reconfig",
            _ => eventId.ToString()
        };
    }

    private static string GetEndFileReason(int reason)
    {
        return reason switch
        {
            EndFileReasonEof => "eof",
            2 => "stop",
            3 => "quit",
            EndFileReasonError => "error",
            EndFileReasonRedirect => "redirect",
            _ => $"unknown-{reason}"
        };
    }

    private static string FormatNullableBool(bool? value)
    {
        return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "unknown";
    }

    private static string FormatTrackValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }

    private static string? JoinCodecs(IEnumerable<string?> codecs)
    {
        var values = codecs
            .Where(codec => !string.IsNullOrWhiteSpace(codec))
            .Select(codec => codec!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return values.Length == 0 ? null : string.Join(",", values);
    }

    private static bool StringEqualsInvariant(string? left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private void WriteDiagnostic(long playbackInstanceId, string message)
    {
        diagnostics.Write($"PlaybackInstance={playbackInstanceId} {message}");
    }

    private void WritePlaybackStageDiagnostic(
        long playbackInstanceId,
        string eventName,
        int trackCount,
        bool hasAudioTrack,
        bool hasVideoTrack,
        bool hasVideoOutParams,
        int error)
    {
        WriteDiagnostic(
            playbackInstanceId,
            $"playback-stage event={eventName} "
            + $"trackCount={trackCount.ToString(CultureInfo.InvariantCulture)} "
            + $"hasVideoTrack={hasVideoTrack} hasAudioTrack={hasAudioTrack} "
            + $"videoOutParams={hasVideoOutParams} error={error.ToString(CultureInfo.InvariantCulture)}");
    }

    private void ClearPendingSeek()
    {
        pendingSeekPlaybackInstanceId = 0;
        pendingSeekSeconds = 0;
        pendingSeekApplied = true;
    }

    private sealed record MpvTrackInfo(
        int Id,
        int? FfIndex,
        int? DemuxIndex,
        bool? IsSelected,
        bool? IsExternal,
        string? Codec,
        string? Language,
        string? Title,
        bool? IsDefault,
        bool? IsForced);

    private sealed class PlaybackAttempt
    {
        private const int Loading = 0;
        private const int Active = 1;
        private const int Terminal = 2;
        private int state = Loading;

        public PlaybackAttempt(IntPtr handle, long playbackInstanceId)
        {
            Handle = handle;
            PlaybackInstanceId = playbackInstanceId;
        }

        public IntPtr Handle { get; }

        public long PlaybackInstanceId { get; }

        public bool TryActivate()
        {
            var previous = Interlocked.CompareExchange(ref state, Active, Loading);
            return previous is Loading or Active;
        }

        public bool TryTimeout()
        {
            return Interlocked.CompareExchange(ref state, Terminal, Loading) == Loading;
        }

        public bool TryTerminate()
        {
            return Interlocked.Exchange(ref state, Terminal) != Terminal;
        }
    }
}
