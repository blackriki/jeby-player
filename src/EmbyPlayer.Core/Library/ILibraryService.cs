using EmbyPlayer.Core.Authentication;

namespace EmbyPlayer.Core.Library;

public interface ILibraryService
{
    Task<LibraryLoadResult> LoadLibrariesAsync(
        AuthSession session,
        CancellationToken cancellationToken);

    Task<LibraryItemsLoadResult> LoadLibraryItemsAsync(
        AuthSession session,
        LibraryItem library,
        int startIndex,
        int limit,
        CancellationToken cancellationToken);

    Task<LibraryItemsLoadResult> LoadLibraryItemsAsync(
        AuthSession session,
        LibraryItem library,
        int startIndex,
        int limit,
        LibraryQuery query,
        CancellationToken cancellationToken);
}
