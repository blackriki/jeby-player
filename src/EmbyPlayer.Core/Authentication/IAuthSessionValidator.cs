namespace EmbyPlayer.Core.Authentication;

public interface IAuthSessionValidator
{
    Task<AuthSessionValidationResult> ValidateAsync(
        AuthSession session,
        CancellationToken cancellationToken);
}
