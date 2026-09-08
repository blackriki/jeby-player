using System.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using EmbyPlayer.UI.Pages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class MediaDetailWheelTests
{
    [TestMethod]
    public async Task SeasonSelector_ForwardsPlainWheelToOuterVerticalViewer()
    {
        var xaml = await ReadRepositoryFileAsync(
            "src", "EmbyPlayer.UI", "Pages", "MediaDetailPage.xaml");
        var code = await ReadRepositoryFileAsync(
            "src", "EmbyPlayer.UI", "Pages", "MediaDetailPage.xaml.cs");

        StringAssert.Contains(xaml, "x:Name=\"MediaDetailScrollViewer\"");
        StringAssert.Contains(xaml, "x:Name=\"SeasonScrollViewer\"");
        StringAssert.Contains(xaml, "PreviewMouseWheel=\"OnSeasonSelectorPreviewMouseWheel\"");
        StringAssert.Contains(code, "MediaDetailScrollViewer.RaiseEvent(new MouseWheelEventArgs");
        StringAssert.Contains(code, "e.Delta");
        StringAssert.Contains(code, "RoutedEvent = Mouse.MouseWheelEvent");
        Assert.IsFalse(MediaDetailPage.ShouldScrollSeasonHorizontally(ModifierKeys.None, 500));
    }

    [TestMethod]
    public void SeasonSelector_UsesShiftWheelOnlyWhenHorizontalOverflowExists()
    {
        Assert.IsTrue(MediaDetailPage.ShouldScrollSeasonHorizontally(ModifierKeys.Shift, 500));
        Assert.IsFalse(MediaDetailPage.ShouldScrollSeasonHorizontally(ModifierKeys.Shift, 0));
        Assert.IsFalse(MediaDetailPage.ShouldScrollSeasonHorizontally(ModifierKeys.None, 500));

        Assert.AreEqual(280, MediaDetailPage.CalculateSeasonHorizontalWheelTarget(400, 120, 500));
        Assert.AreEqual(0, MediaDetailPage.CalculateSeasonHorizontalWheelTarget(20, 120, 500));
        Assert.AreEqual(500, MediaDetailPage.CalculateSeasonHorizontalWheelTarget(480, -120, 500));
    }

    [TestMethod]
    public async Task EpisodeTracks_KeepPlainWheelVerticalAndUseShiftWheelHorizontally()
    {
        var xaml = await ReadRepositoryFileAsync(
            "src", "EmbyPlayer.UI", "Pages", "MediaDetailPage.xaml");
        var code = await ReadRepositoryFileAsync(
            "src", "EmbyPlayer.UI", "Pages", "MediaDetailPage.xaml.cs");

        Assert.AreEqual(2, CountOccurrences(xaml, "PreviewMouseWheel=\"OnEpisodeTrackPreviewMouseWheel\""));
        Assert.AreEqual(2, CountOccurrences(xaml, "PreviewKeyDown=\"OnEpisodeTrackPreviewKeyDown\""));
        StringAssert.Contains(code, "ShouldScrollEpisodeTrackHorizontally");
        StringAssert.Contains(code, "MediaDetailScrollViewer.RaiseEvent(new MouseWheelEventArgs");
        Assert.AreEqual(6, CountOccurrences(xaml, "Click=\"OnHorizontalRowLeftClick\""));
        Assert.AreEqual(6, CountOccurrences(xaml, "Click=\"OnHorizontalRowRightClick\""));
        Assert.AreEqual(6, CountOccurrences(xaml, "HorizontalScrollBarVisibility=\"Hidden\""));
        StringAssert.Contains(code, "FindVisualChild<ScrollViewer>(listBox)");
        Assert.IsTrue(MediaDetailPage.ShouldScrollEpisodeTrackHorizontally(ModifierKeys.Shift, 500));
        Assert.IsFalse(MediaDetailPage.ShouldScrollEpisodeTrackHorizontally(ModifierKeys.None, 500));
        Assert.IsFalse(MediaDetailPage.ShouldScrollEpisodeTrackHorizontally(ModifierKeys.Shift, 0));
    }

    [TestMethod]
    public void EpisodeTrackCentering_ClampsOffsets()
    {
        Assert.AreEqual(354, MediaDetailPage.CalculateCenteredHorizontalOffset(
            currentOffset: 300,
            itemLeft: 250,
            itemWidth: 288,
            viewportWidth: 680,
            scrollableWidth: 1200));
        Assert.AreEqual(0, MediaDetailPage.CalculateCenteredHorizontalOffset(0, 0, 48, 680, 1200));
        Assert.AreEqual(1200, MediaDetailPage.CalculateCenteredHorizontalOffset(1150, 500, 288, 680, 1200));
    }

    [TestMethod]
    public void EpisodeTrackArrows_PageAndClampInternalHorizontalOffset()
    {
        Assert.AreEqual(480, MediaDetailPage.CalculateEpisodeTrackArrowTarget(100, 400, 1, 900));
        Assert.AreEqual(0, MediaDetailPage.CalculateEpisodeTrackArrowTarget(100, 400, -1, 900));
        Assert.AreEqual(900, MediaDetailPage.CalculateEpisodeTrackArrowTarget(800, 400, 1, 900));
    }

    [TestMethod]
    public void HorizontalRowArrows_HideWithoutOverflowAndAtBothEnds()
    {
        Assert.AreEqual((false, false),
            MediaDetailPage.CalculateHorizontalRowArrowVisibility(false, 200, 500));
        Assert.AreEqual((false, false),
            MediaDetailPage.CalculateHorizontalRowArrowVisibility(true, 0, 0));
        Assert.AreEqual((false, true),
            MediaDetailPage.CalculateHorizontalRowArrowVisibility(true, 0, 500));
        Assert.AreEqual((true, true),
            MediaDetailPage.CalculateHorizontalRowArrowVisibility(true, 250, 500));
        Assert.AreEqual((true, false),
            MediaDetailPage.CalculateHorizontalRowArrowVisibility(true, 500, 500));
    }

    [TestMethod]
    public async Task DetailHorizontalRows_UseHomePagingMotionAndDoNotReintroduceLayoutFeedback()
    {
        var xaml = await ReadRepositoryFileAsync(
            "src", "EmbyPlayer.UI", "Pages", "MediaDetailPage.xaml");
        var code = await ReadRepositoryFileAsync(
            "src", "EmbyPlayer.UI", "Pages", "MediaDetailPage.xaml.cs");

        foreach (var rowName in new[]
                 {
                     "SeasonRow",
                     "EpisodeNumberRow",
                     "EpisodeCardRow",
                     "PeopleRow",
                     "ArtworkRow",
                     "SimilarRow",
                 })
        {
            StringAssert.Contains(xaml, $"x:Name=\"{rowName}\"");
        }

        foreach (var buttonName in new[]
                 {
                     "SeasonPreviousButton",
                     "SeasonNextButton",
                     "PeoplePreviousButton",
                     "PeopleNextButton",
                     "ArtworkPreviousButton",
                     "ArtworkNextButton",
                     "SimilarPreviousButton",
                     "SimilarNextButton",
                 })
        {
            StringAssert.Contains(xaml, $"x:Name=\"{buttonName}\"");
        }

        StringAssert.Contains(code, "private const double HorizontalScrollPageRatio = 0.95d;");
        StringAssert.Contains(code, "TimeSpan.FromMilliseconds(220)");
        StringAssert.Contains(code, "new CubicEase { EasingMode = EasingMode.EaseOut }");
        StringAssert.Contains(code, "HandoffBehavior.SnapshotAndReplace");
        StringAssert.Contains(code, "SystemParameters.ClientAreaAnimation");
        StringAssert.Contains(code, "CancelHorizontalScrollAnimation(scrollViewer);");
        StringAssert.Contains(code, "var currentOffset = scrollViewer.HorizontalOffset;");
        StringAssert.Contains(code, "UpdateAllHorizontalRowArrowStates();");
        Assert.IsFalse(code.Contains("UpdateLayout()", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("DispatcherTimer", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("Task.Delay", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("panel.Margin =", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("listBox.Padding =", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task EpisodeCards_PlayFromWholeCardAndUseVisualOnlyHomeOverlay()
    {
        var xaml = await ReadRepositoryFileAsync(
            "src", "EmbyPlayer.UI", "Pages", "MediaDetailPage.xaml");
        var code = await ReadRepositoryFileAsync(
            "src", "EmbyPlayer.UI", "Pages", "MediaDetailPage.xaml.cs");

        Assert.AreEqual(1, CountOccurrences(
            xaml,
            "Command=\"{Binding DataContext.PlayEpisodeCommand, RelativeSource={RelativeSource AncestorType=UserControl}}\""));
        StringAssert.Contains(xaml, "Style=\"{StaticResource EpisodeCardButtonStyle}\"");
        StringAssert.Contains(xaml, "AutomationProperties.Name=\"{Binding PlayAutomationName, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "AutomationProperties.HelpText=\"{Binding AutomationName, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "Style=\"{StaticResource CardOverlayPlayButton}\"");
        StringAssert.Contains(xaml, "Binding IsKeyboardFocusWithin, ElementName=EpisodeCardButton");
        StringAssert.Contains(xaml, "Duration=\"0:0:0.14\"");
        StringAssert.Contains(xaml, "IsHitTestVisible=\"False\"");
        StringAssert.Contains(xaml, "IsTabStop=\"False\"");
        Assert.IsFalse(xaml.Contains("Text=\"{Binding PlayActionText, Mode=OneWay}\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("CornerRadius=\"13,13,0,0\"", StringComparison.Ordinal));
        StringAssert.Contains(xaml, "<Setter Property=\"Height\" Value=\"334\" />");
        StringAssert.Contains(xaml, "Style=\"{StaticResource MediaCardProgressBarStyle}\"");
        StringAssert.Contains(code, "FindEpisodeCardActionButton(");
        StringAssert.Contains(code, "episodeCardButton.Focus();");
        Assert.AreEqual(1, CountOccurrences(
            code,
            "attachedViewModel.PlayEpisodeCommand.Execute(selectedEpisode);"));
    }

    [TestMethod]
    public async Task DetailCards_ReuseHomeCardAndArrowLanguageAcrossMetadataRows()
    {
        var detailXaml = await ReadRepositoryFileAsync(
            "src", "EmbyPlayer.UI", "Pages", "MediaDetailPage.xaml");
        var homeXaml = await ReadRepositoryFileAsync(
            "src", "EmbyPlayer.UI", "Pages", "HomePage.xaml");
        var sharedStyles = await ReadRepositoryFileAsync(
            "src", "EmbyPlayer.UI", "Styles", "IconButtons.xaml");

        StringAssert.Contains(sharedStyles, "x:Key=\"MediaRowArrowButtonStyle\"");
        StringAssert.Contains(sharedStyles, "x:Key=\"MediaSurfaceCardButtonStyle\"");
        StringAssert.Contains(sharedStyles, "CornerRadius=\"10\"");
        StringAssert.Contains(sharedStyles, "Duration=\"0:0:0.14\"");
        StringAssert.Contains(homeXaml, "BasedOn=\"{StaticResource MediaRowArrowButtonStyle}\"");
        StringAssert.Contains(homeXaml, "BasedOn=\"{StaticResource MediaSurfaceCardButtonStyle}\"");
        StringAssert.Contains(detailXaml, "BasedOn=\"{StaticResource MediaRowArrowButtonStyle}\"");
        Assert.AreEqual(2, CountOccurrences(
            detailXaml,
            "BasedOn=\"{StaticResource MediaSurfaceCardButtonStyle}\""));
        StringAssert.Contains(
            detailXaml,
            "x:Key=\"InformationalCardStyle\" TargetType=\"controls:RoundedClipBorder\"");
        Assert.AreEqual(1, CountOccurrences(
            detailXaml,
            "Style=\"{StaticResource InformationalCardStyle}\""));

        var peopleStart = detailXaml.IndexOf("x:Name=\"PeopleSection\"", StringComparison.Ordinal);
        var artworkStart = detailXaml.IndexOf("x:Name=\"ArtworkSection\"", StringComparison.Ordinal);
        Assert.IsTrue(peopleStart >= 0 && artworkStart > peopleStart);
        Assert.IsFalse(detailXaml[peopleStart..artworkStart].Contains(
            "Text=\"{Binding Type, Mode=OneWay}\"",
            StringComparison.Ordinal));

        var peopleTemplateStart = detailXaml.IndexOf("<ItemsControl.ItemTemplate>", peopleStart, StringComparison.Ordinal);
        var peopleTemplateEnd = detailXaml.IndexOf("</ItemsControl.ItemTemplate>", peopleTemplateStart, StringComparison.Ordinal);
        var artworkTemplateStart = detailXaml.IndexOf("<ItemsControl.ItemTemplate>", artworkStart, StringComparison.Ordinal);
        var artworkTemplateEnd = detailXaml.IndexOf("</ItemsControl.ItemTemplate>", artworkTemplateStart, StringComparison.Ordinal);
        Assert.IsTrue(peopleTemplateStart >= 0 && peopleTemplateEnd > peopleTemplateStart);
        Assert.IsTrue(artworkTemplateStart >= 0 && artworkTemplateEnd > artworkTemplateStart);
        StringAssert.Contains(detailXaml[peopleTemplateStart..peopleTemplateEnd], "<Button");
        StringAssert.Contains(detailXaml[peopleTemplateStart..peopleTemplateEnd], "DataContext.OpenPersonCommand");
        Assert.IsFalse(detailXaml[artworkTemplateStart..artworkTemplateEnd].Contains("<Button", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EpisodeCardKeyboardFocus_FindsTheInvokableWholeCardInsteadOfTheVisualOverlay()
    {
        RunOnStaThread(() =>
        {
            var playCommand = new RoutedCommand();
            var root = new Grid();
            var visualOnlyOverlay = new Button();
            var wholeCard = new Button { Command = playCommand };
            root.Children.Add(visualOnlyOverlay);
            root.Children.Add(wholeCard);

            Assert.AreSame(
                wholeCard,
                MediaDetailPage.FindEpisodeCardActionButton(root, playCommand));
        });
    }

    [TestMethod]
    public void FocusedEpisodeSectionOffset_PositionsAndClampsOuterViewer()
    {
        Assert.AreEqual(520, MediaDetailPage.CalculateEpisodeSectionVerticalOffset(0, 700, 180, 2000));
        Assert.AreEqual(420, MediaDetailPage.CalculateEpisodeSectionVerticalOffset(500, 100, 180, 2000));
        Assert.AreEqual(0, MediaDetailPage.CalculateEpisodeSectionVerticalOffset(20, 40, 180, 2000));
        Assert.AreEqual(2000, MediaDetailPage.CalculateEpisodeSectionVerticalOffset(1800, 600, 180, 2000));
    }

    [TestMethod]
    public void EpisodeTrackPositionGate_DeduplicatesRepeatedNotificationsAndOffsetWrites()
    {
        var gate = new EpisodeTrackPositionGate();
        gate.AdvanceGeneration();

        Assert.IsTrue(gate.TryQueue("episode-7", 1180, 1180, out var request));
        Assert.IsFalse(gate.TryQueue("episode-7", 1180, 1180, out _));
        Assert.IsFalse(gate.TryQueue("episode-7", 1180.3, 1179.7, out _));
        Assert.IsTrue(gate.IsCurrent(request));

        gate.Complete(request, applied: true);

        Assert.IsFalse(gate.TryQueue("episode-7", 1180, 1180, out _));
        Assert.IsFalse(MediaDetailPage.ShouldWriteScrollOffset(420, 420.49));
        Assert.IsTrue(MediaDetailPage.ShouldWriteScrollOffset(420, 420.5));

        Assert.IsTrue(gate.TryQueue("episode-7", 1180.6, 1180, out var resized));
        Assert.IsFalse(gate.TryQueue("episode-7", 1180.6, 1180, out _));
        gate.Complete(resized, applied: true);

        gate.AdvanceGeneration();
        Assert.IsTrue(gate.TryQueue("episode-7", 1180.6, 1180, out _));
    }

    [TestMethod]
    public void EpisodeTrackPositionGate_KeepsOnlyLatestSelectionRequest()
    {
        var gate = new EpisodeTrackPositionGate();
        gate.AdvanceGeneration();

        Assert.IsTrue(gate.TryQueue("episode-7", 1180, 1180, out var stale));
        Assert.IsTrue(gate.TryQueue("episode-8", 1180, 1180, out var latest));

        Assert.IsFalse(gate.IsCurrent(stale));
        Assert.IsTrue(gate.IsCurrent(latest));

        gate.Complete(stale, applied: true);
        Assert.IsTrue(gate.IsCurrent(latest));
        gate.Complete(latest, applied: true);
        Assert.IsFalse(gate.TryQueue("episode-8", 1180, 1180, out _));
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            throw exception;
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static async Task<string> ReadRepositoryFileAsync(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "Could not locate repository root.");
        return await File.ReadAllTextAsync(Path.Combine(new[] { directory.FullName }.Concat(pathParts).ToArray()));
    }
}
