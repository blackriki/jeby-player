using EmbyPlayer.Core.Authentication;

namespace EmbyPlayer.Core.People;

public interface IPersonService
{
    Task<PersonLoadResult> LoadPersonAsync(AuthSession session, string personId, CancellationToken cancellationToken);

    Task<PersonWorksLoadResult> LoadWorksAsync(
        AuthSession session, string personId, int startIndex, int limit, CancellationToken cancellationToken);
}
