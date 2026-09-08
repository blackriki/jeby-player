using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EmbyPlayer.UI.ViewModels;
using Microsoft.Win32;

namespace EmbyPlayer.UI.Pages;

public partial class PlayerPage
{
    private readonly Image seekThumbnailImage = new() { Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
    private readonly TextBlock seekThumbnailTime = new() { Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(6) };
    private Border seekThumbnailCard = null!;
    private CancellationTokenSource? seekThumbnailCancellation;
    private CancellationTokenSource? seekDecodeCancellation;
    private TimeSpan latestHoverPosition;
    private bool seekThumbnailRequestRunning;
    private Point seekThumbnailAnchor;
    private TimeSpan? seekThumbnailCompletedPosition;
    private byte[]? seekThumbnailBytes;

    private void InitializePlayerTools()
    {
        var content = new StackPanel();
        content.Children.Add(seekThumbnailImage);
        content.Children.Add(seekThumbnailTime);
        seekThumbnailCard = new Border
        {
            Child = content, Background = new SolidColorBrush(Color.FromRgb(18, 23, 29)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(65, 255, 255, 255)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(3), IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed
        };
        Panel.SetZIndex(seekThumbnailCard, 100);
        OverlayRoot.Children.Add(seekThumbnailCard);
        SeekSlider.AddHandler(MouseMoveEvent, new MouseEventHandler(OnSeekThumbnailMove), true);
        SeekSlider.MouseLeave += (_, _) => HideSeekThumbnail();
        OverlayRoot.SizeChanged += (_, _) => HideSeekThumbnail();
        OverlayRoot.AllowDrop = true;
        OverlayRoot.PreviewDragOver += OnSubtitleDragOver;
        OverlayRoot.PreviewDrop += OnSubtitleDrop;
    }

    private async void OnImportSubtitleClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PlayerViewModel vm || !vm.CanImportLocalSubtitle) return;
        var info = vm.PlaybackInfo;
        var dialog = new OpenFileDialog
        {
            Title = "载入本地字幕", Filter = "字幕文件 (*.srt;*.ass;*.ssa;*.vtt)|*.srt;*.ass;*.ssa;*.vtt",
            CheckFileExists = true, Multiselect = false
        };
        ClosePopups();
        if (dialog.ShowDialog(GetPlayerOwnerWindow()) == true && ReferenceEquals(info, vm.PlaybackInfo))
        {
            await vm.ImportLocalSubtitleAsync(dialog.FileName);
            if (ReferenceEquals(info, vm.PlaybackInfo)) TogglePopup(SubtitlePopup);
        }
    }

    private static string? GetDroppedSubtitle(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files) return null;
        return new[] { ".srt", ".ass", ".ssa", ".vtt" }.Contains(Path.GetExtension(files[0]), StringComparer.OrdinalIgnoreCase) ? files[0] : null;
    }

    private void OnSubtitleDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DataContext is PlayerViewModel { CanImportLocalSubtitle: true } && GetDroppedSubtitle(e.Data) is not null
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnSubtitleDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is PlayerViewModel { CanImportLocalSubtitle: true } vm && GetDroppedSubtitle(e.Data) is { } path)
        {
            var info = vm.PlaybackInfo;
            await vm.ImportLocalSubtitleAsync(path);
            if (ReferenceEquals(info, vm.PlaybackInfo)) TogglePopup(SubtitlePopup);
        }
    }

    private void OnSeekThumbnailMove(object sender, MouseEventArgs e)
    {
        if (DataContext is not PlayerViewModel { CanSeek: true } vm || vm.PlaybackInfo is null
            || SeekSlider.Template.FindName("PART_Track", SeekSlider) is not Track track) return;
        var seconds = vm.PlaybackInfo.RunTimeTicks.GetValueOrDefault() / (double)TimeSpan.TicksPerSecond;
        if (seconds <= 0) return;
        var percent = Math.Clamp(track.ValueFromPoint(e.GetPosition(track)), 0, 100);
        var position = TimeSpan.FromSeconds(Math.Floor(seconds * percent / 100));
        latestHoverPosition = position;
        seekThumbnailTime.Text = latestHoverPosition.TotalHours >= 1 ? latestHoverPosition.ToString(@"h\:mm\:ss") : latestHoverPosition.ToString(@"mm\:ss");
        var width = IsMiniPlayer ? 128d : 192d;
        seekThumbnailCard.Width = width + 8;
        seekThumbnailImage.Width = width;
        seekThumbnailImage.Height = width * 9 / 16;
        var point = e.GetPosition(OverlayRoot);
        var seekOrigin = SeekSlider.TransformToAncestor(OverlayRoot).Transform(new Point());
        seekThumbnailAnchor = new Point(point.X, seekOrigin.Y);
        if (vm.TryGetSeekPreview(position) is { ImageBytes.Length: > 0 } cached) ShowSeekThumbnail(cached);
        PositionSeekThumbnail();
        seekThumbnailCard.Visibility = Visibility.Visible;
        if (seekThumbnailCancellation is null) seekThumbnailCancellation = new CancellationTokenSource();
        if (!seekThumbnailRequestRunning && seekThumbnailCompletedPosition != position)
            _ = LoadSeekThumbnailAsync(vm, seekThumbnailCancellation);
    }

    private async Task LoadSeekThumbnailAsync(PlayerViewModel vm, CancellationTokenSource cancellation)
    {
        seekThumbnailRequestRunning = true;
        try
        {
            EmbyPlayer.Core.Playback.PlaybackPreviewResult result;
            TimeSpan position;
            while (true)
            {
                await Task.Delay(80, cancellation.Token);
                position = latestHoverPosition;
                using var decodeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                seekDecodeCancellation = decodeCancellation;
                try
                {
                    result = await vm.GetSeekPreviewAsync(position, cancellation.Token, decodeCancellation.Token);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    continue;
                }
                finally
                {
                    if (ReferenceEquals(seekDecodeCancellation, decodeCancellation)) seekDecodeCancellation = null;
                }
                if (cancellation.IsCancellationRequested || !ReferenceEquals(cancellation, seekThumbnailCancellation)) return;
                if (position == latestHoverPosition && !decodeCancellation.IsCancellationRequested)
                {
                    if (result.ImageBytes is { Length: > 0 }) ShowSeekThumbnail(result);
                    seekThumbnailCompletedPosition = position;
                    break;
                }
                if (vm.TryGetSeekPreview(latestHoverPosition) is { ImageBytes.Length: > 0 } cached) ShowSeekThumbnail(cached);
            }
            if (cancellation.IsCancellationRequested || !ReferenceEquals(cancellation, seekThumbnailCancellation)) return;
            PositionSeekThumbnail();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Seek preview unavailable: {exception.GetType().Name}");
        }
        finally
        {
            if (ReferenceEquals(cancellation, seekThumbnailCancellation)) seekThumbnailRequestRunning = false;
        }
    }

    private void ShowSeekThumbnail(EmbyPlayer.Core.Playback.PlaybackPreviewResult result)
    {
        if (result.ImageBytes is not { Length: > 0 } bytes) return;
        if (!ReferenceEquals(bytes, seekThumbnailBytes))
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 192;
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            seekThumbnailImage.Source = bitmap;
            seekThumbnailBytes = bytes;
        }
        seekThumbnailImage.Visibility = Visibility.Visible;
        PositionSeekThumbnail();
    }

    private void HideSeekThumbnail()
    {
        seekDecodeCancellation?.Cancel();
        seekThumbnailCancellation?.Cancel();
        seekThumbnailCancellation?.Dispose();
        seekThumbnailCancellation = null;
        seekThumbnailRequestRunning = false;
        seekThumbnailCompletedPosition = null;
        seekThumbnailBytes = null;
        if (seekThumbnailCard is not null) seekThumbnailCard.Visibility = Visibility.Collapsed;
        seekThumbnailImage.Source = null;
        seekThumbnailImage.Visibility = Visibility.Collapsed;
    }

    private void PositionSeekThumbnail()
    {
        seekThumbnailCard.Width = seekThumbnailImage.Visibility == Visibility.Visible ? seekThumbnailImage.Width + 8 : 80;
        var width = seekThumbnailCard.Width;
        var height = seekThumbnailImage.Visibility == Visibility.Visible ? seekThumbnailImage.Height + 44 : 44;
        seekThumbnailCard.Margin = new Thickness(Math.Clamp(seekThumbnailAnchor.X - width / 2, 8,
            Math.Max(8, OverlayRoot.ActualWidth - width - 8)), Math.Max(8, seekThumbnailAnchor.Y - height), 0, 0);
    }
}
