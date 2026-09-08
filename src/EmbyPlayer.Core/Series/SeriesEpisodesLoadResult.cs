namespace EmbyPlayer.Core.Series;

public sealed class SeriesEpisodesLoadResult
{
    private SeriesEpisodesLoadResult(
        bool isSuccess,
        IReadOnlyList<EpisodeInfo>? episodes,
        SeriesLoadError error)
    {
        IsSuccess = isSuccess;
        Episodes = episodes ?? Array.Empty<EpisodeInfo>();
        Error = error;
    }

    public bool IsSuccess { get; }

    public IReadOnlyList<EpisodeInfo> Episodes { get; }

    public SeriesLoadError Error { get; }

    public static SeriesEpisodesLoadResult Success(IReadOnlyList<EpisodeInfo> episodes)
    {
        return new SeriesEpisodesLoadResult(true, episodes, SeriesLoadError.None);
    }

    public static SeriesEpisodesLoadResult Failure(SeriesLoadError error)
    {
        return new SeriesEpisodesLoadResult(false, null, error);
    }
}
