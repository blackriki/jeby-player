using EmbyPlayer.Core.Playback;

namespace EmbyPlayer.Player;

public sealed record PlayerLoadRequest(
    PlaybackInfo PlaybackInfo,
    IntPtr VideoHostHandle,
    long PlaybackInstanceId = 0,
    bool StartPaused = false);
