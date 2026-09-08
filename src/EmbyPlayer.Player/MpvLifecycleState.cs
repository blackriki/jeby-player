namespace EmbyPlayer.Player;

internal enum MpvLifecycleState
{
    NotInitialized,
    Initializing,
    Ready,
    Loading,
    Playing,
    Paused,
    Failed,
    Stopping,
    Disposed
}
