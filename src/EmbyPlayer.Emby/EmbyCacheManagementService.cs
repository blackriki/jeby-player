using System.Net;
using System.Text.Json;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Caching;

namespace EmbyPlayer.Emby;

public sealed class EmbyCacheManagementService : ICacheManagementService
{
    private readonly EmbyImageService imageService;
    private readonly LocalMediaSearchIndex searchIndex;

    public EmbyCacheManagementService(EmbyImageService imageService, LocalMediaSearchIndex searchIndex)
    {
        this.imageService = imageService ?? throw new ArgumentNullException(nameof(imageService));
        this.searchIndex = searchIndex ?? throw new ArgumentNullException(nameof(searchIndex));
    }

    public async Task<CacheUsage> GetUsageAsync(CancellationToken cancellationToken)
    {
        long indexBytes = 0;
        await ExecuteAsync(async () => indexBytes = await searchIndex.GetDiskCacheBytesAsync(cancellationToken)
            .ConfigureAwait(false)).ConfigureAwait(false);
        return new CacheUsage(imageService.GetMemoryCacheBytes(), indexBytes);
    }

    public Task ClearImagesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        imageService.ClearMemoryCache();
        return Task.CompletedTask;
    }

    public Task ClearSearchIndexAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(() => searchIndex.ClearCacheAsync(cancellationToken));

    public Task RebuildSearchIndexAsync(AuthSession session, CancellationToken cancellationToken) =>
        ExecuteAsync(() => searchIndex.RebuildCacheAsync(session, cancellationToken));

    private static async Task ExecuteAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or HttpRequestException or TimeoutException or JsonException)
        {
            var error = exception switch
            {
                HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => CacheOperationError.Unauthorized,
                HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => CacheOperationError.Forbidden,
                HttpRequestException => CacheOperationError.ServerUnreachable,
                TimeoutException => CacheOperationError.ServerTimeout,
                JsonException => CacheOperationError.InvalidResponse,
                _ => CacheOperationError.StorageUnavailable
            };
            throw new CacheOperationException(error, exception);
        }
    }
}
