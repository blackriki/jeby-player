namespace EmbyPlayer.Core.Details;

public sealed class MediaDetailLoadResult
{
    private MediaDetailLoadResult(
        bool isSuccess,
        MediaDetail? detail,
        MediaDetailLoadError error)
    {
        IsSuccess = isSuccess;
        Detail = detail;
        Error = error;
    }

    public bool IsSuccess { get; }

    public MediaDetail? Detail { get; }

    public MediaDetailLoadError Error { get; }

    public static MediaDetailLoadResult Success(MediaDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new MediaDetailLoadResult(true, detail, MediaDetailLoadError.None);
    }

    public static MediaDetailLoadResult Failure(MediaDetailLoadError error)
    {
        return new MediaDetailLoadResult(false, null, error);
    }
}
