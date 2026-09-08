namespace EmbyPlayer.Core.Series;

public sealed class NextEpisodeResult
{
    private NextEpisodeResult(bool isSuccess, NextEpisodeInfo? nextEpisode, NextEpisodeError error)
    {
        IsSuccess = isSuccess;
        NextEpisode = nextEpisode;
        Error = error;
    }

    public bool IsSuccess { get; }

    public NextEpisodeInfo? NextEpisode { get; }

    public NextEpisodeError Error { get; }

    public static NextEpisodeResult Success(NextEpisodeInfo? nextEpisode)
    {
        return new NextEpisodeResult(true, nextEpisode, NextEpisodeError.None);
    }

    public static NextEpisodeResult Failure(NextEpisodeError error)
    {
        return new NextEpisodeResult(false, null, error);
    }
}
