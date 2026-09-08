namespace EmbyPlayer.Core.Home;

public sealed class HomeLoadResult
{
    private HomeLoadResult(HomeData? data, HomeLoadError error)
    {
        Data = data;
        Error = error;
    }

    public HomeData? Data { get; }

    public HomeLoadError Error { get; }

    public bool IsSuccess => Error == HomeLoadError.None && Data is not null;

    public static HomeLoadResult Success(HomeData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new HomeLoadResult(data, HomeLoadError.None);
    }

    public static HomeLoadResult Failure(HomeLoadError error)
    {
        return new HomeLoadResult(null, error);
    }
}
