using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using EmbyPlayer.Core.Settings;
using EmbyPlayer.UI.ViewModels;

namespace EmbyPlayer.UI.Pages;

public partial class PlayerPage
{
    private readonly DispatcherTimer keyboardSeekTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Stopwatch keyboardSeekClock = new();
    private PlayerViewModel? keyboardSeekViewModel;
    private Key keyboardSeekKey;

    private void BeginKeyboardSeek(Key key, PlayerShortcutAction action, bool isRepeat)
    {
        // Repeats from a still-held opposite key or a completed/cancelled press are ignored too.
        if (isRepeat) return;
        CancelKeyboardSeek();
        CancelVideoSpeedPress();
        if (DataContext is not PlayerViewModel vm || !vm.BeginKeyboardSeek(action)) return;
        keyboardSeekViewModel = vm;
        keyboardSeekKey = key;
        BeginVisibilityShortcutActivity(vm);
        keyboardSeekClock.Restart();
        keyboardSeekTimer.Tick -= OnKeyboardSeekTimerTick;
        keyboardSeekTimer.Tick += OnKeyboardSeekTimerTick;
        keyboardSeekTimer.Start();
    }

    private void OnKeyboardSeekTimerTick(object? sender, EventArgs e)
    {
        if (keyboardSeekViewModel is not { } vm) return;
        if (!ReferenceEquals(DataContext, vm) || !vm.IsKeyboardSeekActive)
        {
            CancelKeyboardSeek();
            return;
        }
        vm.UpdateKeyboardSeek(keyboardSeekClock.Elapsed);
    }

    private async void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (keyboardSeekViewModel is not { } vm) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != keyboardSeekKey)
        {
            if (IsSeekModifierKey(key)) CancelKeyboardSeek();
            return;
        }
        e.Handled = true;
        vm.UpdateKeyboardSeek(keyboardSeekClock.Elapsed);
        StopKeyboardSeekTracking();
        try { await vm.CompleteKeyboardSeekAsync().ConfigureAwait(true); }
        catch (Exception exception)
        {
            Debug.WriteLine($"Keyboard seek failed: {exception.GetType().Name}");
            vm.CancelKeyboardSeek();
        }
    }

    private void OnPlayerKeyboardFocusLost(object sender, KeyboardFocusChangedEventArgs e) => CancelKeyboardSeek();

    private static bool IsSeekModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private void StopKeyboardSeekTracking()
    {
        keyboardSeekTimer.Stop();
        keyboardSeekClock.Stop();
        if (keyboardSeekViewModel is not null) EndVisibilityShortcutActivity();
        keyboardSeekViewModel = null;
    }

    private void CancelKeyboardSeek()
    {
        var vm = keyboardSeekViewModel;
        StopKeyboardSeekTracking();
        vm?.CancelKeyboardSeek();
    }
}
