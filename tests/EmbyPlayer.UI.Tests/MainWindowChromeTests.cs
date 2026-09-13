using Microsoft.VisualStudio.TestTools.UnitTesting;
using EmbyPlayer.UI.Services;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class MainWindowChromeTests
{
    [TestMethod]
    public async Task ReleaseSmokeMarker_BypassesOnlyItsDedicatedInstanceCloseGuard()
    {
        var root = FindRepositoryRoot();
        var code = await File.ReadAllTextAsync(MainWindowPath(root, "MainWindow.xaml.cs"));

        Assert.IsTrue(ReleaseSmokeLaunchPolicy.IsReleaseSmoke(
            new[] { "JebyPlayer.exe", ReleaseSmokeLaunchPolicy.Argument }));
        Assert.IsFalse(ReleaseSmokeLaunchPolicy.RequiresSettingsCloseGuard(
            new[] { "JebyPlayer.exe", ReleaseSmokeLaunchPolicy.Argument }));
        Assert.IsFalse(ReleaseSmokeLaunchPolicy.IsReleaseSmoke(
            new[] { "JebyPlayer.exe", "--release-smoke-other" }));
        Assert.IsTrue(ReleaseSmokeLaunchPolicy.RequiresSettingsCloseGuard(
            new[] { "JebyPlayer.exe" }));
        StringAssert.Contains(code, "isReleaseSmoke = ReleaseSmokeLaunchPolicy.IsReleaseSmoke(");
        StringAssert.Contains(code, "lifecycleCoordinator.EvaluateClosing(");
        StringAssert.Contains(code, "SettingsViewModel.RequestWindowClose,");
        StringAssert.Contains(code, "MainWindowClosingDecision.Prepare");
        StringAssert.Contains(code, "lifecycleCoordinator.CompleteClose(");
        StringAssert.Contains(code, "RequestApplicationShutdown");
        StringAssert.Contains(code, "TestDataRootEnvironmentVariable");
        StringAssert.Contains(code, "\"release-smoke\"");
        StringAssert.Contains(code, "CreateAppSettingsService()");
        StringAssert.Contains(code, "MainWindowNativeMessage.WmDestroy");
        StringAssert.Contains(code, "MainWindowNativeMessage.WmNcDestroy");
        StringAssert.Contains(code, "ScheduleUnexpectedWindowShutdown(MainWindowDestructionTrigger.WmDestroy)");
        StringAssert.Contains(code, "ScheduleUnexpectedWindowShutdown(MainWindowDestructionTrigger.WmNcDestroy)");
        StringAssert.Contains(code, "normalCloseAccepted = true;");
        Assert.AreEqual(1, code.Split("normalCloseAccepted = true;").Length - 1);
        StringAssert.Contains(code, "PrepareUnexpectedWindowShutdownAsync");
        StringAssert.Contains(code, "RequestApplicationShutdown");
    }

    [TestMethod]
    public async Task MainWindow_UsesOnePermanentBuiltInWindowChrome()
    {
        var root = FindRepositoryRoot();
        var xaml = await File.ReadAllTextAsync(MainWindowPath(root, "MainWindow.xaml"));
        var code = await File.ReadAllTextAsync(MainWindowPath(root, "MainWindow.xaml.cs"));
        var adapter = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Services",
            "WpfPlayerFullscreenWindow.cs"));

        StringAssert.Contains(xaml, "WindowStyle=\"None\"");
        StringAssert.Contains(xaml, "<shell:WindowChrome.WindowChrome>");
        StringAssert.Contains(xaml, "x:Name=\"MainWindowChrome\"");
        StringAssert.Contains(xaml, "CaptionHeight=\"40\"");
        StringAssert.Contains(xaml, "ResizeBorderThickness=\"6\"");
        Assert.AreEqual(1, CountOccurrences(xaml, "<shell:WindowChrome "));
        Assert.IsFalse(code.Contains("DwmwaCaptionColor", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("DwmwaBorderColor", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("DwmwaTextColor", StringComparison.Ordinal));
        Assert.IsFalse(adapter.Contains("SetWindowLong", StringComparison.Ordinal));
        Assert.IsFalse(adapter.Contains("SetWindowPos", StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Services",
            "PlayerWindowChromeController.cs")));
    }

    [TestMethod]
    public async Task Caption_UsesRequiredDimensionsIconsCommandsAndAccessibility()
    {
        var root = FindRepositoryRoot();
        var xaml = await File.ReadAllTextAsync(MainWindowPath(root, "MainWindow.xaml"));
        var code = await File.ReadAllTextAsync(MainWindowPath(root, "MainWindow.xaml.cs"));
        var icons = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Resources",
            "Icons.xaml"));

        StringAssert.Contains(xaml, "x:Name=\"TitleBar\"");
        StringAssert.Contains(xaml, "Height=\"40\"");
        StringAssert.Contains(xaml, "Property=\"Width\" Value=\"46\"");
        StringAssert.Contains(xaml, "Width=\"16\"");
        StringAssert.Contains(xaml, "Text=\"{x:Static core:ApplicationIdentity.Name}\"");
        StringAssert.Contains(xaml, "Focusable\" Value=\"False\"");
        StringAssert.Contains(xaml, "IsTabStop\" Value=\"False\"");
        foreach (var text in new[] { "最小化", "最大化", "还原", "关闭" })
        {
            StringAssert.Contains(xaml, text);
        }

        StringAssert.Contains(xaml, "Icon.WindowMinimize");
        StringAssert.Contains(xaml, "Icon.WindowMaximize");
        StringAssert.Contains(xaml, "Icon.WindowRestore");
        StringAssert.Contains(xaml, "Icon.Close");
        StringAssert.Contains(code, "SystemCommands.MinimizeWindow(this);");
        StringAssert.Contains(code, "public void MinimizePlayerWindow()");
        StringAssert.Contains(code, "OnMinimizeButtonClick");
        Assert.AreEqual(1, CountOccurrences(code, "SystemCommands.MinimizeWindow(this);"));
        StringAssert.Contains(code, "SystemCommands.MaximizeWindow(this);");
        StringAssert.Contains(code, "SystemCommands.RestoreWindow(this);");
        StringAssert.Contains(code, "SystemCommands.CloseWindow(this);");
        StringAssert.Contains(code, "public void ClosePlayerWindow()");
        StringAssert.Contains(code, "SystemCommands.ShowSystemMenu(this");
        Assert.IsFalse(xaml.Contains("<Path", StringComparison.Ordinal));
        Assert.AreEqual(1, CountOccurrences(icons, "x:Key=\"Icon.WindowMinimize\""));
        Assert.AreEqual(1, CountOccurrences(icons, "x:Key=\"Icon.WindowMaximize\""));
        Assert.AreEqual(1, CountOccurrences(icons, "x:Key=\"Icon.WindowRestore\""));
    }

    [TestMethod]
    public async Task PlayerChrome_IsNotRenderedByMainWindow()
    {
        var root = FindRepositoryRoot();
        var xaml = await File.ReadAllTextAsync(MainWindowPath(root, "MainWindow.xaml"));
        var code = await File.ReadAllTextAsync(MainWindowPath(root, "MainWindow.xaml.cs"));

        Assert.IsFalse(xaml.Contains("PlayerLogo", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("CurrentPageViewModel.LogoUrl", StringComparison.Ordinal));
        StringAssert.Contains(code, "var isCaptionVisible = !isPlayer;");
        Assert.IsFalse(code.Contains("isPlayerCaptionVisible", StringComparison.Ordinal));
        Assert.AreEqual(1, CountOccurrences(code, "SystemCommands.CloseWindow(this);"));
    }

    [TestMethod]
    public async Task Layout_KeepsBrowsingInsetAndPlayerFullBleedWithPlayerCaptionAlwaysHidden()
    {
        var root = FindRepositoryRoot();
        var xaml = await File.ReadAllTextAsync(MainWindowPath(root, "MainWindow.xaml"));
        var code = await File.ReadAllTextAsync(MainWindowPath(root, "MainWindow.xaml.cs"));
        var playerCode = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Pages",
            "PlayerPage.xaml.cs"));

        StringAssert.Contains(xaml, "x:Name=\"ShellContainer\" Margin=\"0,40,0,0\"");
        StringAssert.Contains(code, "var isPlayer = appShellViewModel.CurrentPage == AppPage.Player;");
        StringAssert.Contains(code, "? new Thickness(0)");
        StringAssert.Contains(code, ": new Thickness(0, TitleBarHeight, 0, 0);");
        StringAssert.Contains(code, "TitleBar.Visibility = isCaptionVisible ? Visibility.Visible : Visibility.Collapsed;");
        StringAssert.Contains(code, "MainWindowChrome.CaptionHeight = isCaptionVisible ? TitleBarHeight : 0;");
        StringAssert.Contains(code, "var isCaptionVisible = !isPlayer;");
        StringAssert.Contains(code, "if (isPlayerFullscreen == isFullscreen)");
        StringAssert.Contains(playerCode, "SetPlayerCaptionState(shouldShowChrome, viewModel.IsFullscreen);");
        Assert.IsFalse(playerCode.Contains("PlayerCaptionInset", StringComparison.Ordinal));
        Assert.IsFalse(playerCode.Contains("ApplyVideoHostCaptionInset", StringComparison.Ordinal));
        Assert.IsFalse(playerCode.Contains("VideoHost.Margin", StringComparison.Ordinal));
        Assert.IsFalse(playerCode.Contains("ShellContainer", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Fullscreen_HidesCaptionHitRegionsAndControllerRestoresWindowState()
    {
        var root = FindRepositoryRoot();
        var mainCode = await File.ReadAllTextAsync(MainWindowPath(root, "MainWindow.xaml.cs"));
        var controller = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Services",
            "PlayerFullscreenController.cs"));

        StringAssert.Contains(mainCode, "var isCaptionVisible = !isPlayer;");
        StringAssert.Contains(mainCode, "MainWindowChrome.CaptionHeight = isCaptionVisible ? TitleBarHeight : 0;");
        StringAssert.Contains(mainCode, "MainWindowChrome.ResizeBorderThickness = isPlayerFullscreen");
        StringAssert.Contains(mainCode, "? new Thickness(0)");
        StringAssert.Contains(controller, "window.PrepareForFullscreen();");
        StringAssert.Contains(controller, "window.WindowState = WindowState.Normal;");
        StringAssert.Contains(controller, "window.ApplyFullscreenBounds();");
        StringAssert.Contains(controller, "window.RestoreWindowBounds();");
        StringAssert.Contains(controller, "window.WindowState = saved.WindowState;");
        Assert.IsFalse(controller.Contains("WindowStyle", StringComparison.Ordinal));
        Assert.IsFalse(controller.Contains("ResizeMode", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MaximizedWindow_UsesCurrentMonitorWorkAreaAndRefreshesFrameWithoutStateToggle()
    {
        var root = FindRepositoryRoot();
        var code = await File.ReadAllTextAsync(MainWindowPath(root, "MainWindow.xaml.cs"));
        var adapter = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Services",
            "WpfPlayerFullscreenWindow.cs"));
        var applyWindowLayout = GetCodeSection(
            code,
            "private void ApplyWindowLayout()",
            "private IntPtr WindowMessageHook(");
        var refreshBounds = GetCodeSection(
            code,
            "private void RefreshMaximizedWindowBounds()",
            "private static MaximizedWindowBounds CalculateMaximizedWindowBounds");

        StringAssert.Contains(code, "windowSource.AddHook(windowMessageHook);");
        StringAssert.Contains(code, "windowSource.RemoveHook(windowMessageHook);");
        StringAssert.Contains(code, "message != WmGetMinMaxInfo");
        StringAssert.Contains(code, "MonitorFromWindow(windowHandle, MonitorDefaultToNearest)");
        StringAssert.Contains(code, "monitorInfo.Monitor.ToNativePixelRect()");
        StringAssert.Contains(code, "monitorInfo.WorkArea.ToNativePixelRect()");
        StringAssert.Contains(code, "Marshal.StructureToPtr(minMaxInfo, lParam, fDeleteOld: false);");
        var captionHeight = applyWindowLayout.IndexOf("MainWindowChrome.CaptionHeight", StringComparison.Ordinal);
        var resizeBorder = applyWindowLayout.IndexOf("MainWindowChrome.ResizeBorderThickness", StringComparison.Ordinal);
        var refreshCall = applyWindowLayout.IndexOf("RefreshMaximizedWindowBounds();", StringComparison.Ordinal);
        Assert.IsTrue(captionHeight >= 0 && captionHeight < resizeBorder && resizeBorder < refreshCall);

        StringAssert.Contains(
            refreshBounds,
            "if (isPlayerFullscreen || WindowState != WindowState.Maximized)");
        var setWindowPos = refreshBounds.IndexOf("_ = SetWindowPos(", StringComparison.Ordinal);
        var workAreaLeft = refreshBounds.IndexOf("monitorInfo.WorkArea.Left,", StringComparison.Ordinal);
        var workAreaTop = refreshBounds.IndexOf("monitorInfo.WorkArea.Top,", StringComparison.Ordinal);
        var workAreaWidth = refreshBounds.IndexOf(
            "monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left",
            StringComparison.Ordinal);
        var workAreaHeight = refreshBounds.IndexOf(
            "monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top",
            StringComparison.Ordinal);
        var frameRefreshFlags = refreshBounds.IndexOf(
            "SwpNoZOrder | SwpNoActivate | SwpFrameChanged",
            StringComparison.Ordinal);
        Assert.IsTrue(
            setWindowPos >= 0
            && setWindowPos < workAreaLeft
            && workAreaLeft < workAreaTop
            && workAreaTop < workAreaWidth
            && workAreaWidth < workAreaHeight
            && workAreaHeight < frameRefreshFlags);
        Assert.IsFalse(code.Contains("SystemParameters.WorkArea", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("WindowState = WindowState.Normal", StringComparison.Ordinal));

        StringAssert.Contains(adapter, "monitorInfo.Monitor.Left");
        StringAssert.Contains(adapter, "monitorInfo.Monitor.Right");
        Assert.IsFalse(adapter.Contains("monitorInfo.WorkArea.Left", StringComparison.Ordinal));
    }

    private static string MainWindowPath(string root, string fileName) => Path.Combine(
        root,
        "src",
        "EmbyPlayer.App",
        fileName);

    private static int CountOccurrences(string value, string token) =>
        value.Split(token, StringSplitOptions.None).Length - 1;

    private static string GetCodeSection(string code, string startToken, string endToken)
    {
        var start = code.IndexOf(startToken, StringComparison.Ordinal);
        var end = code.IndexOf(endToken, start + startToken.Length, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        return code[start..end];
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
}
