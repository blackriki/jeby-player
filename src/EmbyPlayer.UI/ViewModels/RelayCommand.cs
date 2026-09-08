using System.Windows.Input;

namespace EmbyPlayer.UI.ViewModels;

public sealed class RelayCommand : ICommand
{
    private readonly Predicate<object?>? canExecute;
    private readonly Action<object?> execute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return canExecute?.Invoke(parameter) ?? true;
    }

    public void Execute(object? parameter)
    {
        execute(parameter);
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
