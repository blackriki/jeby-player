using System.Globalization;

namespace EmbyPlayer.Player;

public sealed partial class MpvPlayerService
{
    public Task<PlayerOperationResult> SetSubtitleAdjustmentAsync(
        long playbackInstanceId,
        SubtitleAdjustmentKind kind,
        double value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(value))
        {
            return Task.FromResult(PlayerOperationResult.Failure(PlayerError.SubtitleAdjustmentFailed));
        }

        var property = kind switch
        {
            SubtitleAdjustmentKind.DelaySeconds => (Name: "sub-delay", Value: Math.Clamp(value, -60, 60)),
            SubtitleAdjustmentKind.Scale => (Name: "sub-scale", Value: Math.Clamp(value, 0.5, 3)),
            SubtitleAdjustmentKind.Position => (Name: "sub-pos", Value: Math.Clamp(value, 0, 100)),
            _ => (Name: string.Empty, Value: 0d)
        };
        if (property.Name.Length == 0)
        {
            return Task.FromResult(PlayerOperationResult.Failure(PlayerError.SubtitleAdjustmentFailed));
        }

        return SetTrackPropertiesAsync(
            playbackInstanceId,
            PlayerError.SubtitleAdjustmentFailed,
            "subtitle-adjustment",
            cancellationToken,
            (property.Name, property.Value.ToString("0.###", CultureInfo.InvariantCulture)));
    }

    public async Task<PlayerTechnicalInfo?> GetTechnicalInfoAsync(
        long playbackInstanceId,
        CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (handle == IntPtr.Zero || playbackInstanceId == 0 || playbackInstanceId != activePlaybackInstanceId
                || lifecycleState is MpvLifecycleState.Stopping or MpvLifecycleState.Disposed or MpvLifecycleState.Failed)
            {
                return null;
            }

            return new PlayerTechnicalInfo(
                ToDimension(nativeApi.GetDoubleProperty(handle, "video-params/w")),
                ToDimension(nativeApi.GetDoubleProperty(handle, "video-params/h")),
                nativeApi.GetPropertyString(handle, "video-format"));
        }
        catch (Exception)
        {
            WriteDiagnostic(playbackInstanceId, "technical-info-read-failed");
            return null;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private static int? ToDimension(double? value)
        => value is > 0 and <= int.MaxValue && double.IsFinite(value.Value) ? (int)value.Value : null;
}
