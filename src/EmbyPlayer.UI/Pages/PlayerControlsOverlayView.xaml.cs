using System.Windows;
using System.Windows.Controls;

namespace EmbyPlayer.UI.Pages;

public partial class PlayerControlsOverlayView : UserControl
{
    public PlayerControlsOverlayView()
    {
        InitializeComponent();
    }

    public void Attach(UIElement overlayContent)
    {
        OverlayContentHost.Content = overlayContent;
    }

    public UIElement? Detach()
    {
        var content = OverlayContentHost.Content as UIElement;
        OverlayContentHost.Content = null;
        return content;
    }
}
