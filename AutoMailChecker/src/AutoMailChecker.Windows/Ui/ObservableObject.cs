using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace AutoMailChecker.Windows.Ui;

/// <summary>画面に値の変化を知らせる ViewModel の基底。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// 非同期の処理を行うコマンド。実行中は押せなくする（二重に押されないように）。
/// 例外は onError に渡す（画面に出すのは ViewModel の役目）。
/// </summary>
public sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await execute();
        }
        catch (Exception ex) when (onError is not null)
        {
            onError(ex);
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

/// <summary>
/// 入力欄の誤りを持てる ViewModel。誤りのある欄は赤枠になり、理由が下に出る（画面設計: 共通ルール）。
/// </summary>
public abstract class EditableObject : ObservableObject, INotifyDataErrorInfo
{
    private readonly Dictionary<string, List<string>> _errors = [];

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public bool HasErrors => _errors.Count > 0;

    public System.Collections.IEnumerable GetErrors(string? propertyName) =>
        propertyName is not null && _errors.TryGetValue(propertyName, out var messages) ? messages : Array.Empty<string>();

    public void AddError(string propertyName, string message)
    {
        if (!_errors.TryGetValue(propertyName, out var messages))
        {
            _errors[propertyName] = messages = [];
        }

        messages.Add(message);
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
        OnPropertyChanged(nameof(HasErrors));
    }

    public void ClearErrors()
    {
        var names = _errors.Keys.ToList();
        _errors.Clear();
        foreach (var name in names)
        {
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(name));
        }

        OnPropertyChanged(nameof(HasErrors));
    }
}

/// <summary>同期の処理を行うコマンド。</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();
}

/// <summary>選択肢（表示名と値）。</summary>
public sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}
