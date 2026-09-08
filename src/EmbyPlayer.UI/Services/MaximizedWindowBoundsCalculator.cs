namespace EmbyPlayer.UI.Services;

public readonly record struct NativePixelRect(
    int Left,
    int Top,
    int Right,
    int Bottom);

public readonly record struct MaximizedWindowBounds(
    int X,
    int Y,
    int Width,
    int Height);

public static class MaximizedWindowBoundsCalculator
{
    public static MaximizedWindowBounds Calculate(
        NativePixelRect monitor,
        NativePixelRect workArea) => new(
            workArea.Left - monitor.Left,
            workArea.Top - monitor.Top,
            workArea.Right - workArea.Left,
            workArea.Bottom - workArea.Top);
}
