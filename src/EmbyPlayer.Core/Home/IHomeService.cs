using EmbyPlayer.Core.Authentication;

namespace EmbyPlayer.Core.Home;

public interface IHomeService
{
    Task<HomeLoadResult> LoadHomeAsync(AuthSession session, CancellationToken cancellationToken);

    Task<HomeSectionItemsLoadResult> LoadSectionAsync(
        AuthSession session,
        HomeSectionKind section,
        int startIndex,
        int limit,
        CancellationToken cancellationToken);
}
