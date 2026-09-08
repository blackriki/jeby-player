namespace EmbyPlayer.Core.Home;

public static class MediaLibraryClassifier
{
    public static bool IsAnimationLibrary(MediaLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);

        return library.Type.Contains("anime", StringComparison.OrdinalIgnoreCase)
            || library.Type.Contains("animation", StringComparison.OrdinalIgnoreCase)
            || library.Name.Contains("动画", StringComparison.OrdinalIgnoreCase);
    }
}
