using EmbyPlayer.Core.Authentication;

namespace EmbyPlayer.Core.Series;

public interface ISeriesService
{
    Task<SeriesSeasonsLoadResult> LoadSeasonsAsync(
        AuthSession session,
        string seriesId,
        CancellationToken cancellationToken);

    Task<SeriesEpisodesLoadResult> LoadEpisodesAsync(
        AuthSession session,
        string seriesId,
        string seasonId,
        CancellationToken cancellationToken);
}
