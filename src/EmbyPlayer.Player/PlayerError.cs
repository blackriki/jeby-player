namespace EmbyPlayer.Player;

public enum PlayerError
{
    None,
    NoPlayableUrl,
    RuntimeMissing,
    InitializationFailed,
    HeaderSetupFailed,
    LoadFailed,
    ResumeFailed,
    SeekFailed,
    VolumeFailed,
    MuteFailed,
    AudioTrackFailed,
    SubtitleTrackFailed,
    PlaybackFailed,
    SubtitleAdjustmentFailed,
    PlaybackSpeedFailed
}
