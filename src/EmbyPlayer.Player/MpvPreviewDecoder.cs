using System.Globalization;
using EmbyPlayer.Core.Playback;

namespace EmbyPlayer.Player;

internal interface IMpvPreviewDecoder : IDisposable
{
    PlaybackPreviewResult Decode(PlaybackInfo info, TimeSpan position, CancellationToken cancellationToken);
}

// Called only by the preview service's serialized background worker. This handle never belongs to the main player.
internal sealed class MpvPreviewDecoder : IMpvPreviewDecoder
{
    private readonly IMpvNativeApi native = new LibMpvNativeApi();
    private IntPtr handle;

    public PlaybackPreviewResult Decode(PlaybackInfo info, TimeSpan position, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (handle == IntPtr.Zero)
        {
            handle = native.Create();
            if (handle == IntPtr.Zero) return PlaybackPreviewResult.Failure(PlaybackPreviewError.Unavailable);
            foreach (var (key, value) in new[]
            {
                ("config", "no"), ("load-scripts", "no"), ("terminal", "no"), ("msg-level", "all=no"),
                ("vo", "null"), ("ao", "null"), ("audio", "no"), ("sub", "no"), ("pause", "yes"),
                ("keep-open", "yes"), ("hwdec", "no"), ("vd-lavc-threads", "2"), ("cache", "no"),
                ("network-timeout", "5"), ("vf", "scale=w=320:h=320:force_original_aspect_ratio=decrease"),
                ("start", position.TotalSeconds.ToString("R", CultureInfo.InvariantCulture))
            })
                if (native.SetOptionString(handle, key, value) < 0) return PlaybackPreviewResult.Failure(PlaybackPreviewError.Unavailable);

            var fields = new List<string>();
            foreach (var header in info.MediaSource.RequiredHttpHeaders)
            {
                if (string.IsNullOrWhiteSpace(header.Key) || header.Key.IndexOfAny(['\r', '\n']) >= 0
                    || header.Value.IndexOfAny(['\r', '\n']) >= 0)
                    return PlaybackPreviewResult.Failure(PlaybackPreviewError.InvalidResponse);
                fields.Add(($"{header.Key}: {header.Value}").Replace("\\", "\\\\", StringComparison.Ordinal).Replace(",", "\\,", StringComparison.Ordinal));
            }
            if (native.SetOptionString(handle, "http-header-fields", string.Join(',', fields)) < 0
                || native.Initialize(handle) < 0
                || native.Command(handle, ["loadfile", info.PlaybackPath!, "replace"]) < 0)
                return PlaybackPreviewResult.Failure(PlaybackPreviewError.Unavailable);
        }
        else
        {
            // Drain events from the previous completed frame before waiting for this seek's restart.
            while (native.WaitEvent(handle, 0).EventId != MpvEventId.None) cancellationToken.ThrowIfCancellationRequested();
            if (native.Command(handle, ["seek", position.TotalSeconds.ToString("R", CultureInfo.InvariantCulture), "absolute+exact"]) < 0)
                return PlaybackPreviewResult.Failure(PlaybackPreviewError.Unavailable);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var notification = native.WaitEvent(handle, 0.05);
            if (notification.EventId == MpvEventId.Shutdown || notification.EventId == MpvEventId.EndFile && notification.EndFileReason != 5)
                return PlaybackPreviewResult.Failure(PlaybackPreviewError.ServerUnreachable);
            if (notification.EventId != MpvEventId.PlaybackRestart) continue;
            if (native.GetFlagProperty(handle, "seekable") != true)
                return PlaybackPreviewResult.Failure(PlaybackPreviewError.Unavailable);
            var actualPosition = native.GetDoubleProperty(handle, "time-pos");
            if (actualPosition is null || !double.IsFinite(actualPosition.Value)
                || Math.Abs(actualPosition.Value - position.TotalSeconds) > 1.5)
                return PlaybackPreviewResult.Failure(PlaybackPreviewError.InvalidResponse);
            cancellationToken.ThrowIfCancellationRequested();
            var bitmap = MpvPreviewFrameReader.CaptureBitmap(handle);
            cancellationToken.ThrowIfCancellationRequested();
            return bitmap is null ? PlaybackPreviewResult.Failure(PlaybackPreviewError.InvalidResponse)
                : new(bitmap, TimeSpan.FromSeconds(actualPosition.Value), PlaybackPreviewError.None);
        }
    }

    public void Dispose()
    {
        var old = handle;
        handle = IntPtr.Zero;
        if (old != IntPtr.Zero) native.TerminateDestroy(old);
    }
}
