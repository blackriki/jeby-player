using EmbyPlayer.Core.Settings;

namespace EmbyPlayer.UI.ViewModels;

public sealed partial class PlayerViewModel
{
    private long keyboardSeekInstance;
    private TimeSpan keyboardSeekOrigin;
    private int keyboardSeekDirection;
    private int keyboardSeekStep;

    public bool IsKeyboardSeekActive => keyboardSeekInstance != 0 && CanAcceptPlayerEvent(keyboardSeekInstance);
    public string KeyboardSeekFeedbackText => $"{(keyboardSeekDirection < 0 ? "快退" : "快进")}  {CurrentTimeText}";

    public bool BeginKeyboardSeek(PlayerShortcutAction action)
    {
        if (action is not (PlayerShortcutAction.SeekBackward or PlayerShortcutAction.SeekForward)) return false;
        CancelKeyboardSeek();
        if (!CanSeek || isSeekDragging || isCompleted) return false;
        keyboardSeekInstance = currentPlaybackInstanceId;
        keyboardSeekOrigin = currentPosition ?? TimeSpan.Zero;
        keyboardSeekDirection = action == PlayerShortcutAction.SeekBackward ? -1 : 1;
        keyboardSeekStep = playerPreferences.SeekSeconds;
        BeginSeekDrag();
        UpdateKeyboardSeek(TimeSpan.Zero);
        OnPropertyChanged(nameof(IsKeyboardSeekActive));
        return true;
    }

    /// <summary>Preview only. Native seeking occurs once on key release, independently of OS repeat rate.</summary>
    public void UpdateKeyboardSeek(TimeSpan elapsed)
    {
        if (!IsKeyboardSeekActive || !CanSeek || isCompleted)
        {
            CancelKeyboardSeek();
            return;
        }
        // A tap is one step. Beyond 400 ms, smoothly accelerate at 1, 3, then 6 steps per second.
        var held = Math.Max(0, elapsed.TotalSeconds - 0.4);
        var steps = 1 + Math.Min(held, 1.5) + Math.Min(Math.Max(0, held - 1.5), 2.5) * 3
            + Math.Max(0, held - 4) * 6;
        var target = ClampSeekTarget(keyboardSeekOrigin + TimeSpan.FromSeconds(keyboardSeekDirection * keyboardSeekStep * steps));
        if (target is null) { CancelKeyboardSeek(); return; }
        UpdateSeekDrag(CalculateProgressPercent(target, duration));
        OnPropertyChanged(nameof(KeyboardSeekFeedbackText));
    }

    public async Task CompleteKeyboardSeekAsync()
    {
        if (!IsKeyboardSeekActive || !CanSeek || isCompleted) { CancelKeyboardSeek(); return; }
        keyboardSeekInstance = 0;
        OnPropertyChanged(nameof(IsKeyboardSeekActive));
        await CompleteSeekDragAsync(SeekPercent).ConfigureAwait(true);
    }

    public void CancelKeyboardSeek()
    {
        var instance = keyboardSeekInstance;
        if (instance == 0) return;
        keyboardSeekInstance = 0;
        // A delayed old page/key release must never cancel a new item's pointer drag.
        if (instance != 0 && IsCurrentPlaybackInstance(instance)) CancelSeekDrag();
        OnPropertyChanged(nameof(IsKeyboardSeekActive));
    }
}
