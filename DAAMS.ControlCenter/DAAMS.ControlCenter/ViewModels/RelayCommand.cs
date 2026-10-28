using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ControlCenter.ViewModels;

/// <summary>
/// Standard ICommand implementation used throughout the Control Center for
/// button/navigation commanding, avoiding code-behind event handlers.
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    /// <summary>
    /// Creates a command that wraps an async execute function so exceptions are
    /// caught and reported rather than crashing the app on an unobserved
    /// <c>async void</c> invocation.
    /// </summary>
    public static RelayCommand FromAsync(Func<object?, Task> execute,
        Action<Exception>? onError = null,
        Predicate<object?>? canExecute = null)
    {
        return new RelayCommand(async parameter =>
        {
            try
            {
                await execute(parameter);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RelayCommand] Unhandled async command error: {ex}");
                onError?.Invoke(ex);
            }
        }, canExecute);
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);
}
