using EmbyPlayer.Core.Settings;
using EmbyPlayer.UI.Controls;
using EmbyPlayer.UI.Pages;
using EmbyPlayer.UI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class PlayerPageXamlTests
{
    [TestMethod]
    public void PlayerPage_UsesFullBleedVideoAndOverlayChrome()
    {
        var xaml = File.ReadAllText(FindPlayerPageXamlPath());

        StringAssert.Contains(xaml, "x:Name=\"PlayerSurface\"");
        StringAssert.Contains(xaml, "x:Name=\"VideoArea\"");
        StringAssert.Contains(xaml, "<controls:PlayerVideoHost x:Name=\"VideoHost\" />");
        StringAssert.Contains(xaml, "x:Name=\"ControlsLayer\"");
        StringAssert.Contains(xaml, "x:Name=\"TopChrome\"");
        StringAssert.Contains(xaml, "x:Name=\"BottomChrome\"");
        StringAssert.Contains(xaml, "LinearGradientBrush");
        Assert.IsFalse(xaml.Contains("TopChromeRow", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("BottomChromeRow", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OverlayTopChrome_UsesAuthenticatedLogoFallbackAndWindowControls()
    {
        var xaml = File.ReadAllText(FindPlayerPageXamlPath());
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());
        var topChromeStart = xaml.IndexOf("x:Name=\"TopChrome\"", StringComparison.Ordinal);
        var bottomChromeStart = xaml.IndexOf("x:Name=\"BottomChrome\"", topChromeStart, StringComparison.Ordinal);
        var topChrome = xaml[topChromeStart..bottomChromeStart];
        var minimizeHandler = GetMethod(
            code,
            "private void OnMinimizeWindowButtonClick",
            "internal static bool TryMinimizePlayerWindow");
        var minimizeStart = topChrome.IndexOf("x:Name=\"MinimizeWindowButton\"", StringComparison.Ordinal);
        var minimizeEnd = topChrome.IndexOf("</Button>", minimizeStart, StringComparison.Ordinal);
        var minimizeButton = topChrome[minimizeStart..minimizeEnd];
        var closeStart = topChrome.IndexOf("x:Name=\"CloseWindowButton\"", StringComparison.Ordinal);
        var closeEnd = topChrome.IndexOf("</Button>", closeStart, StringComparison.Ordinal);
        var closeButton = topChrome[closeStart..closeEnd];
        var backStart = topChrome.IndexOf("Command=\"{Binding BackCommand}\"", StringComparison.Ordinal);
        var logoStart = topChrome.IndexOf("x:Name=\"OverlayPlayerLogo\"", StringComparison.Ordinal);
        var titleStart = topChrome.IndexOf("Text=\"{Binding Title, Mode=OneWay}\"", StringComparison.Ordinal);

        StringAssert.Contains(topChrome, "Height=\"111\"");
        StringAssert.Contains(topChrome, "Height=\"44\" Margin=\"24,23,18,0\"");
        Assert.AreEqual(2, CountOccurrences(topChrome, "<ColumnDefinition Width=\"16\" />"));
        Assert.IsTrue(backStart >= 0 && backStart < logoStart && logoStart < titleStart && titleStart < minimizeStart && minimizeStart < closeStart);
        StringAssert.Contains(topChrome, "x:Name=\"OverlayPlayerLogo\"");
        StringAssert.Contains(topChrome, "ImageUrl=\"{Binding LogoUrl, Mode=OneWay}\"");
        StringAssert.Contains(topChrome, "Height=\"30\"");
        StringAssert.Contains(topChrome, "MaxWidth=\"150\"");
        StringAssert.Contains(topChrome, "MaxHeight=\"30\"");
        StringAssert.Contains(topChrome, "<Image Width=\"22\"");
        StringAssert.Contains(topChrome, "Stretch=\"Uniform\"");
        StringAssert.Contains(topChrome, "Owner.Icon");
        StringAssert.Contains(topChrome, "Binding IsImageLoaded, ElementName=OverlayPlayerLogo");
        Assert.IsFalse(topChrome.Contains("<ColumnDefinition Width=\"180\"", StringComparison.Ordinal));
        StringAssert.Contains(topChrome, "x:Name=\"MinimizeWindowButton\"");
        StringAssert.Contains(topChrome, "<StackPanel Grid.Column=\"5\" Orientation=\"Horizontal\">");
        StringAssert.Contains(minimizeButton, "Width=\"44\"");
        StringAssert.Contains(minimizeButton, "Height=\"44\"");
        StringAssert.Contains(minimizeButton, "Style=\"{StaticResource PlayerIconButton}\"");
        StringAssert.Contains(minimizeButton, "ToolTip=\"最小化\"");
        StringAssert.Contains(minimizeButton, "AutomationProperties.Name=\"最小化\"");
        StringAssert.Contains(minimizeButton, "AutomationProperties.AutomationId=\"MinimizePlayerWindowButton\"");
        StringAssert.Contains(minimizeButton, "Data=\"{StaticResource Icon.WindowMinimize}\"");
        StringAssert.Contains(minimizeButton, "Click=\"OnMinimizeWindowButtonClick\"");
        StringAssert.Contains(minimizeButton, "Size=\"20\"");
        StringAssert.Contains(topChrome, "x:Name=\"CloseWindowButton\"");
        StringAssert.Contains(closeButton, "Grid.Column=\"6\"");
        StringAssert.Contains(closeButton, "Width=\"44\"");
        StringAssert.Contains(closeButton, "Height=\"44\"");
        StringAssert.Contains(closeButton, "Size=\"20\"");
        StringAssert.Contains(topChrome, "ToolTip=\"关闭\"");
        StringAssert.Contains(topChrome, "AutomationProperties.Name=\"关闭\"");
        StringAssert.Contains(topChrome, "Data=\"{StaticResource Icon.Close}\"");
        StringAssert.Contains(topChrome, "Click=\"OnCloseWindowButtonClick\"");
        Assert.IsFalse(closeButton.Contains("Command=\"{Binding BackCommand}\"", StringComparison.Ordinal));
        Assert.IsFalse(closeButton.Contains("Margin=\"-", StringComparison.Ordinal));
        Assert.IsFalse(closeButton.Contains("TranslateTransform", StringComparison.Ordinal));
        StringAssert.Contains(code, "GetPlayerWindowHost()?.ClosePlayerWindow();");
        StringAssert.Contains(code, "TryMinimizePlayerWindow(GetPlayerWindowHost());");
        StringAssert.Contains(minimizeHandler, "TryMinimizePlayerWindow(GetPlayerWindowHost())");
        foreach (var disallowedOperation in new[] { "DataContext", "Pause", "Stop", "Back", "ToggleFullscreen", "ExitFullscreen", "WindowState" })
        {
            Assert.IsFalse(minimizeHandler.Contains(disallowedOperation, StringComparison.Ordinal));
        }

        Assert.IsFalse(code.Contains("controlsOverlayWindow?.Owner?.Close()", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MinimizeRequest_ChangesOnlyTheHostWindowState()
    {
        RunOnStaThread(() =>
        {
            var host = new TestPlayerWindowHost
            {
                WindowState = WindowState.Maximized,
            };

            Assert.IsTrue(PlayerPage.TryMinimizePlayerWindow(host));

            Assert.AreEqual(WindowState.Minimized, host.WindowState);
            Assert.AreEqual(1, host.MinimizeCallCount);
            Assert.AreEqual(0, host.CloseCallCount);
            Assert.AreEqual(0, host.CaptionStateCallCount);
            Assert.IsFalse(PlayerPage.TryMinimizePlayerWindow(null));
        });
    }

    [TestMethod]
    public void CaptionStateRequest_SupportsCaptionOnlyHostAcrossPlayerLifecycle()
    {
        RunOnStaThread(() =>
        {
            var host = new TestPlayerCaptionHost();

            Assert.IsTrue(PlayerPage.TrySetPlayerCaptionState(host, isVisible: false, isFullscreen: false));
            Assert.IsTrue(PlayerPage.TrySetPlayerCaptionState(host, isVisible: false, isFullscreen: true));
            Assert.IsTrue(PlayerPage.TrySetPlayerCaptionState(host, isVisible: true, isFullscreen: false));

            Assert.AreEqual(3, host.CaptionStates.Count);
            Assert.AreEqual((false, false), host.CaptionStates[0]);
            Assert.AreEqual((false, true), host.CaptionStates[1]);
            Assert.AreEqual((true, false), host.CaptionStates[2]);
            Assert.IsFalse(PlayerPage.TrySetPlayerCaptionState(null, isVisible: true, isFullscreen: false));
        });
    }

    [TestMethod]
    public void OverlayWindowInteraction_UsesEightDipEdgesBelowInteractiveControls()
    {
        var xaml = File.ReadAllText(FindPlayerPageXamlPath());
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());
        var resizeStart = xaml.IndexOf("x:Name=\"WindowResizeHitZones\"", StringComparison.Ordinal);
        var controlsStart = xaml.IndexOf("x:Name=\"ControlsLayer\"", resizeStart, StringComparison.Ordinal);
        var resizeLayer = xaml[resizeStart..controlsStart];

        Assert.IsTrue(resizeStart >= 0 && resizeStart < controlsStart);
        StringAssert.Contains(resizeLayer, "Panel.ZIndex=\"5\"");
        StringAssert.Contains(resizeLayer, "Focusable=\"False\"");
        StringAssert.Contains(resizeLayer, "KeyboardNavigation.TabNavigation=\"None\"");
        StringAssert.Contains(resizeLayer, "Height=\"103\" Margin=\"8,8,8,0\"");
        StringAssert.Contains(resizeLayer, "Tag=\"Caption\"");
        foreach (var tag in new[]
                 {
                     "Top", "Bottom", "Left", "Right",
                     "TopLeft", "TopRight", "BottomLeft", "BottomRight"
                 })
        {
            StringAssert.Contains(resizeLayer, $"Tag=\"{tag}\"");
        }

        Assert.AreEqual(4, CountOccurrences(resizeLayer, "Width=\"8\" Height=\"8\""));
        Assert.AreEqual(2, CountOccurrences(resizeLayer, "Height=\"8\" Margin=\"8,0\""));
        Assert.AreEqual(2, CountOccurrences(resizeLayer, "Width=\"8\" Margin=\"0,8\""));
        Assert.AreEqual(2, CountOccurrences(resizeLayer, "Cursor=\"SizeWE\""));
        Assert.AreEqual(2, CountOccurrences(resizeLayer, "Cursor=\"SizeNS\""));
        Assert.AreEqual(2, CountOccurrences(resizeLayer, "Cursor=\"SizeNWSE\""));
        Assert.AreEqual(2, CountOccurrences(resizeLayer, "Cursor=\"SizeNESW\""));
        StringAssert.Contains(xaml[controlsStart..], "Panel.ZIndex=\"10\"");
        StringAssert.Contains(xaml, "PreviewMouseLeftButtonDown=\"OnTopChromePreviewMouseLeftButtonDown\"");
        StringAssert.Contains(code, "PlayerWindowHitTarget.Caption");
        StringAssert.Contains(code, "target, IsPlayerFullscreen, e.ClickCount");
        StringAssert.Contains(code, "current is ButtonBase or Thumb or Slider");
        StringAssert.Contains(code, "owner.WindowState");
        StringAssert.Contains(code, "owner.ResizeMode");
        Assert.IsFalse(code.Contains("ReleaseCapture", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("SendMessage", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PlayerPage_UsesSharedPlayerIconsAndRealCommands()
    {
        var xaml = File.ReadAllText(FindPlayerPageXamlPath());

        StringAssert.Contains(xaml, "{StaticResource Icon.Back}");
        StringAssert.Contains(xaml, "{StaticResource Icon.Play}");
        StringAssert.Contains(xaml, "{StaticResource Icon.Pause}");
        StringAssert.Contains(xaml, "{StaticResource Icon.VolumeHigh}");
        StringAssert.Contains(xaml, "{StaticResource Icon.Subtitle}");
        StringAssert.Contains(xaml, "{StaticResource Icon.AudioTrack}");
        StringAssert.Contains(xaml, "{StaticResource Icon.FullscreenEnter}");
        StringAssert.Contains(xaml, "{StaticResource Icon.Close}");
        StringAssert.Contains(xaml, "Command=\"{Binding BackCommand}\"");
        StringAssert.Contains(xaml, "Command=\"{Binding TogglePlayPauseCommand}\"");
        StringAssert.Contains(xaml, "Command=\"{Binding ToggleMuteCommand}\"");
        StringAssert.Contains(xaml, "Command=\"{Binding SkipSegmentCommand}\"");
        StringAssert.Contains(xaml, "Command=\"{Binding PlayNextEpisodeCommand}\"");
        Assert.IsFalse(xaml.Contains("PlayPauseGlyph", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("MuteGlyph", StringComparison.Ordinal));
        StringAssert.Contains(xaml, "CornerRadius=\"24\"");
        StringAssert.Contains(xaml, "CornerRadius=\"38\"");
        StringAssert.Contains(xaml, "Text=\"{Binding MediaMetadataText, Mode=OneWay}\"");
    }

    [TestMethod]
    public void SeekSlider_PreservesRealBindingAndExpandedHitArea()
    {
        var xaml = File.ReadAllText(FindPlayerPageXamlPath());

        StringAssert.Contains(xaml, "x:Name=\"SeekHitArea\"");
        StringAssert.Contains(xaml, "<controls:SettingsSlider x:Name=\"SeekSlider\"");
        StringAssert.Contains(xaml, "Value=\"{Binding SeekPercent, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"");
        StringAssert.Contains(xaml, "IsEnabled=\"{Binding CanSeek, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "Height=\"14\" Margin=\"6,0\"");
        StringAssert.Contains(xaml, "<Grid Height=\"34\" Background=\"Transparent\">");
        StringAssert.Contains(xaml, "Width\" Value=\"12\"");
        Assert.IsFalse(xaml.Contains("OnSeekHitAreaMouseLeftButtonUp", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TrackAndVolumeMenus_UseMutuallyExclusivePopups()
    {
        var xaml = File.ReadAllText(FindPlayerPageXamlPath());
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());
        var volumeCompleted = GetMethod(
            code,
            "private async void OnVolumeDragCompleted",
            "internal static bool TryCompleteVolumeDrag");
        var volumeValueChanged = GetMethod(
            code,
            "private void OnVolumeSliderValueChanged",
            "private void OnSeekSliderValueChanged");

        foreach (var name in new[] { "VolumePopup", "SubtitlePopup", "AudioPopup", "QualityPopup", "PlaybackInfoPopup", "QueuePopup" })
        {
            StringAssert.Contains(xaml, $"x:Name=\"{name}\"");
        }

        Assert.AreEqual(1, CountOccurrences(xaml, "StaysOpen=\"True\""));
        Assert.AreEqual(7, CountOccurrences(xaml, "StaysOpen=\"False\""));
        StringAssert.Contains(xaml, "PreviewMouseLeftButtonDown=\"OnOverlayRootPreviewMouseLeftButtonDown\"");
        StringAssert.Contains(xaml, "Opened=\"OnPopupOpened\"");
        StringAssert.Contains(xaml, "Closed=\"OnPopupClosed\"");
        StringAssert.Contains(xaml, "ItemsSource=\"{Binding SubtitleTracks, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "ItemsSource=\"{Binding AudioTracks, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "Value=\"{Binding Volume, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "<controls:SettingsSlider x:Name=\"VolumeSlider\"");
        StringAssert.Contains(xaml, "Orientation\" Value=\"Vertical\"");
        StringAssert.Contains(xaml, "Width=\"62\" Height=\"198\"");
        StringAssert.Contains(xaml, "x:Key=\"PlayerVolumeIconStyle\"");
        StringAssert.Contains(xaml, "DataTrigger Binding=\"{Binding Volume}\" Value=\"0\"");
        Assert.AreEqual(2, CountOccurrences(xaml, "Style=\"{StaticResource PlayerVolumeIconStyle}\""));
        StringAssert.Contains(xaml, "Binding IsSelected");
        StringAssert.Contains(xaml, "Value=\"#253D2A\"");
        StringAssert.Contains(code, "VolumeSlider.TrackDragStarted += OnVolumeDragStarted;");
        StringAssert.Contains(code, "VolumeSlider.TrackDragCompleted += OnVolumeDragCompleted;");
        StringAssert.Contains(volumeCompleted, "TryCompleteVolumeDrag(VolumeSlider, volumeBeforeDrag, e.Canceled, out var valueToCommit)");
        StringAssert.Contains(volumeCompleted, "await viewModel.SetVolumeAsync(valueToCommit).ConfigureAwait(true);");
        Assert.AreEqual(1, CountOccurrences(volumeCompleted, "SetVolumeAsync("));
        Assert.IsFalse(volumeValueChanged.Contains("SetVolumeAsync", StringComparison.Ordinal));
        StringAssert.Contains(code, "FindAncestor<Slider>(source)");
        Assert.IsFalse(xaml.Contains("OnVolumeSliderMouseLeftButtonUp", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("ignoreNextVolumeClick", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("ContextMenu", StringComparison.Ordinal));
    }

    [TestMethod]
    public void VolumePopupLifecycle_ClosesWithoutTakingTheOverlayPress()
    {
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());
        var overlayPointerDown = GetMethod(
            code,
            "private void OnOverlayRootPreviewMouseLeftButtonDown",
            "internal static bool ShouldCloseVolumePopupBeforeOverlayInput");
        var unloaded = GetMethod(
            code,
            "private async void OnUnloaded",
            "private void OnDataContextChanged");
        var closeOverlay = GetMethod(
            code,
            "private void CloseControlsOverlayWindow",
            "private void OnOverlayOwnerBoundsChanged");
        var ownerStateChanged = GetMethod(
            code,
            "private void OnOverlayOwnerStateChanged",
            "private void SubscribeToApplicationDeactivation");
        var subscribeApplication = GetMethod(
            code,
            "private void SubscribeToApplicationDeactivation",
            "private void UnsubscribeFromApplicationDeactivation");
        var unsubscribeApplication = GetMethod(
            code,
            "private void UnsubscribeFromApplicationDeactivation",
            "private void OnApplicationDeactivated");
        var applicationDeactivated = GetMethod(
            code,
            "private void OnApplicationDeactivated",
            "private void OnOverlayOwnerClosed");

        StringAssert.Contains(overlayPointerDown, "ClosePopups();");
        Assert.IsFalse(overlayPointerDown.Contains("e.Handled", StringComparison.Ordinal));
        StringAssert.Contains(unloaded, "ClosePopups();");
        StringAssert.Contains(closeOverlay, "UnsubscribeFromApplicationDeactivation();");
        StringAssert.Contains(closeOverlay, "ClosePopups();");
        StringAssert.Contains(ownerStateChanged, "WindowState.Minimized");
        StringAssert.Contains(ownerStateChanged, "ClosePopups();");
        StringAssert.Contains(subscribeApplication, "Application.Current");
        StringAssert.Contains(subscribeApplication, "applicationDeactivationSource is not null");
        StringAssert.Contains(subscribeApplication, "Deactivated += OnApplicationDeactivated;");
        StringAssert.Contains(unsubscribeApplication, "Deactivated -= OnApplicationDeactivated;");
        StringAssert.Contains(unsubscribeApplication, "applicationDeactivationSource = null;");
        StringAssert.Contains(applicationDeactivated, "ClosePopups();");
        Assert.IsFalse(code.Contains("owner.Deactivated", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("controlsOverlayWindow.Deactivated", StringComparison.Ordinal));
    }

    [TestMethod]
    public void VolumeDragCompletion_CommitsReleaseAndRestoresCanceledPreview()
    {
        RunOnStaThread(() =>
        {
            var slider = new SettingsSlider
            {
                Minimum = 0,
                Maximum = 100,
                Value = 70,
            };

            Assert.IsTrue(PlayerPage.TryCompleteVolumeDrag(
                slider,
                valueBeforeDrag: 30,
                canceled: false,
                out var releasedValue));
            Assert.AreEqual(70d, releasedValue);
            Assert.AreEqual(70d, slider.Value);

            Assert.IsFalse(PlayerPage.TryCompleteVolumeDrag(
                slider,
                valueBeforeDrag: 30,
                canceled: true,
                out var canceledValue));
            Assert.AreEqual(30d, canceledValue);
            Assert.AreEqual(30d, slider.Value);
        });
    }

    [TestMethod]
    public void VolumePopupPointerRouting_RealPopupKeepsInternalInputAndDoesNotConsumeFirstSeekPress()
    {
        RunOnStaThread(() =>
        {
            var overlay = new Grid();
            var seekSlider = new PreviewMouseDownProbeSlider
            {
                Minimum = 0,
                Maximum = 100,
                Height = 32,
            };
            var volumeButton = new Button();
            overlay.Children.Add(seekSlider);
            overlay.Children.Add(volumeButton);

            var volumeSlider = new PreviewMouseDownProbeSlider
            {
                Minimum = 0,
                Maximum = 100,
                Orientation = Orientation.Vertical,
                Width = 32,
                Height = 120,
            };
            var popupAction = new Button { Content = "Popup action" };
            var popupInlineText = new TextBlock();
            var popupRun = new Run("Popup run");
            var popupHyperlink = new Hyperlink(new Run("Popup hyperlink"));
            popupInlineText.Inlines.Add(popupRun);
            popupInlineText.Inlines.Add(popupHyperlink);
            var popupStack = new StackPanel();
            popupStack.Children.Add(volumeSlider);
            popupStack.Children.Add(popupAction);
            popupStack.Children.Add(popupInlineText);
            var popupContent = new Border { Child = popupStack };
            var volumePopup = new Popup
            {
                PlacementTarget = volumeButton,
                Placement = PlacementMode.Top,
                StaysOpen = true,
                AllowsTransparency = true,
                Child = popupContent,
            };
            overlay.Children.Add(volumePopup);

            var externalText = new TextBlock();
            var externalRun = new Run("Overlay run");
            externalText.Inlines.Add(externalRun);
            overlay.Children.Add(externalText);

            var overlayPreviewCount = 0;
            overlay.PreviewMouseLeftButtonDown += (_, e) =>
            {
                overlayPreviewCount++;
                if (PlayerPage.ShouldCloseVolumePopupBeforeOverlayInput(
                        volumePopup.IsOpen,
                        e.OriginalSource as DependencyObject,
                        volumeButton,
                        volumePopup.Child))
                {
                    volumePopup.IsOpen = false;
                }
            };

            var window = new Window
            {
                Width = 320,
                Height = 220,
                Left = -10000,
                Top = -10000,
                ShowActivated = false,
                ShowInTaskbar = false,
                Content = overlay,
            };

            try
            {
                window.Show();
                volumePopup.IsOpen = true;
                PumpDispatcher();

                Assert.IsNotNull(
                    PresentationSource.FromVisual(popupContent),
                    "The regression must exercise an opened Popup presentation source.");

                RaisePreviewLeftButtonDown(volumeSlider);

                Assert.IsTrue(volumePopup.IsOpen, "Volume track input must keep its Popup open.");
                Assert.AreEqual(1, volumeSlider.PreviewMouseLeftButtonDownCount);
                Assert.AreEqual(1, overlayPreviewCount, "Popup input must reach its logical OverlayRoot route.");

                RaisePreviewLeftButtonDown(popupAction);

                Assert.IsTrue(volumePopup.IsOpen, "Other content inside the Popup must stay interactive.");
                Assert.AreEqual(2, overlayPreviewCount);

                RaisePreviewLeftButtonDown(popupRun);
                RaisePreviewLeftButtonDown(popupHyperlink);

                Assert.IsTrue(volumePopup.IsOpen, "Popup Run and Hyperlink content must stay internal.");
                Assert.AreEqual(4, overlayPreviewCount);

                RaisePreviewLeftButtonDown(seekSlider);

                Assert.IsFalse(volumePopup.IsOpen, "The Popup must close before the seek target handles the same press.");
                Assert.AreEqual(1, seekSlider.PreviewMouseLeftButtonDownCount);
                Assert.AreEqual(5, overlayPreviewCount);

                volumePopup.IsOpen = true;
                var externalRunPreviewCount = 0;
                externalRun.PreviewMouseLeftButtonDown += (_, _) => externalRunPreviewCount++;

                RaisePreviewLeftButtonDown(externalRun);

                Assert.IsFalse(volumePopup.IsOpen, "Inline content outside the Popup must close it.");
                Assert.AreEqual(1, externalRunPreviewCount, "The same unhandled press must continue to its target.");
                Assert.AreEqual(6, overlayPreviewCount);

                volumePopup.IsOpen = true;
                var triggerCloseCount = 0;
                volumeButton.PreviewMouseLeftButtonDown += (_, e) =>
                {
                    triggerCloseCount++;
                    volumePopup.IsOpen = false;
                    e.Handled = true;
                };

                RaisePreviewLeftButtonDown(volumeButton);

                Assert.IsFalse(volumePopup.IsOpen);
                Assert.AreEqual(1, triggerCloseCount, "The existing trigger path must close exactly once.");
                Assert.AreEqual(7, overlayPreviewCount);
            }
            finally
            {
                volumePopup.IsOpen = false;
                window.Close();
                PumpDispatcher();
            }
        });
    }

    [TestMethod]
    public void NextEpisodePrompt_PreservesCommandsAndAutomation()
    {
        var xaml = File.ReadAllText(FindPlayerPageXamlPath());

        StringAssert.Contains(xaml, "x:Name=\"NextEpisodeCard\"");
        StringAssert.Contains(xaml, "Binding IsNextEpisodePromptVisible");
        StringAssert.Contains(xaml, "Text=\"{Binding NextEpisode.Title");
        StringAssert.Contains(xaml, "Command=\"{Binding PlayNextEpisodeCommand}\"");
        StringAssert.Contains(xaml, "Command=\"{Binding CancelAutoPlayNextEpisodeCommand}\"");
        StringAssert.Contains(xaml, "AutoPlayNextEpisodeCountdown");
        StringAssert.Contains(xaml, "Text=\"{Binding NextEpisodeStatusText, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "CancelAutoPlayNextEpisodeButton");
        StringAssert.Contains(xaml, "Value=\"{Binding AutoPlayCountdownProgress, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "Width=\"376\"");
        StringAssert.Contains(xaml, "MaxWidth=\"380\"");
        StringAssert.Contains(xaml, "Height=\"96\"");
        StringAssert.Contains(xaml, "HorizontalAlignment=\"Right\"");
        StringAssert.Contains(xaml, "Value=\"0,0,28,132\"");
        Assert.IsFalse(xaml.Contains("Value=\"0,0,28,34\"", StringComparison.Ordinal));
        StringAssert.Contains(xaml, "BorderBrush=\"#28FFFFFF\"");
        StringAssert.Contains(xaml, "ColumnDefinition Width=\"34\"");
        StringAssert.Contains(xaml, "x:Key=\"NextEpisodeCardActivation\"");
        StringAssert.Contains(xaml, "Foreground\" Value=\"#2852B54B\"");
        StringAssert.Contains(xaml, "ValueChanged=\"OnNextEpisodeActivationValueChanged\"");
        StringAssert.Contains(xaml, "ToolTip=\"继续观看片尾\"");
        StringAssert.Contains(xaml, "AutomationProperties.Name=\"继续观看片尾\"");
        var cancelButtonStart = xaml.IndexOf(
            "AutomationProperties.AutomationId=\"CancelAutoPlayNextEpisodeButton\"",
            StringComparison.Ordinal);
        Assert.IsTrue(cancelButtonStart >= 0);
        var cancelButtonTagStart = xaml.LastIndexOf("<Button", cancelButtonStart, StringComparison.Ordinal);
        var cancelButtonTagEnd = xaml.IndexOf('>', cancelButtonStart);
        var cancelButtonTag = xaml[cancelButtonTagStart..cancelButtonTagEnd];
        Assert.IsFalse(cancelButtonTag.Contains("Visibility=", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("NextEpisodeCountdownProgress", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("Text=\"即将播放下一集\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("Content=\"取消\"", StringComparison.Ordinal));
        var normalizedXaml = xaml.Replace("\r\n", "\n", StringComparison.Ordinal);
        var controlsLayerEnd = normalizedXaml.IndexOf(
            "</Grid>\n\n    <Button x:Name=\"SkipSegmentButton\"",
            StringComparison.Ordinal);
        Assert.IsTrue(controlsLayerEnd >= 0, "Timed action overlays must be outside the fading ControlsLayer.");
    }

    [TestMethod]
    public void SkipSegmentAction_UsesRealCommandAndAccessibleIndependentOverlayButton()
    {
        var xaml = File.ReadAllText(FindPlayerPageXamlPath());

        StringAssert.Contains(xaml, "x:Key=\"SkipSegmentButton\"");
        StringAssert.Contains(xaml, "x:Name=\"SkipSegmentButton\"");
        StringAssert.Contains(xaml, "Binding IsSkipSegmentVisible");
        StringAssert.Contains(xaml, "Command=\"{Binding SkipSegmentCommand}\"");
        StringAssert.Contains(xaml, "Text=\"{Binding SkipSegmentButtonText, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "ToolTip=\"{Binding SkipSegmentButtonText, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "AutomationProperties.Name=\"{Binding SkipSegmentButtonText, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "AutomationProperties.AutomationId=\"SkipPlaybackSegmentButton\"");
        StringAssert.Contains(xaml, "Value=\"0,0,28,132\"");
        Assert.IsFalse(xaml.Contains("Value=\"0,0,28,34\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeBehind_DefersAutoHideForInteractionAndPopupState()
    {
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());

        StringAssert.Contains(code, "IsAnyPopupOpen");
        StringAssert.Contains(code, "ShouldDeferControlAutoHide");
        StringAssert.Contains(code, "isSeekDragging");
        StringAssert.Contains(code, "isVolumeDragging");
        StringAssert.Contains(code, "isPointerOverControls");
        StringAssert.Contains(code, "isPointerOverPopup");
        StringAssert.Contains(code, "TogglePopup(VolumePopup)");
        StringAssert.Contains(code, "TogglePopup(SubtitlePopup)");
        StringAssert.Contains(code, "TogglePopup(AudioPopup)");
        var speedCode = File.ReadAllText(Path.Combine(Path.GetDirectoryName(FindPlayerPageCodeBehindPath())!, "PlayerPage.Speed.cs"));
        StringAssert.Contains(speedCode, "TogglePlayPauseCommand.Execute(null)");
        StringAssert.Contains(code, "RestorePlaybackStateAfterDoubleClickAsync");
        StringAssert.Contains(code, "FocusOverlaySurface");
        Assert.IsFalse(code.Contains("MouseMove detected\");\n        ClosePopups", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CursorAutoHide_AppliesToWindowedAndFullscreenPlayback()
    {
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());

        StringAssert.Contains(code, "if (!viewModel.IsControlsOverlayVisible && IsPointerWithinPlayer())");
        StringAssert.Contains(code, "IsPlaying: true");
        StringAssert.Contains(code, "IsPaused: false");
        StringAssert.Contains(code, "StartCursorActivityMonitor();");
        StringAssert.Contains(code, "ShowControlsFromPointerActivity(force: true);");
        Assert.IsFalse(code.Contains("viewModel.IsFullscreen && !viewModel.IsControlsOverlayVisible", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CursorAutoHide_RestoresAtPlayerAndOwnerLifecycleBoundaries()
    {
        var xaml = File.ReadAllText(FindPlayerPageXamlPath());
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());

        StringAssert.Contains(xaml, "MouseLeave=\"OnPlayerMouseLeave\"");
        StringAssert.Contains(code, "SubscribeToApplicationDeactivation();");
        StringAssert.Contains(code, "UnsubscribeFromApplicationDeactivation();");
        var applicationDeactivated = GetMethod(
            code,
            "private void OnApplicationDeactivated",
            "private void OnOverlayOwnerClosed");
        StringAssert.Contains(applicationDeactivated, "RestoreMouseCursor();");
        StringAssert.Contains(code, "private void RestoreMouseCursor()");
        StringAssert.Contains(code, "StopCursorActivityMonitor();");
        StringAssert.Contains(code, "ShowMouseCursor();");
    }

    [TestMethod]
    public void CursorAutoHide_UsesExistingInteractionGatesAndPreservesFullscreenFlow()
    {
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());

        StringAssert.Contains(code, "if (ShouldDeferControlAutoHide())");
        StringAssert.Contains(code, "IsAnyPopupOpen");
        StringAssert.Contains(code, "isSeekDragging");
        StringAssert.Contains(code, "isVolumeDragging");
        StringAssert.Contains(code, "controller.Toggle();");
        StringAssert.Contains(code, "SyncFullscreenState(controller.IsFullscreen);");
        StringAssert.Contains(code, "fullscreenController?.Exit();");
    }

    [TestMethod]
    public void ControlsVisibility_OnlyTargetsOverlayChromeWithoutNativeVideoMutation()
    {
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());

        StringAssert.Contains(code, "var shouldShowChrome = viewModel.IsControlsOverlayVisible || viewModel.IsPaused || IsAnyPopupOpen;");
        StringAssert.Contains(code, "SetPlayerCaptionState(shouldShowChrome, viewModel.IsFullscreen);");
        StringAssert.Contains(code, "GetPlayerOwnerWindow() as IPlayerCaptionHost");
        StringAssert.Contains(code, "GetPlayerOwnerWindow() as IPlayerWindowHost");
        StringAssert.Contains(code, "host.SetPlayerCaptionState(isVisible, isFullscreen);");
        StringAssert.Contains(code, "ControlsLayer.Visibility = Visibility.Visible;");
        StringAssert.Contains(code, "ControlsLayer.IsHitTestVisible = shouldShow;");
        StringAssert.Contains(code, "ControlsLayer.BeginAnimation(OpacityProperty, animation);");
        Assert.IsFalse(code.Contains("VideoHost.Margin", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("ApplyVideoHostCaptionInset", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("PlayerWindowChromeController", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("HideWindowChrome", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("RestoreWindowChrome", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("SetWindowLong", StringComparison.Ordinal));

        var unload = GetMethod(code, "private async void OnUnloaded", "private void OnDataContextChanged");
        StringAssert.Contains(unload, "ExitFullscreen();");
        StringAssert.Contains(unload, "SetPlayerCaptionState(isVisible: true, isFullscreen: false);");
        var exitFullscreen = GetMethod(code, "private void ExitFullscreen", "private void SyncFullscreenState");
        StringAssert.Contains(exitFullscreen, "fullscreenController?.Exit();");
    }

    [TestMethod]
    public void RapidSpaceDuringFadeOut_ReversesToOneStableVisibleTarget()
    {
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());
        var keyHandler = GetMethod(
            code,
            "private async void OnPreviewKeyDown",
            "private void OnPlayerMouseMove");
        var applyVisibility = GetMethod(
            code,
            "private void ApplyFullscreenChromeVisibility",
            "private bool IsAnyPopupOpen");
        var beginActivity = GetMethod(
            code,
            "private void BeginVisibilityShortcutActivity",
            "private void EndVisibilityShortcutActivity");
        var beginAnimation = GetMethod(
            code,
            "private void BeginControlsOpacityAnimation",
            "private void CompleteControlsOpacityAnimation");
        var completeAnimation = GetMethod(
            code,
            "private void CompleteControlsOpacityAnimation",
            "private bool IsAnyPopupOpen");
        var beginActivityCall = keyHandler.IndexOf("BeginVisibilityShortcutActivity(viewModel);", StringComparison.Ordinal);
        var awaitAction = keyHandler.IndexOf("await viewModel.HandleShortcutKeyAsync(key, modifiers)", StringComparison.Ordinal);

        Assert.IsTrue(PlayerPage.ShouldRevealControlsForShortcut(PlayerShortcutAction.TogglePlayPause));
        Assert.IsTrue(beginActivityCall >= 0 && beginActivityCall < awaitAction);
        StringAssert.Contains(beginActivity, "viewModel.ShowControlsOverlay();");
        StringAssert.Contains(applyVisibility, "if (!ControlsLayer.IsHitTestVisible)");
        StringAssert.Contains(beginAnimation, "ControlsLayer.IsHitTestVisible = shouldShow;");
        Assert.AreEqual(1, CountOccurrences(beginAnimation, "ControlsLayer.BeginAnimation("));
        StringAssert.Contains(completeAnimation, "if (!ReferenceEquals(activeControlsOpacityAnimation, animation))");
        StringAssert.Contains(completeAnimation, "ApplyPendingControlsOverlayBoundsSync();");
        Assert.IsFalse(completeAnimation.Contains("HideWindowChrome();", StringComparison.Ordinal));
        Assert.IsFalse(completeAnimation.Contains("RestoreWindowChrome();", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ControlsTransition_DoesNotRequestOverlayOrVideoBoundsChanges()
    {
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());
        var applyVisibility = GetMethod(
            code,
            "private void ApplyFullscreenChromeVisibility",
            "private void BeginControlsOpacityAnimation");
        var completeAnimation = GetMethod(
            code,
            "private void CompleteControlsOpacityAnimation",
            "private bool IsAnyPopupOpen");
        var completionGuard = completeAnimation.IndexOf("ReferenceEquals(activeControlsOpacityAnimation, animation)", StringComparison.Ordinal);
        var clearAnimation = completeAnimation.IndexOf("activeControlsOpacityAnimation = null;", StringComparison.Ordinal);
        var applyPendingBounds = completeAnimation.IndexOf("ApplyPendingControlsOverlayBoundsSync();", StringComparison.Ordinal);

        StringAssert.Contains(applyVisibility, "BeginControlsOpacityAnimation(shouldShow: true);");
        StringAssert.Contains(applyVisibility, "BeginControlsOpacityAnimation(shouldShow: false);");
        Assert.IsFalse(applyVisibility.Contains("SyncControlsOverlayBounds", StringComparison.Ordinal));
        Assert.IsFalse(applyVisibility.Contains("RequestControlsOverlayBoundsSync", StringComparison.Ordinal));
        Assert.IsFalse(applyVisibility.Contains("VideoHost", StringComparison.Ordinal));
        Assert.IsTrue(
            completionGuard >= 0
            && completionGuard < clearAnimation
            && clearAnimation < applyPendingBounds);
        Assert.IsFalse(completeAnimation.Contains("HideWindowChrome();", StringComparison.Ordinal));
        Assert.IsFalse(completeAnimation.Contains("RestoreWindowChrome();", StringComparison.Ordinal));
        Assert.IsFalse(completeAnimation.Contains("SetPlayerCaptionState", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OverlayBoundsSync_IsCoalescedDeferredAndChangedOnly()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());
        var overlayCode = File.ReadAllText(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Pages",
            "PlayerControlsOverlayWindow.xaml.cs"));
        var requestSync = GetMethod(
            code,
            "private void RequestControlsOverlayBoundsSync",
            "private void ApplyPendingControlsOverlayBoundsSync");
        var applyPendingSync = GetMethod(
            code,
            "private void ApplyPendingControlsOverlayBoundsSync",
            "private void SyncControlsOverlayBounds");
        var showOverlay = GetMethod(
            code,
            "private void ShowControlsOverlayWindow",
            "private void CloseControlsOverlayWindow");

        StringAssert.Contains(requestSync, "isControlsOverlayBoundsSyncPending");
        StringAssert.Contains(requestSync, "if (activeControlsOpacityAnimation is null)");
        Assert.AreEqual(1, CountOccurrences(code, "Dispatcher.BeginInvoke(ApplyPendingControlsOverlayBoundsSync"));
        StringAssert.Contains(applyPendingSync, "!isControlsOverlayBoundsSyncPending || activeControlsOpacityAnimation is not null");
        Assert.IsTrue(
            showOverlay.IndexOf("SyncControlsOverlayBounds();", StringComparison.Ordinal)
            < showOverlay.IndexOf("controlsOverlayWindow.Show();", StringComparison.Ordinal));
        StringAssert.Contains(code, "controlsOverlayWindow.SetBounds(");
        StringAssert.Contains(overlayCode, "if (Left.Equals(bounds.Left)");
        StringAssert.Contains(overlayCode, "&& Height.Equals(bounds.Height))");
    }

    [TestMethod]
    public void FullscreenTransition_ForcesOwnerLayoutBetweenBoundsMutationAndStateSync()
    {
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());
        var toggle = GetMethod(code, "private void ToggleFullscreen", "private void ExitFullscreen");
        var exit = GetMethod(code, "private void ExitFullscreen", "private void SyncFullscreenState");

        Assert.IsTrue(
            toggle.IndexOf("controller.Toggle();", StringComparison.Ordinal)
            < toggle.IndexOf("overlayOwnerWindow?.UpdateLayout();", StringComparison.Ordinal));
        Assert.IsTrue(
            toggle.IndexOf("overlayOwnerWindow?.UpdateLayout();", StringComparison.Ordinal)
            < toggle.IndexOf("SyncFullscreenState(controller.IsFullscreen);", StringComparison.Ordinal));
        Assert.IsTrue(
            exit.IndexOf("fullscreenController?.Exit();", StringComparison.Ordinal)
            < exit.IndexOf("overlayOwnerWindow?.UpdateLayout();", StringComparison.Ordinal));
        Assert.IsTrue(
            exit.IndexOf("overlayOwnerWindow?.UpdateLayout();", StringComparison.Ordinal)
            < exit.IndexOf("SyncFullscreenState(false);", StringComparison.Ordinal));
        Assert.IsFalse(toggle.Contains("Task.Delay", StringComparison.Ordinal));
        Assert.IsFalse(exit.Contains("Task.Delay", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("isFullscreenTransition", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NativeVideoAndOverlayBounds_AreIndependentOfControlsCaptionVisibility()
    {
        var xaml = File.ReadAllText(FindPlayerPageXamlPath());
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());
        var applyVisibility = GetMethod(
            code,
            "private void ApplyFullscreenChromeVisibility",
            "private void BeginControlsOpacityAnimation");
        var syncBounds = GetMethod(
            code,
            "private void SyncControlsOverlayBounds",
            "private void HideMouseCursor");

        StringAssert.Contains(syncBounds, "new Rect(screenDip.X, screenDip.Y, VideoHost.ActualWidth, VideoHost.ActualHeight)");
        Assert.IsFalse(syncBounds.Contains("shouldShowChrome", StringComparison.Ordinal));
        Assert.IsFalse(syncBounds.Contains("caption", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(applyVisibility.Contains("VideoHost", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("GetVideoHostMargin", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("PlayerCaptionInset", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("<controls:PlayerVideoHost x:Name=\"VideoHost\" Margin=", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PlayerPage_FullscreenFixDoesNotUsePageMarginOrDelayedRepair()
    {
        var xaml = File.ReadAllText(FindPlayerPageXamlPath());
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());
        var toggle = GetMethod(code, "private void ToggleFullscreen", "private void ExitFullscreen");
        var exit = GetMethod(code, "private void ExitFullscreen", "private void SyncFullscreenState");

        var playerSurfaceStart = xaml.IndexOf("<Grid x:Name=\"PlayerSurface\"", StringComparison.Ordinal);
        Assert.IsTrue(playerSurfaceStart >= 0);
        var playerSurfaceDeclaration = xaml.Substring(
            playerSurfaceStart,
            Math.Min(180, xaml.Length - playerSurfaceStart));
        Assert.IsFalse(playerSurfaceDeclaration.Contains("Margin=", StringComparison.Ordinal));
        Assert.IsFalse(toggle.Contains("Task.Delay", StringComparison.Ordinal));
        Assert.IsFalse(exit.Contains("Task.Delay", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PhysicalPointerTracker_FirstAndIdenticalSamplesDoNotWake()
    {
        var tracker = new PhysicalPointerPositionTracker();

        Assert.IsFalse(tracker.Observe(new PhysicalPixelPoint(800, 450)));
        Assert.IsFalse(tracker.Observe(new PhysicalPixelPoint(800, 450)));
    }

    [TestMethod]
    public void PhysicalPointerTracker_OnePhysicalPixelWakes()
    {
        var tracker = new PhysicalPointerPositionTracker();
        tracker.Refresh(new PhysicalPixelPoint(800, 450));

        Assert.IsTrue(tracker.Observe(new PhysicalPixelPoint(801, 450)));
        Assert.IsFalse(tracker.Observe(new PhysicalPixelPoint(801, 450)));
    }

    [TestMethod]
    public void PhysicalPointerTracking_LifecyclePreservesAndRefreshesBaseline()
    {
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());
        var loaded = GetMethod(code, "private void OnLoaded", "private async void OnUnloaded");
        var mouseLeave = GetMethod(code, "private void OnPlayerMouseLeave", "private void ShowControlsFromPointerActivity");
        var startMonitor = GetMethod(code, "private void StartCursorActivityMonitor", "private void StopCursorActivityMonitor");
        var stopMonitor = GetMethod(code, "private void StopCursorActivityMonitor", "private void ShowMouseCursor");

        StringAssert.Contains(loaded, "RefreshPhysicalPointerBaseline();");
        StringAssert.Contains(mouseLeave, "RefreshPhysicalPointerBaseline();");
        StringAssert.Contains(startMonitor, "RefreshPhysicalPointerBaseline();");
        Assert.IsFalse(stopMonitor.Contains("pointerPositionTracker", StringComparison.Ordinal));
        StringAssert.Contains(code, "GetPhysicalCursorPos(out var nativePoint)");
        Assert.IsFalse(code.Contains("GetPosition(this)", StringComparison.Ordinal));
    }

    [DataTestMethod]
    [DataRow(PlayerShortcutAction.TogglePlayPause, true)]
    [DataRow(PlayerShortcutAction.SeekBackward, true)]
    [DataRow(PlayerShortcutAction.SeekForward, true)]
    [DataRow(PlayerShortcutAction.VolumeUp, true)]
    [DataRow(PlayerShortcutAction.VolumeDown, true)]
    [DataRow(PlayerShortcutAction.NextSubtitle, true)]
    [DataRow(PlayerShortcutAction.SubtitleEarlier, true)]
    [DataRow(PlayerShortcutAction.SubtitleLater, true)]
    [DataRow(PlayerShortcutAction.NextAudioTrack, true)]
    [DataRow(PlayerShortcutAction.ToggleMute, false)]
    [DataRow(PlayerShortcutAction.ToggleFullscreen, false)]
    [DataRow(null, false)]
    public void KeyboardActivity_IdentifiesActionsThatRevealControls(PlayerShortcutAction? action, bool expected)
    {
        Assert.AreEqual(expected, PlayerPage.ShouldRevealControlsForShortcut(action));
    }

    [TestMethod]
    public void ShortcutVisibility_UsesResolvedActionAndStillDispatchesMute()
    {
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());
        var keyHandler = GetMethod(
            code,
            "private async void OnPreviewKeyDown",
            "private void OnPlayerMouseMove");

        Assert.IsFalse(PlayerPage.ShouldRevealControlsForShortcut(PlayerShortcutAction.ToggleMute));
        var resolve = keyHandler.IndexOf("var action = viewModel.ResolveShortcut(key, modifiers);", StringComparison.Ordinal);
        var visibility = keyHandler.IndexOf("var shouldRevealControls = ShouldRevealControlsForShortcut(action);", StringComparison.Ordinal);
        var dispatch = keyHandler.IndexOf("await viewModel.HandleShortcutKeyAsync(key, modifiers)", StringComparison.Ordinal);
        Assert.IsTrue(resolve >= 0 && resolve < visibility && visibility < dispatch);
        StringAssert.Contains(keyHandler, "if (shouldRevealControls)");
        Assert.IsFalse(keyHandler.Contains("action == PlayerShortcutAction.ToggleMute", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RapidOverlappingSpace_RestartsTimerOnlyAfterLastCompletion()
    {
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());
        var keyHandler = GetMethod(
            code,
            "private async void OnPreviewKeyDown",
            "private void OnPlayerMouseMove");
        var beginActivity = GetMethod(
            code,
            "private void BeginVisibilityShortcutActivity",
            "private void EndVisibilityShortcutActivity");
        var endActivity = GetMethod(
            code,
            "private void EndVisibilityShortcutActivity",
            "private void ResetControlsAutoHideTimer");
        var resetTimer = GetMethod(
            code,
            "private void ResetControlsAutoHideTimer",
            "internal static TimeSpan GetControlsAutoHideInterval");
        var beginCall = keyHandler.IndexOf("BeginVisibilityShortcutActivity(viewModel);", StringComparison.Ordinal);
        var awaitAction = keyHandler.IndexOf("await viewModel.HandleShortcutKeyAsync(key, modifiers)", StringComparison.Ordinal);
        var finallyBlock = keyHandler.IndexOf("finally", StringComparison.Ordinal);
        var endCall = keyHandler.IndexOf("EndVisibilityShortcutActivity();", finallyBlock, StringComparison.Ordinal);
        var increment = beginActivity.IndexOf("visibilityShortcutActivityDepth++;", StringComparison.Ordinal);
        var overlapGuard = beginActivity.IndexOf("if (visibilityShortcutActivityDepth > 1)", StringComparison.Ordinal);
        var stopTimer = beginActivity.IndexOf("controlsHideTimer.Stop();", StringComparison.Ordinal);
        var showControls = beginActivity.IndexOf("viewModel.ShowControlsOverlay();", StringComparison.Ordinal);
        var underflowGuard = endActivity.IndexOf("if (visibilityShortcutActivityDepth == 0)", StringComparison.Ordinal);
        var decrement = endActivity.IndexOf("visibilityShortcutActivityDepth--;", StringComparison.Ordinal);
        var finalCompletion = endActivity.IndexOf(
            "if (visibilityShortcutActivityDepth == 0 && IsLoaded)",
            decrement,
            StringComparison.Ordinal);
        var finalReset = endActivity.IndexOf("ResetControlsAutoHideTimer();", finalCompletion, StringComparison.Ordinal);
        var activeGuard = resetTimer.IndexOf("if (visibilityShortcutActivityDepth > 0)", StringComparison.Ordinal);
        var timerStart = resetTimer.IndexOf("controlsHideTimer.Start();", StringComparison.Ordinal);

        Assert.IsTrue(PlayerPage.ShouldRevealControlsForShortcut(PlayerShortcutAction.TogglePlayPause));
        Assert.IsTrue(
            beginCall >= 0
            && beginCall < awaitAction
            && awaitAction < finallyBlock
            && finallyBlock < endCall);
        Assert.IsTrue(
            increment >= 0
            && increment < overlapGuard
            && overlapGuard < stopTimer
            && stopTimer < showControls);
        Assert.IsTrue(
            underflowGuard >= 0
            && underflowGuard < decrement
            && decrement < finalCompletion
            && finalCompletion < finalReset);
        Assert.IsTrue(activeGuard >= 0 && activeGuard < timerStart);
    }

    [TestMethod]
    public void PauseSeekResume_KeepsPausedControlsVisibleAndSchedulesAfterResume()
    {
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());
        var applyVisibility = GetMethod(
            code,
            "private void ApplyFullscreenChromeVisibility",
            "private bool IsAnyPopupOpen");
        var resetTimer = GetMethod(
            code,
            "private void ResetControlsAutoHideTimer",
            "internal static TimeSpan GetControlsAutoHideInterval");

        Assert.IsTrue(PlayerPage.ShouldRevealControlsForShortcut(PlayerShortcutAction.TogglePlayPause));
        Assert.IsTrue(PlayerPage.ShouldRevealControlsForShortcut(PlayerShortcutAction.SeekBackward));
        Assert.IsTrue(PlayerPage.ShouldRevealControlsForShortcut(PlayerShortcutAction.SeekForward));
        StringAssert.Contains(applyVisibility, "viewModel.IsControlsOverlayVisible || viewModel.IsPaused");
        StringAssert.Contains(resetTimer, "IsPlaying: true, IsPaused: false");
        StringAssert.Contains(resetTimer, "controlsHideTimer.Start();");
    }

    [TestMethod]
    public void PlaybackStateChanges_ResetAutoHideTimerWithoutPointerActivity()
    {
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());
        var propertyChanged = GetMethod(
            code,
            "private void OnViewModelPropertyChanged",
            "private void AttachVideoHost");
        var playbackReset = propertyChanged.IndexOf(
            "if (e.PropertyName is nameof(PlayerViewModel.IsPlaying)",
            StringComparison.Ordinal);
        var pausedTransition = propertyChanged.IndexOf(
            "or nameof(PlayerViewModel.IsPaused)",
            playbackReset,
            StringComparison.Ordinal);
        var settingTransition = propertyChanged.IndexOf(
            "or nameof(PlayerViewModel.ControlsHideSeconds))",
            pausedTransition,
            StringComparison.Ordinal);
        var reset = propertyChanged.IndexOf(
            "ResetControlsAutoHideTimer();",
            settingTransition,
            StringComparison.Ordinal);

        Assert.IsTrue(
            playbackReset >= 0
            && playbackReset < pausedTransition
            && pausedTransition < settingTransition
            && settingTransition < reset);
        Assert.IsFalse(propertyChanged.Contains("ShowControlsFromPointerActivity", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InitialPlayingAfterLoad_SchedulesAutoHideAndUnloadCannotRestartIt()
    {
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());
        var loaded = GetMethod(code, "private void OnLoaded", "private async void OnUnloaded");
        var resetTimer = GetMethod(
            code,
            "private void ResetControlsAutoHideTimer",
            "internal static TimeSpan GetControlsAutoHideInterval");
        var lifecycleGuard = resetTimer.IndexOf("if (!IsLoaded)", StringComparison.Ordinal);
        var playingEligibility = resetTimer.IndexOf(
            "IsPlaying: true, IsPaused: false",
            lifecycleGuard,
            StringComparison.Ordinal);
        var timerStart = resetTimer.IndexOf(
            "controlsHideTimer.Start();",
            playingEligibility,
            StringComparison.Ordinal);

        StringAssert.Contains(loaded, "ResetControlsAutoHideTimer();");
        Assert.IsTrue(
            lifecycleGuard >= 0
            && lifecycleGuard < playingEligibility
            && playingEligibility < timerStart);
    }

    [TestMethod]
    public void AutoHide_ReusesExistingTimerAndPointerLeaveRestartPath()
    {
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());

        Assert.AreEqual(2, CountOccurrences(code, "private readonly DispatcherTimer"));
        StringAssert.Contains(code, "controlsHideTimer.Interval = GetControlsAutoHideInterval(viewModel.ControlsHideSeconds);");
        StringAssert.Contains(code, "private void OnPlayerMouseLeave(object sender, MouseEventArgs e)");
        var mouseLeaveStart = code.IndexOf("private void OnPlayerMouseLeave", StringComparison.Ordinal);
        var nextMethodStart = code.IndexOf("private void ShowControlsFromPointerActivity", mouseLeaveStart, StringComparison.Ordinal);
        var mouseLeaveMethod = code[mouseLeaveStart..nextMethodStart];
        StringAssert.Contains(mouseLeaveMethod, "ResetControlsAutoHideTimer();");
    }

    [TestMethod]
    public void PlayerControls_AreHostedInOwnedTransparentOverlayWindow()
    {
        var root = FindRepositoryRoot();
        var page = File.ReadAllText(FindPlayerPageXamlPath());
        var window = File.ReadAllText(Path.Combine(root, "src", "EmbyPlayer.UI", "Pages", "PlayerControlsOverlayWindow.xaml"));
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());

        StringAssert.Contains(page, "x:Name=\"OverlayRoot\"");
        StringAssert.Contains(page, "Background=\"#01000000\"");
        StringAssert.Contains(window, "AllowsTransparency=\"True\"");
        StringAssert.Contains(window, "ShowInTaskbar=\"False\"");
        StringAssert.Contains(window, "Topmost=\"False\"");
        StringAssert.Contains(code, "Owner = owner");
        StringAssert.Contains(code, "controlsOverlayWindow.Attach(OverlayRoot, DataContext)");
        StringAssert.Contains(code, "CloseControlsOverlayWindow();");
        Assert.IsFalse(code.Contains("controlsOverlayWindow.Topmost = true", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void OverlayWindow_SynchronizesVideoBoundsAndDpi()
    {
        var code = File.ReadAllText(FindPlayerPageCodeBehindPath());

        StringAssert.Contains(code, "VideoHost.PointToScreen");
        StringAssert.Contains(code, "TransformFromDevice.Transform");
        StringAssert.Contains(code, "TransformToDevice.Transform");
        StringAssert.Contains(code, "NextEpisodeCard.Width = videoPixels.X < 1500 ? 344 : 376");
        StringAssert.Contains(code, "VideoHost.ActualWidth");
        StringAssert.Contains(code, "VideoHost.ActualHeight");
        StringAssert.Contains(code, "LocationChanged += OnOverlayOwnerBoundsChanged");
        StringAssert.Contains(code, "SizeChanged += OnOverlayOwnerBoundsChanged");
        StringAssert.Contains(code, "StateChanged += OnOverlayOwnerStateChanged");
        StringAssert.Contains(code, "VideoHost.SizeChanged += OnVideoHostSizeChanged");
        StringAssert.Contains(code, "TopChrome.IsMouseOver");
    }

    [TestMethod]
    public void PlayerPage_SharedIconResourcesExist()
    {
        var root = FindRepositoryRoot();
        var icons = File.ReadAllText(Path.Combine(root, "src", "EmbyPlayer.UI", "Resources", "Icons.xaml"));

        foreach (var key in new[] { "Icon.Back", "Icon.Play", "Icon.Pause", "Icon.Next", "Icon.VolumeHigh", "Icon.Subtitle", "Icon.AudioTrack", "Icon.FullscreenEnter", "Icon.FullscreenExit", "Icon.Mute", "Icon.WindowMinimize", "Icon.Close" })
        {
            StringAssert.Contains(icons, $"x:Key=\"{key}\"");
        }
    }

    private static string FindPlayerPageXamlPath() => FindPlayerPageFile("PlayerPage.xaml");

    private static string FindPlayerPageCodeBehindPath() => FindPlayerPageFile("PlayerPage.xaml.cs");

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

    private static void RaisePreviewLeftButtonDown(DependencyObject source)
    {
        var eventArgs = new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent,
        };

        switch (source)
        {
            case UIElement uiElement:
                uiElement.RaiseEvent(eventArgs);
                break;
            case ContentElement contentElement:
                contentElement.RaiseEvent(eventArgs);
                break;
            default:
                Assert.Fail($"Unsupported input source: {source.GetType().Name}");
                break;
        }
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class PreviewMouseDownProbeSlider : SettingsSlider
    {
        public int PreviewMouseLeftButtonDownCount { get; private set; }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            PreviewMouseLeftButtonDownCount++;
            base.OnPreviewMouseLeftButtonDown(e);
        }
    }

    private sealed class TestPlayerWindowHost : Window, IPlayerWindowHost
    {
        public int CaptionStateCallCount { get; private set; }

        public int CloseCallCount { get; private set; }

        public int MinimizeCallCount { get; private set; }

        public void SetPlayerCaptionState(bool isVisible, bool isFullscreen)
        {
            CaptionStateCallCount++;
        }

        public void MinimizePlayerWindow()
        {
            MinimizeCallCount++;
            WindowState = WindowState.Minimized;
        }

        public void ClosePlayerWindow()
        {
            CloseCallCount++;
        }
    }

    private sealed class TestPlayerCaptionHost : Window, IPlayerCaptionHost
    {
        public List<(bool IsVisible, bool IsFullscreen)> CaptionStates { get; } = new();

        public void SetPlayerCaptionState(bool isVisible, bool isFullscreen)
        {
            CaptionStates.Add((isVisible, isFullscreen));
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

    private static string GetMethod(string code, string methodStart, string nextMethodStart)
    {
        var start = code.IndexOf(methodStart, StringComparison.Ordinal);
        var end = code.IndexOf(nextMethodStart, start, StringComparison.Ordinal);
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

        Assert.Fail("Could not locate repository root.");
        return string.Empty;
    }

    private static string FindPlayerPageFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "EmbyPlayer.UI", "Pages", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        Assert.Fail($"Could not locate {fileName} from the test output directory.");
        return string.Empty;
    }
}
