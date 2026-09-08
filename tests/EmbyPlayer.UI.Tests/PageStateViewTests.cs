using EmbyPlayer.UI.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class PageStateViewTests
{
    [DataTestMethod]
    [DataRow(true, true, true, PageStateKind.Loading)]
    [DataRow(false, true, true, PageStateKind.Error)]
    [DataRow(false, true, false, PageStateKind.Empty)]
    [DataRow(false, false, false, PageStateKind.Content)]
    public void ResolveState_AlwaysReturnsOneExclusiveState(
        bool isLoading,
        bool isEmpty,
        bool hasError,
        PageStateKind expected)
    {
        Assert.AreEqual(expected, PageStateView.ResolveState(isLoading, isEmpty, hasError));
    }

    [TestMethod]
    public async Task SharedView_DefinesLoadingEmptyErrorAndRetryPresentation()
    {
        var root = FindRepositoryRoot();
        var xaml = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Controls",
            "PageStateView.xaml"));

        StringAssert.Contains(xaml, "Value=\"Loading\"");
        StringAssert.Contains(xaml, "Value=\"Empty\"");
        StringAssert.Contains(xaml, "Value=\"Error\"");
        StringAssert.Contains(xaml, "Value=\"Content\"");
        StringAssert.Contains(xaml, "controls:LoadingSpinner");
        Assert.IsFalse(xaml.Contains("<ProgressBar", StringComparison.Ordinal));
        StringAssert.Contains(xaml, "Command=\"{Binding RetryCommand, ElementName=Root}\"");
        StringAssert.Contains(xaml, "IsEnabled=\"{Binding IsRetryEnabled, ElementName=Root}\"");
        StringAssert.Contains(xaml, "Icon.Retry");
        Assert.IsFalse(xaml.Contains("StackTrace", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ContentState_CollapsesTheControlAndAllInactivePanels()
    {
        var root = FindRepositoryRoot();
        var xaml = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Controls",
            "PageStateView.xaml"));

        StringAssert.Contains(xaml, "Binding=\"{Binding CurrentState, ElementName=Root}\" Value=\"Content\"");
        StringAssert.Contains(xaml, "<Setter Property=\"Visibility\" Value=\"Collapsed\" />");
        foreach (var panel in new[] { "LoadingPanel", "EmptyPanel", "ErrorPanel" })
        {
            StringAssert.Contains(xaml, $"x:Name=\"{panel}\"");
        }

        Assert.IsFalse(xaml.Contains("Visibility\" Value=\"Hidden", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task LoadingSpinner_IsSharedVectorAnimationWithoutFixedLayoutHeight()
    {
        var root = FindRepositoryRoot();
        var spinnerXaml = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Controls",
            "LoadingSpinner.xaml"));
        var spinnerCode = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Controls",
            "LoadingSpinner.xaml.cs"));
        var shellXaml = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Controls",
            "AppShell.xaml"));

        StringAssert.Contains(spinnerXaml, "x:Name=\"SpinnerTrack\"");
        StringAssert.Contains(spinnerXaml, "x:Name=\"SpinnerSweep\"");
        StringAssert.Contains(spinnerXaml, "CenterX=\"16\" CenterY=\"16\"");
        Assert.IsFalse(spinnerXaml.Contains("RenderTransformOrigin", StringComparison.Ordinal));
        Assert.IsFalse(spinnerXaml.Contains("ProgressBar", StringComparison.Ordinal));
        Assert.IsFalse(spinnerXaml.Contains("MinHeight", StringComparison.Ordinal));
        StringAssert.Contains(spinnerCode, "private const int SegmentCount = 24;");
        StringAssert.Contains(spinnerCode, "private const double RingCenter = 16;");
        StringAssert.Contains(spinnerCode, "private const double RingRadius = 12.125;");
        StringAssert.Contains(spinnerCode, "private const double RingStrokeThickness = 3.75;");
        StringAssert.Contains(spinnerCode, "SpinnerTrack.Data = CreateTrackGeometry();");
        StringAssert.Contains(spinnerCode, "Data = CreateArcGeometry(startAngle, endAngle)");
        StringAssert.Contains(spinnerCode, "var opacity = SmoothStep(progress);");
        StringAssert.Contains(spinnerCode, "return progress * progress * (3 - (2 * progress));");
        StringAssert.Contains(spinnerCode, "StrokeStartLineCap = PenLineCap.Flat");
        StringAssert.Contains(spinnerCode, "index == SegmentCount - 1 ? PenLineCap.Round : PenLineCap.Flat");
        StringAssert.Contains(spinnerCode, "SpinnerSweep.Children.Add(segment);");
        StringAssert.Contains(spinnerCode, "DoubleAnimation");
        StringAssert.Contains(spinnerCode, "TimeSpan.FromMilliseconds(950)");
        StringAssert.Contains(spinnerCode, "RepeatBehavior.Forever");
        StringAssert.Contains(spinnerCode, "Unloaded += OnUnloaded");
        StringAssert.Contains(spinnerCode, "IsVisibleChanged += OnIsVisibleChanged");
        StringAssert.Contains(spinnerCode, "BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null)");

        StringAssert.Contains(shellXaml, "controls:PageStateView IsLoading=\"{Binding IsStartupLoading, Mode=OneWay}\"");
    }

    [TestMethod]
    public async Task HomeContentLayout_DoesNotReserveStateHeightOrAddBlankHeroRows()
    {
        var root = FindRepositoryRoot();
        var xaml = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Pages",
            "HomePage.xaml"));
        xaml = xaml.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.IsFalse(xaml.Contains("MinHeight=\"96\"", StringComparison.Ordinal));

        var heroEnd = xaml.IndexOf("</Grid>\n\n        <controls:PageStateView IsEmpty=\"{Binding IsLibrariesEmpty", StringComparison.Ordinal);
        var libraryRow = xaml.IndexOf("<Grid x:Name=\"LibraryRow\"", StringComparison.Ordinal);
        Assert.IsTrue(heroEnd >= 0, "The library state remains directly after the existing Hero grid.");
        Assert.IsTrue(libraryRow > heroEnd, "The library row remains in the same compact stack as its state view.");

        var continueTitle = xaml.IndexOf("<TextBlock Text=\"\u7ee7\u7eed\u89c2\u770b\"", StringComparison.Ordinal);
        var continueState = xaml.IndexOf("IsContinueWatchingEmpty", StringComparison.Ordinal);
        var continueRow = xaml.IndexOf("x:Name=\"ContinueWatchingRow\"", StringComparison.Ordinal);
        Assert.IsTrue(continueTitle >= 0 && continueTitle < continueState && continueState < continueRow);
    }

    [TestMethod]
    public async Task BrowsePages_UseSharedPageStateView_AndPlayerDoesNot()
    {
        var root = FindRepositoryRoot();
        foreach (var page in new[] { "HomePage.xaml", "LibraryPage.xaml", "SearchPage.xaml", "MediaDetailPage.xaml" })
        {
            var xaml = await File.ReadAllTextAsync(Path.Combine(root, "src", "EmbyPlayer.UI", "Pages", page));
            StringAssert.Contains(xaml, "controls:PageStateView", $"{page} must use the shared state view.");
        }

        var playerXaml = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Pages",
            "PlayerPage.xaml"));
        Assert.IsFalse(playerXaml.Contains("PageStateView", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src"))
                && Directory.Exists(Path.Combine(directory.FullName, "tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }

}
