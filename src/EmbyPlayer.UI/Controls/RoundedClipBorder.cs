using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EmbyPlayer.UI.Controls;

public sealed class RoundedClipBorder : Border
{
    public RoundedClipBorder()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arrangedSize = base.ArrangeOverride(finalSize);
        Clip = null;

        if (Child is null)
        {
            return arrangedSize;
        }

        var childSize = Child.RenderSize;
        if (childSize.Width <= 0 || childSize.Height <= 0)
        {
            Child.Clip = null;
            return arrangedSize;
        }

        var borderInset = Math.Max(BorderThickness.Left, BorderThickness.Top);
        var maximumRadius = Math.Min(childSize.Width, childSize.Height) / 2;
        var radius = Math.Clamp(CornerRadius.TopLeft - borderInset, 0, maximumRadius);
        Child.Clip = new RectangleGeometry(
            new Rect(0, 0, childSize.Width, childSize.Height),
            radius,
            radius);

        return arrangedSize;
    }
}
