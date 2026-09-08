namespace EmbyPlayer.Core.Settings;

public sealed record PlayerPreferences(
    int DefaultVolume,
    int SeekSeconds,
    int ControlsHideSeconds,
    string DefaultSubtitleLanguage,
    string DefaultAudioLanguage,
    bool RememberLastVolume = false,
    int? LastVolume = null,
    bool AutoPlayNextEpisode = true,
    PlayerShortcutBindings? Shortcuts = null,
    bool DefaultSubtitlesEnabled = true)
{
    public PlayerShortcutBindings EffectiveShortcuts => Shortcuts ?? PlayerShortcutBindings.Default;

    public static PlayerPreferences Default { get; } = new(
        100,
        10,
        3,
        string.Empty,
        string.Empty,
        false,
        null,
        true);

    public PlayerPreferences Normalize()
    {
        var shortcuts = EffectiveShortcuts.Normalize();
        var normalizedLastVolume = LastVolume.HasValue
            ? Math.Clamp(LastVolume.Value, 0, 100)
            : (int?)null;
        return new PlayerPreferences(
            Math.Clamp(DefaultVolume, 0, 100),
            Math.Clamp(SeekSeconds, 5, 60),
            Math.Clamp(ControlsHideSeconds, 1, 10),
            DefaultSubtitleLanguage?.Trim() ?? string.Empty,
            DefaultAudioLanguage?.Trim() ?? string.Empty,
            RememberLastVolume,
            normalizedLastVolume,
            AutoPlayNextEpisode,
            shortcuts == PlayerShortcutBindings.Default ? null : shortcuts,
            DefaultSubtitlesEnabled);
    }
}
