namespace EmbyPlayer.Core.Settings;

public enum PlayerShortcutAction
{
    TogglePlayPause, SeekBackward, SeekForward, VolumeUp, VolumeDown, ToggleMute,
    ToggleFullscreen, NextSubtitle, SubtitleEarlier, SubtitleLater, NextAudioTrack
}

public sealed record PlayerShortcutBindings(
    string TogglePlayPause = "Space", string SeekBackward = "Left", string SeekForward = "Right",
    string VolumeUp = "Up", string VolumeDown = "Down", string ToggleMute = "M",
    string ToggleFullscreen = "F", string NextSubtitle = "J", string SubtitleEarlier = "Ctrl+Left",
    string SubtitleLater = "Ctrl+Right", string NextAudioTrack = "A")
{
    public static PlayerShortcutBindings Default { get; } = new();

    public string Get(PlayerShortcutAction action) => action switch
    {
        PlayerShortcutAction.TogglePlayPause => TogglePlayPause,
        PlayerShortcutAction.SeekBackward => SeekBackward,
        PlayerShortcutAction.SeekForward => SeekForward,
        PlayerShortcutAction.VolumeUp => VolumeUp,
        PlayerShortcutAction.VolumeDown => VolumeDown,
        PlayerShortcutAction.ToggleMute => ToggleMute,
        PlayerShortcutAction.ToggleFullscreen => ToggleFullscreen,
        PlayerShortcutAction.NextSubtitle => NextSubtitle,
        PlayerShortcutAction.SubtitleEarlier => SubtitleEarlier,
        PlayerShortcutAction.SubtitleLater => SubtitleLater,
        PlayerShortcutAction.NextAudioTrack => NextAudioTrack,
        _ => string.Empty
    };

    public PlayerShortcutBindings With(PlayerShortcutAction action, string gesture) => action switch
    {
        PlayerShortcutAction.TogglePlayPause => this with { TogglePlayPause = gesture },
        PlayerShortcutAction.SeekBackward => this with { SeekBackward = gesture },
        PlayerShortcutAction.SeekForward => this with { SeekForward = gesture },
        PlayerShortcutAction.VolumeUp => this with { VolumeUp = gesture },
        PlayerShortcutAction.VolumeDown => this with { VolumeDown = gesture },
        PlayerShortcutAction.ToggleMute => this with { ToggleMute = gesture },
        PlayerShortcutAction.ToggleFullscreen => this with { ToggleFullscreen = gesture },
        PlayerShortcutAction.NextSubtitle => this with { NextSubtitle = gesture },
        PlayerShortcutAction.SubtitleEarlier => this with { SubtitleEarlier = gesture },
        PlayerShortcutAction.SubtitleLater => this with { SubtitleLater = gesture },
        PlayerShortcutAction.NextAudioTrack => this with { NextAudioTrack = gesture },
        _ => this
    };

    public PlayerShortcutBindings Normalize()
    {
        var normalized = this;
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in Enum.GetValues<PlayerShortcutAction>())
        {
            if (!PlayerShortcutGesture.TryNormalize(Get(action), out var gesture, out _)
                || (gesture.Length > 0 && !assigned.Add(gesture))) return Default;
            normalized = normalized.With(action, gesture);
        }
        return normalized;
    }

    public bool TryChange(PlayerShortcutAction action, string gesture, out PlayerShortcutBindings result, out string? error)
    {
        result = this;
        if (!PlayerShortcutGesture.TryNormalize(gesture, out var normalized, out error)) return false;
        foreach (var other in Enum.GetValues<PlayerShortcutAction>())
        {
            if (other != action && normalized.Length > 0 && Get(other) == normalized)
            {
                error = $"此快捷键已用于“{GetLabel(other)}”，请先清除该绑定或选择其他按键。";
                return false;
            }
        }
        result = With(action, normalized);
        return true;
    }

    public static string GetLabel(PlayerShortcutAction action) => action switch
    {
        PlayerShortcutAction.TogglePlayPause => "播放 / 暂停",
        PlayerShortcutAction.SeekBackward => "快退",
        PlayerShortcutAction.SeekForward => "快进",
        PlayerShortcutAction.VolumeUp => "增大音量",
        PlayerShortcutAction.VolumeDown => "减小音量",
        PlayerShortcutAction.ToggleMute => "静音 / 取消静音",
        PlayerShortcutAction.ToggleFullscreen => "全屏 / 退出全屏",
        PlayerShortcutAction.NextSubtitle => "切换下一条字幕",
        PlayerShortcutAction.SubtitleEarlier => "字幕提前 0.1 秒",
        PlayerShortcutAction.SubtitleLater => "字幕延后 0.1 秒",
        PlayerShortcutAction.NextAudioTrack => "切换下一条音轨",
        _ => string.Empty
    };
}

public static class PlayerShortcutGesture
{
    private static readonly HashSet<string> Keys = new(
        Enumerable.Range('A', 26).Select(value => ((char)value).ToString())
            .Concat(Enumerable.Range(0, 10).Select(value => $"D{value}"))
            .Concat(Enumerable.Range(0, 10).Select(value => $"NumPad{value}"))
            .Concat(Enumerable.Range(1, 12).Select(value => $"F{value}"))
            .Concat(new[] { "Space", "Left", "Right", "Up", "Down", "Enter", "Home", "End", "PageUp", "PageDown", "Back", "Delete", "Insert", "Add", "Subtract", "Multiply", "Divide", "Decimal" }),
        StringComparer.OrdinalIgnoreCase);

    public static bool TryNormalize(string? value, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var parts = value.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts[..^1])
        {
            if (!(part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Shift", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) || !modifiers.Add(part))
            {
                error = "请使用 Ctrl、Shift、Alt 与一个普通按键的组合，Windows 系统键不能设置。";
                return false;
            }
        }
        var key = Keys.FirstOrDefault(candidate => candidate.Equals(parts[^1], StringComparison.OrdinalIgnoreCase));
        if (key is null || key == "F10"
            || (modifiers.Contains("Alt") && (key is "F4" or "Space" or "Left" or "Right"
                || modifiers.Contains("Ctrl") && key == "Delete")))
        {
            error = "此按键暂不支持或已被保留。可使用字母、数字、方向键或功能键；Esc、Tab、Windows 键及系统窗口操作不能设置。";
            return false;
        }
        normalized = string.Join('+', new[] { "Ctrl", "Shift", "Alt" }.Where(modifiers.Contains).Append(key));
        return true;
    }
}
