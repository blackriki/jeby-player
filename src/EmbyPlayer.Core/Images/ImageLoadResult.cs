namespace EmbyPlayer.Core.Images;

public sealed class ImageLoadResult
{
    private ImageLoadResult(
        bool isSuccess,
        byte[]? imageBytes,
        string? contentType,
        ImageLoadError error)
    {
        IsSuccess = isSuccess;
        ImageBytes = imageBytes;
        ContentType = contentType;
        Error = error;
    }

    public bool IsSuccess { get; }

    public byte[]? ImageBytes { get; }

    public string? ContentType { get; }

    public ImageLoadError Error { get; }

    public static ImageLoadResult Success(byte[] imageBytes, string? contentType)
    {
        return new ImageLoadResult(true, imageBytes, contentType, ImageLoadError.None);
    }

    public static ImageLoadResult Failure(ImageLoadError error)
    {
        return new ImageLoadResult(false, null, null, error);
    }
}
