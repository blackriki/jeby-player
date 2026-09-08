using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class PlayerEnhancementRuntimeTests
{
    [TestMethod]
    public void CompactPlayerRestoresBrowsingBoundsAndNativeMinimum()
    {
        RunOnSta(() => WithPage(0, (_, _, _, _, _) =>
        {
            var owner = new EmbyPlayer.App.MainWindow { Left = 90, Top = 70, Width = 1280, Height = 800 };
            try
            {
                var handle = new WindowInteropHelper(owner).EnsureHandle();
                SetCompactWindowMode(owner, true);
                Assert.AreEqual(640d, owner.MinWidth);
                Assert.AreEqual(360d, owner.MinHeight);
                var info = new SizingInfo();
                SendSizingMessage(handle, 0x0024, IntPtr.Zero, ref info);
                var dpi = VisualTreeHelper.GetDpi(owner);
                Assert.AreEqual((int)Math.Ceiling(640 * dpi.DpiScaleX), info.MinTrack.X);
                Assert.AreEqual((int)Math.Ceiling(360 * dpi.DpiScaleY), info.MinTrack.Y);

                owner.Width = 640;
                owner.Height = 360;
                owner.Left = 240;
                owner.Top = 160;
                SetCompactWindowMode(owner, true); // Caption/fullscreen updates must not overwrite the saved bounds.
                SetCompactWindowMode(owner, false);
                // PlayerPage.Unloaded may restore its compact pre-fullscreen rectangle
                // after the navigation notification; browsing restoration must win last.
                owner.Width = 800;
                owner.Height = 450;
                owner.Left = 300;
                owner.Top = 200;
                Pump();
                Assert.AreEqual(1100d, owner.MinWidth);
                Assert.AreEqual(700d, owner.MinHeight);
                Assert.AreEqual(1280d, owner.Width);
                Assert.AreEqual(800d, owner.Height);
                Assert.AreEqual(90d, owner.Left);
                Assert.AreEqual(70d, owner.Top);
            }
            finally { CloseCompactWindowFixture(owner); }
        }));
    }

    [TestMethod]
    public void CompactPlayerDoesNotChangeMaximizedStateWhenReturning()
    {
        RunOnSta(() => WithPage(0, (_, _, _, _, _) =>
        {
            var owner = new EmbyPlayer.App.MainWindow { Left = 90, Top = 70 };
            try
            {
                SetCompactWindowMode(owner, true);
                owner.WindowState = WindowState.Maximized;
                SetCompactWindowMode(owner, false);
                Pump();
                Assert.AreEqual(WindowState.Maximized, owner.WindowState);
                Assert.AreEqual(1100d, owner.MinWidth);
                Assert.AreEqual(700d, owner.MinHeight);
            }
            finally { CloseCompactWindowFixture(owner); }
        }));
    }

    private static void SetCompactWindowMode(EmbyPlayer.App.MainWindow owner, bool isPlayer) =>
        typeof(EmbyPlayer.App.MainWindow).GetMethod("ApplyPageWindowSize", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(owner, new object[] { isPlayer });

    private static void CloseCompactWindowFixture(EmbyPlayer.App.MainWindow owner)
    {
        var lifecycle = (EmbyPlayer.UI.Services.MainWindowLifecycleCoordinator)typeof(EmbyPlayer.App.MainWindow)
            .GetField("lifecycleCoordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        lifecycle.CompleteClose(false, "compact-sizing-test", () => { });
        typeof(EmbyPlayer.App.MainWindow).GetField("allowWindowClose", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(owner, true);
        owner.Close();
        Pump();
    }
}
