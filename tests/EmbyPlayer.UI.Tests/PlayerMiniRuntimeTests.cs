using System.Windows;
using System.Windows.Controls;
using EmbyPlayer.UI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class PlayerEnhancementRuntimeTests
{
    [TestMethod]
    public void MiniPlayerRestoresBoundsAndTopmostAndKeepsControlsReachable()
    {
        RunOnSta(() => WithPage(0, (page, _, _, _, window) =>
        {
            window.MinWidth = 640; window.MinHeight = 360;
            var bounds = new Rect(window.Left, window.Top, window.Width, window.Height);
            var priorTopmost = window.Topmost;
            ((Button)page.FindName("MiniPlayerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Assert.AreEqual(480d, window.Width);
            Assert.AreEqual(270d, window.Height);
            Assert.IsTrue(window.Topmost);
            foreach (var name in new[] { "TopChrome", "BottomChrome" })
            {
                var chrome = (FrameworkElement)page.FindName(name);
                foreach (var button in Descendants<Button>(chrome).Where(b => b.IsVisible))
                {
                    var area = button.TransformToAncestor(chrome).TransformBounds(new Rect(button.RenderSize));
                    Assert.IsTrue(area.Left >= 0 && area.Right <= chrome.ActualWidth + 1, $"{name}: {area}");
                }
            }
            ((Button)page.FindName("PinPlayerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsFalse(window.Topmost);
            ((Button)page.FindName("MiniPlayerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Assert.AreEqual(bounds, new Rect(window.Left, window.Top, window.Width, window.Height));
            Assert.AreEqual(priorTopmost, window.Topmost);
            Assert.AreEqual(640d, window.MinWidth);
            Assert.AreEqual(360d, window.MinHeight);
        }));
    }

    [TestMethod]
    public void MiniPlayerReturnsToPriorFullscreenAndThenOriginalWindow()
    {
        RunOnSta(() => WithPage(0, (page, viewModel, _, _, window) =>
        {
            var bounds = new Rect(window.Left, window.Top, window.Width, window.Height);
            var toggle = page.GetType().GetMethod("ToggleFullscreen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            toggle.Invoke(page, null); Pump();
            Assert.IsTrue(viewModel.IsFullscreen);
            ((Button)page.FindName("MiniPlayerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Assert.IsFalse(viewModel.IsFullscreen);
            Assert.AreEqual(480d, window.Width);
            ((Button)page.FindName("MiniPlayerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Assert.IsTrue(viewModel.IsFullscreen);
            toggle.Invoke(page, null); Pump();
            Assert.AreEqual(bounds, new Rect(window.Left, window.Top, window.Width, window.Height));
        }));
    }

    [TestMethod]
    public void MiniControllerRestoresMaximizedAndPinnedState()
    {
        RunOnSta(() => WithPage(0, (_, _, _, _, window) =>
        {
            window.Topmost = true;
            window.WindowState = WindowState.Maximized; Pump();
            var controller = new PlayerMiniWindowController(window);
            controller.Enter(); controller.Enter();
            Assert.AreEqual(WindowState.Normal, window.WindowState);
            window.Topmost = false;
            controller.Exit(); controller.Exit();
            Assert.AreEqual(WindowState.Maximized, window.WindowState);
            Assert.IsTrue(window.Topmost);
        }));
    }
}
