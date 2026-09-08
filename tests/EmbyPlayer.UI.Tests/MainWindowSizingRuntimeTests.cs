using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class PlayerEnhancementRuntimeTests
{
    [TestMethod]
    public void MainWindowNativeSizingHonorsWpfMinimumAtCurrentDpi()
    {
        RunOnSta(() => WithPage(0, (_, _, _, _, _) =>
        {
            // Create a real native MainWindow without showing it or invoking its login/startup flow.
            var owner = new EmbyPlayer.App.MainWindow();
            try
            {
                var handle = new WindowInteropHelper(owner).EnsureHandle();
                foreach (var minimum in new[] { (1100d, 700d), (1200.25, 750.25) })
                {
                    owner.MinWidth = minimum.Item1;
                    owner.MinHeight = minimum.Item2;
                    var dpi = VisualTreeHelper.GetDpi(owner);
                    var info = new SizingInfo();
                    SendSizingMessage(handle, 0x0024, IntPtr.Zero, ref info);
                    Assert.IsTrue(info.MinTrack.X >= Math.Ceiling(owner.MinWidth * dpi.DpiScaleX),
                        $"Native width {info.MinTrack.X} permits clipping the WPF minimum.");
                    Assert.IsTrue(info.MinTrack.Y >= Math.Ceiling(owner.MinHeight * dpi.DpiScaleY),
                        $"Native height {info.MinTrack.Y} permits clipping the WPF minimum.");
                    Assert.IsTrue(info.MaxSize.X > 0 && info.MaxSize.Y > 0,
                        "The work-area maximize bounds must remain populated.");
                }
            }
            finally
            {
                // Closing a production MainWindow normally shuts down the entire application.
                // Complete its coordinator with a local callback so this fixture can release
                // the HWND without terminating the shared WPF test application.
                var lifecycle = (EmbyPlayer.UI.Services.MainWindowLifecycleCoordinator)typeof(EmbyPlayer.App.MainWindow)
                    .GetField("lifecycleCoordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
                lifecycle.CompleteClose(false, "sizing-test", () => { });
                typeof(EmbyPlayer.App.MainWindow).GetField("allowWindowClose", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(owner, true);
                owner.Close();
                Pump();
            }
        }));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizingPoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizingInfo
    {
        public SizingPoint Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack;
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendSizingMessage(IntPtr window, uint message, IntPtr wParam, ref SizingInfo info);
}
