using System.Collections.Concurrent;

namespace EmbyPlayer.Player;

public sealed partial class MpvPlayerService
{
    private readonly ConcurrentDictionary<int, string> localSubtitlePaths = new();

    public async Task<LocalSubtitleImportResult> ImportLocalSubtitleAsync(
        long playbackInstanceId, string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var failure = new LocalSubtitleImportResult(false, Array.Empty<PlayerTrackInfo>());
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath)
            || !new[] { ".srt", ".ass", ".ssa", ".vtt" }.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase))
            return failure;

        // File checks and synchronous native loading must not block the WPF dispatcher.
        return await Task.Run(async () =>
        {
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (handle == IntPtr.Zero || playbackInstanceId == 0 || playbackInstanceId != activePlaybackInstanceId
                    || lifecycleState is not (MpvLifecycleState.Ready or MpvLifecycleState.Playing or MpvLifecycleState.Paused))
                    return failure;
                var fullPath = Path.GetFullPath(filePath);
                if (!File.Exists(fullPath)) return failure;
                cancellationToken.ThrowIfCancellationRequested();
                var oldSid = nativeApi.GetPropertyString(handle, "sid") ?? "no";
                var oldVisibility = nativeApi.GetPropertyString(handle, "sub-visibility") ?? "yes";
                var result = nativeApi.Command(handle, new[] { "sub-add", fullPath, "cached", Path.GetFileName(fullPath) });
                var selectedId = GetIntProperty(handle, "sid");
                if (result < 0 || !selectedId.HasValue || !GetMpvTracks(handle, "sub").Any(t => t.Id == selectedId && t.IsExternal == true)
                    || nativeApi.SetPropertyString(handle, "sub-visibility", "yes") < 0)
                {
                    nativeApi.SetPropertyString(handle, "sid", oldSid);
                    nativeApi.SetPropertyString(handle, "sub-visibility", oldVisibility);
                    WriteDiagnostic(playbackInstanceId, "local-subtitle-import-failed");
                    return failure;
                }
                localSubtitlePaths[selectedId.Value] = fullPath;
                WriteDiagnostic(playbackInstanceId, "local-subtitle-import-success");
                return new LocalSubtitleImportResult(true, GetAllMpvTracks(handle));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                WriteDiagnostic(playbackInstanceId, $"local-subtitle-import-exception type={exception.GetType().Name}");
                return failure;
            }
            finally { lifecycleGate.Release(); }
        }, cancellationToken).ConfigureAwait(false);
    }
}
