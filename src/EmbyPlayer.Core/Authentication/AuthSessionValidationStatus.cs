namespace EmbyPlayer.Core.Authentication;

public enum AuthSessionValidationStatus
{
    Valid,
    InvalidToken,
    Forbidden,
    NetworkUnavailable,
    ServerTimeout,
    ValidationFailed
}
