using EmbyPlayer.Core.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Core.Tests;

[TestClass]
public sealed class SubtitlePreferencePersistenceTests
{
    [TestMethod]
    public async Task DefaultSubtitles_LegacyConfigurationPreservesAutomaticBehaviorAndFalseSurvivesRestartAndVolumeUpdate()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EmbyPlayer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"PlayerPreferences\":{\"DefaultSubtitleLanguage\":\"eng\",\"DefaultVolume\":61}}");
            var service = new FileAppSettingsService(path);
            var legacy = await service.GetPlayerPreferencesAsync(CancellationToken.None);
            Assert.IsTrue(legacy.DefaultSubtitlesEnabled);
            Assert.AreEqual("eng", legacy.DefaultSubtitleLanguage);
            await service.SavePlayerPreferencesAsync(legacy with { DefaultSubtitlesEnabled = false }, CancellationToken.None);
            await service.UpdatePlayerPreferencesAsync(value => value with { LastVolume = 47 }, CancellationToken.None);
            var restored = await new FileAppSettingsService(path).GetPlayerPreferencesAsync(CancellationToken.None);
            Assert.IsFalse(restored.DefaultSubtitlesEnabled);
            Assert.AreEqual("eng", restored.DefaultSubtitleLanguage);
            Assert.AreEqual(61, restored.DefaultVolume);
            Assert.AreEqual(47, restored.LastVolume);
            await service.SavePlayerPreferencesAsync(PlayerPreferences.Default, CancellationToken.None);
            Assert.IsTrue((await new FileAppSettingsService(path).GetPlayerPreferencesAsync(CancellationToken.None)).DefaultSubtitlesEnabled);
        }
        finally { Directory.Delete(directory, true); }
    }
}
