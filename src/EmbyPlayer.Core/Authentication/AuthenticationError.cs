namespace EmbyPlayer.Core.Authentication;

public enum AuthenticationError
{
    None,
    MissingServer,
    InvalidCredentials,
    ServerUnreachable,
    ServerTimeout,
    LoginFailed,
    Cancelled
}
