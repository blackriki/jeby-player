namespace EmbyPlayer.Player;

public sealed class PlayerProgressChangedEventArgs : EventArgs
{
    public PlayerProgressChangedEventArgs(
        long playbackInstanceId,
        TimeSpan? position,
        TimeSpan? duration,
        double? percent,
        bool? isPaused)
    {
        PlaybackInstanceId = playbackInstanceId;
        Position = position;
        Duration = duration;
        Percent = percent;
        IsPaused = isPaused;
    }

    public long PlaybackInstanceId { get; }

    public TimeSpan? Position { get; }

    public TimeSpan? Duration { get; }

    public double? Percent { get; }

    public bool? IsPaused { get; }
}
