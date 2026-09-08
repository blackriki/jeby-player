namespace EmbyPlayer.Core.Series;

public sealed class SeriesSeasonsLoadResult
{
    private SeriesSeasonsLoadResult(
        bool isSuccess,
        IReadOnlyList<SeasonInfo>? seasons,
        SeriesLoadError error)
    {
        IsSuccess = isSuccess;
        Seasons = seasons ?? Array.Empty<SeasonInfo>();
        Error = error;
    }

    public bool IsSuccess { get; }

    public IReadOnlyList<SeasonInfo> Seasons { get; }

    public SeriesLoadError Error { get; }

    public static SeriesSeasonsLoadResult Success(IReadOnlyList<SeasonInfo> seasons)
    {
        return new SeriesSeasonsLoadResult(true, seasons, SeriesLoadError.None);
    }

    public static SeriesSeasonsLoadResult Failure(SeriesLoadError error)
    {
        return new SeriesSeasonsLoadResult(false, null, error);
    }
}
