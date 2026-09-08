using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class PlayerEnhancementRuntimeTests
{
    [TestMethod]
    public void PlayerButtonsDoNotPaintStickyFocusBorderAndKeepKeyboardAdorner()
    {
        RunOnSta(() => WithPage(0, (page, _, _, _, _) =>
        {
            foreach (var name in new[] { "MiniPlayerButton", "PinPlayerButton", "VolumeButton", "SubtitleMenuButton" })
            {
                var button = (Button)page.FindName(name);
                button.ApplyTemplate();
                var chrome = (Border)button.Template.FindName("Chrome", button);
                var original = ((SolidColorBrush)chrome.BorderBrush).Color;
                button.Focus(); Pump();
                Assert.IsTrue(button.IsKeyboardFocused, "The regression must exercise a focused button: " + name);
                Assert.AreEqual(original, ((SolidColorBrush)chrome.BorderBrush).Color, name);
                Assert.IsNotNull(button.FocusVisualStyle, "Keyboard navigation retains a separate focus adorner.");
            }
        }));
    }
}
