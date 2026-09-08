using System.Text.Json;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Core.Tests;

[TestClass]
public sealed class FileAppSettingsServiceTests
{
    [TestMethod]
    public async Task SaveLastServerBaseAsync_SavesAndLoadsServerBase()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var service = new FileAppSettingsService(settingsFilePath);

        await service.SaveLastServerBaseAsync("http://media.local:8096", CancellationToken.None);
        var loadedServerBase = await service.GetLastServerBaseAsync(CancellationToken.None);

        Assert.AreEqual("http://media.local:8096", loadedServerBase);
    }

    [TestMethod]
    public async Task SaveLastServerBaseAsync_DoesNotWriteCredentialsOrTokenFields()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var service = new FileAppSettingsService(settingsFilePath);

        await service.SaveLastServerBaseAsync("http://media.local:8096", CancellationToken.None);
        var fileContent = await File.ReadAllTextAsync(settingsFilePath);

        Assert.IsFalse(fileContent.Contains("username", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(fileContent.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(fileContent.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(fileContent.Contains("accessToken", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(fileContent.Contains("Pw", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task RecentServers_PersistSuccessfulAddressesInMostRecentOrderAndLimitToFive()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var service = new FileAppSettingsService(settingsFilePath);
        foreach (var index in Enumerable.Range(1, 6))
        {
            await service.SaveLastServerBaseAsync($"https://media-{index}.local", CancellationToken.None);
        }
        await service.SaveLastServerBaseAsync(" HTTPS://MEDIA-3.LOCAL:443/ ", CancellationToken.None);

        var reopened = new FileAppSettingsService(settingsFilePath);
        CollectionAssert.AreEqual(
            new[] { "https://media-3.local", "https://media-6.local", "https://media-5.local", "https://media-4.local", "https://media-2.local" },
            (await reopened.GetRecentServerBasesAsync(CancellationToken.None)).ToArray());
        Assert.AreEqual("https://media-3.local", await reopened.GetLastServerBaseAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task RecentServers_NormalizeHostAndSchemeButPreserveCaseSensitivePaths()
    {
        var service = new FileAppSettingsService(CreateSettingsFilePath());
        await service.SaveLastServerBaseAsync("HTTP://MEDIA.LOCAL:80/Emby/", CancellationToken.None);
        await service.SaveLastServerBaseAsync("http://media.local/emby", CancellationToken.None);
        await service.SaveLastServerBaseAsync("media.local/Emby", CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "http://media.local/Emby", "http://media.local/emby" },
            (await service.GetRecentServerBasesAsync(CancellationToken.None)).ToArray());
        await service.RemoveRecentServerBaseAsync("HTTP://MEDIA.LOCAL:80/Emby/", CancellationToken.None);
        CollectionAssert.AreEqual(
            new[] { "http://media.local/emby" },
            (await service.GetRecentServerBasesAsync(CancellationToken.None)).ToArray());
    }

    [TestMethod]
    public async Task RecentServers_DoNotPersistUrlCredentialsQueryOrFragment()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var service = new FileAppSettingsService(settingsFilePath);
        await service.SaveLastServerBaseAsync(
            "https://private-user:private-password@MEDIA.LOCAL:443/Emby/?api_key=test-access-token#private-fragment",
            CancellationToken.None);

        Assert.AreEqual("https://media.local/Emby", await service.GetLastServerBaseAsync(CancellationToken.None));
        CollectionAssert.AreEqual(new[] { "https://media.local/Emby" },
            (await service.GetRecentServerBasesAsync(CancellationToken.None)).ToArray());
        var content = await File.ReadAllTextAsync(settingsFilePath);
        Assert.IsFalse(content.Contains("private-", StringComparison.Ordinal));
        Assert.IsFalse(content.Contains("test-access-token", StringComparison.Ordinal));
        Assert.IsFalse(content.Contains("api_key", StringComparison.Ordinal));
    }

    [DataTestMethod]
    [DataRow("{\"LastServerBase\":\"HTTP://MEDIA.LOCAL:80/Emby/\"}")]
    [DataRow("{\"LastServerBase\":\"HTTP://MEDIA.LOCAL:80/Emby/\",\"RecentServerBases\":null}")]
    public async Task RecentServers_LegacyLastServerIsDisplayedAndRetainedWhenConnectingElsewhere(string legacyJson)
    {
        var settingsFilePath = CreateSettingsFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFilePath)!);
        await File.WriteAllTextAsync(settingsFilePath, legacyJson);
        var service = new FileAppSettingsService(settingsFilePath);

        CollectionAssert.AreEqual(new[] { "http://media.local/Emby" },
            (await service.GetRecentServerBasesAsync(CancellationToken.None)).ToArray());
        await service.SaveLastServerBaseAsync("https://other.local", CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "https://other.local", "http://media.local/Emby" },
            (await new FileAppSettingsService(settingsFilePath).GetRecentServerBasesAsync(CancellationToken.None)).ToArray());
    }

    [TestMethod]
    public async Task RecentServers_RemovingLegacyEntryPersistsEmptyListWithoutChangingCurrentServerOrRevivingIt()
    {
        var settingsFilePath = CreateSettingsFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFilePath)!);
        await File.WriteAllTextAsync(settingsFilePath, "{\"LastServerBase\":\"http://media.local:8096\"}");
        var service = new FileAppSettingsService(settingsFilePath);

        await service.RemoveRecentServerBaseAsync("HTTP://MEDIA.LOCAL:8096/", CancellationToken.None);
        var reopened = new FileAppSettingsService(settingsFilePath);
        Assert.AreEqual("http://media.local:8096", await reopened.GetLastServerBaseAsync(CancellationToken.None));
        Assert.AreEqual(0, (await reopened.GetRecentServerBasesAsync(CancellationToken.None)).Count);
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(settingsFilePath)))
        {
            Assert.AreEqual(0, document.RootElement.GetProperty("RecentServerBases").GetArrayLength());
        }

        await reopened.ClearAuthSessionMetadataAsync(CancellationToken.None);
        await reopened.ClearLastServerBaseAsync(CancellationToken.None);
        Assert.AreEqual(0, (await new FileAppSettingsService(settingsFilePath).GetRecentServerBasesAsync(CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task RecentServers_SwitchingServerPreservesLegacyHistoryBeforeClearingLastServer()
    {
        var settingsFilePath = CreateSettingsFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFilePath)!);
        await File.WriteAllTextAsync(settingsFilePath, "{\"LastServerBase\":\"https://media.local/Emby/\"}");
        var service = new FileAppSettingsService(settingsFilePath);

        await service.ClearLastServerBaseAsync(CancellationToken.None);

        var reopened = new FileAppSettingsService(settingsFilePath);
        Assert.IsNull(await reopened.GetLastServerBaseAsync(CancellationToken.None));
        CollectionAssert.AreEqual(new[] { "https://media.local/Emby" },
            (await reopened.GetRecentServerBasesAsync(CancellationToken.None)).ToArray());
    }

    [TestMethod]
    public async Task RecentServers_RemovalPreservesAuthenticationAndCurrentServer()
    {
        var service = new FileAppSettingsService(CreateSettingsFilePath());
        await service.SaveLastServerBaseAsync("http://old.local", CancellationToken.None);
        await service.SaveLastServerBaseAsync("https://current.local", CancellationToken.None);
        var metadata = new AuthSessionMetadata("https://current.local", "user-1", "Test User", "server-1");
        await service.SaveAuthSessionMetadataAsync(metadata, CancellationToken.None);

        await service.RemoveRecentServerBaseAsync("https://current.local", CancellationToken.None);

        Assert.AreEqual("https://current.local", await service.GetLastServerBaseAsync(CancellationToken.None));
        var storedMetadata = await service.GetAuthSessionMetadataAsync(CancellationToken.None);
        Assert.AreEqual(metadata.ServerBase, storedMetadata!.ServerBase);
        Assert.AreEqual(metadata.UserId, storedMetadata.UserId);
        Assert.AreEqual(metadata.UserName, storedMetadata.UserName);
        Assert.AreEqual(metadata.ServerId, storedMetadata.ServerId);
        CollectionAssert.AreEqual(new[] { "http://old.local" },
            (await service.GetRecentServerBasesAsync(CancellationToken.None)).ToArray());
        await service.ClearAuthSessionMetadataAsync(CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "http://old.local" },
            (await service.GetRecentServerBasesAsync(CancellationToken.None)).ToArray());
    }

    [TestMethod]
    public async Task RecentServers_StoredHistoryNormalizesValidEntriesWithoutFallingBackToLast()
    {
        var settingsFilePath = CreateSettingsFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFilePath)!);
        await File.WriteAllTextAsync(settingsFilePath,
            """
            {
                "LastServerBase": "https://excluded.local",
                "RecentServerBases": [ "", "ftp://invalid.local", "HTTPS://MEDIA.LOCAL:443/Emby/",
                    "https://media.local/Emby?api_key=ignored", "https://media.local/emby" ]
            }
            """);
        var service = new FileAppSettingsService(settingsFilePath);

        CollectionAssert.AreEqual(new[] { "https://media.local/Emby", "https://media.local/emby" },
            (await service.GetRecentServerBasesAsync(CancellationToken.None)).ToArray());
    }

    [TestMethod]
    public async Task RecentServers_ConcurrentMutationsPreserveOtherSettingsAndHistory()
    {
        var service = new FileAppSettingsService(CreateSettingsFilePath());
        await service.SaveLastServerBaseAsync("https://removed.local", CancellationToken.None);
        await service.SaveLastServerBaseAsync("https://retained.local", CancellationToken.None);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preferences = PlayerPreferences.Default with { SeekSeconds = 25 };
        var metadata = new AuthSessionMetadata("https://new.local", "user-1", "Test User", "server-1");
        async Task StartTogetherAsync(Func<Task> update)
        {
            await start.Task;
            await update();
        }
        var updates = new[]
        {
            StartTogetherAsync(() => service.SaveLastServerBaseAsync("https://new.local", CancellationToken.None)),
            StartTogetherAsync(() => service.RemoveRecentServerBaseAsync("https://removed.local", CancellationToken.None)),
            StartTogetherAsync(() => service.SaveDeviceIdAsync("device-1", CancellationToken.None)),
            StartTogetherAsync(() => service.SavePlayerPreferencesAsync(preferences, CancellationToken.None)),
            StartTogetherAsync(() => service.SaveSearchHistoryAsync(new[] { "Movie" }, CancellationToken.None)),
            StartTogetherAsync(() => service.SaveAuthSessionMetadataAsync(metadata, CancellationToken.None))
        };
        start.SetResult();
        await Task.WhenAll(updates);

        CollectionAssert.AreEqual(new[] { "https://new.local", "https://retained.local" },
            (await service.GetRecentServerBasesAsync(CancellationToken.None)).ToArray());
        Assert.AreEqual("https://new.local", await service.GetLastServerBaseAsync(CancellationToken.None));
        Assert.AreEqual("device-1", await service.GetDeviceIdAsync(CancellationToken.None));
        Assert.AreEqual(preferences, await service.GetPlayerPreferencesAsync(CancellationToken.None));
        var storedMetadata = await service.GetAuthSessionMetadataAsync(CancellationToken.None);
        Assert.AreEqual(metadata.ServerBase, storedMetadata!.ServerBase);
        Assert.AreEqual(metadata.UserId, storedMetadata.UserId);
        Assert.AreEqual(metadata.UserName, storedMetadata.UserName);
        Assert.AreEqual(metadata.ServerId, storedMetadata.ServerId);
        CollectionAssert.AreEqual(new[] { "Movie" }, (await service.GetSearchHistoryAsync(CancellationToken.None)).ToArray());
    }

    [TestMethod]
    public async Task DeviceId_FirstRun_GeneratesAndSavesDeviceId()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var settingsService = new FileAppSettingsService(settingsFilePath);
        var deviceIdService = new SettingsDeviceIdService(settingsService);

        var deviceId = await deviceIdService.GetOrCreateDeviceIdAsync(CancellationToken.None);
        var savedDeviceId = await settingsService.GetDeviceIdAsync(CancellationToken.None);

        Assert.IsFalse(string.IsNullOrWhiteSpace(deviceId));
        Assert.AreEqual(deviceId, savedDeviceId);
    }

    [TestMethod]
    public async Task DeviceId_ExistingValue_ReusesStableDeviceId()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var settingsService = new FileAppSettingsService(settingsFilePath);
        var deviceIdService = new SettingsDeviceIdService(settingsService);

        await settingsService.SaveDeviceIdAsync("stable-device-id", CancellationToken.None);

        var firstDeviceId = await deviceIdService.GetOrCreateDeviceIdAsync(CancellationToken.None);
        var secondDeviceId = await deviceIdService.GetOrCreateDeviceIdAsync(CancellationToken.None);

        Assert.AreEqual("stable-device-id", firstDeviceId);
        Assert.AreEqual("stable-device-id", secondDeviceId);
    }

    [TestMethod]
    public async Task DeviceId_ConcurrentFirstRun_ReturnsOneStableValue()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var settingsService = new FileAppSettingsService(settingsFilePath);
        var deviceIdService = new SettingsDeviceIdService(settingsService);

        var deviceIds = await Task.WhenAll(
            Enumerable.Range(0, 20)
                .Select(_ => deviceIdService.GetOrCreateDeviceIdAsync(CancellationToken.None)));

        Assert.AreEqual(1, deviceIds.Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(deviceIds[0], await settingsService.GetDeviceIdAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task DeviceId_FirstSaveFails_SecondCallRetriesAndPersistsValue()
    {
        var settingsService = new RetryableDeviceSettingsService();
        var deviceIdService = new SettingsDeviceIdService(settingsService);

        await Assert.ThrowsExceptionAsync<IOException>(
            () => deviceIdService.GetOrCreateDeviceIdAsync(CancellationToken.None));
        var deviceId = await deviceIdService.GetOrCreateDeviceIdAsync(CancellationToken.None);

        Assert.AreEqual(2, settingsService.SaveCallCount);
        Assert.AreEqual(deviceId, settingsService.DeviceId);
        Assert.AreEqual(32, deviceId.Length);
    }

    [TestMethod]
    public async Task SaveAuthSessionMetadataAsync_DoesNotWriteAccessTokenOrPassword()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var service = new FileAppSettingsService(settingsFilePath);

        await service.SaveLastServerBaseAsync("http://media.local:8096", CancellationToken.None);
        await service.SaveDeviceIdAsync("stable-device-id", CancellationToken.None);
        await service.SaveAuthSessionMetadataAsync(
            new AuthSessionMetadata(
                "http://media.local:8096",
                "user-1",
                "Test User",
                "server-1"),
            CancellationToken.None);

        var fileContent = await File.ReadAllTextAsync(settingsFilePath);

        StringAssert.Contains(fileContent, "LastServerBase");
        StringAssert.Contains(fileContent, "DeviceId");
        StringAssert.Contains(fileContent, "UserId");
        StringAssert.Contains(fileContent, "UserName");
        StringAssert.Contains(fileContent, "ServerId");
        Assert.IsFalse(fileContent.Contains("test-access-token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(fileContent.Contains("AccessToken", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(fileContent.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(fileContent.Contains("Pw", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ClearAuthSessionMetadataAsync_PreservesLastServerBaseAndDeviceId()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var service = new FileAppSettingsService(settingsFilePath);

        await service.SaveDeviceIdAsync("stable-device-id", CancellationToken.None);
        await service.SaveAuthSessionMetadataAsync(
            new AuthSessionMetadata(
                "http://media.local:8096",
                "user-1",
                "Test User",
                "server-1"),
            CancellationToken.None);

        await service.ClearAuthSessionMetadataAsync(CancellationToken.None);

        Assert.AreEqual("http://media.local:8096", await service.GetLastServerBaseAsync(CancellationToken.None));
        Assert.AreEqual("stable-device-id", await service.GetDeviceIdAsync(CancellationToken.None));
        Assert.IsNull(await service.GetAuthSessionMetadataAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task ClearLastServerBaseAsync_ClearsOnlyServerAndPreservesLocalPreferences()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var service = new FileAppSettingsService(settingsFilePath);
        var preferences = PlayerPreferences.Default with { SeekSeconds = 30 };

        await service.SaveDeviceIdAsync("stable-device-id", CancellationToken.None);
        await service.SavePlayerPreferencesAsync(preferences, CancellationToken.None);
        await service.SaveSearchHistoryAsync(new[] { "Movie", "Series" }, CancellationToken.None);
        await service.SaveAuthSessionMetadataAsync(
            new AuthSessionMetadata(
                "http://media.local:8096",
                "user-1",
                "Test User",
                "server-1"),
            CancellationToken.None);

        await service.ClearLastServerBaseAsync(CancellationToken.None);

        Assert.IsNull(await service.GetLastServerBaseAsync(CancellationToken.None));
        Assert.AreEqual("stable-device-id", await service.GetDeviceIdAsync(CancellationToken.None));
        Assert.AreEqual(preferences, await service.GetPlayerPreferencesAsync(CancellationToken.None));
        CollectionAssert.AreEqual(
            new[] { "Movie", "Series" },
            (await service.GetSearchHistoryAsync(CancellationToken.None)).ToArray());
        var fileContent = await File.ReadAllTextAsync(settingsFilePath);
        StringAssert.Contains(fileContent, "UserId");
        Assert.IsFalse(fileContent.Contains("LastServerBase", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GetPlayerPreferencesAsync_WithoutSavedValue_ReturnsDefaults()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var service = new FileAppSettingsService(settingsFilePath);

        var preferences = await service.GetPlayerPreferencesAsync(CancellationToken.None);

        Assert.AreEqual(PlayerPreferences.Default, preferences);
        Assert.IsTrue(preferences.AutoPlayNextEpisode);
    }

    [TestMethod]
    public async Task SavePlayerPreferencesAsync_SavesNonSensitiveNormalizedPreferences()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var service = new FileAppSettingsService(settingsFilePath);

        await service.SavePlayerPreferencesAsync(
            new PlayerPreferences(120, 500, -1, " zh ", " jpn ", true, 120, false),
            CancellationToken.None);

        var preferences = await service.GetPlayerPreferencesAsync(CancellationToken.None);
        var fileContent = await File.ReadAllTextAsync(settingsFilePath);

        Assert.AreEqual(100, preferences.DefaultVolume);
        Assert.AreEqual(60, preferences.SeekSeconds);
        Assert.AreEqual(1, preferences.ControlsHideSeconds);
        Assert.AreEqual("zh", preferences.DefaultSubtitleLanguage);
        Assert.AreEqual("jpn", preferences.DefaultAudioLanguage);
        Assert.IsTrue(preferences.RememberLastVolume);
        Assert.AreEqual(100, preferences.LastVolume);
        Assert.IsFalse(preferences.AutoPlayNextEpisode);
        Assert.IsFalse(fileContent.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(fileContent.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task GetPlayerPreferencesAsync_CorruptSettings_ReturnsDefaults()
    {
        var settingsFilePath = CreateSettingsFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFilePath)!);
        await File.WriteAllTextAsync(settingsFilePath, "{not valid json");
        var service = new FileAppSettingsService(settingsFilePath);

        var preferences = await service.GetPlayerPreferencesAsync(CancellationToken.None);

        Assert.AreEqual(PlayerPreferences.Default, preferences);
    }

    [TestMethod]
    public async Task GetPlayerPreferencesAsync_UnknownFieldsAreIgnoredAndValuesAreClamped()
    {
        var settingsFilePath = CreateSettingsFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFilePath)!);
        await File.WriteAllTextAsync(
            settingsFilePath,
            """
            {
              "Unknown": "ignored",
              "PlayerPreferences": {
                "DefaultVolume": -20,
                "SeekSeconds": 999,
                "ControlsHideSeconds": 0,
                "DefaultSubtitleLanguage": " zh ",
                "DefaultAudioLanguage": " eng ",
                "RememberLastVolume": true,
                "LastVolume": -5,
                "AutoPlayNextEpisode": false
              }
            }
            """);
        var service = new FileAppSettingsService(settingsFilePath);

        var preferences = await service.GetPlayerPreferencesAsync(CancellationToken.None);

        Assert.AreEqual(0, preferences.DefaultVolume);
        Assert.AreEqual(60, preferences.SeekSeconds);
        Assert.AreEqual(1, preferences.ControlsHideSeconds);
        Assert.AreEqual("zh", preferences.DefaultSubtitleLanguage);
        Assert.AreEqual("eng", preferences.DefaultAudioLanguage);
        Assert.IsTrue(preferences.RememberLastVolume);
        Assert.AreEqual(0, preferences.LastVolume);
        Assert.IsFalse(preferences.AutoPlayNextEpisode);
    }

    [TestMethod]
    public async Task SavePlayerPreferencesAsync_DoesNotPersistSensitiveFields()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var service = new FileAppSettingsService(settingsFilePath);

        await service.SavePlayerPreferencesAsync(PlayerPreferences.Default, CancellationToken.None);

        var fileContent = await File.ReadAllTextAsync(settingsFilePath);
        Assert.IsFalse(fileContent.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(fileContent.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(fileContent.Contains("accessToken", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task SaveSearchHistoryAsync_NormalizesDeduplicatesAndKeepsEightEntries()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var service = new FileAppSettingsService(settingsFilePath);

        await service.SaveSearchHistoryAsync(
            new[] { " Movie ", "movie", "Series", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine" },
            CancellationToken.None);
        var history = await service.GetSearchHistoryAsync(CancellationToken.None);

        Assert.AreEqual(8, history.Count);
        Assert.AreEqual("Movie", history[0]);
        Assert.AreEqual(1, history.Count(value => string.Equals(value, "movie", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(history.Contains("Nine"));
    }

    [TestMethod]
    public async Task ConcurrentDifferentSetters_PreserveEveryUpdatedSection()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var service = new FileAppSettingsService(settingsFilePath);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preferences = PlayerPreferences.Default with { SeekSeconds = 25 };

        async Task StartTogetherAsync(Func<Task> update)
        {
            await start.Task;
            await update();
        }

        var updates = new[]
        {
            StartTogetherAsync(() => service.SaveLastServerBaseAsync("http://media.local:8096", CancellationToken.None)),
            StartTogetherAsync(() => service.SaveDeviceIdAsync("device-1", CancellationToken.None)),
            StartTogetherAsync(() => service.SavePlayerPreferencesAsync(preferences, CancellationToken.None)),
            StartTogetherAsync(() => service.SaveSearchHistoryAsync(new[] { "Movie", "Series" }, CancellationToken.None))
        };

        start.TrySetResult();
        await Task.WhenAll(updates);

        Assert.AreEqual("http://media.local:8096", await service.GetLastServerBaseAsync(CancellationToken.None));
        Assert.AreEqual("device-1", await service.GetDeviceIdAsync(CancellationToken.None));
        Assert.AreEqual(preferences, await service.GetPlayerPreferencesAsync(CancellationToken.None));
        CollectionAssert.AreEqual(
            new[] { "Movie", "Series" },
            (await service.GetSearchHistoryAsync(CancellationToken.None)).ToArray());
    }

    [TestMethod]
    public async Task ConcurrentRepeatedWrites_KeepPrimaryJsonValid()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var service = new FileAppSettingsService(settingsFilePath);
        for (var index = 0; index < 25; index++)
        {
            await Task.WhenAll(
                service.SaveLastServerBaseAsync($"http://media-{index}.local:8096", CancellationToken.None),
                service.SaveDeviceIdAsync($"device-{index}", CancellationToken.None),
                service.SaveSearchHistoryAsync(new[] { $"query-{index}" }, CancellationToken.None));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(settingsFilePath));
            Assert.AreEqual(JsonValueKind.Object, document.RootElement.ValueKind);
            if (index == 24)
            {
                Assert.AreEqual("device-24", document.RootElement.GetProperty("DeviceId").GetString());
            }
        }
    }

    [TestMethod]
    public async Task SecondSave_BackupContainsPreviousPrimaryVersion()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var service = new FileAppSettingsService(settingsFilePath);
        await service.SaveLastServerBaseAsync("http://media.local:8096", CancellationToken.None);
        var firstVersion = await File.ReadAllTextAsync(settingsFilePath);

        await service.SaveDeviceIdAsync("device-1", CancellationToken.None);

        Assert.AreEqual(firstVersion, await File.ReadAllTextAsync($"{settingsFilePath}.bak"));
        using var current = JsonDocument.Parse(await File.ReadAllTextAsync(settingsFilePath));
        Assert.AreEqual("device-1", current.RootElement.GetProperty("DeviceId").GetString());
    }

    [DataTestMethod]
    [DataRow("corrupt")]
    [DataRow("empty")]
    [DataRow("missing")]
    public async Task InvalidOrMissingPrimary_ValidBackupRestoresPrimaryAndPreservesBackup(string primaryState)
    {
        var settingsFilePath = CreateSettingsFilePath();
        var directoryPath = Path.GetDirectoryName(settingsFilePath)!;
        Directory.CreateDirectory(directoryPath);
        var backupFilePath = $"{settingsFilePath}.bak";
        const string backupJson = """
            {
              "LastServerBase": "http://backup.local:8096",
              "DeviceId": "backup-device"
            }
            """;
        await File.WriteAllTextAsync(backupFilePath, backupJson);
        if (primaryState == "corrupt")
        {
            await File.WriteAllTextAsync(settingsFilePath, "{not valid json");
        }
        else if (primaryState == "empty")
        {
            await File.WriteAllTextAsync(settingsFilePath, string.Empty);
        }

        var service = new FileAppSettingsService(settingsFilePath);

        Assert.AreEqual("http://backup.local:8096", await service.GetLastServerBaseAsync(CancellationToken.None));
        Assert.AreEqual(backupJson, await File.ReadAllTextAsync(backupFilePath));
        using var restored = JsonDocument.Parse(await File.ReadAllTextAsync(settingsFilePath));
        Assert.AreEqual("backup-device", restored.RootElement.GetProperty("DeviceId").GetString());
    }

    [TestMethod]
    public async Task CorruptPrimaryAndBackup_ReturnDefaultsAndPreserveBothFiles()
    {
        var settingsFilePath = CreateSettingsFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFilePath)!);
        const string corruptPrimary = "{bad primary";
        const string corruptBackup = "{bad backup";
        await File.WriteAllTextAsync(settingsFilePath, corruptPrimary);
        await File.WriteAllTextAsync($"{settingsFilePath}.bak", corruptBackup);
        var service = new FileAppSettingsService(settingsFilePath);

        Assert.IsNull(await service.GetLastServerBaseAsync(CancellationToken.None));
        Assert.AreEqual(PlayerPreferences.Default, await service.GetPlayerPreferencesAsync(CancellationToken.None));
        Assert.AreEqual(corruptPrimary, await File.ReadAllTextAsync(settingsFilePath));
        Assert.AreEqual(corruptBackup, await File.ReadAllTextAsync($"{settingsFilePath}.bak"));
    }

    [TestMethod]
    public async Task CancelledSave_LeavesPrimaryUnchangedAndNoTempFile()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var service = new FileAppSettingsService(settingsFilePath);
        await service.SaveLastServerBaseAsync("http://media.local:8096", CancellationToken.None);
        var originalPrimary = await File.ReadAllTextAsync(settingsFilePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(
            () => service.SaveDeviceIdAsync("device-1", cancellation.Token));

        Assert.AreEqual(originalPrimary, await File.ReadAllTextAsync(settingsFilePath));
        Assert.AreEqual(
            0,
            Directory.GetFiles(
                Path.GetDirectoryName(settingsFilePath)!,
                $"{Path.GetFileName(settingsFilePath)}.*.tmp").Length);
    }

    [TestMethod]
    public async Task CancelledWait_DoesNotReleaseGateOwnedByAnotherUpdate()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var service = new FileAppSettingsService(settingsFilePath);
        await service.SavePlayerPreferencesAsync(PlayerPreferences.Default, CancellationToken.None);
        using var updateEntered = new ManualResetEventSlim();
        using var releaseUpdate = new ManualResetEventSlim();
        var holdingUpdate = Task.Run(() => service.UpdatePlayerPreferencesAsync(
            current =>
            {
                updateEntered.Set();
                releaseUpdate.Wait();
                return current with { DefaultVolume = 64 };
            },
            CancellationToken.None));
        Assert.IsTrue(updateEntered.Wait(TimeSpan.FromSeconds(5)));
        Task? followingUpdate = null;

        try
        {
            using var cancellation = new CancellationTokenSource();
            var cancelledUpdate = service.SaveDeviceIdAsync("cancelled-device", cancellation.Token);
            cancellation.Cancel();

            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => cancelledUpdate);
            followingUpdate = service.SaveSearchHistoryAsync(new[] { "Movie" }, CancellationToken.None);
            Assert.IsFalse(followingUpdate.IsCompleted);
        }
        finally
        {
            releaseUpdate.Set();
        }

        await holdingUpdate;
        await followingUpdate!;
        Assert.IsNull(await service.GetDeviceIdAsync(CancellationToken.None));
        CollectionAssert.AreEqual(
            new[] { "Movie" },
            (await service.GetSearchHistoryAsync(CancellationToken.None)).ToArray());
    }

    [TestMethod]
    public async Task AtomicPreferenceUpdates_PreserveSettingsFieldsAndLatestVolume()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var service = new FileAppSettingsService(settingsFilePath);
        await service.SavePlayerPreferencesAsync(
            PlayerPreferences.Default with { RememberLastVolume = true, LastVolume = 25 },
            CancellationToken.None);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task UpdateSettingsAsync()
        {
            await start.Task;
            await service.UpdatePlayerPreferencesAsync(
                current => current with { DefaultVolume = 64, SeekSeconds = 30 },
                CancellationToken.None);
        }

        async Task UpdateVolumeAsync()
        {
            await start.Task;
            await service.UpdatePlayerPreferencesAsync(
                current => current with { LastVolume = 77 },
                CancellationToken.None);
        }

        var settingsUpdate = UpdateSettingsAsync();
        var volumeUpdate = UpdateVolumeAsync();
        start.TrySetResult();
        await Task.WhenAll(settingsUpdate, volumeUpdate);

        var preferences = await service.GetPlayerPreferencesAsync(CancellationToken.None);
        Assert.AreEqual(64, preferences.DefaultVolume);
        Assert.AreEqual(30, preferences.SeekSeconds);
        Assert.AreEqual(77, preferences.LastVolume);
    }

    private static string CreateSettingsFilePath()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "EmbyPlayer.Tests", Guid.NewGuid().ToString("N"));
        return Path.Combine(directoryPath, "settings.json");
    }

    private sealed class RetryableDeviceSettingsService : IAppSettingsService
    {
        public string? DeviceId { get; private set; }

        public int SaveCallCount { get; private set; }

        public Task<string?> GetDeviceIdAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(DeviceId);
        }

        public Task SaveDeviceIdAsync(string deviceId, CancellationToken cancellationToken)
        {
            SaveCallCount++;
            if (SaveCallCount == 1)
            {
                throw new IOException("settings failure");
            }

            DeviceId = deviceId;
            return Task.CompletedTask;
        }

        public Task<string?> GetLastServerBaseAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveLastServerBaseAsync(string serverBase, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ClearLastServerBaseAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetRecentServerBasesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveRecentServerBaseAsync(string serverBase, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PlayerPreferences> GetPlayerPreferencesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SavePlayerPreferencesAsync(PlayerPreferences preferences, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PlayerPreferences> UpdatePlayerPreferencesAsync(Func<PlayerPreferences, PlayerPreferences> update, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetSearchHistoryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveSearchHistoryAsync(IReadOnlyList<string> searchHistory, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AuthSessionMetadata?> GetAuthSessionMetadataAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveAuthSessionMetadataAsync(AuthSessionMetadata metadata, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ClearAuthSessionMetadataAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
