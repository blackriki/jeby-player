namespace EmbyPlayer.Core.Details;

public sealed class SimilarMediaLoadResult
{
    private SimilarMediaLoadResult(
        bool isSuccess,
        IReadOnlyList<SimilarMediaItem> items,
        SimilarMediaLoadError error)
    {
        IsSuccess = isSuccess;
        Items = items;
        Error = error;
    }

    public bool IsSuccess { get; }

    public IReadOnlyList<SimilarMediaItem> Items { get; }

    public SimilarMediaLoadError Error { get; }

    public static SimilarMediaLoadResult Success(IReadOnlyList<SimilarMediaItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new SimilarMediaLoadResult(true, items, SimilarMediaLoadError.None);
    }

    public static SimilarMediaLoadResult Failure(SimilarMediaLoadError error)
    {
        return new SimilarMediaLoadResult(false, Array.Empty<SimilarMediaItem>(), error);
    }
}
