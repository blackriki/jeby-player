using EmbyPlayer.UI.Pages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class FixedPageBackButtonXamlTests
{
    [TestMethod]
    public void ScrollScrimVisibility_UsesSmallTopThreshold()
    {
        Assert.IsFalse(ScrollScrimController.ShouldShow(0));
        Assert.IsFalse(ScrollScrimController.ShouldShow(ScrollScrimController.VisibleOffsetThreshold));
        Assert.IsTrue(ScrollScrimController.ShouldShow(ScrollScrimController.VisibleOffsetThreshold + 0.1));
    }

    [TestMethod]
    public async Task FixedBackScrims_AreNonInteractiveOverlaysWithSharedLayering()
    {
        var stylesXaml = await ReadRepositoryFileAsync("src", "EmbyPlayer.UI", "Styles", "IconButtons.xaml");
        var scrimStyle = GetElementContaining(
            stylesXaml,
            "x:Key=\"FixedPageBackScrim\"",
            "<Style",
            "</Style>");

        StringAssert.Contains(scrimStyle.Text, "TargetType=\"Border\"");
        StringAssert.Contains(scrimStyle.Text, "Property=\"Height\" Value=\"104\"");
        StringAssert.Contains(scrimStyle.Text, "Property=\"HorizontalAlignment\" Value=\"Stretch\"");
        StringAssert.Contains(scrimStyle.Text, "Property=\"VerticalAlignment\" Value=\"Top\"");
        StringAssert.Contains(scrimStyle.Text, "Property=\"IsHitTestVisible\" Value=\"False\"");
        StringAssert.Contains(scrimStyle.Text, "Property=\"Opacity\" Value=\"0\"");
        StringAssert.Contains(scrimStyle.Text, "Property=\"Panel.ZIndex\" Value=\"40\"");
        StringAssert.Contains(scrimStyle.Text, "LinearGradientBrush StartPoint=\"0,0\" EndPoint=\"0,1\"");
        StringAssert.Contains(scrimStyle.Text, "Color=\"#00000000\" Offset=\"1\"");
        Assert.IsFalse(scrimStyle.Text.Contains("Margin=", StringComparison.Ordinal));

        foreach (var pageName in new[] { "LibraryPage.xaml", "SearchPage.xaml" })
        {
            var xaml = await ReadPageAsync(pageName);
            var scrim = GetOpeningTagContaining(xaml, "x:Name=\"FixedBackScrim\"", "<Border");
            var header = GetOpeningTagContaining(xaml, "x:Name=\"FixedPageHeader\"", "<Grid");
            var scrollMarker = pageName == "LibraryPage.xaml"
                ? "x:Name=\"LibraryScrollViewer\""
                : "x:Name=\"SearchScrollViewer\"";
            var scrollMarkerIndex = xaml.IndexOf(scrollMarker, StringComparison.Ordinal);
            var scrollStart = xaml.LastIndexOf("<ScrollViewer", scrollMarkerIndex, StringComparison.Ordinal);

            Assert.IsTrue(scrim.End <= scrollStart);
            StringAssert.Contains(scrim.Text, "Grid.RowSpan=\"2\"");
            StringAssert.Contains(scrim.Text, "Style=\"{StaticResource FixedPageBackScrim}\"");
            StringAssert.Contains(header.Text, "Panel.ZIndex=\"50\"");
            Assert.IsFalse(scrim.Text.Contains("Margin=", StringComparison.Ordinal));
            Assert.IsFalse(scrim.Text.Contains("TranslateTransform", StringComparison.Ordinal));
        }

        var detailXaml = await ReadPageAsync("MediaDetailPage.xaml");
        var detailScrim = GetOpeningTagContaining(detailXaml, "x:Name=\"FixedBackScrim\"", "<Border");
        var detailBack = GetButtonContaining(detailXaml, "x:Name=\"FixedBackButton\"");
        var detailScrollStart = detailXaml.IndexOf("x:Name=\"MediaDetailScrollViewer\"", StringComparison.Ordinal);
        var detailScrollEnd = detailXaml.LastIndexOf("</ScrollViewer>", detailScrim.Start, StringComparison.Ordinal)
                              + "</ScrollViewer>".Length;

        Assert.IsTrue(detailScrollStart >= 0 && detailScrollEnd <= detailScrim.Start);
        Assert.IsTrue(detailScrim.End <= detailBack.Start);
        StringAssert.Contains(detailScrim.Text, "Visibility=\"{Binding IsContentVisible, Mode=OneWay, Converter={StaticResource BooleanToVisibilityConverter}}\"");
        StringAssert.Contains(detailScrim.Text, "Style=\"{StaticResource FixedPageBackScrim}\"");
        StringAssert.Contains(detailBack.Text, "Panel.ZIndex=\"50\"");
    }

    [TestMethod]
    public async Task ScrollHandlers_UpdateSharedScrimWithoutReplacingSearchOffsetPersistence()
    {
        var libraryXaml = await ReadPageAsync("LibraryPage.xaml");
        var libraryCode = await ReadPageAsync("LibraryPage.xaml.cs");
        var searchXaml = await ReadPageAsync("SearchPage.xaml");
        var searchCode = await ReadPageAsync("SearchPage.xaml.cs");
        var detailXaml = await ReadPageAsync("MediaDetailPage.xaml");
        var detailCode = await ReadPageAsync("MediaDetailPage.xaml.cs");

        StringAssert.Contains(libraryXaml, "ScrollChanged=\"OnScrollChanged\"");
        StringAssert.Contains(libraryCode, "scrollScrimController?.Update(e.VerticalOffset);");
        StringAssert.Contains(searchXaml, "ScrollChanged=\"OnScrollChanged\"");
        StringAssert.Contains(searchCode, "scrollScrimController?.Update(e.VerticalOffset);");
        StringAssert.Contains(searchCode, "viewModel.ScrollOffset = e.VerticalOffset;");
        StringAssert.Contains(detailXaml, "ScrollChanged=\"OnMediaDetailScrollChanged\"");
        StringAssert.Contains(detailCode, "scrollScrimController?.Update(e.VerticalOffset);");
        StringAssert.Contains(searchCode, "private readonly DoubleAnimation fadeInAnimation = CreateAnimation(1);");
        StringAssert.Contains(searchCode, "private readonly DoubleAnimation fadeOutAnimation = CreateAnimation(0);");
        Assert.AreEqual(1, CountOccurrences(searchCode, "new DoubleAnimation"));
        Assert.IsFalse(searchCode.Contains("Storyboard", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task LibraryAndSearch_BackButtonsUseFixedHeaderOutsideMainScrollViewer()
    {
        foreach (var pageName in new[] { "LibraryPage.xaml", "SearchPage.xaml" })
        {
            var xaml = await ReadPageAsync(pageName);
            var headerStart = xaml.IndexOf("x:Name=\"FixedPageHeader\"", StringComparison.Ordinal);
            var backButton = GetButtonContaining(xaml, "x:Name=\"FixedBackButton\"");
            var scrollMarker = pageName == "LibraryPage.xaml"
                ? "x:Name=\"LibraryScrollViewer\""
                : "x:Name=\"SearchScrollViewer\"";
            var scrollMarkerIndex = xaml.IndexOf(scrollMarker, StringComparison.Ordinal);
            var scrollStart = xaml.LastIndexOf("<ScrollViewer", scrollMarkerIndex, StringComparison.Ordinal);

            Assert.IsTrue(headerStart >= 0 && headerStart < backButton.Start);
            Assert.IsTrue(
                backButton.End <= scrollStart,
                $"{pageName} must close FixedBackButton before its named main ScrollViewer starts.");
            StringAssert.Contains(xaml, "<RowDefinition Height=\"Auto\" />");
            StringAssert.Contains(xaml, "<RowDefinition Height=\"*\" />");
            StringAssert.Contains(backButton.Text, "Command=\"{Binding NavigateHomeCommand}\"");
            StringAssert.Contains(backButton.Text, "ToolTip=\"\u8fd4\u56de\u9996\u9875\"");
            StringAssert.Contains(backButton.Text, "AutomationProperties.Name=\"\u8fd4\u56de\u9996\u9875\"");
            StringAssert.Contains(backButton.Text, "AutomationProperties.HelpText=\"\u8fd4\u56de\u5e94\u7528\u9996\u9875\u3002\"");
            StringAssert.Contains(backButton.Text, "Data=\"{StaticResource Icon.Back}\"");
            StringAssert.Contains(backButton.Text, "Style=\"{StaticResource PageBackButton}\"");
            StringAssert.Contains(backButton.Text, "Style=\"{StaticResource PageBackButtonIcon}\"");
            Assert.IsFalse(backButton.Text.Contains("Margin=\"-", StringComparison.Ordinal));
            Assert.IsFalse(backButton.Text.Contains("TranslateTransform", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task Search_MainScrollViewerKeepsOffsetRestorationContract()
    {
        var xaml = await ReadPageAsync("SearchPage.xaml");
        var code = await ReadPageAsync("SearchPage.xaml.cs");

        StringAssert.Contains(xaml, "x:Name=\"SearchScrollViewer\"");
        StringAssert.Contains(xaml, "ScrollChanged=\"OnScrollChanged\"");
        StringAssert.Contains(code, "SearchScrollViewer.ScrollToVerticalOffset(viewModel.ScrollOffset);");
        StringAssert.Contains(code, "viewModel.ScrollOffset = SearchScrollViewer.VerticalOffset;");
        StringAssert.Contains(code, "viewModel.ScrollOffset = e.VerticalOffset;");
    }

    [TestMethod]
    public async Task MediaDetail_NormalBackButtonOverlaysContentBelowRefreshNotices()
    {
        var xaml = await ReadPageAsync("MediaDetailPage.xaml");
        var backButton = GetButtonContaining(xaml, "x:Name=\"FixedBackButton\"");
        var scrollStart = xaml.IndexOf("x:Name=\"MediaDetailScrollViewer\"", StringComparison.Ordinal);
        var scrollEnd = xaml.LastIndexOf("</ScrollViewer>", backButton.Start, StringComparison.Ordinal);
        var scrollContent = xaml[scrollStart..scrollEnd];
        var refreshBorder = GetElementContaining(
            xaml,
            "Visibility=\"{Binding IsRefreshing, Mode=OneWay",
            "<Border",
            "</Border>");
        var refreshErrorBorder = GetElementContaining(
            xaml,
            "Visibility=\"{Binding IsRefreshErrorVisible, Mode=OneWay",
            "<Border",
            "</Border>");

        Assert.IsTrue(scrollStart >= 0 && scrollEnd > scrollStart && backButton.Start > scrollEnd);
        Assert.IsTrue(refreshBorder.Start > backButton.End);
        Assert.IsTrue(refreshErrorBorder.Start > backButton.End);
        Assert.IsFalse(scrollContent.Contains("Command=\"{Binding BackCommand}\"", StringComparison.Ordinal));
        StringAssert.Contains(backButton.Text, "Command=\"{Binding BackCommand}\"");
        StringAssert.Contains(backButton.Text, "Visibility=\"{Binding IsContentVisible, Mode=OneWay, Converter={StaticResource BooleanToVisibilityConverter}}\"");
        StringAssert.Contains(backButton.Text, "Margin=\"54,32,0,0\"");
        StringAssert.Contains(backButton.Text, "ToolTip=\"\u8fd4\u56de\u4e0a\u4e00\u9875\"");
        StringAssert.Contains(backButton.Text, "AutomationProperties.Name=\"\u8fd4\u56de\"");
        StringAssert.Contains(backButton.Text, "AutomationProperties.HelpText=\"\u8fd4\u56de\u4e0a\u4e00\u4e2a\u9875\u9762\u3002\"");
        StringAssert.Contains(backButton.Text, "Data=\"{StaticResource Icon.Back}\"");
        StringAssert.Contains(backButton.Text, "Style=\"{StaticResource PageBackButton}\"");
        StringAssert.Contains(backButton.Text, "Style=\"{StaticResource PageBackButtonIcon}\"");
        StringAssert.Contains(backButton.Text, "Panel.ZIndex=\"50\"");
        StringAssert.Contains(refreshBorder.Text, "Panel.ZIndex=\"60\"");
        StringAssert.Contains(refreshErrorBorder.Text, "Panel.ZIndex=\"60\"");
        Assert.IsFalse(backButton.Text.Contains("Margin=\"-", StringComparison.Ordinal));
        Assert.IsFalse(backButton.Text.Contains("TranslateTransform", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PlayerPage_DoesNotAdoptOrdinaryFixedBackButton()
    {
        var xaml = await ReadPageAsync("PlayerPage.xaml");

        Assert.IsFalse(xaml.Contains("x:Name=\"FixedPageHeader\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("x:Name=\"FixedBackButton\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("Style=\"{StaticResource PageBackButton}\"", StringComparison.Ordinal));
    }

    private static (int Start, int End, string Text) GetButtonContaining(string xaml, string marker)
    {
        return GetElementContaining(xaml, marker, "<Button", "</Button>");
    }

    private static (int Start, int End, string Text) GetElementContaining(
        string xaml,
        string marker,
        string openingTag,
        string closingTag)
    {
        var markerIndex = xaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.IsTrue(markerIndex >= 0, $"Missing marker: {marker}");
        var start = xaml.LastIndexOf(openingTag, markerIndex, StringComparison.Ordinal);
        var closingTagStart = xaml.IndexOf(closingTag, markerIndex, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && closingTagStart > markerIndex, $"Could not isolate element containing: {marker}");
        var end = closingTagStart + closingTag.Length;
        return (start, end, xaml[start..end]);
    }

    private static (int Start, int End, string Text) GetOpeningTagContaining(
        string xaml,
        string marker,
        string openingTag)
    {
        var markerIndex = xaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.IsTrue(markerIndex >= 0, $"Missing marker: {marker}");
        var start = xaml.LastIndexOf(openingTag, markerIndex, StringComparison.Ordinal);
        var end = xaml.IndexOf('>', markerIndex) + 1;
        Assert.IsTrue(start >= 0 && end > markerIndex, $"Could not isolate opening tag containing: {marker}");
        return (start, end, xaml[start..end]);
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0; index += token.Length)
        {
            count++;
        }

        return count;
    }

    private static async Task<string> ReadPageAsync(string pageName)
    {
        return await ReadRepositoryFileAsync("src", "EmbyPlayer.UI", "Pages", pageName);
    }

    private static async Task<string> ReadRepositoryFileAsync(params string[] segments)
    {
        var path = Path.Combine(new[] { FindRepositoryRoot() }.Concat(segments).ToArray());
        return await File.ReadAllTextAsync(path);
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

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
