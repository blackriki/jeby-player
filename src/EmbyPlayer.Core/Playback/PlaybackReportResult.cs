namespace EmbyPlayer.Core.Playback;

public sealed class PlaybackReportResult
{
    private PlaybackReportResult(bool isSuccess, PlaybackReportError error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public PlaybackReportError Error { get; }

    public static PlaybackReportResult Success()
    {
        return new PlaybackReportResult(true, PlaybackReportError.None);
    }

    public static PlaybackReportResult Failure(PlaybackReportError error)
    {
        return new PlaybackReportResult(false, error);
    }
}
