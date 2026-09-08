using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace EmbyPlayer.UI.Controls;

public class SettingsSlider : Slider
{
    private bool isTrackDragging;

    public SettingsSlider()
    {
        Unloaded += OnUnloaded;
    }

    internal bool IsTrackDragging => isTrackDragging;

    public event DragStartedEventHandler? TrackDragStarted;

    public event DragCompletedEventHandler? TrackDragCompleted;

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled || IsThumbSource(e.OriginalSource as DependencyObject))
        {
            base.OnPreviewMouseLeftButtonDown(e);
            return;
        }

        Focus();
        var track = GetTrack();
        if (track is null || !BeginTrackDrag(track, e.GetPosition(track)))
        {
            base.OnPreviewMouseLeftButtonDown(e);
            return;
        }

        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (!isTrackDragging)
        {
            base.OnPreviewMouseMove(e);
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndTrackDrag();
            return;
        }

        var track = GetTrack();
        if (track is not null)
        {
            var pointerPosition = e.GetPosition(track);
            var isHorizontal = Orientation == Orientation.Horizontal;
            ContinueTrackDrag(
                isHorizontal ? pointerPosition.X : pointerPosition.Y,
                isHorizontal ? track.ActualWidth : track.ActualHeight,
                isHorizontal ? track.Thumb?.ActualWidth ?? 0d : track.Thumb?.ActualHeight ?? 0d);
        }

        e.Handled = true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!isTrackDragging)
        {
            base.OnPreviewMouseLeftButtonUp(e);
            return;
        }

        EndTrackDrag();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        ResetTrackDragState();
        base.OnLostMouseCapture(e);
    }

    internal bool BeginTrackDrag(
        double pointerCoordinate,
        double trackLength,
        double thumbLength)
    {
        if (!TryCalculateValue(pointerCoordinate, trackLength, thumbLength, out var value)
            || !CapturePointer())
        {
            return false;
        }

        isTrackDragging = true;
        TrackDragStarted?.Invoke(this, new DragStartedEventArgs(0d, 0d));
        SetCurrentValue(ValueProperty, value);
        return true;
    }

    internal bool ContinueTrackDrag(
        double pointerCoordinate,
        double trackLength,
        double thumbLength)
    {
        return isTrackDragging && TryUpdateValue(pointerCoordinate, trackLength, thumbLength);
    }

    internal void EndTrackDrag(bool canceled = false)
    {
        CompleteTrackDrag(canceled, releasePointer: true);
    }

    internal void ResetTrackDragState()
    {
        CompleteTrackDrag(canceled: true, releasePointer: false);
    }

    internal virtual bool CapturePointer()
    {
        return CaptureMouse();
    }

    internal virtual void ReleasePointer()
    {
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    internal static bool TryCalculateHorizontalValue(
        double pointerX,
        double trackWidth,
        double thumbWidth,
        double minimum,
        double maximum,
        bool isDirectionReversed,
        out double value)
    {
        return TryCalculateAxisValue(
            pointerX,
            trackWidth,
            thumbWidth,
            minimum,
            maximum,
            isDirectionReversed,
            out value);
    }

    internal static bool TryCalculateVerticalValue(
        double pointerY,
        double trackHeight,
        double thumbHeight,
        double minimum,
        double maximum,
        bool isDirectionReversed,
        out double value)
    {
        return TryCalculateAxisValue(
            trackHeight - pointerY,
            trackHeight,
            thumbHeight,
            minimum,
            maximum,
            isDirectionReversed,
            out value);
    }

    internal static bool IsThumbSource(DependencyObject? source)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is Thumb)
            {
                return true;
            }
        }

        return false;
    }

    private bool BeginTrackDrag(Track track, Point pointerPosition)
    {
        var isHorizontal = Orientation == Orientation.Horizontal;
        return BeginTrackDrag(
            isHorizontal ? pointerPosition.X : pointerPosition.Y,
            isHorizontal ? track.ActualWidth : track.ActualHeight,
            isHorizontal ? track.Thumb?.ActualWidth ?? 0d : track.Thumb?.ActualHeight ?? 0d);
    }

    private bool TryUpdateValue(
        double pointerCoordinate,
        double trackLength,
        double thumbLength)
    {
        if (!TryCalculateValue(pointerCoordinate, trackLength, thumbLength, out var value))
        {
            return false;
        }

        SetCurrentValue(ValueProperty, value);
        return true;
    }

    private bool TryCalculateValue(
        double pointerCoordinate,
        double trackLength,
        double thumbLength,
        out double value)
    {
        value = Minimum;
        return Orientation == Orientation.Horizontal
            ? TryCalculateHorizontalValue(
                pointerCoordinate,
                trackLength,
                thumbLength,
                Minimum,
                Maximum,
                IsDirectionReversed,
                out value)
            : TryCalculateVerticalValue(
                pointerCoordinate,
                trackLength,
                thumbLength,
                Minimum,
                Maximum,
                IsDirectionReversed,
                out value);
    }

    private static bool TryCalculateAxisValue(
        double pointerCoordinate,
        double trackLength,
        double thumbLength,
        double minimum,
        double maximum,
        bool isDirectionReversed,
        out double value)
    {
        value = minimum;

        if (!double.IsFinite(pointerCoordinate)
            || !double.IsFinite(trackLength)
            || !double.IsFinite(thumbLength)
            || !double.IsFinite(minimum)
            || !double.IsFinite(maximum)
            || trackLength <= 0
            || thumbLength < 0
            || maximum < minimum)
        {
            return false;
        }

        if (maximum == minimum)
        {
            return true;
        }

        var constrainedThumbLength = Math.Min(thumbLength, trackLength);
        var availableLength = trackLength - constrainedThumbLength;
        if (availableLength <= 0)
        {
            return false;
        }

        var normalized = Math.Clamp(
            (pointerCoordinate - (constrainedThumbLength / 2d)) / availableLength,
            0d,
            1d);

        if (isDirectionReversed)
        {
            normalized = 1d - normalized;
        }

        value = Math.Clamp(
            minimum + ((maximum - minimum) * normalized),
            minimum,
            maximum);
        return true;
    }

    private void CompleteTrackDrag(bool canceled, bool releasePointer)
    {
        if (!isTrackDragging)
        {
            return;
        }

        isTrackDragging = false;
        if (releasePointer)
        {
            ReleasePointer();
        }

        TrackDragCompleted?.Invoke(this, new DragCompletedEventArgs(0d, 0d, canceled));
    }

    private Track? GetTrack()
    {
        ApplyTemplate();
        return GetTemplateChild("PART_Track") as Track;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        EndTrackDrag(canceled: true);
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        if (current is Visual or Visual3D)
        {
            return VisualTreeHelper.GetParent(current);
        }

        return current switch
        {
            FrameworkContentElement contentElement => contentElement.Parent,
            ContentElement content => ContentOperations.GetParent(content),
            _ => null,
        };
    }
}
