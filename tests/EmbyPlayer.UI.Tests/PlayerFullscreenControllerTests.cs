using System.Windows;
using EmbyPlayer.UI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class PlayerFullscreenControllerTests
{
    [TestMethod]
    public void Enter_PreparesChromeBeforeStateTopmostAndMonitorBounds()
    {
        var window = new FakeFullscreenWindow
        {
            WindowState = WindowState.Normal,
            Topmost = false
        };
        window.Operations.Clear();
        var controller = new PlayerFullscreenController(window);

        controller.Enter();

        Assert.IsTrue(controller.IsFullscreen);
        Assert.AreEqual(WindowState.Normal, window.WindowState);
        Assert.IsTrue(window.Topmost);
        CollectionAssert.AreEqual(
            new[]
            {
                "PrepareForFullscreen",
                "WindowState:Normal",
                "Topmost:True",
                "ApplyFullscreenBounds"
            },
            window.Operations);
    }

    [TestMethod]
    public void Exit_RestoresOriginalMaximizedStateAndTopmostValue()
    {
        var window = new FakeFullscreenWindow
        {
            WindowState = WindowState.Maximized,
            Topmost = false
        };
        window.Operations.Clear();
        var controller = new PlayerFullscreenController(window);

        controller.Enter();
        window.Operations.Clear();
        controller.Exit();

        Assert.IsFalse(controller.IsFullscreen);
        Assert.AreEqual(WindowState.Maximized, window.WindowState);
        Assert.IsFalse(window.Topmost);
        CollectionAssert.AreEqual(
            new[]
            {
                "RestoreWindowBounds",
                "Topmost:False",
                "WindowState:Maximized"
            },
            window.Operations);
    }

    [TestMethod]
    public void Toggle_EntersAndExitsFullscreen()
    {
        var window = new FakeFullscreenWindow();
        var controller = new PlayerFullscreenController(window);

        controller.Toggle();
        Assert.IsTrue(controller.IsFullscreen);

        controller.Toggle();
        Assert.IsFalse(controller.IsFullscreen);
        Assert.AreEqual(WindowState.Normal, window.WindowState);
        Assert.IsFalse(window.Topmost);
    }

    [TestMethod]
    public void Enter_WhenAlreadyFullscreen_DoesNotOverwriteOriginalState()
    {
        var window = new FakeFullscreenWindow
        {
            WindowState = WindowState.Normal,
            Topmost = false
        };
        var controller = new PlayerFullscreenController(window);

        controller.Enter();
        window.Topmost = false;
        controller.Enter();
        controller.Exit();

        Assert.AreEqual(WindowState.Normal, window.WindowState);
        Assert.IsFalse(window.Topmost);
    }

    [TestMethod]
    public void Exit_WhenNotFullscreen_DoesNothing()
    {
        var window = new FakeFullscreenWindow
        {
            WindowState = WindowState.Minimized,
            Topmost = true
        };
        var controller = new PlayerFullscreenController(window);

        controller.Exit();

        Assert.IsFalse(controller.IsFullscreen);
        Assert.AreEqual(WindowState.Minimized, window.WindowState);
        Assert.IsTrue(window.Topmost);
    }

    [TestMethod]
    public async Task WpfAdapter_HasNoNativeStyleOrGeometryMutationPath()
    {
        var root = FindRepositoryRoot();
        var code = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Services",
            "WpfPlayerFullscreenWindow.cs"));

        StringAssert.Contains(code, "IPlayerFullscreenWindow");
        StringAssert.Contains(code, "MonitorFromWindow");
        StringAssert.Contains(code, "GetMonitorInfo");
        StringAssert.Contains(code, "TransformFromDevice");
        StringAssert.Contains(code, "monitorInfo.Monitor");
        Assert.IsFalse(code.Contains("SetWindowLong", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("SetWindowPos", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("GwlStyle", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("PlayerWindowGeometry", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("WindowStyle", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("ResizeMode", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EmbyPlayer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed class FakeFullscreenWindow : IPlayerFullscreenWindow
    {
        private WindowState windowState = WindowState.Normal;
        private bool topmost;

        public List<string> Operations { get; } = [];

        public WindowState WindowState
        {
            get => windowState;
            set
            {
                windowState = value;
                Operations.Add($"WindowState:{value}");
            }
        }

        public bool Topmost
        {
            get => topmost;
            set
            {
                topmost = value;
                Operations.Add($"Topmost:{value}");
            }
        }

        public void PrepareForFullscreen()
        {
            Operations.Add(nameof(PrepareForFullscreen));
        }

        public void ApplyFullscreenBounds()
        {
            Operations.Add(nameof(ApplyFullscreenBounds));
        }

        public void RestoreWindowBounds()
        {
            Operations.Add(nameof(RestoreWindowBounds));
        }
    }
}
