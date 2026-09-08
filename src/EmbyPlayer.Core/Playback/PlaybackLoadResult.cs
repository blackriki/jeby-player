namespace EmbyPlayer.Core.Playback;

public sealed class PlaybackLoadResult
{
    private PlaybackLoadResult(bool isSuccess, PlaybackInfo? playbackInfo, PlaybackLoadError error)
    {
        IsSuccess = isSuccess;
        PlaybackInfo = playbackInfo;
        Error = error;
    }

    public bool IsSuccess { get; }

    public PlaybackInfo? PlaybackInfo { get; }

    public PlaybackLoadError Error { get; }

    public static PlaybackLoadResult Success(PlaybackInfo playbackInfo)
    {
        ArgumentNullException.ThrowIfNull(playbackInfo);
        return new PlaybackLoadResult(true, playbackInfo, PlaybackLoadError.None);
    }

    public static PlaybackLoadResult Failure(PlaybackLoadError error)
    {
        return new PlaybackLoadResult(false, null, error);
    }
}
