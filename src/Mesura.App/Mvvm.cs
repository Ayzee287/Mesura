using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Mesura.App;

/// <summary>A minimal observable base; the app is small enough not to need an MVVM framework (architecture §3.3).</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            Raise(name);
        }
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A command backed by a delegate, with a guard that can disable it.</summary>
public sealed class RelayCommand(Action run, Func<bool>? canRun = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canRun?.Invoke() ?? true;

    public void Execute(object? parameter) => run();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// An asynchronous command. It disables itself while running, and any exception is handed to <c>onError</c> (shown to
/// the user) instead of escaping an <c>async void</c> and taking the whole app down.
/// </summary>
public sealed class AsyncCommand(Func<Task> run, Func<bool>? canRun, Action<Exception> onError) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (canRun?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await run();
        }
        catch (Exception e)
        {
            onError(e);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>A command with a parameter (the item a button in a list acts on), with a guard that can disable it.</summary>
public sealed class RelayCommand<T>(Action<T?> run, Func<T?, bool>? canRun = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canRun?.Invoke(parameter is T item ? item : default) ?? true;

    public void Execute(object? parameter) => run(parameter is T item ? item : default);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>An asynchronous command with a parameter, guarded like <see cref="AsyncCommand"/>.</summary>
public sealed class AsyncCommand<T>(Func<T?, Task> run, Func<T?, bool>? canRun, Action<Exception> onError) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (canRun?.Invoke(parameter is T item ? item : default) ?? true);

    public async void Execute(object? parameter)
    {
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await run(parameter is T item ? item : default);
        }
        catch (Exception e)
        {
            onError(e);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
