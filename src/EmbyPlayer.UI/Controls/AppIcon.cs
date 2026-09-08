using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
#if DEBUG
using System.Diagnostics;
#endif

namespace EmbyPlayer.UI.Controls;

public enum AppIconRenderMode
{
    Stroke,
    Fill,
}

public sealed class AppIcon : Control
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(
            nameof(Data),
            typeof(Geometry),
            typeof(AppIcon),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(
            nameof(Size),
            typeof(double),
            typeof(AppIcon),
            new FrameworkPropertyMetadata(
                18d,
                FrameworkPropertyMetadataOptions.AffectsMeasure
                | FrameworkPropertyMetadataOptions.AffectsArrange
                | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StretchProperty =
        DependencyProperty.Register(
            nameof(Stretch),
            typeof(Stretch),
            typeof(AppIcon),
            new FrameworkPropertyMetadata(Stretch.Uniform, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RenderModeProperty =
        DependencyProperty.Register(
            nameof(RenderMode),
            typeof(AppIconRenderMode),
            typeof(AppIcon),
            new FrameworkPropertyMetadata(AppIconRenderMode.Stroke, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(
            nameof(StrokeThickness),
            typeof(double),
            typeof(AppIcon),
            new FrameworkPropertyMetadata(1.9d, FrameworkPropertyMetadataOptions.AffectsRender));

    static AppIcon()
    {
        FocusableProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(false));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(false));
        UseLayoutRoundingProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(true));
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(true));
        HorizontalAlignmentProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(HorizontalAlignment.Center));
        VerticalAlignmentProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(VerticalAlignment.Center));
    }

    public AppIcon()
    {
#if DEBUG
        Loaded += (_, _) => WriteDiagnostics("Loaded");
#endif
    }

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public Stretch Stretch
    {
        get => (Stretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public AppIconRenderMode RenderMode
    {
        get => (AppIconRenderMode)GetValue(RenderModeProperty);
        set => SetValue(RenderModeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
#if DEBUG
        WriteDiagnostics("ApplyTemplate");
#endif
    }

#if DEBUG
    private void WriteDiagnostics(string stage)
    {
        var fillPath = GetTemplateChild("FillPath") as System.Windows.Shapes.Path;
        var strokePath = GetTemplateChild("StrokePath") as System.Windows.Shapes.Path;
        var viewbox = GetTemplateChild("IconViewbox") as Viewbox;
        var canvas = GetTemplateChild("IconCanvas") as FrameworkElement;
        var activePath = RenderMode == AppIconRenderMode.Fill ? fillPath : strokePath;

        Debug.WriteLine(
            $"AppIcon {stage}: "
            + $"DataNull={Data is null}; "
            + $"TemplateNull={Template is null}; "
            + $"StyleNull={Style is null}; "
            + $"Size={Size}; "
            + $"Desired={DesiredSize.Width:0.##}x{DesiredSize.Height:0.##}; "
            + $"Render={RenderSize.Width:0.##}x{RenderSize.Height:0.##}; "
            + $"Actual={ActualWidth:0.##}x{ActualHeight:0.##}; "
            + $"Foreground={DescribeBrush(Foreground)}; "
            + $"RenderMode={RenderMode}; "
            + $"FillPathFound={fillPath is not null}; "
            + $"StrokePathFound={strokePath is not null}; "
            + $"ActivePathDataNull={activePath?.Data is null}; "
            + $"ActivePathFill={DescribeBrush(activePath?.Fill)}; "
            + $"ActivePathStroke={DescribeBrush(activePath?.Stroke)}; "
            + $"ActivePathVisibility={activePath?.Visibility}; "
            + $"Viewbox={viewbox?.ActualWidth:0.##}x{viewbox?.ActualHeight:0.##}; "
            + $"Canvas={canvas?.ActualWidth:0.##}x{canvas?.ActualHeight:0.##}");
    }

    private static string DescribeBrush(Brush? brush)
    {
        return brush switch
        {
            null => "null",
            SolidColorBrush solid => solid.Color.ToString(),
            _ => brush.GetType().Name,
        };
    }
#endif
}
