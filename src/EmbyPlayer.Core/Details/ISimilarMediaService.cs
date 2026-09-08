using EmbyPlayer.Core.Authentication;

namespace EmbyPlayer.Core.Details;

public interface ISimilarMediaService
{
    Task<SimilarMediaLoadResult> LoadSimilarAsync(
        AuthSession session,
        string itemId,
        string itemType,
        CancellationToken cancellationToken);
}
