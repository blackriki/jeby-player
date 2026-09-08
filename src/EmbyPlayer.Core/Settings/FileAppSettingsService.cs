using System.Text.Json;
using System.Text.Json.Serialization;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Servers;

namespace EmbyPlayer.Core.Settings;

public sealed class FileAppSettingsService : IAppSettingsService
{
    private const int RecentServerLimit = 5;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly string settingsFilePath;
    private readonly string backupFilePath;
    // This gate intentionally coordinates only callers sharing this service instance.
    private readonly SemaphoreSlim settingsGate = new(1, 1);

    public FileAppSettingsService()
        : this(GetDefaultSettingsFilePath())
    {
    }

    public FileAppSettingsService(string settingsFilePath)
    {
        if (string.IsNullOrWhiteSpace(settingsFilePath))
        {
            throw new ArgumentException("Settings file path is required.", nameof(settingsFilePath));
        }

        this.settingsFilePath = Path.GetFullPath(settingsFilePath);
        backupFilePath = $"{this.settingsFilePath}.bak";
    }

    public Task<string?> GetLastServerBaseAsync(CancellationToken cancellationToken)
    {
        return ReadSettingsValueAsync(
            settings => string.IsNullOrWhiteSpace(settings.LastServerBase) ? null : settings.LastServerBase,
            cancellationToken);
    }

    public Task SaveLastServerBaseAsync(string serverBase, CancellationToken cancellationToken)
    {
        var normalized = ServerUrlNormalizer.Normalize(serverBase);
        if (!normalized.IsSuccess)
        {
            throw new ArgumentException("A valid HTTP or HTTPS server base is required.", nameof(serverBase));
        }

        var normalizedServerBase = normalized.Server!.ServerBase;
        return UpdateSettingsAsync(
            settings =>
            {
                settings.RecentServerBases = new[] { normalizedServerBase }
                    .Concat(MapRecentServerBases(settings))
                    .Distinct(StringComparer.Ordinal)
                    .Take(RecentServerLimit)
                    .ToList();
                settings.LastServerBase = normalizedServerBase;
            },
            cancellationToken);
    }

    public Task ClearLastServerBaseAsync(CancellationToken cancellationToken)
    {
        return UpdateSettingsAsync(
            settings =>
            {
                settings.RecentServerBases = MapRecentServerBases(settings).ToList();
                settings.LastServerBase = null;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<string>> GetRecentServerBasesAsync(CancellationToken cancellationToken)
    {
        return ReadSettingsValueAsync(MapRecentServerBases, cancellationToken);
    }

    public Task RemoveRecentServerBaseAsync(string serverBase, CancellationToken cancellationToken)
    {
        var normalized = ServerUrlNormalizer.Normalize(serverBase);
        if (!normalized.IsSuccess)
        {
            throw new ArgumentException("A valid HTTP or HTTPS server base is required.", nameof(serverBase));
        }

        return UpdateSettingsAsync(
            settings => settings.RecentServerBases = MapRecentServerBases(settings)
                .Where(value => !string.Equals(value, normalized.Server!.ServerBase, StringComparison.Ordinal))
                .ToList(),
            cancellationToken);
    }

    private static IReadOnlyList<string> MapRecentServerBases(AppSettingsDocument settings)
    {
        var candidates = settings.RecentServerBases
            ?? new List<string> { settings.LastServerBase ?? string.Empty };
        return candidates
            .Select(ServerUrlNormalizer.Normalize)
            .Where(result => result.IsSuccess)
            .Select(result => result.Server!.ServerBase)
            .Distinct(StringComparer.Ordinal)
            .Take(RecentServerLimit)
            .ToArray();
    }

    public Task<string?> GetDeviceIdAsync(CancellationToken cancellationToken)
    {
        return ReadSettingsValueAsync(
            settings => string.IsNullOrWhiteSpace(settings.DeviceId) ? null : settings.DeviceId,
            cancellationToken);
    }

    public Task SaveDeviceIdAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("Device id is required.", nameof(deviceId));
        }

        return UpdateSettingsAsync(
            settings => settings.DeviceId = deviceId,
            cancellationToken);
    }

    public Task<PlayerPreferences> GetPlayerPreferencesAsync(CancellationToken cancellationToken)
    {
        return ReadSettingsValueAsync(MapPlayerPreferences, cancellationToken);
    }

    public Task SavePlayerPreferencesAsync(
        PlayerPreferences preferences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        var normalized = preferences.Normalize();
        return UpdateSettingsAsync(
            settings => settings.PlayerPreferences = MapPlayerPreferences(normalized),
            cancellationToken);
    }

    public Task<PlayerPreferences> UpdatePlayerPreferencesAsync(
        Func<PlayerPreferences, PlayerPreferences> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        return UpdateSettingsAsync(
            settings =>
            {
                var updatedPreferences = update(MapPlayerPreferences(settings)).Normalize();
                settings.PlayerPreferences = MapPlayerPreferences(updatedPreferences);
                return updatedPreferences;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<string>> GetSearchHistoryAsync(CancellationToken cancellationToken)
    {
        return ReadSettingsValueAsync<IReadOnlyList<string>>(
            settings => settings.SearchHistory?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Take(8)
                .ToArray()
                ?? Array.Empty<string>(),
            cancellationToken);
    }

    public Task SaveSearchHistoryAsync(
        IReadOnlyList<string> searchHistory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(searchHistory);

        return UpdateSettingsAsync(
            settings => settings.SearchHistory = searchHistory
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList(),
            cancellationToken);
    }

    public Task<AuthSessionMetadata?> GetAuthSessionMetadataAsync(CancellationToken cancellationToken)
    {
        return ReadSettingsValueAsync(
            settings =>
            {
                if (string.IsNullOrWhiteSpace(settings.LastServerBase)
                    || string.IsNullOrWhiteSpace(settings.UserId)
                    || string.IsNullOrWhiteSpace(settings.UserName))
                {
                    return null;
                }

                return new AuthSessionMetadata(
                    settings.LastServerBase,
                    settings.UserId,
                    settings.UserName,
                    settings.ServerId ?? string.Empty);
            },
            cancellationToken);
    }

    public Task SaveAuthSessionMetadataAsync(
        AuthSessionMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        return UpdateSettingsAsync(
            settings =>
            {
                settings.LastServerBase = metadata.ServerBase;
                settings.UserId = metadata.UserId;
                settings.UserName = metadata.UserName;
                settings.ServerId = metadata.ServerId;
            },
            cancellationToken);
    }

    public Task ClearAuthSessionMetadataAsync(CancellationToken cancellationToken)
    {
        return UpdateSettingsAsync(
            settings =>
            {
                settings.UserId = null;
                settings.UserName = null;
                settings.ServerId = null;
            },
            cancellationToken);
    }

    private static string GetDefaultSettingsFilePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "EmbyPlayer", "settings.json");
    }

    private async Task<TResult> ReadSettingsValueAsync<TResult>(
        Func<AppSettingsDocument, TResult> readValue,
        CancellationToken cancellationToken)
    {
        await settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await ReadSettingsUnderLockAsync(cancellationToken).ConfigureAwait(false);
            return readValue(settings);
        }
        finally
        {
            settingsGate.Release();
        }
    }

    private async Task UpdateSettingsAsync(
        Action<AppSettingsDocument> update,
        CancellationToken cancellationToken)
    {
        await UpdateSettingsAsync(
            settings =>
            {
                update(settings);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResult> UpdateSettingsAsync<TResult>(
        Func<AppSettingsDocument, TResult> update,
        CancellationToken cancellationToken)
    {
        await settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await ReadSettingsUnderLockAsync(cancellationToken).ConfigureAwait(false);
            var result = update(settings);
            await WriteSettingsUnderLockAsync(settings, preserveCurrentAsBackup: true, cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
        finally
        {
            settingsGate.Release();
        }
    }

    private async Task<AppSettingsDocument> ReadSettingsUnderLockAsync(
        CancellationToken cancellationToken)
    {
        var settings = await TryReadSettingsFileAsync(settingsFilePath, cancellationToken).ConfigureAwait(false);
        if (settings is not null)
        {
            return settings;
        }

        var backupSettings = await TryReadSettingsFileAsync(backupFilePath, cancellationToken).ConfigureAwait(false);
        if (backupSettings is null)
        {
            return new AppSettingsDocument();
        }

        try
        {
            await WriteSettingsUnderLockAsync(
                    backupSettings,
                    preserveCurrentAsBackup: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The valid backup still provides a usable snapshot when primary repair is temporarily blocked.
        }

        return backupSettings;
    }

    private static async Task<AppSettingsDocument?> TryReadSettingsFileAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<AppSettingsDocument>(json);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task WriteSettingsUnderLockAsync(
        AppSettingsDocument settings,
        bool preserveCurrentAsBackup,
        CancellationToken cancellationToken)
    {
        var directoryPath = Path.GetDirectoryName(settingsFilePath)!;
        Directory.CreateDirectory(directoryPath);
        var tempFilePath = Path.Combine(
            directoryPath,
            $"{Path.GetFileName(settingsFilePath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                tempFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            {
                await JsonSerializer
                    .SerializeAsync(stream, settings, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(settingsFilePath))
            {
                File.Replace(
                    tempFilePath,
                    settingsFilePath,
                    preserveCurrentAsBackup ? backupFilePath : null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempFilePath, settingsFilePath);
            }
        }
        finally
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
    }

    private static PlayerPreferences MapPlayerPreferences(AppSettingsDocument settings)
    {
        var preferences = settings.PlayerPreferences;
        return preferences is null
            ? PlayerPreferences.Default
            : new PlayerPreferences(
                preferences.DefaultVolume ?? PlayerPreferences.Default.DefaultVolume,
                preferences.SeekSeconds ?? PlayerPreferences.Default.SeekSeconds,
                preferences.ControlsHideSeconds ?? PlayerPreferences.Default.ControlsHideSeconds,
                preferences.DefaultSubtitleLanguage ?? PlayerPreferences.Default.DefaultSubtitleLanguage,
                preferences.DefaultAudioLanguage ?? PlayerPreferences.Default.DefaultAudioLanguage,
                preferences.RememberLastVolume ?? PlayerPreferences.Default.RememberLastVolume,
                preferences.LastVolume,
                preferences.AutoPlayNextEpisode ?? PlayerPreferences.Default.AutoPlayNextEpisode,
                preferences.Shortcuts,
                preferences.DefaultSubtitlesEnabled ?? PlayerPreferences.Default.DefaultSubtitlesEnabled)
            .Normalize();
    }

    private static PlayerPreferencesDocument MapPlayerPreferences(PlayerPreferences preferences)
    {
        return new PlayerPreferencesDocument
        {
            DefaultVolume = preferences.DefaultVolume,
            SeekSeconds = preferences.SeekSeconds,
            ControlsHideSeconds = preferences.ControlsHideSeconds,
            DefaultSubtitleLanguage = preferences.DefaultSubtitleLanguage,
            DefaultAudioLanguage = preferences.DefaultAudioLanguage,
            RememberLastVolume = preferences.RememberLastVolume,
            LastVolume = preferences.LastVolume,
            AutoPlayNextEpisode = preferences.AutoPlayNextEpisode,
            Shortcuts = preferences.Shortcuts,
            DefaultSubtitlesEnabled = preferences.DefaultSubtitlesEnabled
        };
    }

    private sealed class AppSettingsDocument
    {
        public string? LastServerBase { get; set; }

        public List<string>? RecentServerBases { get; set; }

        public string? DeviceId { get; set; }

        public string? UserId { get; set; }

        public string? UserName { get; set; }

        public string? ServerId { get; set; }

        public PlayerPreferencesDocument? PlayerPreferences { get; set; }

        public List<string>? SearchHistory { get; set; }
    }

    private sealed class PlayerPreferencesDocument
    {
        public int? DefaultVolume { get; set; }

        public int? SeekSeconds { get; set; }

        public int? ControlsHideSeconds { get; set; }

        public string? DefaultSubtitleLanguage { get; set; }

        public bool? DefaultSubtitlesEnabled { get; set; }

        public string? DefaultAudioLanguage { get; set; }

        public bool? RememberLastVolume { get; set; }

        public int? LastVolume { get; set; }

        public bool? AutoPlayNextEpisode { get; set; }

        public PlayerShortcutBindings? Shortcuts { get; set; }
    }
}
