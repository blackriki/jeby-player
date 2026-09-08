using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class MediaCardBadgeStyleTests
{
    [TestMethod]
    public async Task AppResources_DefineUnifiedMediaCardBadgeStyles()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.App", "App.xaml");
        var xaml = await File.ReadAllTextAsync(appPath);

        StringAssert.Contains(xaml, "MediaFavoriteBadgeStyle");
        StringAssert.Contains(xaml, "MediaPlayedBadgeStyle");
        StringAssert.Contains(xaml, "MediaCardProgressBarStyle");
        StringAssert.Contains(xaml, "MediaTypeBadgeStyle");
        StringAssert.Contains(xaml, "AppPrimaryButtonStyle");
        StringAssert.Contains(xaml, "AppSecondaryButtonStyle");
        StringAssert.Contains(xaml, "AppDangerButtonStyle");
        StringAssert.Contains(xaml, "AppIconButtonStyle");
        StringAssert.Contains(xaml, "FocusVisualStyle");
    }

    [TestMethod]
    public async Task MediaPages_UseUnifiedBadgeAndProgressStyles()
    {
        var repositoryRoot = FindRepositoryRoot();
        var pagePaths = new[]
        {
            Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "HomePage.xaml"),
            Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "LibraryPage.xaml"),
            Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "SearchPage.xaml")
        };

        foreach (var pagePath in pagePaths)
        {
            var xaml = await File.ReadAllTextAsync(pagePath);
            StringAssert.Contains(xaml, "MediaFavoriteBadgeStyle");
            StringAssert.Contains(xaml, "MediaPlayedBadgeStyle");
            StringAssert.Contains(xaml, "MediaCardProgressBarStyle");
        }
    }

    [TestMethod]
    public async Task LibraryAndSearchPages_TruncateLongText()
    {
        var repositoryRoot = FindRepositoryRoot();
        var pagePaths = new[]
        {
            Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "LibraryPage.xaml"),
            Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "SearchPage.xaml")
        };

        foreach (var pagePath in pagePaths)
        {
            var xaml = await File.ReadAllTextAsync(pagePath);
            StringAssert.Contains(xaml, "TextTrimming=\"CharacterEllipsis\"");
        }
    }

    [TestMethod]
    public async Task LibraryPosterCards_DoNotRenderAChromeBorder()
    {
        var repositoryRoot = FindRepositoryRoot();
        var pagePath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "LibraryPage.xaml");
        var xaml = await File.ReadAllTextAsync(pagePath);
        var styleStart = xaml.IndexOf("<Style x:Key=\"PosterButtonStyle\"", StringComparison.Ordinal);
        var styleEnd = xaml.IndexOf("</Style>", styleStart, StringComparison.Ordinal);
        var style = xaml.Substring(styleStart, styleEnd - styleStart);

        StringAssert.Contains(style, "<Setter Property=\"BorderThickness\" Value=\"0\" />");
        StringAssert.Contains(style, "<Setter Property=\"BorderBrush\" Value=\"Transparent\" />");
        StringAssert.Contains(style, "<controls:RoundedClipBorder x:Name=\"CardChrome\"");
        StringAssert.Contains(style, "CornerRadius=\"10\"");
        Assert.IsFalse(style.Contains("Property=\"BorderBrush\" Value=\"#52B54B\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MediaCardProgress_UsesTheRoundedDetailProgressTemplate()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.App", "App.xaml");
        var homePath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "Pages", "HomePage.xaml");
        var appXaml = await File.ReadAllTextAsync(appPath);
        var homeXaml = await File.ReadAllTextAsync(homePath);

        var styleStart = appXaml.IndexOf("<Style x:Key=\"MediaCardProgressBarStyle\"", StringComparison.Ordinal);
        var styleEnd = appXaml.IndexOf("</Style>", styleStart, StringComparison.Ordinal);
        var style = appXaml.Substring(styleStart, styleEnd - styleStart);

        StringAssert.Contains(style, "CornerRadius=\"3\"");
        StringAssert.Contains(style, "x:Name=\"PART_Indicator\"");
        StringAssert.Contains(style, "RadiusX=\"3\"");
        StringAssert.Contains(homeXaml, "Height=\"3\"");
        StringAssert.Contains(homeXaml, "Style=\"{StaticResource MediaCardProgressBarStyle}\"");
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
