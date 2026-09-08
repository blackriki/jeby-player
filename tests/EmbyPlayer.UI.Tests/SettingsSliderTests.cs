using System.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using EmbyPlayer.UI.Controls;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class SettingsSliderTests
{
    [TestMethod]
    public void HorizontalValue_MapsPointerToThumbCenter()
    {
        Assert.IsTrue(SettingsSlider.TryCalculateHorizontalValue(
            pointerX: 60,
            trackWidth: 264,
            thumbWidth: 24,
            minimum: 0,
            maximum: 100,
            isDirectionReversed: false,
            out var value));

        Assert.AreEqual(20d, value, 0.001d);
    }

    [TestMethod]
    public void HorizontalValue_CanMoveContinuouslyFromTwentyToEightyPercent()
    {
        Assert.IsTrue(SettingsSlider.TryCalculateHorizontalValue(
            60, 264, 24, 0, 100, false, out var start));
        Assert.IsTrue(SettingsSlider.TryCalculateHorizontalValue(
            204, 264, 24, 0, 100, false, out var end));

        Assert.AreEqual(20d, start, 0.001d);
        Assert.AreEqual(80d, end, 0.001d);
    }

    [TestMethod]
    public void HorizontalValue_ClampsOutsideTrackAndSupportsCustomRange()
    {
        Assert.IsTrue(SettingsSlider.TryCalculateHorizontalValue(
            -100, 264, 24, 20, 80, false, out var minimum));
        Assert.IsTrue(SettingsSlider.TryCalculateHorizontalValue(
            500, 264, 24, 20, 80, false, out var maximum));

        Assert.AreEqual(20d, minimum);
        Assert.AreEqual(80d, maximum);
    }

    [TestMethod]
    public void HorizontalValue_HonorsDirectionReversal()
    {
        Assert.IsTrue(SettingsSlider.TryCalculateHorizontalValue(
            60, 264, 24, 0, 100, true, out var value));

        Assert.AreEqual(80d, value, 0.001d);
    }

    [TestMethod]
    public void HorizontalValue_RejectsInvalidOrUnarrangedGeometry()
    {
        Assert.IsFalse(SettingsSlider.TryCalculateHorizontalValue(
            double.NaN, 264, 24, 0, 100, false, out _));
        Assert.IsFalse(SettingsSlider.TryCalculateHorizontalValue(
            60, 0, 24, 0, 100, false, out _));
        Assert.IsFalse(SettingsSlider.TryCalculateHorizontalValue(
            60, 24, 24, 0, 100, false, out _));
    }

    [TestMethod]
    public void VerticalValue_MapsTopToMaximumBottomToMinimumAndHonorsDirectionReversal()
    {
        Assert.IsTrue(SettingsSlider.TryCalculateVerticalValue(
            12, 264, 24, 0, 100, false, out var top));
        Assert.IsTrue(SettingsSlider.TryCalculateVerticalValue(
            252, 264, 24, 0, 100, false, out var bottom));
        Assert.IsTrue(SettingsSlider.TryCalculateVerticalValue(
            12, 264, 24, 0, 100, true, out var reversedTop));

        Assert.AreEqual(100d, top, 0.001d);
        Assert.AreEqual(0d, bottom, 0.001d);
        Assert.AreEqual(0d, reversedTop, 0.001d);
    }

    [TestMethod]
    public void ThumbSource_IsExcludedFromTrackManipulation()
    {
        RunOnStaThread(() =>
        {
            Assert.IsTrue(SettingsSlider.IsThumbSource(new Thumb()));
            Assert.IsFalse(SettingsSlider.IsThumbSource(new SettingsSlider()));
        });
    }

    [TestMethod]
    public void DirectManipulation_JumpsThenContinuesUntilReleased()
    {
        RunOnStaThread(() =>
        {
            var slider = new TestSettingsSlider
            {
                Minimum = 0,
                Maximum = 100,
            };

            Assert.IsTrue(slider.BeginTrackDrag(60, 264, 24));
            Assert.AreEqual(20d, slider.Value, 0.001d);
            Assert.IsTrue(slider.IsTrackDragging);

            Assert.IsTrue(slider.ContinueTrackDrag(204, 264, 24));
            Assert.AreEqual(80d, slider.Value, 0.001d);

            slider.EndTrackDrag();
            Assert.IsFalse(slider.IsTrackDragging);
            Assert.IsTrue(slider.WasPointerReleased);

            Assert.IsFalse(slider.ContinueTrackDrag(120, 264, 24));
            Assert.AreEqual(80d, slider.Value, 0.001d);
        });
    }

    [TestMethod]
    public void VerticalDirectManipulation_JumpsThenContinuesUntilReleased()
    {
        RunOnStaThread(() =>
        {
            var slider = new TestSettingsSlider
            {
                Minimum = 0,
                Maximum = 100,
                Orientation = Orientation.Vertical,
            };

            Assert.IsTrue(slider.BeginTrackDrag(60, 264, 24));
            Assert.AreEqual(80d, slider.Value, 0.001d);

            Assert.IsTrue(slider.ContinueTrackDrag(204, 264, 24));
            Assert.AreEqual(20d, slider.Value, 0.001d);

            slider.EndTrackDrag();
            Assert.IsFalse(slider.IsTrackDragging);
            Assert.IsTrue(slider.WasPointerReleased);
        });
    }

    [TestMethod]
    public void VolumeThenSeek_FirstTrackDragGetsCaptureAndStartsNormally()
    {
        RunOnStaThread(() =>
        {
            var pointerCapture = new TestPointerCapture();
            var volume = new TestSettingsSlider(pointerCapture)
            {
                Minimum = 0,
                Maximum = 100,
                Orientation = Orientation.Vertical,
            };
            var seek = new TestSettingsSlider(pointerCapture)
            {
                Minimum = 0,
                Maximum = 100,
            };
            var seekStartedCount = 0;
            seek.TrackDragStarted += (_, _) => seekStartedCount++;

            Assert.IsTrue(volume.BeginTrackDrag(60, 264, 24));
            Assert.AreSame(volume, pointerCapture.Owner);
            volume.EndTrackDrag();

            Assert.IsFalse(volume.IsTrackDragging);
            Assert.IsNull(pointerCapture.Owner);
            Assert.IsTrue(seek.BeginTrackDrag(60, 264, 24));
            Assert.AreSame(seek, pointerCapture.Owner);
            Assert.AreEqual(1, seekStartedCount);
            Assert.AreEqual(20d, seek.Value, 0.001d);
            Assert.IsTrue(seek.ContinueTrackDrag(204, 264, 24));
            Assert.AreEqual(80d, seek.Value, 0.001d);

            seek.EndTrackDrag();
            Assert.IsFalse(seek.IsTrackDragging);
            Assert.IsNull(pointerCapture.Owner);
        });
    }

    [TestMethod]
    public void DirectManipulation_RaisesLifecycleAroundImmediateAndContinuousValues()
    {
        RunOnStaThread(() =>
        {
            var slider = new TestSettingsSlider
            {
                Minimum = 0,
                Maximum = 100,
            };
            var startedCount = 0;
            var valueChangedCount = 0;
            var completedCount = 0;
            bool? wasCanceled = null;

            slider.TrackDragStarted += (_, _) =>
            {
                startedCount++;
                Assert.AreEqual(0, valueChangedCount, "The drag must begin before the initial jump is published.");
            };
            slider.ValueChanged += (_, _) => valueChangedCount++;
            slider.TrackDragCompleted += (_, e) =>
            {
                completedCount++;
                wasCanceled = e.Canceled;
            };

            Assert.IsTrue(slider.BeginTrackDrag(60, 264, 24));
            Assert.AreEqual(20d, slider.Value, 0.001d);
            Assert.AreEqual(1, startedCount);
            Assert.AreEqual(1, valueChangedCount);
            Assert.AreEqual(0, completedCount);

            Assert.IsTrue(slider.ContinueTrackDrag(204, 264, 24));
            Assert.AreEqual(80d, slider.Value, 0.001d);
            Assert.AreEqual(2, valueChangedCount);

            slider.EndTrackDrag();

            Assert.AreEqual(1, completedCount);
            Assert.AreEqual(false, wasCanceled);
        });
    }

    [TestMethod]
    public void LostCapture_CancelsActiveDirectManipulation()
    {
        RunOnStaThread(() =>
        {
            var slider = new TestSettingsSlider();
            bool? wasCanceled = null;
            slider.TrackDragCompleted += (_, e) => wasCanceled = e.Canceled;

            Assert.IsTrue(slider.BeginTrackDrag(60, 264, 24));

            slider.ResetTrackDragState();

            Assert.AreEqual(true, wasCanceled);
            Assert.IsFalse(slider.IsTrackDragging);
        });
    }

    [TestMethod]
    public void FailedPointerCapture_DoesNotJumpOrStartDirectManipulation()
    {
        RunOnStaThread(() =>
        {
            var slider = new TestSettingsSlider
            {
                Maximum = 100,
                Value = 35,
                CanCapturePointer = false,
            };
            var startedCount = 0;
            slider.TrackDragStarted += (_, _) => startedCount++;

            Assert.IsFalse(slider.BeginTrackDrag(60, 264, 24));

            Assert.AreEqual(35d, slider.Value, 0.001d);
            Assert.AreEqual(0, startedCount);
            Assert.IsFalse(slider.IsTrackDragging);
        });
    }

    [TestMethod]
    public void LostCapture_ClearsDirectManipulationState()
    {
        RunOnStaThread(() =>
        {
            var slider = new TestSettingsSlider();

            Assert.IsTrue(slider.BeginTrackDrag(60, 264, 24));
            Assert.IsTrue(slider.IsTrackDragging);

            slider.ResetTrackDragState();

            Assert.IsFalse(slider.IsTrackDragging);
            Assert.IsFalse(slider.ContinueTrackDrag(204, 264, 24));
        });
    }

    [TestMethod]
    public async Task Control_UsesMouseCaptureLifecycleWithoutPageCodeBehind()
    {
        var root = FindRepositoryRoot();
        var controlCode = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Controls",
            "SettingsSlider.cs"));
        var pageCode = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Pages",
            "SettingsPage.xaml.cs"));
        var settingsStyles = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Styles",
            "SettingsPageStyles.xaml"));
        var playerXaml = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Pages",
            "PlayerPage.xaml"));

        StringAssert.Contains(controlCode, "OnPreviewMouseLeftButtonDown");
        StringAssert.Contains(controlCode, "OnPreviewMouseMove");
        StringAssert.Contains(controlCode, "OnPreviewMouseLeftButtonUp");
        StringAssert.Contains(controlCode, "OnLostMouseCapture");
        StringAssert.Contains(controlCode, "CaptureMouse()");
        StringAssert.Contains(controlCode, "ReleaseMouseCapture()");
        StringAssert.Contains(controlCode, "Unloaded += OnUnloaded");
        StringAssert.Contains(settingsStyles, "IsDirectionReversed=\"{TemplateBinding IsDirectionReversed}\"");
        Assert.AreEqual(
            2,
            CountOccurrences(playerXaml, "IsDirectionReversed=\"{TemplateBinding IsDirectionReversed}\""));
        Assert.IsFalse(pageCode.Contains("CaptureMouse", StringComparison.Ordinal));
        Assert.IsFalse(pageCode.Contains("IsTrackDragging", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InteractiveSliderInventory_UsesSharedControlWithIntentionalOrientations()
    {
        var root = FindRepositoryRoot();
        var xamlNamespace = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var sliderInventory = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.xaml", SearchOption.AllDirectories)
            .SelectMany(path => XDocument.Load(path)
                .Descendants()
                .Where(element => element.Name.LocalName is "Slider" or "SettingsSlider")
                .Select(element =>
                {
                    var relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
                    var name = element.Attribute(xamlNamespace + "Name")?.Value ?? "(unnamed)";
                    var orientation = element.Attribute("Orientation")?.Value ?? "Horizontal";
                    return $"{relativePath}:{element.Name.LocalName}:{name}:{orientation}";
                }))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                "src/EmbyPlayer.UI/Pages/PlayerPage.xaml:SettingsSlider:SeekSlider:Horizontal",
                "src/EmbyPlayer.UI/Pages/PlayerPage.xaml:SettingsSlider:VolumeSlider:Vertical",
                "src/EmbyPlayer.UI/Pages/SettingsPage.xaml:SettingsSlider:DefaultVolumeSlider:Horizontal",
            },
            sliderInventory);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbyPlayer.sln")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "Repository root could not be found.");
        return directory.FullName;
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

    private sealed class TestSettingsSlider : SettingsSlider
    {
        private readonly TestPointerCapture? pointerCapture;

        public TestSettingsSlider(TestPointerCapture? pointerCapture = null)
        {
            this.pointerCapture = pointerCapture;
        }

        public bool CanCapturePointer { get; init; } = true;

        public bool WasPointerReleased { get; private set; }

        internal override bool CapturePointer()
        {
            return CanCapturePointer
                && (pointerCapture?.TryCapture(this) ?? true);
        }

        internal override void ReleasePointer()
        {
            WasPointerReleased = true;
            pointerCapture?.Release(this);
        }
    }

    private sealed class TestPointerCapture
    {
        public TestSettingsSlider? Owner { get; private set; }

        public bool TryCapture(TestSettingsSlider slider)
        {
            if (Owner is not null)
            {
                return false;
            }

            Owner = slider;
            return true;
        }

        public void Release(TestSettingsSlider slider)
        {
            if (ReferenceEquals(Owner, slider))
            {
                Owner = null;
            }
        }
    }
}
