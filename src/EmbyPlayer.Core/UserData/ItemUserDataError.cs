namespace EmbyPlayer.Core.UserData;

public enum ItemUserDataError
{
    None,
    Unauthorized,
    Forbidden,
    NotFound,
    ServerUnreachable,
    ServerTimeout,
    ServerError,
    Cancelled
}
