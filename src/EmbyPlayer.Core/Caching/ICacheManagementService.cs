using EmbyPlayer.Core.Authentication;

namespace EmbyPlayer.Core.Caching;

public interface ICacheManagementService
{
    Task<CacheUsage> GetUsageAsync(CancellationToken cancellationToken);

    Task ClearImagesAsync(CancellationToken cancellationToken);

    Task ClearSearchIndexAsync(CancellationToken cancellationToken);

    Task RebuildSearchIndexAsync(AuthSession session, CancellationToken cancellationToken);
}

public sealed record CacheUsage(long ImageMemoryBytes, long SearchIndexDiskBytes);

public enum CacheOperationError
{
    StorageUnavailable,
    Unauthorized,
    Forbidden,
    ServerTimeout,
    ServerUnreachable,
    InvalidResponse
}

public sealed class CacheOperationException : Exception
{
    public CacheOperationException(CacheOperationError error, Exception innerException)
        : base("Cache maintenance could not finish.", innerException)
    {
        Error = error;
    }

    public CacheOperationError Error { get; }
}
