namespace EmbyPlayer.Player;

internal enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    EndFile = 7,
    FileLoaded = 8,
    VideoReconfig = 17,
    AudioReconfig = 18,
    PlaybackRestart = 21
}
