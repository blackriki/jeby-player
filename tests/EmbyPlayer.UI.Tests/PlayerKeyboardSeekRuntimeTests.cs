using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EmbyPlayer.Core.Settings;
using EmbyPlayer.UI.Pages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class PlayerEnhancementRuntimeTests
{
    [TestMethod]
    public void KeyboardSeekPage_HeldKeyUsesTimerAndModifierReleaseCancels()
    {
        RunOnSta(() => WithPage(0, (page, vm, player, _, _) =>
        {
            RaiseSeekKey(page, Key.Right, Keyboard.PreviewKeyDownEvent);
            var frame = new System.Windows.Threading.DispatcherFrame();
            var stop = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
            stop.Tick += (_, _) => { stop.Stop(); frame.Continue = false; };
            stop.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Assert.IsTrue(vm.IsKeyboardSeekActive);
            Assert.AreEqual(0, player.SeekCallCount);
            RaiseSeekKey(page, Key.Right, Keyboard.PreviewKeyUpEvent);
            Pump();
            Assert.AreEqual(1, player.SeekCallCount);
            Assert.IsTrue(player.LastSeekPosition.TotalSeconds >= 12);

            var begin = typeof(PlayerPage).GetMethod("BeginKeyboardSeek", BindingFlags.Instance | BindingFlags.NonPublic)!;
            begin.Invoke(page, new object[] { Key.K, PlayerShortcutAction.SeekForward, false });
            RaiseSeekKey(page, Key.LeftCtrl, Keyboard.PreviewKeyUpEvent);
            RaiseSeekKey(page, Key.K, Keyboard.PreviewKeyUpEvent);
            Assert.IsFalse(vm.IsKeyboardSeekActive);
            Assert.AreEqual(1, player.SeekCallCount);
        }));
    }

    [TestMethod]
    public void KeyboardSeekPage_TapRoutesKeyDownAndKeyUpToSingleCommit()
    {
        RunOnSta(() => WithPage(0, (page, vm, player, _, _) =>
        {
            var calls = player.SeekCallCount;
            RaiseSeekKey(page, Key.Right, Keyboard.PreviewKeyDownEvent);
            Assert.IsTrue(vm.IsKeyboardSeekActive);
            Assert.AreEqual(calls, player.SeekCallCount);
            var feedback = (Border)page.FindName("KeyboardSeekFeedback");
            Assert.AreEqual(Visibility.Visible, feedback.Visibility);
            // Key state may already be released before its queued WPF key-up event is dispatched.
            typeof(PlayerPage).GetMethod("OnKeyboardSeekTimerTick", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(page, new object?[] { null, EventArgs.Empty });
            Assert.IsTrue(vm.IsKeyboardSeekActive);
            RaiseSeekKey(page, Key.Right, Keyboard.PreviewKeyUpEvent);
            Pump();
            Assert.AreEqual(calls + 1, player.SeekCallCount);
            Assert.IsFalse(vm.IsKeyboardSeekActive);
            Assert.AreEqual(Visibility.Collapsed, feedback.Visibility);
        }));
    }

    [TestMethod]
    public void KeyboardSeekPage_OsRepeatDoesNotReplaceHoldOrQueueSeeks()
    {
        RunOnSta(() => WithPage(0, (page, vm, player, _, _) =>
        {
            RaiseSeekKey(page, Key.Right, Keyboard.PreviewKeyDownEvent);
            vm.UpdateKeyboardSeek(TimeSpan.FromSeconds(5));
            var preview = vm.SeekPercent;
            var begin = typeof(PlayerPage).GetMethod("BeginKeyboardSeek", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (var index = 0; index < 20; index++)
                begin.Invoke(page, new object[] { Key.Right, PlayerShortcutAction.SeekForward, true });
            Assert.AreEqual(preview, vm.SeekPercent);
            Assert.AreEqual(0, player.SeekCallCount);
            typeof(PlayerPage).GetMethod("CancelKeyboardSeek", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
            RaiseSeekKey(page, Key.Right, Keyboard.PreviewKeyUpEvent);
            Assert.AreEqual(0, player.SeekCallCount);
        }));
    }

    [TestMethod]
    public void KeyboardSeekPage_OppositeKeyIgnoresOldReleaseAndPopupCancels()
    {
        RunOnSta(() => WithPage(0, (page, vm, player, _, _) =>
        {
            RaiseSeekKey(page, Key.Right, Keyboard.PreviewKeyDownEvent);
            RaiseSeekKey(page, Key.Left, Keyboard.PreviewKeyDownEvent);
            RaiseSeekKey(page, Key.Right, Keyboard.PreviewKeyUpEvent);
            Assert.IsTrue(vm.IsKeyboardSeekActive);
            Assert.AreEqual(0, player.SeekCallCount);
            Open(page, "SubtitleMenuButton", Popup(page, "SubtitlePopup"));
            Assert.IsFalse(vm.IsKeyboardSeekActive);
            RaiseSeekKey(page, Key.Left, Keyboard.PreviewKeyUpEvent);
            Assert.AreEqual(0, player.SeekCallCount);
        }));
    }

    [TestMethod]
    public void KeyboardSeekPage_DeactivationAndFullscreenCancelWithoutCommit()
    {
        RunOnSta(() => WithPage(0, (page, vm, player, _, _) =>
        {
            RaiseSeekKey(page, Key.Right, Keyboard.PreviewKeyDownEvent);
            typeof(PlayerPage).GetMethod("OnApplicationDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(page, new object?[] { null, EventArgs.Empty });
            Assert.IsFalse(vm.IsKeyboardSeekActive);
            RaiseSeekKey(page, Key.Right, Keyboard.PreviewKeyUpEvent);
            RaiseSeekKey(page, Key.Right, Keyboard.PreviewKeyDownEvent);
            typeof(PlayerPage).GetMethod("ToggleFullscreen", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
            Assert.IsFalse(vm.IsKeyboardSeekActive);
            RaiseSeekKey(page, Key.Right, Keyboard.PreviewKeyUpEvent);
            Assert.AreEqual(0, player.SeekCallCount);
        }));
    }

    private static void RaiseSeekKey(PlayerPage page, Key key, RoutedEvent routedEvent) =>
        page.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(page), 0, key) { RoutedEvent = routedEvent });
}
