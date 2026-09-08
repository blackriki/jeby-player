namespace EmbyPlayer.Core.Authentication;

public sealed class AuthenticationResult
{
    private AuthenticationResult(bool isSuccess, AuthSession? session, AuthenticationError error)
    {
        IsSuccess = isSuccess;
        Session = session;
        Error = error;
    }

    public bool IsSuccess { get; }

    public AuthSession? Session { get; }

    public AuthenticationError Error { get; }

    public static AuthenticationResult Success(AuthSession session)
    {
        return new AuthenticationResult(true, session, AuthenticationError.None);
    }

    public static AuthenticationResult Failure(AuthenticationError error)
    {
        if (error == AuthenticationError.None)
        {
            throw new ArgumentException("Failure requires an error.", nameof(error));
        }

        return new AuthenticationResult(false, null, error);
    }
}
