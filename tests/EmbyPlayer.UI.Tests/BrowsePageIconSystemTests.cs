using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class BrowsePageIconSystemTests
{
    private static readonly string[] PageNames =
    {
        "LibraryPage.xaml",
        "SearchPage.xaml",
        "SettingsPage.xaml"
    };

    [TestMethod]
    public async Task BrowsePages_UseSharedIconsWithoutLegacyGlyphsOrPathData()
    {
        var repositoryRoot = FindRepositoryRoot();
        var pageDirectory = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages");
        var pages = await Task.WhenAll(PageNames.Select(
            page => File.ReadAllTextAsync(Path.Combine(pageDirectory, page))));

        foreach (var xaml in pages)
        {
            foreach (var legacyValue in new[]
                     {
                         "Segoe MDL2 Assets",
                         "Segoe Fluent Icons",
                         "FontFamily=",
                         "<Path",
                         "&#x2665;",
                         "▶",
                         "♡",
                         "♥",
                         "‹",
                         "›"
                     })
            {
                Assert.IsFalse(
                    xaml.Contains(legacyValue, StringComparison.Ordinal),
                    $"Legacy page icon remains: {legacyValue}");
            }
        }

        var sharedStateView = await File.ReadAllTextAsync(Path.Combine(
            repositoryRoot,
            "src",
            "EmbyPlayer.UI",
            "Controls",
            "PageStateView.xaml"));
        var combined = string.Concat(pages) + sharedStateView;
        foreach (var iconKey in new[]
                 {
                     "Icon.Back",
                     "Icon.Retry",
                     "Icon.Search",
                     "Icon.Empty",
                     "Icon.Error",
                     "Icon.FavoriteFilled",
                     "Icon.Watched",
                     "Icon.Account",
                     "Icon.Play",
                     "Icon.Info",
                     "Icon.Server",
                     "Icon.Database",
                     "Icon.Refresh",
                     "Icon.Reset",
                     "Icon.Logout"
                 })
        {
            StringAssert.Contains(combined, $"{{StaticResource {iconKey}}}");
        }
    }

    [TestMethod]
    public async Task BrowsePageIconMigration_PreservesCommandsAndAccessibilityContracts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var pageDirectory = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages");
        var library = await File.ReadAllTextAsync(Path.Combine(pageDirectory, "LibraryPage.xaml"));
        var search = await File.ReadAllTextAsync(Path.Combine(pageDirectory, "SearchPage.xaml"));
        var settings = await File.ReadAllTextAsync(Path.Combine(pageDirectory, "SettingsPage.xaml"));

        foreach (var command in new[] { "NavigateHomeCommand", "RetryCommand", "SelectLibraryCommand", "OpenMediaCommand" })
        {
            StringAssert.Contains(library, command);
        }

        foreach (var command in new[] { "NavigateHomeCommand", "RetryCommand", "SelectFilterCommand", "OpenResultCommand" })
        {
            StringAssert.Contains(search, command);
        }

        StringAssert.Contains(settings, "OnCategoryClick");
        foreach (var command in new[]
                 {
                     "RetrySaveCommand",
                     "RestoreDefaultsCommand",
                     "ConfirmRestoreDefaultsCommand",
                     "SaveAndLeaveCommand"
                 })
        {
            StringAssert.Contains(settings, $"Command=\"{{Binding {command}}}\"");
        }

        Assert.IsFalse(settings.Contains("OnSavePreviewClick", StringComparison.Ordinal));
        Assert.IsFalse(settings.Contains("OnRestoreDefaultsClick", StringComparison.Ordinal));
        foreach (var xaml in new[] { library, search, settings })
        {
            StringAssert.Contains(xaml, "ToolTip=");
            StringAssert.Contains(xaml, "AutomationProperties.Name=");
            StringAssert.Contains(xaml, "AutomationProperties.HelpText=");
        }
    }

    [TestMethod]
    public async Task SharedAppIcon_RemainsNonInteractiveAndNewKeysAreCentralized()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appIconPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Controls", "AppIcon.cs");
        var iconStylesPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Styles", "IconButtons.xaml");
        var iconsPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Resources", "Icons.xaml");
        var appIcon = await File.ReadAllTextAsync(appIconPath);
        var iconStyles = await File.ReadAllTextAsync(iconStylesPath);
        var icons = await File.ReadAllTextAsync(iconsPath);

        StringAssert.Contains(appIcon, "FocusableProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(false))");
        StringAssert.Contains(appIcon, "IsHitTestVisibleProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(false))");
        StringAssert.Contains(iconStyles, "<Setter Property=\"Focusable\" Value=\"False\" />");
        StringAssert.Contains(iconStyles, "<Setter Property=\"IsHitTestVisible\" Value=\"False\" />");

        foreach (var iconKey in new[]
                 {
                     "Icon.ChevronDown",
                     "Icon.Filter",
                     "Icon.Check",
                     "Icon.Empty",
                     "Icon.Error",
                     "Icon.Account",
                     "Icon.Appearance",
                     "Icon.About",
                     "Icon.Server",
                     "Icon.Database",
                     "Icon.Clear",
                     "Icon.Refresh",
                     "Icon.Save",
                     "Icon.Reset",
                     "Icon.Logout"
                 })
        {
            Assert.AreEqual(1, CountOccurrences(icons, $"x:Key=\"{iconKey}\""));
        }
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
