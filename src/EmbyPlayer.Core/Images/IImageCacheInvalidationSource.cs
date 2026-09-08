namespace EmbyPlayer.Core.Images;

public interface IImageCacheInvalidationSource
{
    event EventHandler? CacheCleared;
}
