using System.Windows.Input;
using EmbyPlayer.Core.Settings;

namespace EmbyPlayer.UI.ViewModels;

public sealed class ShortcutSettingsViewModel : ViewModelBase
{
    private readonly Func<bool> canEdit;
    private PlayerShortcutBindings bindings = PlayerShortcutBindings.Default;
    private ShortcutSettingRow? capturingRow;
    private string? errorMessage;

    public ShortcutSettingsViewModel(Func<bool> canEdit)
    {
        this.canEdit = canEdit;
        Rows = Enum.GetValues<PlayerShortcutAction>().Select(action => new ShortcutSettingRow(this, action)).ToArray();
        CaptureCommand = new RelayCommand(row => StartCapture(row as ShortcutSettingRow), _ => canEdit());
        ClearCommand = new RelayCommand(row => { if (row is ShortcutSettingRow setting) TryAssign(setting.Action, string.Empty); }, _ => canEdit());
        RestoreDefaultsCommand = new RelayCommand(_ => SetBindings(PlayerShortcutBindings.Default, true), _ => canEdit());
    }

    public event EventHandler? Changed;
    public IReadOnlyList<ShortcutSettingRow> Rows { get; }
    public PlayerShortcutBindings Bindings => bindings;
    public ShortcutSettingRow? CapturingRow => capturingRow;
    public string? ErrorMessage => errorMessage;
    public bool HasError => errorMessage is not null;
    public ICommand CaptureCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand RestoreDefaultsCommand { get; }

    public void Load(PlayerShortcutBindings value) => SetBindings(value, false);

    public void StartCapture(ShortcutSettingRow? row)
    {
        if (!canEdit() || row is null) return;
        capturingRow = row;
        errorMessage = null;
        Notify();
    }

    public void CancelCapture()
    {
        capturingRow = null;
        errorMessage = null;
        Notify();
    }

    public bool Capture(Key key, ModifierKeys modifiers)
    {
        if (capturingRow is null) return false;
        if (key == Key.Escape) { CancelCapture(); return true; }
        if (PlayerShortcutInput.IsModifierKey(key)) return true;
        TryAssign(capturingRow.Action, PlayerShortcutInput.Format(key, modifiers));
        return true;
    }

    public bool TryAssign(PlayerShortcutAction action, string gesture)
    {
        if (!canEdit()) return false;
        if (!bindings.TryChange(action, gesture, out var updated, out var error))
        {
            errorMessage = error;
            Notify();
            return false;
        }
        SetBindings(updated, true);
        return true;
    }

    public void NotifyCommandStates()
    {
        foreach (var command in new[] { CaptureCommand, ClearCommand, RestoreDefaultsCommand })
            ((RelayCommand)command).RaiseCanExecuteChanged();
    }

    private void SetBindings(PlayerShortcutBindings value, bool notifyChange)
    {
        var changed = value != bindings;
        bindings = value;
        capturingRow = null;
        errorMessage = null;
        Notify();
        if (notifyChange && changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Notify()
    {
        foreach (var row in Rows) row.Refresh();
        OnPropertyChanged(nameof(CapturingRow));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
    }
}

public sealed class ShortcutSettingRow(ShortcutSettingsViewModel owner, PlayerShortcutAction action) : ViewModelBase
{
    public PlayerShortcutAction Action { get; } = action;
    public string Label => PlayerShortcutBindings.GetLabel(Action);
    public string Gesture => owner.Bindings.Get(Action);
    public bool IsCapturing => ReferenceEquals(owner.CapturingRow, this);
    public string ButtonText => IsCapturing ? "请按快捷键…（Esc 取消）" : Gesture.Length == 0 ? "未设置，点击录入" : PlayerShortcutInput.Display(Gesture);
    public string AutomationName => $"{Label}快捷键：{ButtonText}";
    public string? ErrorMessage => IsCapturing ? owner.ErrorMessage : null;
    public bool HasError => ErrorMessage is not null;
    internal void Refresh()
    {
        OnPropertyChanged(nameof(Gesture));
        OnPropertyChanged(nameof(IsCapturing));
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
    }
}

public static class PlayerShortcutInput
{
    public static string Display(string gesture) => string.Join('+', gesture.Split('+').Select(part =>
        part.Length == 2 && part[0] == 'D' && char.IsAsciiDigit(part[1]) ? part[1].ToString() : part));

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.System;

    public static string Format(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key switch { Key.Return => "Enter", Key.Prior => "PageUp", Key.Next => "PageDown", _ => key.ToString() });
        return string.Join('+', parts);
    }
}
