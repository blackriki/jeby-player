using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class SearchExperienceTests
{
    [TestMethod]
    public async Task SearchPage_UsesSharedStatesIconsAndMediaCardStyles()
    {
        var xaml = await ReadRepositoryFileAsync("src", "EmbyPlayer.UI", "Pages", "SearchPage.xaml");

        StringAssert.Contains(xaml, "controls:PageStateView");
        StringAssert.Contains(xaml, "controls:LoadingSpinner");
        StringAssert.Contains(xaml, "Icon.Search");
        StringAssert.Contains(xaml, "Icon.Close");
        StringAssert.Contains(xaml, "Icon.History");
        StringAssert.Contains(xaml, "MediaCardProgressBarStyle");
        StringAssert.Contains(xaml, "controls:AuthenticatedImage");
        StringAssert.Contains(xaml, "controls:RoundedClipBorder");
        Assert.IsFalse(xaml.Contains("Path Data=", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SearchPage_UsesSharedSearchBoxAndCompactHistoryChips()
    {
        var searchPage = await ReadRepositoryFileAsync("src", "EmbyPlayer.UI", "Pages", "SearchPage.xaml");
        var homePage = await ReadRepositoryFileAsync("src", "EmbyPlayer.UI", "Pages", "HomePage.xaml");
        var appResources = await ReadRepositoryFileAsync("src", "EmbyPlayer.App", "App.xaml");

        StringAssert.Contains(appResources, "x:Key=\"AppSearchTextBoxStyle\"");
        StringAssert.Contains(searchPage, "Style=\"{StaticResource AppSearchTextBoxStyle}\"");
        StringAssert.Contains(homePage, "Style=\"{StaticResource AppSearchTextBoxStyle}\"");
        StringAssert.Contains(searchPage, "ItemsSource=\"{Binding VisibleFilters}\"");
        StringAssert.Contains(searchPage, "<WrapPanel Orientation=\"Horizontal\" />");
        StringAssert.Contains(searchPage, "HistoryChipTextButtonStyle");
        StringAssert.Contains(searchPage, "HistoryChipDeleteButtonStyle");
        StringAssert.Contains(searchPage, "x:Name=\"ResultItemsControl\"");
    }

    [TestMethod]
    public async Task SearchPage_DefinesDesktopKeyboardAndAccessibleIconActions()
    {
        var xaml = await ReadRepositoryFileAsync("src", "EmbyPlayer.UI", "Pages", "SearchPage.xaml");
        var codeBehind = await ReadRepositoryFileAsync("src", "EmbyPlayer.UI", "Pages", "SearchPage.xaml.cs");
        var mainWindow = await ReadRepositoryFileAsync("src", "EmbyPlayer.App", "MainWindow.xaml.cs");

        StringAssert.Contains(xaml, "KeyDown=\"OnSearchKeyDown\"");
        StringAssert.Contains(xaml, "ToolTip=\"清除搜索词\"");
        StringAssert.Contains(xaml, "ToolTip=\"删除此条搜索历史\"");
        StringAssert.Contains(codeBehind, "e.Key == Key.Enter");
        StringAssert.Contains(codeBehind, "e.Key == Key.Escape");
        StringAssert.Contains(mainWindow, "ModifierKeys.Control");
        StringAssert.Contains(mainWindow, "ActivateSearch()");
    }

    [TestMethod]
    public async Task PlayerPageProductFiles_AreNotPartOfSearchExperience()
    {
        var playerPage = await ReadRepositoryFileAsync("src", "EmbyPlayer.UI", "Pages", "PlayerPage.xaml");

        Assert.IsFalse(playerPage.Contains("SearchHistory", StringComparison.Ordinal));
        Assert.IsFalse(playerPage.Contains("ActivateSearch", StringComparison.Ordinal));
    }

    private static Task<string> ReadRepositoryFileAsync(params string[] segments)
    {
        return File.ReadAllTextAsync(Path.Combine(new[] { FindRepositoryRoot() }.Concat(segments).ToArray()));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
