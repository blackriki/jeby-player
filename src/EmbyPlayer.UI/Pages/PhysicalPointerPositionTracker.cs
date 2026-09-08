namespace EmbyPlayer.UI.Pages;

internal readonly record struct PhysicalPixelPoint(int X, int Y);

internal sealed class PhysicalPointerPositionTracker
{
    private PhysicalPixelPoint? lastPosition;

    public bool Observe(PhysicalPixelPoint position)
    {
        var moved = lastPosition is { } previous && previous != position;
        lastPosition = position;
        return moved;
    }

    public void Refresh(PhysicalPixelPoint position)
    {
        lastPosition = position;
    }
}
