using EmbyPlayer.Core.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Core.Tests;

[TestClass]
public sealed class PlayerShortcutBindingsTests
{
    [DataTestMethod]
    [DataRow("Escape")]
    [DataRow("Tab")]
    [DataRow("Alt+F4")]
    [DataRow("Alt+Space")]
    [DataRow("Alt+Left")]
    [DataRow("Ctrl+Alt+Delete")]
    [DataRow("Win+L")]
    [DataRow("Ctrl+Ctrl+A")]
    [DataRow("F10")]
    public void ReservedOrInvalidKeys_AreRejected(string gesture)
    {
        Assert.IsFalse(PlayerShortcutGesture.TryNormalize(gesture, out _, out var error));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    public void BindingChange_NormalizesModifiersRejectsConflictsAndAllowsClearing()
    {
        var original = PlayerShortcutBindings.Default;
        Assert.IsTrue(original.TryChange(PlayerShortcutAction.ToggleMute, " shift + ctrl + k ", out var custom, out _));
        Assert.AreEqual("Ctrl+Shift+K", custom.ToggleMute);
        Assert.IsTrue(PlayerShortcutGesture.TryNormalize("alt+ctrl+k", out var controlAlt, out _));
        Assert.AreEqual("Ctrl+Alt+K", controlAlt);
        Assert.IsFalse(custom.TryChange(PlayerShortcutAction.NextSubtitle, "Ctrl+Shift+K", out var unchanged, out var error));
        Assert.AreEqual(custom, unchanged);
        StringAssert.Contains(error, "静音");
        Assert.IsTrue(custom.TryChange(PlayerShortcutAction.ToggleMute, "", out var cleared, out _));
        Assert.IsTrue(cleared.TryChange(PlayerShortcutAction.NextSubtitle, "Ctrl+Shift+K", out _, out _));
    }

    [TestMethod]
    public void Normalize_InvalidPersistedMappingRestoresSafeDefaultsAndDefaultEquality()
    {
        var preferences = PlayerPreferences.Default with { Shortcuts = new PlayerShortcutBindings(ToggleMute: "Space") };
        Assert.AreEqual(PlayerPreferences.Default, preferences.Normalize());
        Assert.AreEqual(PlayerPreferences.Default, (PlayerPreferences.Default with { Shortcuts = PlayerShortcutBindings.Default }).Normalize());
        Assert.AreEqual("Space", preferences.Normalize().EffectiveShortcuts.TogglePlayPause);
    }

    [TestMethod]
    public async Task CustomBindings_PersistAcrossServiceInstancesAndConcurrentVolumeUpdates()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EmbyPlayer.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var service = new FileAppSettingsService(path);
            var custom = PlayerShortcutBindings.Default with { ToggleMute = "Ctrl+Shift+K", NextSubtitle = string.Empty };
            await service.SavePlayerPreferencesAsync(PlayerPreferences.Default with { Shortcuts = custom }, CancellationToken.None);
            await Task.WhenAll(
                service.UpdatePlayerPreferencesAsync(value => value with { LastVolume = 47 }, CancellationToken.None),
                service.SaveDeviceIdAsync("test-device", CancellationToken.None));
            var restarted = new FileAppSettingsService(path);
            var loaded = await restarted.GetPlayerPreferencesAsync(CancellationToken.None);
            Assert.AreEqual(custom, loaded.EffectiveShortcuts);
            Assert.AreEqual(47, loaded.LastVolume);
            Assert.AreEqual("test-device", await restarted.GetDeviceIdAsync(CancellationToken.None));
            await restarted.SavePlayerPreferencesAsync(loaded with { Shortcuts = null }, CancellationToken.None);
            Assert.AreEqual(PlayerShortcutBindings.Default, (await new FileAppSettingsService(path).GetPlayerPreferencesAsync(CancellationToken.None)).EffectiveShortcuts);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task LegacyPreferences_LoadDefaultShortcutsWithoutChangingOtherValues()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EmbyPlayer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"PlayerPreferences\":{\"DefaultVolume\":62,\"SeekSeconds\":15}}");
            var loaded = await new FileAppSettingsService(path).GetPlayerPreferencesAsync(CancellationToken.None);
            Assert.AreEqual(62, loaded.DefaultVolume);
            Assert.AreEqual(15, loaded.SeekSeconds);
            Assert.AreEqual(PlayerShortcutBindings.Default, loaded.EffectiveShortcuts);
            await File.WriteAllTextAsync(path, "{\"PlayerPreferences\":{\"Shortcuts\":{\"ToggleMute\":\"Ctrl+K\"}}}");
            var partial = await new FileAppSettingsService(path).GetPlayerPreferencesAsync(CancellationToken.None);
            Assert.AreEqual("Space", partial.EffectiveShortcuts.TogglePlayPause);
            Assert.AreEqual("Ctrl+K", partial.EffectiveShortcuts.ToggleMute);
        }
        finally { Directory.Delete(directory, true); }
    }
}
