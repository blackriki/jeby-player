using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EmbyPlayer.UI.Controls;

public partial class AuthenticatedImage : UserControl
{
    public static readonly DependencyProperty ImageUrlProperty = DependencyProperty.Register(
        nameof(ImageUrl),
        typeof(string),
        typeof(AuthenticatedImage),
        new PropertyMetadata(null, OnImageUrlChanged));

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText),
        typeof(string),
        typeof(AuthenticatedImage),
        new PropertyMetadata("海报"));

    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(
        nameof(Stretch),
        typeof(Stretch),
        typeof(AuthenticatedImage),
        new PropertyMetadata(Stretch.UniformToFill));

    public static readonly DependencyProperty ImageHorizontalAlignmentProperty = DependencyProperty.Register(
        nameof(ImageHorizontalAlignment),
        typeof(HorizontalAlignment),
        typeof(AuthenticatedImage),
        new PropertyMetadata(HorizontalAlignment.Center));

    public static readonly DependencyProperty ShowPlaceholderProperty = DependencyProperty.Register(
        nameof(ShowPlaceholder),
        typeof(bool),
        typeof(AuthenticatedImage),
        new PropertyMetadata(true, OnShowPlaceholderChanged));

    private static readonly DependencyPropertyKey IsImageLoadedPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsImageLoaded),
            typeof(bool),
            typeof(AuthenticatedImage),
            new PropertyMetadata(false));

    public static readonly DependencyProperty IsImageLoadedProperty =
        IsImageLoadedPropertyKey.DependencyProperty;

    private CancellationTokenSource? loadCancellation;
    private long loadVersion;
    private string? displayedImageUrl;
    private int displayedCacheGeneration;

    public AuthenticatedImage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public string? ImageUrl
    {
        get => (string?)GetValue(ImageUrlProperty);
        set => SetValue(ImageUrlProperty, value);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public Stretch Stretch
    {
        get => (Stretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public HorizontalAlignment ImageHorizontalAlignment
    {
        get => (HorizontalAlignment)GetValue(ImageHorizontalAlignmentProperty);
        set => SetValue(ImageHorizontalAlignmentProperty, value);
    }

    public bool ShowPlaceholder
    {
        get => (bool)GetValue(ShowPlaceholderProperty);
        set => SetValue(ShowPlaceholderProperty, value);
    }

    public bool IsImageLoaded => (bool)GetValue(IsImageLoadedProperty);

    private static void OnImageUrlChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not AuthenticatedImage authenticatedImage)
        {
            return;
        }

        authenticatedImage.CancelLoad();
        authenticatedImage.ClearImage();
        if (authenticatedImage.IsLoaded)
        {
            _ = authenticatedImage.LoadImageAsync();
        }
    }

    private static void OnShowPlaceholderChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is AuthenticatedImage authenticatedImage)
        {
            authenticatedImage.UpdatePlaceholderVisibility();
        }
    }

    private async Task LoadImageAsync()
    {
        if (HasCurrentImage())
        {
            return;
        }

        CancelLoad();
        ClearImage();

        var requestedImageUrl = ImageUrl;
        var imageService = ImageLoaderServices.ImageService;
        var session = ImageLoaderServices.CurrentSessionService?.CurrentSession;

        if (string.IsNullOrWhiteSpace(requestedImageUrl)
            || imageService is null
            || session is null)
        {
            return;
        }

        var version = Interlocked.Increment(ref loadVersion);
        var cacheGeneration = ImageLoaderServices.CacheGeneration;
        var cancellation = new CancellationTokenSource();
        loadCancellation = cancellation;
        try
        {
            var result = await imageService
                .LoadImageAsync(
                    session,
                    requestedImageUrl,
                    cancellation.Token)
                .ConfigureAwait(true);

            if (version != loadVersion
                || cacheGeneration != ImageLoaderServices.CacheGeneration
                || !string.Equals(ImageUrl, requestedImageUrl, StringComparison.Ordinal)
                || !result.IsSuccess
                || result.ImageBytes is null)
            {
                return;
            }

            BitmapImage imageSource;
            try
            {
                imageSource = CreateBitmapImage(result.ImageBytes);
            }
            catch (ArgumentException)
            {
                return;
            }

            ImageElement.Source = imageSource;
            displayedImageUrl = requestedImageUrl;
            displayedCacheGeneration = cacheGeneration;
            SetImageLoaded(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (NotSupportedException)
        {
        }
        finally
        {
            if (ReferenceEquals(loadCancellation, cancellation))
            {
                loadCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ImageLoaderServices.CacheCleared += OnCacheCleared;
        if (!HasCurrentImage())
        {
            _ = LoadImageAsync();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ImageLoaderServices.CacheCleared -= OnCacheCleared;
        CancelLoad();
    }

    private void OnCacheCleared(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            CancelLoad();
            ClearImage();
            if (IsLoaded) _ = LoadImageAsync();
        });
    }

    private void CancelLoad()
    {
        Interlocked.Increment(ref loadVersion);
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        loadCancellation = null;
    }

    private bool HasCurrentImage() =>
        IsImageLoaded
        && ImageElement.Source is not null
        && displayedCacheGeneration == ImageLoaderServices.CacheGeneration
        && string.Equals(displayedImageUrl, ImageUrl, StringComparison.Ordinal);

    private void ClearImage()
    {
        displayedImageUrl = null;
        ImageElement.Source = null;
        SetImageLoaded(false);
    }

    private void SetImageLoaded(bool value)
    {
        SetValue(IsImageLoadedPropertyKey, value);
        UpdatePlaceholderVisibility();
    }

    private void UpdatePlaceholderVisibility()
    {
        var visibility = ShowPlaceholder && !IsImageLoaded
            ? Visibility.Visible
            : Visibility.Collapsed;
        RootGrid.Background = ShowPlaceholder
            ? new SolidColorBrush(Color.FromRgb(0x15, 0x1A, 0x20))
            : Brushes.Transparent;
        PlaceholderBorder.Visibility = visibility;
        PlaceholderTextBlock.Visibility = visibility;
    }

    private static BitmapImage CreateBitmapImage(byte[] imageBytes)
    {
        using var stream = new MemoryStream(imageBytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
