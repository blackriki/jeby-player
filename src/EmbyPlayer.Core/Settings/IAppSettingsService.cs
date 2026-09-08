using EmbyPlayer.Core.Authentication;

namespace EmbyPlayer.Core.Settings;

public interface IAppSettingsService
{
    Task<string?> GetLastServerBaseAsync(CancellationToken cancellationToken);

    Task SaveLastServerBaseAsync(string serverBase, CancellationToken cancellationToken);

    Task ClearLastServerBaseAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetRecentServerBasesAsync(CancellationToken cancellationToken);

    Task RemoveRecentServerBaseAsync(string serverBase, CancellationToken cancellationToken);

    Task<string?> GetDeviceIdAsync(CancellationToken cancellationToken);

    Task SaveDeviceIdAsync(string deviceId, CancellationToken cancellationToken);

    Task<PlayerPreferences> GetPlayerPreferencesAsync(CancellationToken cancellationToken);

    Task SavePlayerPreferencesAsync(PlayerPreferences preferences, CancellationToken cancellationToken);

    Task<PlayerPreferences> UpdatePlayerPreferencesAsync(
        Func<PlayerPreferences, PlayerPreferences> update,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetSearchHistoryAsync(CancellationToken cancellationToken);

    Task SaveSearchHistoryAsync(IReadOnlyList<string> searchHistory, CancellationToken cancellationToken);

    Task<AuthSessionMetadata?> GetAuthSessionMetadataAsync(CancellationToken cancellationToken);

    Task SaveAuthSessionMetadataAsync(AuthSessionMetadata metadata, CancellationToken cancellationToken);

    Task ClearAuthSessionMetadataAsync(CancellationToken cancellationToken);
}
