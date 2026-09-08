using System.Windows.Input;
using EmbyPlayer.Core.Settings;

namespace EmbyPlayer.UI.ViewModels;

public sealed partial class PlayerViewModel
{
    public PlayerShortcutAction? ResolveShortcut(Key key, ModifierKeys modifiers)
    {
        if (!PlayerShortcutGesture.TryNormalize(PlayerShortcutInput.Format(key, modifiers), out var gesture, out _)) return null;
        foreach (var action in Enum.GetValues<PlayerShortcutAction>())
            if (gesture.Length > 0 && playerPreferences.EffectiveShortcuts.Get(action) == gesture) return action;
        return null;
    }

    public string GetShortcutText(PlayerShortcutAction action) => PlayerShortcutInput.Display(playerPreferences.EffectiveShortcuts.Get(action));

    public string ShortcutHelpText => string.Join(" · ", Enum.GetValues<PlayerShortcutAction>()
        .Where(action => GetShortcutText(action).Length > 0)
        .Select(action => $"{GetShortcutText(action)} {PlayerShortcutBindings.GetLabel(action)}")) + " · 快进/快退：轻按一步，长按加速定位，松开跳转 · Esc 关闭菜单 / 退出全屏 / 返回";

    public void NotifyShortcutBindingsChanged()
    {
        OnPropertyChanged(nameof(ShortcutHelpText));
        OnPropertyChanged(nameof(FullscreenButtonTooltip));
    }

    public Task<bool> HandleShortcutKeyAsync(Key key) => HandleShortcutKeyAsync(key, ModifierKeys.None);

    public async Task<bool> HandleShortcutKeyAsync(Key key, ModifierKeys modifiers)
    {
        if (!acceptsPlayerEvents) return false;
        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            await StopAndNavigateBackAsync().ConfigureAwait(true);
            return true;
        }
        var action = ResolveShortcut(key, modifiers);
        if (action is null) return false;
        switch (action)
        {
            case PlayerShortcutAction.TogglePlayPause: await TogglePlayPauseAsync().ConfigureAwait(true); break;
            case PlayerShortcutAction.SeekBackward: await SeekRelativeAsync(TimeSpan.FromSeconds(-playerPreferences.SeekSeconds)).ConfigureAwait(true); break;
            case PlayerShortcutAction.SeekForward: await SeekRelativeAsync(TimeSpan.FromSeconds(playerPreferences.SeekSeconds)).ConfigureAwait(true); break;
            case PlayerShortcutAction.VolumeUp: await AdjustVolumeAsync(5).ConfigureAwait(true); break;
            case PlayerShortcutAction.VolumeDown: await AdjustVolumeAsync(-5).ConfigureAwait(true); break;
            case PlayerShortcutAction.ToggleMute: await ToggleMuteAsync().ConfigureAwait(true); break;
            case PlayerShortcutAction.SubtitleEarlier: await AdjustSubtitleAsync("earlier").ConfigureAwait(true); break;
            case PlayerShortcutAction.SubtitleLater: await AdjustSubtitleAsync("later").ConfigureAwait(true); break;
            case PlayerShortcutAction.NextSubtitle:
                if (CanUseTrackControls() && Volatile.Read(ref trackOperationsInFlight) == 0)
                {
                    var tracks = SubtitleTracks.Where(track => track.IsOffOption || track.MpvTrackId.HasValue || track.IsExternal).ToArray();
                    if (tracks.Length > 0)
                    {
                        var selected = Array.FindIndex(tracks, track => track.IsSelected);
                        await SelectSubtitleTrackAsync(tracks[(selected + 1) % tracks.Length]).ConfigureAwait(true);
                    }
                }
                break;
            case PlayerShortcutAction.NextAudioTrack:
                if (CanUseTrackControls() && Volatile.Read(ref trackOperationsInFlight) == 0)
                {
                    var tracks = AudioTracks.Where(track => track.MpvTrackId.HasValue).ToArray();
                    if (tracks.Length > 0)
                    {
                        var selected = Array.FindIndex(tracks, track => track.IsSelected);
                        await SelectAudioTrackAsync(tracks[(selected + 1) % tracks.Length]).ConfigureAwait(true);
                    }
                }
                break;
            // The page owns native-window fullscreen, after resolving this same action.
            case PlayerShortcutAction.ToggleFullscreen: break;
        }
        return true;
    }
}
