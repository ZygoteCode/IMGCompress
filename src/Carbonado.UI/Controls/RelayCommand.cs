using System.Windows.Input;

namespace Carbonado.UI.Controls;

/// <summary>Minimal command exposed by controls (no MVVM toolkit in Carbonado.UI).</summary>
internal sealed class RelayCommand(Action execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute();

    public void Execute(object? parameter)
    {
        if (canExecute())
        {
            execute();
        }
    }
}
