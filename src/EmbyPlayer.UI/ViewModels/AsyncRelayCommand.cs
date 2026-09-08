using System.Windows.Input;

namespace EmbyPlayer.UI.ViewModels;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<bool>? canExecute;
    private readonly Func<Task> execute;
    private readonly Predicate<object?>? parameterCanExecute;
    private readonly Func<object?, Task>? parameterExecute;
    private bool isExecuting;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute;
    }

    public AsyncRelayCommand(
        Func<object?, Task> execute,
        Predicate<object?>? canExecute = null)
    {
        parameterExecute = execute ?? throw new ArgumentNullException(nameof(execute));
        parameterCanExecute = canExecute;
        this.execute = () => parameterExecute(null);
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return !isExecuting
            && (parameterExecute is not null
                ? parameterCanExecute?.Invoke(parameter) ?? true
                : canExecute?.Invoke() ?? true);
    }

    public async void Execute(object? parameter)
    {
        await ExecuteAsync(parameter).ConfigureAwait(false);
    }

    public async Task ExecuteAsync()
    {
        await ExecuteAsync(null).ConfigureAwait(true);
    }

    public async Task ExecuteAsync(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        try
        {
            isExecuting = true;
            NotifyCanExecuteChanged();
            await (parameterExecute is not null
                    ? parameterExecute(parameter)
                    : execute())
                .ConfigureAwait(true);
        }
        finally
        {
            isExecuting = false;
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
