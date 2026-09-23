using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FakeSqlCompare;

public sealed class MainViewModel : ObservableObject
{
    private AppPage _page = AppPage.Connect;
    private string _headerHint = "配置源库与目标库，然后开始对比";
    private string _typeFilter = "全部";
    private string _statusFilter = "diff";
    private SchemaObject? _selectedObject;
    private bool _showScript;
    private bool _backup = true;
    private bool _confirmDrop;
    private int _deployPage;
    private bool _deploying;
    private bool _deploySucceeded;
    private string _toast = "";
    private int _compareStep;
    private bool _isDark = ThemeService.Current == "Dark";
    private DispatcherTimer? _toastTimer;
    private CompareSession? _session;
    private CancellationTokenSource? _compareCts;
    private CancellationTokenSource? _deployCts;

    public MainViewModel()
    {
        Source = new ConnectionProfile { Server = "localhost" };
        Target = new ConnectionProfile { Server = "localhost" };
        SourceDatabases = [];
        TargetDatabases = [];
        AllObjects = [];
        FilteredObjects = [];
        DeployLogs = [];
        Projects = [];

        CompareCommand = new AsyncRelayCommand(StartCompareAsync);
        RecheckCommand = new AsyncRelayCommand(RecheckAsync);
        TestSourceCommand = new AsyncRelayCommand(() => TestAsync(Source, SourceDatabases, announce: true));
        TestTargetCommand = new AsyncRelayCommand(() => TestAsync(Target, TargetDatabases, announce: true));
        HomeCommand = new RelayCommand(GoHome);
        ThemeCommand = new RelayCommand(ToggleTheme);
        SaveProjectCommand = new RelayCommand(SaveProject);
        SaveProjectAsCommand = new RelayCommand(SaveProjectAs);
        LoadProjectCommand = new RelayCommand(LoadSelectedProject, () => SelectedProject is not null);
        SwapSidesCommand = new RelayCommand(SwapSides, () => Page is AppPage.Connect);
        DeleteProjectCommand = new RelayCommand(DeleteProject, () => SelectedProject is not null);
        SetTypeCommand = new RelayCommand(p => { TypeFilter = p?.ToString() ?? "全部"; });
        SetStatusCommand = new RelayCommand(p => { StatusFilter = p?.ToString() ?? "diff"; });
        SelectSafeCommand = new RelayCommand(() =>
        {
            foreach (var o in AllObjects)
                o.IsSelected = o.Status is DiffStatus.Different or DiffStatus.SourceOnly;
        });
        ClearSelectCommand = new RelayCommand(() => { foreach (var o in AllObjects) o.IsSelected = false; });
        CopyScriptCommand = new RelayCommand(() =>
        {
            Clipboard.SetText(CombinedScript);
            ShowToast("脚本已复制到剪贴板");
        });
        ExportScriptCommand = new AsyncRelayCommand(ExportScriptAsync);
        OpenDeployCommand = new AsyncRelayCommand(OpenDeployAsync, () => SelectedCount > 0);
        CancelDeployCommand = new RelayCommand(() =>
        {
            _deployCts?.Cancel();
            Page = AppPage.Workspace;
        });
        DeployNextCommand = new AsyncRelayCommand(DeployNextAsync, CanDeployNext);
        ShowDefCommand = new RelayCommand(() => ShowScript = false);
        ShowScriptTabCommand = new RelayCommand(() => ShowScript = true);
        ListSourceDbCommand = new AsyncRelayCommand(() => TestAsync(Source, SourceDatabases, announce: false));
        ListTargetDbCommand = new AsyncRelayCommand(() => TestAsync(Target, TargetDatabases, announce: false));
        RestoreProjects();
    }

    public ConnectionProfile Source { get; }
    public ConnectionProfile Target { get; }
    public ObservableCollection<string> SourceDatabases { get; }
    public ObservableCollection<string> TargetDatabases { get; }
    public ObservableCollection<SchemaObject> AllObjects { get; }
    public ObservableCollection<SchemaObject> FilteredObjects { get; }
    public ObservableCollection<string> DeployLogs { get; }
    public ObservableCollection<SavedProject> Projects { get; }

    public AsyncRelayCommand CompareCommand { get; }
    public AsyncRelayCommand RecheckCommand { get; }
    public AsyncRelayCommand TestSourceCommand { get; }
    public AsyncRelayCommand TestTargetCommand { get; }
    public RelayCommand HomeCommand { get; }
    public RelayCommand ThemeCommand { get; }
    public RelayCommand SaveProjectCommand { get; }
    public RelayCommand SaveProjectAsCommand { get; }
    public RelayCommand LoadProjectCommand { get; }
    public RelayCommand SwapSidesCommand { get; }
    public RelayCommand DeleteProjectCommand { get; }
    public RelayCommand SetTypeCommand { get; }
    public RelayCommand SetStatusCommand { get; }
    public RelayCommand SelectSafeCommand { get; }
    public RelayCommand ClearSelectCommand { get; }
    public RelayCommand CopyScriptCommand { get; }
    public AsyncRelayCommand ExportScriptCommand { get; }
    public AsyncRelayCommand OpenDeployCommand { get; }
    public RelayCommand CancelDeployCommand { get; }
    public AsyncRelayCommand DeployNextCommand { get; }
    public RelayCommand ShowDefCommand { get; }
    public RelayCommand ShowScriptTabCommand { get; }
    public AsyncRelayCommand ListSourceDbCommand { get; }
    public AsyncRelayCommand ListTargetDbCommand { get; }

    public AppPage Page
    {
        get => _page;
        set
        {
            if (Set(ref _page, value))
            {
                Raise(nameof(IsConnect));
                Raise(nameof(IsComparing));
                Raise(nameof(IsWorkspace));
                Raise(nameof(IsDeploy));
                Raise(nameof(ShowBack));
                Raise(nameof(ModeBadge));
                Raise(nameof(CompareIsIndeterminate));
                SwapSidesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsConnect => Page == AppPage.Connect;
    public bool IsComparing => Page == AppPage.Comparing;
    public bool IsWorkspace => Page == AppPage.Workspace;
    public bool IsDeploy => Page == AppPage.Deploy;
    public bool ShowBack => Page != AppPage.Connect;
    public string ModeBadge => Page == AppPage.Connect ? "未对比" : "真实连接";
    public bool HasProjects => Projects.Count > 0;
    public bool HasSelectedProject => SelectedProject is not null;

    private SavedProject? _selectedProject;
    private bool _loadingProject;
    public SavedProject? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (!Set(ref _selectedProject, value)) return;
            Raise(nameof(HasSelectedProject));
            DeleteProjectCommand.RaiseCanExecuteChanged();
            LoadProjectCommand.RaiseCanExecuteChanged();
            if (_loadingProject || value is null) return;
            ApplySavedProject(value);
        }
    }

    public string HeaderHint { get => _headerHint; set => Set(ref _headerHint, value); }
    public bool IsDark
    {
        get => _isDark;
        private set
        {
            if (Set(ref _isDark, value))
            {
                Raise(nameof(ThemeToolTip));
                Raise(nameof(IsLight));
            }
        }
    }
    public bool IsLight => !IsDark;
    public string ThemeToolTip => IsDark ? "切换到明亮模式" : "切换到黑暗模式";

    public string TypeFilter { get => _typeFilter; set { if (Set(ref _typeFilter, value)) { Raise(nameof(TypeAll)); Raise(nameof(TypeTable)); Raise(nameof(TypeView)); Raise(nameof(TypeProc)); Raise(nameof(TypeFunc)); RefreshFilter(); } } }
    public bool TypeAll => TypeFilter == "全部";
    public bool TypeTable => TypeFilter == "表";
    public bool TypeView => TypeFilter == "视图";
    public bool TypeProc => TypeFilter == "存储过程";
    public bool TypeFunc => TypeFilter == "函数";

    public string StatusFilter { get => _statusFilter; set { if (Set(ref _statusFilter, value)) { Raise(nameof(StatusDiff)); Raise(nameof(StatusAll)); Raise(nameof(StatusSelected)); RefreshFilter(); } } }
    public bool StatusDiff => StatusFilter == "diff";
    public bool StatusAll => StatusFilter == "all";
    public bool StatusSelected => StatusFilter == "selected";

    public SchemaObject? SelectedObject
    {
        get => _selectedObject;
        set { if (Set(ref _selectedObject, value)) Raise(nameof(SelectedScript)); }
    }

    public bool ShowScript { get => _showScript; set { if (Set(ref _showScript, value)) Raise(nameof(ShowDef)); } }
    public bool ShowDef => !_showScript;
    public string SelectedScript => SelectedObject?.Script ?? "";

    public bool Backup { get => _backup; set => Set(ref _backup, value); }
    public bool ConfirmDrop
    {
        get => _confirmDrop;
        set { if (Set(ref _confirmDrop, value)) DeployNextCommand.RaiseCanExecuteChanged(); }
    }
    public int DeployPage
    {
        get => _deployPage;
        set
        {
            if (Set(ref _deployPage, value))
            {
                Raise(nameof(ShowDeployReview));
                Raise(nameof(ShowDeployResult));
                Raise(nameof(DeployNextLabel));
                DeployNextCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public bool ShowDeployReview => DeployPage == 0;
    public bool ShowDeployResult => DeployPage == 1;
    public bool HasDrops => DropCount > 0;
    public bool Deploying { get => _deploying; set { if (Set(ref _deploying, value)) { Raise(nameof(DeployNextLabel)); DeployNextCommand.RaiseCanExecuteChanged(); } } }
    public bool DeploySucceeded
    {
        get => _deploySucceeded;
        private set { if (Set(ref _deploySucceeded, value)) Raise(nameof(DeployNextLabel)); }
    }
    public string DeployNextLabel => Deploying ? "执行中…" : DeployPage == 1 ? (DeploySucceeded ? "完成" : "关闭") : "确认更新";
    public string CombinedScript { get; private set; } = "";
    public string Toast { get => _toast; set { if (Set(ref _toast, value)) Raise(nameof(HasToast)); } }
    public bool HasToast => !string.IsNullOrEmpty(Toast);

    public int CompareStep { get => _compareStep; set { if (Set(ref _compareStep, value)) { Raise(nameof(ComparePercent)); Raise(nameof(CompareStepItems)); Raise(nameof(CompareIsIndeterminate)); } } }
    public double ComparePercent => Math.Min(100, (_compareStep + 1) * 20);
    public bool CompareIsIndeterminate => Page == AppPage.Comparing && CompareStep is 2;
    public IEnumerable<StepItem> CompareStepItems =>
        CompareSteps.Select((s, i) => new StepItem { Text = s, Done = i < CompareStep, Current = i == CompareStep });
    public string[] CompareSteps { get; } = ["连接源库", "连接目标库", "抽取架构（DacFx）", "整理差异", "就绪"];

    public int DiffCount => AllObjects.Count(o => o.Status != DiffStatus.Equal);
    public int SelectedCount => AllObjects.Count(o => o.IsSelected && o.Status != DiffStatus.Equal);
    public int CreateCount => AllObjects.Count(o => o.IsSelected && o.Status == DiffStatus.SourceOnly);
    public int AlterCount => AllObjects.Count(o => o.IsSelected && o.Status == DiffStatus.Different);
    public int DropCount => AllObjects.Count(o => o.IsSelected && o.Status == DiffStatus.TargetOnly);
    public string CountsText => $"差异 {DiffCount} · 已选 {SelectedCount}（创建 {CreateCount} / 修改 {AlterCount} / 删除 {DropCount}）";
    public IEnumerable<SchemaObject> SelectedForDeploy => AllObjects.Where(o => o.IsSelected && o.Status != DiffStatus.Equal);

    public void RefreshCounts()
    {
        Raise(nameof(DiffCount));
        Raise(nameof(SelectedCount));
        Raise(nameof(CreateCount));
        Raise(nameof(AlterCount));
        Raise(nameof(DropCount));
        Raise(nameof(HasDrops));
        Raise(nameof(CountsText));
        Raise(nameof(SelectedForDeploy));
        OpenDeployCommand.RaiseCanExecuteChanged();
        RebuildScript();
    }

    private void RefreshFilter()
    {
        FilteredObjects.Clear();
        foreach (var o in AllObjects)
        {
            if (TypeFilter != "全部" && o.Type != TypeFilter) continue;
            if (StatusFilter == "diff" && o.Status == DiffStatus.Equal) continue;
            if (StatusFilter == "selected" && !o.IsSelected) continue;
            FilteredObjects.Add(o);
        }
        SelectedObject ??= FilteredObjects.FirstOrDefault();
        if (SelectedObject is not null && !FilteredObjects.Contains(SelectedObject))
            SelectedObject = FilteredObjects.FirstOrDefault();
    }

    private void RebuildScript()
    {
        var objs = SelectedForDeploy.ToList();
        if (objs.Count == 0)
        {
            CombinedScript = "-- 未勾选任何对象";
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine("/* 预览（按对象拼接）。导出/一键更新时由 DacFx 按依赖顺序重生成。");
            sb.AppendLine($"   源: {Source.Server}.{Source.Database}");
            sb.AppendLine($"   目标: {Target.Server}.{Target.Database}");
            sb.AppendLine("*/");
            sb.AppendLine("SET XACT_ABORT ON;");
            sb.AppendLine("SET NOCOUNT ON;");
            sb.AppendLine();
            foreach (var o in objs)
            {
                sb.AppendLine($"/* ---- {o.FullName} ({o.StatusLabel}) ---- */");
                sb.AppendLine(o.Script);
                sb.AppendLine();
            }
            CombinedScript = sb.ToString();
        }
        Raise(nameof(CombinedScript));
    }

    private void LoadObjects(IEnumerable<SchemaObject> items)
    {
        AllObjects.Clear();
        foreach (var o in items)
        {
            o.SelectionChanged = RefreshCounts;
            if (o.Status is DiffStatus.Different or DiffStatus.SourceOnly)
                o.IsSelected = true;
            AllObjects.Add(o);
        }
        SelectedObject = null;
        RefreshFilter();
        RefreshCounts();
    }

    private async Task TestAsync(ConnectionProfile profile, ObservableCollection<string> dbs, bool announce)
    {
        if (string.IsNullOrWhiteSpace(profile.Server))
        {
            ShowToast("请填写服务器");
            return;
        }
        if (profile.IsSqlAuth && string.IsNullOrWhiteSpace(profile.User))
        {
            ShowToast("请填写用户名");
            return;
        }
        if (!announce && profile.Tested && dbs.Count > 0)
            return;
        if (profile.Testing) return;

        profile.Testing = true;
        profile.Tested = false;
        try
        {
            var list = await ConnectionService.ListDatabasesAsync(profile);
            var keep = profile.Database;
            dbs.Clear();
            foreach (var name in list) dbs.Add(name);
            if (string.IsNullOrWhiteSpace(keep))
            {
                profile.Database = list.FirstOrDefault(x => x is not ("master" or "model" or "msdb")) ?? list.FirstOrDefault() ?? "";
            }
            else
            {
                if (!list.Contains(keep))
                    dbs.Insert(0, keep);
                profile.Database = keep;
            }
            profile.Tested = true;
            MirrorDatabases(profile, dbs);
            if (announce)
                ShowToast($"已连接，列出 {list.Count} 个数据库");
        }
        catch (Exception ex)
        {
            ShowToast("连接失败：" + ConnectionService.Describe(ex));
        }
        finally
        {
            profile.Testing = false;
        }
    }

    private void MirrorDatabases(ConnectionProfile from, ObservableCollection<string> fromDbs)
    {
        var other = ReferenceEquals(from, Source) ? Target : Source;
        var otherDbs = ReferenceEquals(from, Source) ? TargetDatabases : SourceDatabases;
        if (!SameServerLogin(from, other)) return;
        var keep = other.Database;
        otherDbs.Clear();
        foreach (var name in fromDbs) otherDbs.Add(name);
        if (!string.IsNullOrWhiteSpace(keep))
        {
            if (!otherDbs.Contains(keep))
                otherDbs.Insert(0, keep);
            other.Database = keep;
        }
        other.Tested = true;
    }

    private static bool SameServerLogin(ConnectionProfile a, ConnectionProfile b)
        => string.Equals(a.Server.Trim(), b.Server.Trim(), StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.Auth, b.Auth, StringComparison.Ordinal)
           && (a.IsWindowsAuth
               || (string.Equals(a.User, b.User, StringComparison.OrdinalIgnoreCase)
                   && a.Password == b.Password));

    private async Task StartCompareAsync()
    {
        if (string.IsNullOrWhiteSpace(Source.Server) || string.IsNullOrWhiteSpace(Target.Server)
            || string.IsNullOrWhiteSpace(Source.Database) || string.IsNullOrWhiteSpace(Target.Database))
        {
            ShowToast("请填写源和目标的服务器、数据库");
            return;
        }
        if (string.Equals(Source.Server.Trim(), Target.Server.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Source.Database.Trim(), Target.Database.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            ShowToast("源和目标不能是同一个库");
            return;
        }

        _compareCts?.Cancel();
        _compareCts = new CancellationTokenSource();
        var ct = _compareCts.Token;
        _session = null;
        Page = AppPage.Comparing;
        HeaderHint = "正在对比架构…";
        CompareStep = 0;
        try
        {
            await ConnectionService.TestAsync(Source, ct);
            CompareStep = 1;
            await ConnectionService.TestAsync(Target, ct);
            CompareStep = 2;
            var session = await Task.Run(() => CompareService.Compare(Source, Target, ct), ct);
            ct.ThrowIfCancellationRequested();
            CompareStep = 3;
            _session = session;
            LoadObjects(session.Objects);
            CompareStep = 4;
            Page = AppPage.Workspace;
            HeaderHint = $"{Source.Server}.{Source.Database}  →  {Target.Server}.{Target.Database}";
            PersistSession();
            if (session.Objects.Count == 0)
                ShowToast("两边结构一致，没有列出的差异");
        }
        catch (OperationCanceledException)
        {
            if (Page == AppPage.Comparing)
                ReturnFromFailedCompare();
        }
        catch (Exception ex)
        {
            ReturnFromFailedCompare();
            ShowToast("对比失败：" + ConnectionService.Describe(ex));
        }
    }

    private void ReturnFromFailedCompare()
    {
        if (AllObjects.Count > 0)
        {
            Page = AppPage.Workspace;
            HeaderHint = $"{Source.Server}.{Source.Database}  →  {Target.Server}.{Target.Database}";
        }
        else
        {
            Page = AppPage.Connect;
            HeaderHint = "配置源库与目标库，然后开始对比";
        }
    }

    private async Task RecheckAsync()
    {
        await StartCompareAsync();
    }

    private void RestoreProjects()
    {
        var state = ProjectStore.Load();
        _loadingProject = true;
        try
        {
            foreach (var p in state.Projects)
                Projects.Add(p);
            if (state.LastSource is not null) ProjectStore.Apply(Source, state.LastSource);
            if (state.LastTarget is not null) ProjectStore.Apply(Target, state.LastTarget);
            if (state.LastProjectId is Guid id)
                SelectedProject = Projects.FirstOrDefault(p => p.Id == id);
        }
        finally
        {
            _loadingProject = false;
        }
        Projects.CollectionChanged += (_, _) => Raise(nameof(HasProjects));
        Raise(nameof(HasProjects));
    }

    private void SaveProject()
    {
        if (!CanSaveConnections()) return;
        if (SelectedProject is null)
        {
            SaveProjectAs();
            return;
        }

        ProjectStore.Fill(SelectedProject, Source, Target);
        PersistSession();
        ShowToast("已保存 " + SelectedProject.Name);
    }

    public void LoadSelectedProject()
    {
        if (SelectedProject is null) return;
        ApplySavedProject(SelectedProject);
        ShowToast("已载入 " + SelectedProject.Name);
    }

    private void ApplySavedProject(SavedProject project)
    {
        ProjectStore.Apply(Source, project.Source);
        ProjectStore.Apply(Target, project.Target);
        EnsureDatabaseListed(SourceDatabases, Source.Database);
        EnsureDatabaseListed(TargetDatabases, Target.Database);
    }

    private static void EnsureDatabaseListed(ObservableCollection<string> dbs, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!dbs.Contains(name)) dbs.Insert(0, name);
    }

    private void SaveProjectAs()
    {
        if (!CanSaveConnections()) return;
        var owner = Application.Current.MainWindow;
        if (owner is null) return;

        var suggested = !string.IsNullOrWhiteSpace(SelectedProject?.Name)
            ? SelectedProject!.Name
            : SavedProject.MakeName(Source, Target);
        if (!AliasPromptWindow.TryAsk(owner, suggested, out var alias))
            return;

        var project = Projects.FirstOrDefault(p => string.Equals(p.Name, alias, StringComparison.OrdinalIgnoreCase));
        if (project is not null)
        {
            if (MessageBox.Show($"已有名为「{alias}」的连接，覆盖？", "FakeSqlCompare",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }
        else
        {
            project = new SavedProject { Id = Guid.NewGuid() };
            Projects.Add(project);
        }

        ProjectStore.Fill(project, Source, Target, alias);
        _loadingProject = true;
        SelectedProject = project;
        _loadingProject = false;
        PersistSession();
        ShowToast("已另存为 " + alias);
    }

    private bool CanSaveConnections()
    {
        if (string.IsNullOrWhiteSpace(Source.Server) || string.IsNullOrWhiteSpace(Target.Server)
            || string.IsNullOrWhiteSpace(Source.Database) || string.IsNullOrWhiteSpace(Target.Database))
        {
            ShowToast("请先填写源和目标的服务器、数据库");
            return false;
        }
        return true;
    }

    private void SwapSides()
    {
        var snapshot = new ConnectionProfile();
        snapshot.CopyFrom(Source);
        Source.CopyFrom(Target);
        Target.CopyFrom(snapshot);

        var srcDbs = SourceDatabases.ToList();
        SourceDatabases.Clear();
        foreach (var name in TargetDatabases) SourceDatabases.Add(name);
        TargetDatabases.Clear();
        foreach (var name in srcDbs) TargetDatabases.Add(name);

        PersistSession();
        ShowToast("已互换源和目标");
    }

    private void DeleteProject()
    {
        if (SelectedProject is null) return;
        var name = SelectedProject.Name;
        if (MessageBox.Show($"删除已保存的连接「{name}」？", "FakeSqlCompare",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        Projects.Remove(SelectedProject);
        SelectedProject = null;
        PersistSession();
        ShowToast("已删除 " + name);
    }

    private void PersistSession()
    {
        ProjectStore.Save(new ProjectState
        {
            LastProjectId = SelectedProject?.Id,
            LastSource = ProjectStore.Capture(Source),
            LastTarget = ProjectStore.Capture(Target),
            Projects = Projects.ToList()
        });
    }

    private void GoHome()
    {
        _compareCts?.Cancel();
        _deployCts?.Cancel();
        Page = AppPage.Connect;
        HeaderHint = "配置源库与目标库，然后开始对比";
    }

    private void ToggleTheme()
    {
        ThemeService.Toggle();
        IsDark = ThemeService.Current == "Dark";
    }

    private async Task OpenDeployAsync()
    {
        if (SelectedCount == 0) return;
        if (!await EnsureDeployScriptAsync()) return;
        ConfirmDrop = false;
        DeployPage = 0;
        DeployLogs.Clear();
        Page = AppPage.Deploy;
        HeaderHint = $"更新 {Target.Database}";
    }

    private async Task<bool> EnsureDeployScriptAsync()
    {
        if (_session is null)
        {
            ShowToast("请先完成对比");
            return false;
        }
        try
        {
            ShowToast("正在按依赖顺序生成脚本…");
            var keys = SelectedForDeploy.Select(o => o.Id).ToList();
            var includeDrops = DropCount > 0;
            var script = await Task.Run(() => _session.GenerateScript(keys, includeDrops));
            CombinedScript = script;
            Raise(nameof(CombinedScript));
            return true;
        }
        catch (Exception ex)
        {
            ShowToast("生成脚本失败：" + ConnectionService.Describe(ex));
            return false;
        }
    }

    private bool CanDeployNext()
    {
        if (Deploying) return false;
        if (DeployPage == 0 && HasDrops && !ConfirmDrop) return false;
        return true;
    }

    private async Task DeployNextAsync()
    {
        if (DeployPage == 1)
        {
            Page = AppPage.Workspace;
            HeaderHint = $"{Source.Server}.{Source.Database}  →  {Target.Server}.{Target.Database}";
            return;
        }
        await RunDeployAsync();
    }

    private async Task RunDeployAsync()
    {
        DeployPage = 1;
        Deploying = true;
        DeploySucceeded = false;
        DeployLogs.Clear();
        if (_session is null)
        {
            Deploying = false;
            DeployLogs.Add("✗ 没有对比会话，请先重新对比");
            return;
        }

        _deployCts?.Cancel();
        _deployCts = new CancellationTokenSource();
        var log = new Progress<string>(line => DeployLogs.Add(line));
        try
        {
            await DeployService.RunAsync(
                _session.TargetConnectionString,
                Target.Database,
                CombinedScript,
                Backup,
                log,
                _deployCts.Token);
            DeploySucceeded = true;
            Deploying = false;
            DeployLogs.Add("✓ 更新完成，正在重新对比…");
            await RecheckAsync();
            if (AllObjects.Count > 0)
                ShowToast($"更新完成，仍有 {AllObjects.Count} 个差异");
            return;
        }
        catch (OperationCanceledException)
        {
            DeployLogs.Add("✗ 已取消");
        }
        catch (Exception ex)
        {
            var msg = ConnectionService.Describe(ex);
            if (!DeployLogs.Any(x => x.Contains(msg)))
                DeployLogs.Add("✗ " + msg);
        }
        finally
        {
            Deploying = false;
        }
    }

    private async Task ExportScriptAsync()
    {
        if (!await EnsureDeployScriptAsync()) return;
        var dlg = new SaveFileDialog
        {
            Filter = "SQL 脚本 (*.sql)|*.sql",
            FileName = $"deploy_{Target.Database}.sql"
        };
        if (dlg.ShowDialog() == true)
        {
            await File.WriteAllTextAsync(dlg.FileName, CombinedScript);
            ShowToast("已导出脚本");
        }
    }

    public void ShowToast(string message)
    {
        Toast = message;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast = "";
        };
        _toastTimer.Start();
    }
}
