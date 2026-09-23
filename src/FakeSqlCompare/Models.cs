using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FakeSqlCompare;

public sealed class StepItem
{
    public string Text { get; init; } = "";
    public bool Done { get; init; }
    public bool Current { get; init; }
}
public enum DiffStatus { Equal, Different, SourceOnly, TargetOnly }
public enum LineKind { Eq, Add, Del, Chg }
public enum AppPage { Connect, Comparing, Workspace, Deploy }

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _can;
    public RelayCommand(Action execute, Func<bool>? can = null)
        : this(_ => execute(), can is null ? null : _ => can()) { }
    public RelayCommand(Action<object?> execute, Func<object?, bool>? can = null)
    {
        _execute = execute;
        _can = can;
    }
    public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand : System.Windows.Input.ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _can;
    private bool _running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? can = null)
        : this(_ => execute(), can is null ? null : _ => can()) { }

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? can = null)
    {
        _execute = execute;
        _can = can;
    }

    public bool CanExecute(object? parameter) => !_running && (_can?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        RaiseCanExecuteChanged();
        try { await _execute(parameter); }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class ConnectionProfile : ObservableObject
{
    private string _server = "";
    private string _database = "";
    private string _auth = "windows";
    private string _user = "sa";
    private string _password = "";
    private bool _tested;
    private bool _testing;

    public string Server { get => _server; set { if (Set(ref _server, value)) Tested = false; } }
    public string Database { get => _database; set => Set(ref _database, value); }
    public string Auth { get => _auth; set { if (Set(ref _auth, value)) { Raise(nameof(IsSqlAuth)); Raise(nameof(IsWindowsAuth)); Tested = false; } } }
    public bool IsWindowsAuth { get => _auth == "windows"; set { if (value) Auth = "windows"; } }
    public bool IsSqlAuth { get => _auth == "sql"; set { if (value) Auth = "sql"; } }
    public string User { get => _user; set { if (Set(ref _user, value)) Tested = false; } }
    public string Password { get => _password; set { if (Set(ref _password, value)) Tested = false; } }
    public bool Testing { get => _testing; set { if (Set(ref _testing, value)) Raise(nameof(TestLabel)); } }
    public bool Tested { get => _tested; set { if (Set(ref _tested, value)) Raise(nameof(TestLabel)); } }
    public string TestLabel => Testing ? "连接中…" : Tested ? "连接成功" : "测试连接";

    public void CopyFrom(ConnectionProfile other)
    {
        Server = other.Server;
        Database = other.Database;
        Auth = other.Auth;
        User = other.User;
        Password = other.Password;
        Tested = other.Tested;
        Testing = false;
    }
}

public sealed class SqlLine
{
    public LineKind Kind { get; init; }
    public string Text { get; init; } = "";
    public string Marker => Kind switch { LineKind.Add => "+", LineKind.Del => "−", LineKind.Chg => "~", _ => "" };
}

public sealed class SchemaObject : ObservableObject
{
    private bool _isSelected;
    public string Id { get; init; } = "";
    public string Schema { get; init; } = "dbo";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "表";
    public DiffStatus Status { get; init; }
    public string Action { get; init; } = "";
    public string Script { get; init; } = "";
    public IReadOnlyList<SqlLine> SourceLines { get; init; } = [];
    public IReadOnlyList<SqlLine> TargetLines { get; init; } = [];
    public string FullName => $"{Schema}.{Name}";
    public bool CanToggle => Status != DiffStatus.Equal;
    public string StatusLabel => Status switch
    {
        DiffStatus.Different => "不同",
        DiffStatus.SourceOnly => "仅源",
        DiffStatus.TargetOnly => "仅目标",
        _ => "相同"
    };
    public Func<SchemaObject, bool, bool>? CanSelect { get; set; }
    public Action? SelectionChanged { get; set; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Status == DiffStatus.Equal) return;
            if (CanSelect is not null && !CanSelect(this, value))
            {
                Raise();
                return;
            }
            if (Set(ref _isSelected, value)) SelectionChanged?.Invoke();
        }
    }
}
