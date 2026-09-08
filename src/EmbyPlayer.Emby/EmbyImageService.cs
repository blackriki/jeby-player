using System.Net;
using System.Collections.Concurrent;
using EmbyPlayer.Core;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Images;

namespace EmbyPlayer.Emby;

public sealed class EmbyImageService : IImageService, IImageCacheInvalidationSource
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private ConcurrentDictionary<(string UserId, string AbsoluteImageUrl), ImageCacheEntry> memoryCache = new();
    private readonly IDeviceIdService deviceIdService;
    private readonly HttpClient httpClient;

    public EmbyImageService(HttpClient httpClient, IDeviceIdService deviceIdService, string cacheDirectory)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.deviceIdService = deviceIdService ?? throw new ArgumentNullException(nameof(deviceIdService));
        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            throw new ArgumentException("Cache directory is required.", nameof(cacheDirectory));
        }
    }

    public event EventHandler? CacheCleared;

    public long GetMemoryCacheBytes() => memoryCache.Values.Sum(entry => (long)entry.ImageBytes.Length);

    public void ClearMemoryCache()
    {
        Interlocked.Exchange(ref memoryCache, new());
        CacheCleared?.Invoke(this, EventArgs.Empty);
    }

    public async Task<ImageLoadResult> LoadImageAsync(
        AuthSession session,
        string imageUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var imageUri))
        {
            return ImageLoadResult.Failure(ImageLoadError.NotFound);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var requestCache = memoryCache;
        var cacheKey = (session.UserId, AbsoluteImageUrl: imageUri.AbsoluteUri);
        if (requestCache.TryGetValue(cacheKey, out var cachedImage))
        {
            return ImageLoadResult.Success(cachedImage.ImageBytes, cachedImage.ContentType);
        }

        var result = await DownloadImageAsync(
                imageUri,
                session.AccessToken,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess || result.ImageBytes is null)
        {
            return result;
        }

        requestCache.TryAdd(cacheKey, new ImageCacheEntry(result.ImageBytes, result.ContentType));

        return result;
    }

    private async Task<ImageLoadResult> DownloadImageAsync(
        Uri imageUri,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var deviceId = await deviceIdService
            .GetOrCreateDeviceIdAsync(cancellationToken)
            .ConfigureAwait(false);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, imageUri);
            request.Headers.TryAddWithoutValidation("X-Emby-Token", accessToken);
            request.Headers.TryAddWithoutValidation(
                "X-Emby-Authorization",
                $"Emby Client=\"{ApplicationIdentity.Name}\", Device=\"Windows\", DeviceId=\"{deviceId}\", Version=\"{ApplicationIdentity.Version}\"");

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ImageLoadResult.Failure(ImageLoadError.Unauthorized);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return ImageLoadResult.Failure(ImageLoadError.NotFound);
            }

            if (!response.IsSuccessStatusCode)
            {
                return ImageLoadResult.Failure(ImageLoadError.ServerError);
            }

            var bytes = await response.Content
                .ReadAsByteArrayAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            return bytes.Length == 0
                ? ImageLoadResult.Failure(ImageLoadError.InvalidResponse)
                : ImageLoadResult.Success(bytes, response.Content.Headers.ContentType?.MediaType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ImageLoadResult.Failure(ImageLoadError.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return ImageLoadResult.Failure(ImageLoadError.ServerTimeout);
        }
        catch (HttpRequestException)
        {
            return ImageLoadResult.Failure(ImageLoadError.ServerUnreachable);
        }
    }

    private sealed record ImageCacheEntry(byte[] ImageBytes, string? ContentType);
}
