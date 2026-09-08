using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace EmbyPlayer.Player;

internal sealed class LegacyMpvPlayerService : IPlayerService
{
    private const string LibMpvName = "libmpv-2";
    private const int MpvFormatFlag = 3;
    private const int EndFileReasonEof = 0;
    private const int EndFileReasonError = 4;
    private readonly object syncRoot = new();
    private IntPtr handle;
    private bool disposed;

    public event EventHandler<PlayerStatusChangedEventArgs>? StatusChanged;

    public event EventHandler<PlayerProgressChangedEventArgs>? ProgressChanged
    {
        add { }
        remove { }
    }

    public Task<PlayerOperationResult> LoadAsync(
        PlayerLoadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.PlaybackInfo.PlaybackPath))
        {
            return Task.FromResult(PlayerOperationResult.Failure(PlayerError.NoPlayableUrl));
        }

        if (request.VideoHostHandle == IntPtr.Zero)
        {
            return Task.FromResult(PlayerOperationResult.Failure(PlayerError.InitializationFailed));
        }

        lock (syncRoot)
        {
            EnsureNotDisposed();
            DestroyHandle();
            WriteDiagnostic(
                "load-start "
                + $"hwndNonZero={request.VideoHostHandle != IntPtr.Zero} "
                + "widBeforeInitialize=True "
                + $"playbackMode={GetPlaybackMode(request)} "
                + $"playbackPathKind={GetPlaybackPathKind(request.PlaybackInfo.PlaybackPath)} "
                + $"headersCount={request.PlaybackInfo.MediaSource.RequiredHttpHeaders.Count} "
                + $"hasXEmbyTokenHeader={HasHeader(request, "X-Emby-Token")}");

            try
            {
                handle = mpv_create();
                if (handle == IntPtr.Zero)
                {
                    WriteDiagnostic("init-failed handleCreated=False");
                    return Task.FromResult(PlayerOperationResult.Failure(PlayerError.InitializationFailed));
                }

                _ = mpv_request_log_messages(handle, ToNativeString("warn"));
                if (SetOptionString("terminal", "no") < 0
                    || SetOptionString("msg-level", "all=no") < 0
                    || SetOptionString("wid", request.VideoHostHandle.ToInt64().ToString()) < 0)
                {
                    WriteDiagnostic("init-failed optionSet=False");
                    DestroyHandle();
                    return Task.FromResult(PlayerOperationResult.Failure(PlayerError.InitializationFailed));
                }

                if (mpv_initialize(handle) < 0)
                {
                    WriteDiagnostic("init-failed initialize=False");
                    DestroyHandle();
                    return Task.FromResult(PlayerOperationResult.Failure(PlayerError.InitializationFailed));
                }

                WriteDiagnostic("init-success");
                if (!TryApplyRequiredHeaders(request))
                {
                    WriteDiagnostic("header-setup-failed");
                    DestroyHandle();
                    return Task.FromResult(PlayerOperationResult.Failure(PlayerError.HeaderSetupFailed));
                }

                StartEventLoop(handle);
                var loadResult = LoadFile(request);
                WriteDiagnostic($"loadfile-called result={loadResult}");
                if (loadResult < 0)
                {
                    DestroyHandle();
                    return Task.FromResult(PlayerOperationResult.Failure(PlayerError.LoadFailed));
                }

                return Task.FromResult(PlayerOperationResult.Success());
            }
            catch (DllNotFoundException)
            {
                WriteDiagnostic("runtime-missing dllNotFound=True");
                DestroyHandle();
                return Task.FromResult(PlayerOperationResult.Failure(PlayerError.RuntimeMissing));
            }
            catch (BadImageFormatException)
            {
                WriteDiagnostic("runtime-missing badImage=True");
                DestroyHandle();
                return Task.FromResult(PlayerOperationResult.Failure(PlayerError.RuntimeMissing));
            }
            catch (EntryPointNotFoundException)
            {
                WriteDiagnostic("init-failed entryPointMissing=True");
                DestroyHandle();
                return Task.FromResult(PlayerOperationResult.Failure(PlayerError.InitializationFailed));
            }
            catch (SEHException)
            {
                WriteDiagnostic("playback-failed seh=True");
                DestroyHandle();
                return Task.FromResult(PlayerOperationResult.Failure(PlayerError.PlaybackFailed));
            }
        }
    }

    public Task<PlayerOperationResult> PlayAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (syncRoot)
        {
            if (handle == IntPtr.Zero)
            {
                return Task.FromResult(PlayerOperationResult.Failure(PlayerError.PlaybackFailed));
            }

            return Task.FromResult(
                mpv_set_property_string(handle, ToNativeString("pause"), ToNativeString("no")) < 0
                    ? PlayerOperationResult.Failure(PlayerError.PlaybackFailed)
                    : PlayerOperationResult.Success());
        }
    }

    public Task<PlayerOperationResult> PauseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (syncRoot)
        {
            if (handle == IntPtr.Zero)
            {
                return Task.FromResult(PlayerOperationResult.Failure(PlayerError.PlaybackFailed));
            }

            return Task.FromResult(
                mpv_set_property_string(handle, ToNativeString("pause"), ToNativeString("yes")) < 0
                    ? PlayerOperationResult.Failure(PlayerError.PlaybackFailed)
                    : PlayerOperationResult.Success());
        }
    }

    public Task<PlayerOperationResult> SeekAsync(
        long playbackInstanceId,
        TimeSpan position,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (syncRoot)
        {
            if (handle == IntPtr.Zero)
            {
                return Task.FromResult(PlayerOperationResult.Failure(PlayerError.SeekFailed));
            }

            var seconds = Math.Max(0, position.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);
            return Task.FromResult(
                Command(new[] { "seek", seconds, "absolute+exact" }) < 0
                    ? PlayerOperationResult.Failure(PlayerError.SeekFailed)
                    : PlayerOperationResult.Success());
        }
    }

    public Task<PlayerOperationResult> SetVolumeAsync(
        int volume,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (syncRoot)
        {
            if (handle == IntPtr.Zero)
            {
                return Task.FromResult(PlayerOperationResult.Failure(PlayerError.VolumeFailed));
            }

            var clampedVolume = Math.Clamp(volume, 0, 100).ToString(CultureInfo.InvariantCulture);
            return Task.FromResult(
                mpv_set_property_string(handle, ToNativeString("volume"), ToNativeString(clampedVolume)) < 0
                    ? PlayerOperationResult.Failure(PlayerError.VolumeFailed)
                    : PlayerOperationResult.Success());
        }
    }

    public Task<PlayerOperationResult> SetMuteAsync(
        bool isMuted,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (syncRoot)
        {
            if (handle == IntPtr.Zero)
            {
                return Task.FromResult(PlayerOperationResult.Failure(PlayerError.MuteFailed));
            }

            return Task.FromResult(
                mpv_set_property_string(handle, ToNativeString("mute"), ToNativeString(isMuted ? "yes" : "no")) < 0
                    ? PlayerOperationResult.Failure(PlayerError.MuteFailed)
                    : PlayerOperationResult.Success());
        }
    }

    public Task<PlayerOperationResult> SelectAudioTrackAsync(
        long playbackInstanceId,
        int mpvTrackId,
        CancellationToken cancellationToken)
    {
        return SetTrackPropertyAsync(
            "aid",
            Math.Max(0, mpvTrackId).ToString(CultureInfo.InvariantCulture),
            PlayerError.AudioTrackFailed,
            cancellationToken);
    }

    public Task<PlayerOperationResult> SelectSubtitleTrackAsync(
        long playbackInstanceId,
        int mpvTrackId,
        CancellationToken cancellationToken)
    {
        return SetTrackPropertyAsync(
            "sid",
            Math.Max(0, mpvTrackId).ToString(CultureInfo.InvariantCulture),
            PlayerError.SubtitleTrackFailed,
            cancellationToken);
    }

    public Task<PlayerOperationResult> SelectExternalSubtitleAsync(
        long playbackInstanceId,
        int mediaStreamIndex,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(PlayerOperationResult.Failure(PlayerError.SubtitleTrackFailed));
    }

    public Task<PlayerOperationResult> DisableSubtitleAsync(
        long playbackInstanceId,
        CancellationToken cancellationToken)
    {
        return SetTrackPropertyAsync("sid", "no", PlayerError.SubtitleTrackFailed, cancellationToken);
    }

    private Task<PlayerOperationResult> SetTrackPropertyAsync(
        string propertyName,
        string propertyValue,
        PlayerError failureError,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (syncRoot)
        {
            if (handle == IntPtr.Zero)
            {
                return Task.FromResult(PlayerOperationResult.Failure(failureError));
            }

            return Task.FromResult(
                mpv_set_property_string(handle, ToNativeString(propertyName), ToNativeString(propertyValue)) < 0
                    ? PlayerOperationResult.Failure(failureError)
                    : PlayerOperationResult.Success());
        }
    }

    public Task<PlayerOperationResult> StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (syncRoot)
        {
            DestroyHandle();
        }

        return Task.FromResult(PlayerOperationResult.Success());
    }

    public ValueTask DisposeAsync()
    {
        lock (syncRoot)
        {
            if (!disposed)
            {
                DestroyHandle();
                disposed = true;
            }
        }

        return ValueTask.CompletedTask;
    }

    private int SetOptionString(string name, string value)
    {
        return mpv_set_option_string(handle, ToNativeString(name), ToNativeString(value));
    }

    private int LoadFile(PlayerLoadRequest request)
    {
        var startSeconds = Math.Max(0, request.PlaybackInfo.StartPositionTicks) / (double)TimeSpan.TicksPerSecond;
        var args = startSeconds > 0
            ? new[] { "loadfile", request.PlaybackInfo.PlaybackPath!, "replace", $"start={startSeconds.ToString("0.###", CultureInfo.InvariantCulture)}" }
            : new[] { "loadfile", request.PlaybackInfo.PlaybackPath!, "replace" };

        return Command(args);
    }

    private bool TryApplyRequiredHeaders(PlayerLoadRequest request)
    {
        var headers = request.PlaybackInfo.MediaSource.RequiredHttpHeaders;
        if (headers.Count == 0)
        {
            return true;
        }

        var headerFields = new List<string>();
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

            headerFields.Add($"{header.Key}: {header.Value}");
        }

        return SetOptionString("http-header-fields", string.Join(",", headerFields)) >= 0;
    }

    private int Command(IReadOnlyList<string> args)
    {
        var nativeArgs = new IntPtr[args.Count + 1];
        try
        {
            for (var i = 0; i < args.Count; i++)
            {
                nativeArgs[i] = AllocateNativeString(args[i]);
            }

            nativeArgs[^1] = IntPtr.Zero;
            return mpv_command(handle, nativeArgs);
        }
        finally
        {
            foreach (var nativeArg in nativeArgs)
            {
                if (nativeArg != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(nativeArg);
                }
            }
        }
    }

    private void DestroyHandle()
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _ = Command(new[] { "stop" });
        }
        catch
        {
            // Best effort only; terminate_destroy below still releases the handle.
        }

        try
        {
            mpv_wakeup(handle);
        }
        catch
        {
            // Best effort only; terminate_destroy below still releases the handle.
        }

        mpv_terminate_destroy(handle);
        WriteDiagnostic("handle-destroyed");
        handle = IntPtr.Zero;
    }

    private void StartEventLoop(IntPtr eventHandle)
    {
        _ = Task.Run(() => EventLoop(eventHandle));
    }

    private void EventLoop(IntPtr eventHandle)
    {
        while (eventHandle != IntPtr.Zero)
        {
            var eventPointer = mpv_wait_event(eventHandle, 0.25);
            if (eventPointer == IntPtr.Zero)
            {
                continue;
            }

            var mpvEvent = Marshal.PtrToStructure<MpvEvent>(eventPointer);
            if (mpvEvent.EventId == MpvEventId.None)
            {
                continue;
            }

            switch (mpvEvent.EventId)
            {
                case MpvEventId.Shutdown:
                    WriteDiagnostic("event=shutdown");
                    return;
                case MpvEventId.LogMessage:
                    WriteDiagnostic($"event=log-message error={mpvEvent.Error}");
                    break;
                case MpvEventId.FileLoaded:
                case MpvEventId.PlaybackRestart:
                    WritePlaybackState(eventHandle, GetEventName(mpvEvent.EventId), mpvEvent.Error);
                    RaiseStatusChanged(new PlayerStatusChangedEventArgs(
                        PlayerPlaybackState.Playing,
                        hasAudioTrack: HasPropertyString(eventHandle, "current-tracks/audio/id"),
                        hasVideoTrack: HasPropertyString(eventHandle, "current-tracks/video/id"),
                        hasVideoOutParams: HasPropertyString(eventHandle, "video-out-params/w")));
                    break;
                case MpvEventId.AudioReconfig:
                case MpvEventId.VideoReconfig:
                    WritePlaybackState(eventHandle, GetEventName(mpvEvent.EventId), mpvEvent.Error);
                    break;
                case MpvEventId.EndFile:
                    HandleEndFile(eventHandle, mpvEvent);
                    break;
            }
        }
    }

    private void HandleEndFile(IntPtr eventHandle, MpvEvent mpvEvent)
    {
        var reason = EndFileReasonEof;
        var endFileError = mpvEvent.Error;
        if (mpvEvent.Data != IntPtr.Zero)
        {
            var endFile = Marshal.PtrToStructure<MpvEventEndFile>(mpvEvent.Data);
            reason = endFile.Reason;
            endFileError = endFile.Error;
        }

        var reasonText = GetEndFileReason(reason);
        WritePlaybackState(eventHandle, $"end-file reason={reasonText}", endFileError);
        if (reason == EndFileReasonError || endFileError < 0)
        {
            RaiseStatusChanged(new PlayerStatusChangedEventArgs(
                PlayerPlaybackState.Failed,
                PlayerError.LoadFailed,
                reasonText,
                HasPropertyString(eventHandle, "current-tracks/audio/id"),
                HasPropertyString(eventHandle, "current-tracks/video/id"),
                HasPropertyString(eventHandle, "video-out-params/w")));
        }
    }

    private void WritePlaybackState(IntPtr eventHandle, string eventName, int error)
    {
        var hasAudio = HasPropertyString(eventHandle, "current-tracks/audio/id");
        var hasVideo = HasPropertyString(eventHandle, "current-tracks/video/id");
        var hasVideoOutParams = HasPropertyString(eventHandle, "video-out-params/w");
        var idleActive = GetFlagProperty(eventHandle, "idle-active");
        var coreIdle = GetFlagProperty(eventHandle, "core-idle");
        var pause = GetFlagProperty(eventHandle, "pause");
        WriteDiagnostic(
            $"event={eventName} error={error} "
            + $"hasAudioTrack={hasAudio} hasVideoTrack={hasVideo} hasVideoOutParams={hasVideoOutParams} "
            + $"idleActive={FormatNullableBool(idleActive)} coreIdle={FormatNullableBool(coreIdle)} pause={FormatNullableBool(pause)}");
    }

    private static bool HasPropertyString(IntPtr eventHandle, string name)
    {
        var pointer = mpv_get_property_string(eventHandle, ToNativeString(name));
        if (pointer == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return !string.IsNullOrWhiteSpace(Marshal.PtrToStringUTF8(pointer));
        }
        finally
        {
            mpv_free(pointer);
        }
    }

    private static bool? GetFlagProperty(IntPtr eventHandle, string name)
    {
        var value = 0;
        var result = mpv_get_property(eventHandle, ToNativeString(name), MpvFormatFlag, ref value);
        return result < 0 ? null : value != 0;
    }

    private void RaiseStatusChanged(PlayerStatusChangedEventArgs eventArgs)
    {
        StatusChanged?.Invoke(this, eventArgs);
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
            5 => "redirect",
            _ => $"unknown-{reason}"
        };
    }

    private static string FormatNullableBool(bool? value)
    {
        return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "unknown";
    }

    private static void WriteDiagnostic(string message)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EmbyPlayer",
                "logs");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "mpv.log");
            File.AppendAllText(
                logPath,
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // Diagnostics must never make playback fail.
        }
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static byte[] ToNativeString(string value)
    {
        return Encoding.UTF8.GetBytes(value + '\0');
    }

    private static IntPtr AllocateNativeString(string value)
    {
        var bytes = ToNativeString(value);
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return pointer;
    }

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mpv_create();

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_initialize(IntPtr ctx);

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_set_option_string(
        IntPtr ctx,
        byte[] name,
        byte[] data);

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_set_property_string(
        IntPtr ctx,
        byte[] name,
        byte[] data);

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_command(
        IntPtr ctx,
        [In] IntPtr[] args);

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mpv_wait_event(
        IntPtr ctx,
        double timeout);

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_request_log_messages(
        IntPtr ctx,
        byte[] minLevel);

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mpv_get_property_string(
        IntPtr ctx,
        byte[] name);

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_get_property(
        IntPtr ctx,
        byte[] name,
        int format,
        ref int data);

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mpv_free(IntPtr data);

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mpv_wakeup(IntPtr ctx);

    [DllImport(LibMpvName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mpv_terminate_destroy(IntPtr ctx);

    private enum MpvEventId
    {
        None = 0,
        Shutdown = 1,
        LogMessage = 2,
        EndFile = 7,
        FileLoaded = 8,
        AudioReconfig = 18,
        VideoReconfig = 17,
        PlaybackRestart = 21
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MpvEvent
    {
        public readonly MpvEventId EventId;
        public readonly int Error;
        public readonly ulong ReplyUserData;
        public readonly IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MpvEventEndFile
    {
        public readonly int Reason;
        public readonly int Error;
        public readonly long PlaylistEntryId;
        public readonly int PlaylistInsertId;
        public readonly int PlaylistInsertNumEntries;
    }
}
