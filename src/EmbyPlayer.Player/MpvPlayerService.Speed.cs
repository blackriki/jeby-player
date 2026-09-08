using System.Globalization;

namespace EmbyPlayer.Player;

public sealed partial class MpvPlayerService
{
    public Task<PlayerOperationResult> SetPlaybackSpeedAsync(
        long playbackInstanceId,
        double speed,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(speed) || speed is < 0.25 or > 4)
        {
            return Task.FromResult(PlayerOperationResult.Failure(PlayerError.PlaybackSpeedFailed));
        }

        return SetTrackPropertiesAsync(
            playbackInstanceId,
            PlayerError.PlaybackSpeedFailed,
            "playback-speed",
            cancellationToken,
            ("speed", speed.ToString("G", CultureInfo.InvariantCulture)));
    }
}
