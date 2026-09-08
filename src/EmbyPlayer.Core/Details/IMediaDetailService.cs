using EmbyPlayer.Core.Authentication;

namespace EmbyPlayer.Core.Details;

public interface IMediaDetailService
{
    Task<MediaDetailLoadResult> LoadDetailAsync(
        AuthSession session,
        string itemId,
        CancellationToken cancellationToken);
}
