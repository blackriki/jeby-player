using System.Windows;
using EmbyPlayer.UI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class PlayerWindowInteractionTests
{
    [TestMethod]
    public void NativeHitTests_MapCaptionAndAllResizeDirections()
    {
        var expected = new Dictionary<PlayerWindowHitTarget, int>
        {
            [PlayerWindowHitTarget.Caption] = 2,
            [PlayerWindowHitTarget.Left] = 10,
            [PlayerWindowHitTarget.Right] = 11,
            [PlayerWindowHitTarget.Top] = 12,
            [PlayerWindowHitTarget.TopLeft] = 13,
            [PlayerWindowHitTarget.TopRight] = 14,
            [PlayerWindowHitTarget.Bottom] = 15,
            [PlayerWindowHitTarget.BottomLeft] = 16,
            [PlayerWindowHitTarget.BottomRight] = 17
        };

        foreach (var pair in expected)
        {
            Assert.AreEqual(pair.Value, PlayerWindowInteraction.GetNativeHitTest(pair.Key));
        }
    }

    [TestMethod]
    public void CaptionDrag_SendsNativeRequestToOwnerHandleNotOverlayHandle()
    {
        var messenger = new FakeNativeMessenger();
        var interaction = new PlayerWindowInteraction(messenger);
        var ownerHandle = new IntPtr(101);
        var overlayHandle = new IntPtr(202);

        var handled = interaction.Begin(
            ownerHandle,
            WindowState.Normal,
            ResizeMode.CanResize,
            PlayerWindowHitTarget.Caption,
            isFullscreen: false);

        Assert.IsTrue(handled);
        Assert.AreEqual(1, messenger.ReleaseCaptureCallCount);
        Assert.AreEqual(ownerHandle, messenger.LastWindowHandle);
        Assert.AreNotEqual(overlayHandle, messenger.LastWindowHandle);
        Assert.AreEqual(PlayerWindowInteraction.NonClientLeftButtonDown, messenger.LastMessage);
        Assert.AreEqual(2, messenger.LastWParam.ToInt32());
    }

    [TestMethod]
    public void CaptionDoubleClick_SendsNativeDoubleClickToOwner()
    {
        var messenger = new FakeNativeMessenger();
        var interaction = new PlayerWindowInteraction(messenger);

        var handled = interaction.Begin(
            new IntPtr(303),
            WindowState.Normal,
            ResizeMode.CanResize,
            PlayerWindowHitTarget.Caption,
            isFullscreen: false,
            clickCount: 2);

        Assert.IsTrue(handled);
        Assert.AreEqual(PlayerWindowInteraction.NonClientLeftButtonDoubleClick, messenger.LastMessage);
        Assert.AreEqual(2, messenger.LastWParam.ToInt32());
    }

    [TestMethod]
    public void Resize_SendsEveryMappedHitTestToOwner()
    {
        var resizeTargets = Enum.GetValues<PlayerWindowHitTarget>()
            .Where(target => target != PlayerWindowHitTarget.Caption);
        var ownerHandle = new IntPtr(404);

        foreach (var target in resizeTargets)
        {
            var messenger = new FakeNativeMessenger();
            var interaction = new PlayerWindowInteraction(messenger);

            Assert.IsTrue(interaction.Begin(
                ownerHandle,
                WindowState.Normal,
                ResizeMode.CanResize,
                target,
                isFullscreen: false));
            Assert.AreEqual(ownerHandle, messenger.LastWindowHandle);
            Assert.AreEqual(PlayerWindowInteraction.NonClientLeftButtonDown, messenger.LastMessage);
            Assert.AreEqual(PlayerWindowInteraction.GetNativeHitTest(target), messenger.LastWParam.ToInt32());
        }
    }

    [TestMethod]
    public void FullscreenAndMaximizedGuards_DisableResizeButKeepMaximizedCaptionDrag()
    {
        Assert.IsFalse(PlayerWindowInteraction.CanBegin(
            WindowState.Normal,
            ResizeMode.CanResize,
            PlayerWindowHitTarget.Caption,
            isFullscreen: true));
        Assert.IsFalse(PlayerWindowInteraction.CanBegin(
            WindowState.Maximized,
            ResizeMode.CanResize,
            PlayerWindowHitTarget.Right,
            isFullscreen: false));
        Assert.IsTrue(PlayerWindowInteraction.CanBegin(
            WindowState.Maximized,
            ResizeMode.CanResize,
            PlayerWindowHitTarget.Caption,
            isFullscreen: false));
        Assert.IsFalse(PlayerWindowInteraction.CanBegin(
            WindowState.Normal,
            ResizeMode.NoResize,
            PlayerWindowHitTarget.Bottom,
            isFullscreen: false));
    }

    private sealed class FakeNativeMessenger : IPlayerWindowNativeMessenger
    {
        public int ReleaseCaptureCallCount { get; private set; }

        public IntPtr LastWindowHandle { get; private set; }

        public uint LastMessage { get; private set; }

        public IntPtr LastWParam { get; private set; }

        public void ReleaseCapture() => ReleaseCaptureCallCount++;

        public IntPtr SendMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
        {
            LastWindowHandle = windowHandle;
            LastMessage = message;
            LastWParam = wParam;
            return IntPtr.Zero;
        }
    }
}
