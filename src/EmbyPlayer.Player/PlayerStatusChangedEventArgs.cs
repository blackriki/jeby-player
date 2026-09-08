namespace EmbyPlayer.Player;

public sealed class PlayerStatusChangedEventArgs : EventArgs
{
    public PlayerStatusChangedEventArgs(
        PlayerPlaybackState state,
        PlayerError error = PlayerError.None,
        string? endReason = null,
        bool? hasAudioTrack = null,
        bool? hasVideoTrack = null,
        bool? hasVideoOutParams = null,
        IReadOnlyList<PlayerTrackInfo>? tracks = null)
        : this(
            playbackInstanceId: 0,
            state,
            error,
            endReason,
            hasAudioTrack,
            hasVideoTrack,
            hasVideoOutParams,
            tracks)
    {
    }

    public PlayerStatusChangedEventArgs(
        long playbackInstanceId,
        PlayerPlaybackState state,
        PlayerError error = PlayerError.None,
        string? endReason = null,
        bool? hasAudioTrack = null,
        bool? hasVideoTrack = null,
        bool? hasVideoOutParams = null,
        IReadOnlyList<PlayerTrackInfo>? tracks = null)
    {
        PlaybackInstanceId = playbackInstanceId;
        State = state;
        Error = error;
        EndReason = endReason;
        HasAudioTrack = hasAudioTrack;
        HasVideoTrack = hasVideoTrack;
        HasVideoOutParams = hasVideoOutParams;
        Tracks = tracks ?? Array.Empty<PlayerTrackInfo>();
    }

    public long PlaybackInstanceId { get; }

    public PlayerPlaybackState State { get; }

    public PlayerError Error { get; }

    public string? EndReason { get; }

    public bool? HasAudioTrack { get; }

    public bool? HasVideoTrack { get; }

    public bool? HasVideoOutParams { get; }

    public IReadOnlyList<PlayerTrackInfo> Tracks { get; }
}
