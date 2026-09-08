using EmbyPlayer.Core.Authentication;

namespace EmbyPlayer.Core.Images;

public interface IImageService
{
    Task<ImageLoadResult> LoadImageAsync(
        AuthSession session,
        string imageUrl,
        CancellationToken cancellationToken);
}
