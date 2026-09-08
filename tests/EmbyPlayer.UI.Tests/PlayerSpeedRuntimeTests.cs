using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EmbyPlayer.UI.Pages;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class PlayerEnhancementRuntimeTests
{
    [TestMethod]
    public void SpeedMenuFitsMinimumWindowAndSelectsWithoutConflictingWithOtherMenus()
    {
        RunOnSta(() => WithPage(2, (page, viewModel, player, preparation, window) =>
        {
            var chrome = (FrameworkElement)page.FindName("BottomChrome");
            foreach (var button in Descendants<Button>(chrome).Where(button => button.IsVisible))
            {
                var bounds = button.TransformToAncestor(chrome).TransformBounds(new Rect(button.RenderSize));
                Assert.IsTrue(bounds.Left >= 0 && bounds.Right <= chrome.ActualWidth + 1, $"Clipped toolbar: {bounds}");
            }
            Open(page, "SpeedMenuButton", Popup(page, "SpeedPopup"));
            var root = (FrameworkElement)Popup(page, "SpeedPopup").Child;
            var slow = Descendants<Button>(root).Single(button => button.DataContext is PlayerSpeedOption { Speed: 0.5 });
            ActivateWithEnter(slow); Pump();
            Assert.AreEqual(0.5, viewModel.PlaybackSpeed);
            Assert.AreEqual("0.5×", ((Button)page.FindName("SpeedMenuButton")).Content);
            Assert.IsFalse(Popup(page, "SpeedPopup").IsOpen);
            Open(page, "SpeedMenuButton", Popup(page, "SpeedPopup"));
            Open(page, "AudioMenuButton", Popup(page, "AudioPopup"));
            Assert.IsFalse(Popup(page, "SpeedPopup").IsOpen);
            Open(page, "SpeedMenuButton", Popup(page, "SpeedPopup"));
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(page), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            page.RaiseEvent(args); Pump();
            Assert.IsFalse(Popup(page, "SpeedPopup").IsOpen);
        }));
    }

    [TestMethod]
    public void AcceptedVideoPressDistinguishesClickHoldAndCancellationInWpf()
    {
        RunOnSta(() => WithPage(2, (page, viewModel, player, preparation, window) =>
        {
            var surface = (UIElement)page.FindName("VideoArea");
            object? Call(string method, params object[] args) => typeof(PlayerPage)
                .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args);
            // The offscreen host does not own physical mouse input. Exercise the state after capture
            // was accepted; physical pointer capture still requires runnable-app verification.
            void Begin() => Call("TrackAcceptedVideoPress", surface, new Point(300, 300), viewModel);
            void Release() => Call("OnVideoAreaMouseLeftButtonUp", surface,
                new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            viewModel.SetPlaybackSpeedAsync(1.5).GetAwaiter().GetResult();
            Begin();
            ((Task)Call("HandleVideoPressElapsedAsync", true)!).GetAwaiter().GetResult();
            Assert.IsTrue(viewModel.IsTemporaryDoubleSpeed);
            Assert.AreEqual(2d, viewModel.PlaybackSpeed);
            Release(); Pump();
            Assert.AreEqual(1.5, viewModel.PlaybackSpeed);
            Assert.IsTrue(viewModel.IsPlaying, "Releasing a held frame must not pause playback.");
            Begin(); Release(); Pump();
            Assert.IsTrue(viewModel.IsPaused, "A short click still toggles pause.");
            Begin(); ((Task)Call("HandleVideoPressElapsedAsync", true)!).GetAwaiter().GetResult(); Release(); Pump();
            Assert.IsTrue(viewModel.IsPaused, "A long press on a paused frame must not start playback.");
            viewModel.PlayCommand.Execute(null); Pump();
            Begin(); ((Task)Call("HandleVideoPressElapsedAsync", true)!).GetAwaiter().GetResult();
            Call("OnApplicationDeactivated", page, EventArgs.Empty); Pump();
            Assert.AreEqual(1.5, viewModel.PlaybackSpeed);
            Assert.IsFalse(viewModel.IsTemporaryDoubleSpeed);
            Assert.AreNotSame(surface, Mouse.Captured);
            Begin(); ((Task)Call("HandleVideoPressElapsedAsync", false)!).GetAwaiter().GetResult(); Release(); Pump();
            Assert.IsTrue(viewModel.IsPlaying, "Losing the held button cancels without an extra click.");
        }));
    }
}
