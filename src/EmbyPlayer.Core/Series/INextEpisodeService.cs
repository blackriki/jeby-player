using EmbyPlayer.Core.Authentication;

namespace EmbyPlayer.Core.Series;

public interface INextEpisodeService
{
    Task<NextEpisodeResult> GetNextEpisodeAsync(
        AuthSession session,
        string currentItemId,
        CancellationToken cancellationToken);
}
