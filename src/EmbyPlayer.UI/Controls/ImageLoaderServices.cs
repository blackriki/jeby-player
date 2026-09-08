using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Images;

namespace EmbyPlayer.UI.Controls;

public static class ImageLoaderServices
{
    private static int cacheGeneration;

    public static event EventHandler? CacheCleared;

    public static int CacheGeneration => Volatile.Read(ref cacheGeneration);

    public static IImageService? ImageService { get; private set; }

    public static ICurrentSessionService? CurrentSessionService { get; private set; }

    public static void Configure(
        IImageService imageService,
        ICurrentSessionService currentSessionService)
    {
        if (ImageService is IImageCacheInvalidationSource previousSource)
        {
            previousSource.CacheCleared -= OnCacheCleared;
        }
        ImageService = imageService ?? throw new ArgumentNullException(nameof(imageService));
        CurrentSessionService = currentSessionService ?? throw new ArgumentNullException(nameof(currentSessionService));
        if (imageService is IImageCacheInvalidationSource source)
        {
            source.CacheCleared += OnCacheCleared;
        }
        OnCacheCleared(null, EventArgs.Empty);
    }

    private static void OnCacheCleared(object? sender, EventArgs e)
    {
        Interlocked.Increment(ref cacheGeneration);
        CacheCleared?.Invoke(null, EventArgs.Empty);
    }
}
