using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class PlayerEnhancementRuntimeTests
{
    [DataTestMethod]
    [DataRow(640d, 360d)]
    [DataRow(800d, 450d)]
    [DataRow(899d, 360d)]
    public void CompactPlayerKeepsControlsAndEveryMenuReachable(double width, double height)
    {
        RunOnSta(() => WithPage(12, (page, viewModel, player, preparation, window) =>
        {
            window.Width = width;
            window.Height = height;
            window.UpdateLayout(); Pump();
            var more = (Button)page.FindName("MoreMenuButton");
            Assert.IsTrue(more.IsVisible);
            Assert.IsFalse(((FrameworkElement)page.FindName("SecondaryPlayerActions")).IsVisible);
            var chrome = (FrameworkElement)page.FindName("BottomChrome");
            foreach (var button in Descendants<Button>(chrome).Where(button => button.IsVisible))
            {
                var bounds = button.TransformToAncestor(chrome).TransformBounds(new Rect(button.RenderSize));
                Assert.IsTrue(bounds.Left >= 0 && bounds.Right <= chrome.ActualWidth + 1, $"Clipped control: {bounds}");
            }

            foreach (var pair in new[] { ("Subtitle", "SubtitlePopup"), ("Audio", "AudioPopup"),
                ("Quality", "QualityPopup"), ("Speed", "SpeedPopup"), ("Queue", "QueuePopup"), ("Info", "PlaybackInfoPopup") })
            {
                Open(page, "MoreMenuButton", Popup(page, "MorePopup"));
                var action = Descendants<Button>(Popup(page, "MorePopup").Child).Single(button => Equals(button.Tag, pair.Item1));
                action.BringIntoView(); Pump(); ActivateWithEnter(action); Pump();
                var popup = Popup(page, pair.Item2);
                Assert.IsTrue(popup.IsOpen, pair.Item1);
                Assert.AreEqual(1, AllPopups(page).Count(menu => menu.IsOpen));
                Assert.AreSame(more, popup.PlacementTarget);
                var border = (FrameworkElement)popup.Child;
                var origin = more.TransformToAncestor((FrameworkElement)page.FindName("OverlayRoot")).Transform(new Point());
                Assert.IsTrue(origin.X + popup.HorizontalOffset >= 15, pair.Item1);
                Assert.IsTrue(origin.X + popup.HorizontalOffset + border.ActualWidth <= width - 15, pair.Item1);
                Assert.IsTrue(border.ActualWidth <= width - 30, pair.Item1);
                Assert.IsTrue(border.ActualHeight <= height - 98, $"Menu too tall: {pair.Item1} {border.ActualHeight}");
                if (pair.Item1 == "Speed")
                {
                    var fast = Descendants<Button>(border).Single(button => button.DataContext is PlayerSpeedOption { Speed: 3 });
                    fast.BringIntoView(); Pump(); ActivateWithEnter(fast); Pump();
                    Assert.AreEqual(3d, viewModel.PlaybackSpeed);
                }
                popup.IsOpen = false; Pump();
            }

            Open(page, "MoreMenuButton", Popup(page, "MorePopup"));
            page.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(page), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent }); Pump();
            Assert.IsFalse(Popup(page, "MorePopup").IsOpen);
            window.Width = 1100; window.Height = 700; window.UpdateLayout(); Pump();
            Assert.IsFalse(more.IsVisible);
            Assert.IsTrue(((FrameworkElement)page.FindName("SecondaryPlayerActions")).IsVisible);
            Open(page, "SpeedMenuButton", Popup(page, "SpeedPopup"));
            Assert.AreSame(page.FindName("SpeedMenuButton"), Popup(page, "SpeedPopup").PlacementTarget);
        }, CreateQueue(12)));
    }
}
