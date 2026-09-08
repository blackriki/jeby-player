namespace EmbyPlayer.Core.Authentication;

public sealed class AuthSessionValidationResult
{
    private AuthSessionValidationResult(AuthSessionValidationStatus status)
    {
        Status = status;
    }

    public AuthSessionValidationStatus Status { get; }

    public bool IsValid => Status == AuthSessionValidationStatus.Valid;

    public static AuthSessionValidationResult Valid()
    {
        return new AuthSessionValidationResult(AuthSessionValidationStatus.Valid);
    }

    public static AuthSessionValidationResult InvalidToken()
    {
        return new AuthSessionValidationResult(AuthSessionValidationStatus.InvalidToken);
    }

    public static AuthSessionValidationResult Forbidden()
    {
        return new AuthSessionValidationResult(AuthSessionValidationStatus.Forbidden);
    }

    public static AuthSessionValidationResult NetworkUnavailable()
    {
        return new AuthSessionValidationResult(AuthSessionValidationStatus.NetworkUnavailable);
    }

    public static AuthSessionValidationResult ServerTimeout()
    {
        return new AuthSessionValidationResult(AuthSessionValidationStatus.ServerTimeout);
    }

    public static AuthSessionValidationResult ValidationFailed()
    {
        return new AuthSessionValidationResult(AuthSessionValidationStatus.ValidationFailed);
    }
}
