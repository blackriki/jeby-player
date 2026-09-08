namespace EmbyPlayer.Core.Library;

public sealed class MediaLibraryScanResult
{
    private MediaLibraryScanResult(bool isSuccess, FailureReason failureReason)
    {
        IsSuccess = isSuccess;
        Reason = failureReason;
    }

    public bool IsSuccess { get; }

    public FailureReason Reason { get; }

    public static MediaLibraryScanResult Success()
    {
        return new MediaLibraryScanResult(true, FailureReason.None);
    }

    public static MediaLibraryScanResult Failure(FailureReason failureReason)
    {
        return new MediaLibraryScanResult(false, failureReason);
    }

    public enum FailureReason
    {
        None,
        Unauthorized,
        Forbidden,
        ServerUnreachable,
        ServerTimeout,
        ServerError,
        Cancelled
    }
}
