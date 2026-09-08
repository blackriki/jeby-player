using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace EmbyPlayer.UI.Controls;

public partial class LoadingSpinner : UserControl
{
    private const int SegmentCount = 24;
    private const double RingCenter = 16;
    private const double RingRadius = 12.125;
    private const double RingStrokeThickness = 3.75;
    private const double TrailSweepDegrees = 276;
    private const double SegmentOverlapDegrees = 0.35;

    private readonly DoubleAnimation rotationAnimation = new(0, 360, TimeSpan.FromMilliseconds(950))
    {
        RepeatBehavior = RepeatBehavior.Forever,
        FillBehavior = FillBehavior.Stop,
    };

    public LoadingSpinner()
    {
        InitializeComponent();
        BuildRingGeometry();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void BuildRingGeometry()
    {
        SpinnerTrack.Data = CreateTrackGeometry();
        SpinnerTrack.StrokeThickness = RingStrokeThickness;

        var segmentSweep = TrailSweepDegrees / SegmentCount;
        for (var index = 0; index < SegmentCount; index++)
        {
            var startAngle = -90 + (index * segmentSweep);
            var endAngle = startAngle + segmentSweep + SegmentOverlapDegrees;
            var progress = (double)index / (SegmentCount - 1);
            var opacity = SmoothStep(progress);

            var segment = new Path
            {
                Data = CreateArcGeometry(startAngle, endAngle),
                Fill = Brushes.Transparent,
                StrokeThickness = RingStrokeThickness,
                StrokeStartLineCap = PenLineCap.Flat,
                StrokeEndLineCap = index == SegmentCount - 1 ? PenLineCap.Round : PenLineCap.Flat,
                Opacity = opacity,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true,
            };
            segment.SetBinding(Shape.StrokeProperty, new Binding(nameof(Foreground)) { Source = this });
            SpinnerSweep.Children.Add(segment);
        }
    }

    private static Geometry CreateTrackGeometry()
    {
        var geometry = new EllipseGeometry(
            new Point(RingCenter, RingCenter),
            RingRadius,
            RingRadius);
        geometry.Freeze();
        return geometry;
    }

    private static Geometry CreateArcGeometry(double startAngle, double endAngle)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(PointOnRing(startAngle), false, false);
            context.ArcTo(
                PointOnRing(endAngle),
                new Size(RingRadius, RingRadius),
                0,
                false,
                SweepDirection.Clockwise,
                true,
                false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Point PointOnRing(double angleDegrees)
    {
        var angleRadians = angleDegrees * Math.PI / 180;
        return new Point(
            RingCenter + (RingRadius * Math.Cos(angleRadians)),
            RingCenter + (RingRadius * Math.Sin(angleRadians)));
    }

    private static double SmoothStep(double progress)
    {
        return progress * progress * (3 - (2 * progress));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateAnimationState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopAnimation();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateAnimationState();
    }

    private void UpdateAnimationState()
    {
        if (IsLoaded && IsVisible)
        {
            SpinnerRotation.BeginAnimation(
                System.Windows.Media.RotateTransform.AngleProperty,
                rotationAnimation,
                HandoffBehavior.SnapshotAndReplace);
            return;
        }

        StopAnimation();
    }

    private void StopAnimation()
    {
        SpinnerRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        SpinnerRotation.Angle = 0;
    }
}
