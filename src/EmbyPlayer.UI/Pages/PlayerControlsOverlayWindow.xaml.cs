using System.Windows;

namespace EmbyPlayer.UI.Pages;

public partial class PlayerControlsOverlayWindow : Window
{
    public PlayerControlsOverlayWindow()
    {
        InitializeComponent();
    }

    public void Attach(UIElement overlayContent, object? dataContext)
    {
        DataContext = dataContext;
        OverlayView.DataContext = dataContext;
        OverlayView.Attach(overlayContent);
    }

    public void SetBounds(Rect bounds)
    {
        if (Left.Equals(bounds.Left)
            && Top.Equals(bounds.Top)
            && Width.Equals(bounds.Width)
            && Height.Equals(bounds.Height))
        {
            return;
        }

        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    public UIElement? Detach() => OverlayView.Detach();
}
