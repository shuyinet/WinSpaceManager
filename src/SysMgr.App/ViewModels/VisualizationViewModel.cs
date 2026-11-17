using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using SysMgr.Core.Queue;
using SysMgr.Modules;
using SysMgr.Modules.Visualization;

namespace SysMgr.App.ViewModels;

/// <summary>
/// 磁盘可视化页 ViewModel（FR-VIS-01/02/03）。
/// 扫描 → Treemap 下钻/返回 → 筛选（名称/扩展名/大小/日期/属性）→
/// 右键定位/删除（回滚）/迁移 → 标签与快照导出。
/// </summary>
public sealed class VisualizationViewModel : ViewModelBase
{
    private const string DefaultTag = "待处理";
    private const int TreemapDisplayLimit = 400;

    private readonly SysMgrServices _services;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _filterDebounce;

    private TreemapNode? _rawRoot;
    private TreemapNode? _filteredRoot;
    private readonly List<TreemapNode> _navStack = new();
    private readonly HashSet<string> _taggedPaths = new(StringComparer.OrdinalIgnoreCase);

    private bool _isScanning;
    private string _statusText = "选择盘符或输入路径后开始扫描";
    private string _scanPath;
    private string _selectedDrive;
    private TreemapNode? _currentNode;
    private TreemapNode? _selectedNode;
    private string _filterText = string.Empty;
    private long _minSizeMb;
    private DateTime? _modifiedAfter;
    private bool _excludeHidden = true;
    private string _destinationRoot = string.Empty;
    private string? _selectedNodeTag;

    public VisualizationViewModel(SysMgrServices services)
    {
        _services = services;
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.Name)
            .ToList();
        AvailableDrives = new ObservableCollection<string>(drives);
        var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
        _selectedDrive = drives.Contains(systemDrive) ? systemDrive : drives.FirstOrDefault() ?? systemDrive;
        _scanPath = _selectedDrive;

        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsScanning);
        CancelScanCommand = new RelayCommand(_ => _scanCts?.Cancel(), _ => IsScanning);
        BackCommand = new RelayCommand(_ => GoBack(), _ => _navStack.Count > 1);
        NavigateCrumbCommand = new RelayCommand(p => NavigateTo(p as TreemapNode));
        DrillCommand = new RelayCommand(p => DrillInto(p as TreemapNode));
        OpenInExplorerCommand = new RelayCommand(p => OpenInExplorer(p as TreemapNode));
        DeleteCommand = new AsyncRelayCommand(p => DeleteAsync(p as TreemapNode), p => !IsScanning && p is TreemapNode { IsOtherAggregate: false });
        MigrateCommand = new AsyncRelayCommand(p => MigrateAsync(p as TreemapNode), p => !IsScanning && p is TreemapNode { IsOtherAggregate: false } && !string.IsNullOrWhiteSpace(DestinationRoot));
        ToggleTagCommand = new AsyncRelayCommand(p => ToggleTagAsync(p as TreemapNode), p => p is TreemapNode { IsOtherAggregate: false });
        ExportCommand = new AsyncRelayCommand(_ => ExportAsync(), _ => _filteredRoot is not null && !IsScanning);
    }

    public ObservableCollection<string> AvailableDrives { get; }
    public ObservableCollection<TreemapNode> Breadcrumbs { get; } = new();
    public ObservableCollection<TreemapNode> ListItems { get; } = new();

    private IReadOnlyList<TreemapNode> _displayChildren = Array.Empty<TreemapNode>();
    /// <summary>当前层级 Treemap 展示子项（含展示上限截断）。</summary>
    public IReadOnlyList<TreemapNode> DisplayChildren
    {
        get => _displayChildren;
        private set => Set(ref _displayChildren, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (Set(ref _isScanning, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                CancelScanCommand.RaiseCanExecuteChanged();
                DeleteCommand.RaiseCanExecuteChanged();
                MigrateCommand.RaiseCanExecuteChanged();
                ExportCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public string ScanPath { get => _scanPath; set => Set(ref _scanPath, value); }

    public string SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (Set(ref _selectedDrive, value) && !string.IsNullOrWhiteSpace(value))
            {
                ScanPath = value;
            }
        }
    }

    public TreemapNode? CurrentNode { get => _currentNode; private set => Set(ref _currentNode, value); }

    public TreemapNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (Set(ref _selectedNode, value))
            {
                _selectedNodeTag = value is not null && _taggedPaths.Contains(value.Path) ? DefaultTag : null;
                OnPropertyChanged(nameof(SelectedNodeTag));
                OnPropertyChanged(nameof(HasSelection));
                DeleteCommand.RaiseCanExecuteChanged();
                MigrateCommand.RaiseCanExecuteChanged();
                ToggleTagCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelection => SelectedNode is not null;

    /// <summary>当前选中项标签（null=未打标）。</summary>
    public string? SelectedNodeTag { get => _selectedNodeTag; private set => Set(ref _selectedNodeTag, value); }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (Set(ref _filterText, value))
            {
                DebouncedApplyFilter();
            }
        }
    }

    public long MinSizeMb
    {
        get => _minSizeMb;
        set
        {
            if (Set(ref _minSizeMb, value))
            {
                ApplyFilter();
            }
        }
    }

    public DateTime? ModifiedAfter
    {
        get => _modifiedAfter;
        set
        {
            if (Set(ref _modifiedAfter, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool ExcludeHidden
    {
        get => _excludeHidden;
        set
        {
            if (Set(ref _excludeHidden, value))
            {
                ApplyFilter();
            }
        }
    }

    public string DestinationRoot
    {
        get => _destinationRoot;
        set
        {
            if (Set(ref _destinationRoot, value))
            {
                MigrateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand NavigateCrumbCommand { get; }
    public RelayCommand DrillCommand { get; }
    public RelayCommand OpenInExplorerCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand MigrateCommand { get; }
    public AsyncRelayCommand ToggleTagCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }

    // ---------------- 扫描 ----------------

    private async Task ScanAsync()
    {
        var target = ScanPath.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        IsScanning = true;
        _scanCts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(p =>
            StatusText = p.Phase == ScanPhase.Done
                ? "扫描完成"
                : $"扫描中… 已处理 {p.ScannedCount:N0} 项 {p.CurrentPath}");
        try
        {
            _rawRoot = await _services.Visualization.ScanAsync(target, progress, _scanCts.Token);
            await LoadTagsAsync();
            ApplyFilter();
            StatusText =
                $"共 {Converters.BytesFormatter.Format(_rawRoot.TotalBytes)}，{CountFiles(_rawRoot):N0} 个文件";
        }
        catch (OperationCanceledException)
        {
            StatusText = "扫描已取消";
        }
        catch (Exception ex)
        {
            StatusText = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsScanning = false;
            _scanCts.Dispose();
            _scanCts = null;
        }
    }

    private static int CountFiles(TreemapNode node)
        => node.FileCount + node.Children.Where(c => c.IsDirectory).Sum(CountFiles);

    private async Task LoadTagsAsync()
    {
        _taggedPaths.Clear();
        try
        {
            foreach (var kv in await _services.Visualization.GetTagsAsync(CancellationToken.None))
            {
                _taggedPaths.Add(kv.Key);
            }
        }
        catch (Exception)
        {
            // 标签读取失败不阻塞主流程
        }
    }

    // ---------------- 筛选 ----------------

    private async void DebouncedApplyFilter()
    {
        _filterDebounce?.Cancel();
        var cts = _filterDebounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(280, cts.Token);
            if (!cts.IsCancellationRequested)
            {
                ApplyFilter();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ApplyFilter()
    {
        if (_rawRoot is null)
        {
            return;
        }

        var previousPath = CurrentNode?.Path;
        _filteredRoot = _services.Visualization.FilterTree(
            _rawRoot,
            string.IsNullOrWhiteSpace(FilterText) ? null : FilterText,
            MinSizeMb * 1024 * 1024,
            ModifiedAfter.HasValue ? new DateTimeOffset(ModifiedAfter.Value) : null,
            ExcludeHidden);

        // 过滤后重定位当前节点（按路径解析；失败回落根）
        _navStack.Clear();
        _navStack.Add(_filteredRoot);
        if (previousPath is not null && previousPath != _filteredRoot.Path)
        {
            var resolved = ResolvePath(_filteredRoot, previousPath);
            if (resolved is not null)
            {
                _navStack.AddRange(resolved.Skip(1));
            }
        }
        RefreshView();
    }

    /// <summary>按路径从 root 向下解析导航链（含 root），失败返回 null。</summary>
    private static List<TreemapNode>? ResolvePath(TreemapNode root, string path)
    {
        var chain = new List<TreemapNode> { root };
        var current = root;
        while (!current.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            var next = current.Children.FirstOrDefault(c =>
                path.Equals(c.Path, StringComparison.OrdinalIgnoreCase)
                || (c.IsDirectory
                    && path.StartsWith(c.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)));
            if (next is null)
            {
                return null;
            }
            chain.Add(next);
            current = next;
        }
        return chain;
    }

    // ---------------- 导航 ----------------

    public void DrillInto(TreemapNode? node)
    {
        if (node is null || !node.IsDirectory || node.Children.Count == 0)
        {
            return;
        }
        _navStack.Add(node);
        RefreshView();
    }

    public void GoBack()
    {
        if (_navStack.Count > 1)
        {
            _navStack.RemoveAt(_navStack.Count - 1);
            RefreshView();
        }
    }

    private void NavigateTo(TreemapNode? node)
    {
        if (node is null)
        {
            return;
        }
        var index = _navStack.IndexOf(node);
        if (index >= 0)
        {
            _navStack.RemoveRange(index + 1, _navStack.Count - index - 1);
            RefreshView();
        }
    }

    private void RefreshView()
    {
        var current = _navStack.LastOrDefault();
        CurrentNode = current;

        Breadcrumbs.Clear();
        foreach (var crumb in _navStack)
        {
            Breadcrumbs.Add(crumb);
        }
        BackCommand.RaiseCanExecuteChanged();

        ListItems.Clear();
        if (current is not null)
        {
            var children = current.Children;
            DisplayChildren = children.Count > TreemapDisplayLimit
                ? children.Take(TreemapDisplayLimit).ToList()
                : children;
            foreach (var child in children.Take(500))
            {
                ListItems.Add(child);
            }
            OnPropertyChanged(nameof(CurrentSummary));
        }
        else
        {
            DisplayChildren = Array.Empty<TreemapNode>();
        }
        SelectedNode = null;
    }

    public string CurrentSummary => CurrentNode is null
        ? string.Empty
        : $"{Converters.BytesFormatter.Format(CurrentNode.TotalBytes)} · {CurrentNode.Children.Count} 个直接子项";

    // ---------------- 节点操作 ----------------

    public void OpenInExplorer(TreemapNode? node)
    {
        if (node is null || node.IsOtherAggregate)
        {
            return;
        }
        try
        {
            if (node.IsDirectory)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{node.Path}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{node.Path}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            StatusText = $"打开失败：{ex.Message}";
        }
    }

    private async Task DeleteAsync(TreemapNode? node)
    {
        if (node is null || node.IsOtherAggregate)
        {
            return;
        }
        StatusText = $"正在删除 {node.Name}…";
        try
        {
            var result = await _services.Visualization.EnqueueDeleteAsync(
                new[] { node.Path }, null, CancellationToken.None);
            StatusText = result.Success
                ? $"已删除 {node.Name}"
                : $"删除未完成：{result.Error}";
            if (result.Success)
            {
                RemoveNode(node);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"删除异常：{ex.Message}";
        }
    }

    private async Task MigrateAsync(TreemapNode? node)
    {
        if (node is null || node.IsOtherAggregate || string.IsNullOrWhiteSpace(DestinationRoot))
        {
            return;
        }
        StatusText = $"正在迁移 {node.Name}…";
        var progress = new Progress<OperationProgress>(p =>
            StatusText = $"迁移中：{p.Stage} ({p.CurrentActionIndex}/{p.TotalActions})");
        try
        {
            var result = await _services.Visualization.EnqueueMigrationAsync(
                new[] { node.Path }, DestinationRoot.Trim(), progress, CancellationToken.None);
            StatusText = result.Success
                ? $"已迁移 {node.Name}"
                : $"迁移失败：{result.Error}（已回滚）";
            if (result.Success)
            {
                RemoveNode(node);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"迁移异常：{ex.Message}";
        }
    }

    /// <summary>删除/迁移成功后从原始树与过滤树中摘除节点并刷新。</summary>
    private void RemoveNode(TreemapNode node)
    {
        if (_rawRoot is not null)
        {
            _rawRoot = Detach(_rawRoot, node.Path);
        }
        ApplyFilter();
    }

    private static TreemapNode Detach(TreemapNode current, string path)
    {
        if (!current.Children.Any(c => c.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            return current with
            {
                Children = current.Children
                    .Select(c => c.IsDirectory ? Detach(c, path) : c)
                    .ToList()
            };
        }

        var kept = current.Children
            .Where(c => !c.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.IsDirectory ? Detach(c, path) : c)
            .ToList();
        return current with
        {
            Children = kept,
            TotalBytes = kept.Sum(c => c.TotalBytes)
        };
    }

    // ---------------- 标签 / 导出 ----------------

    public bool IsTagged(TreemapNode node) => _taggedPaths.Contains(node.Path);

    private async Task ToggleTagAsync(TreemapNode? node)
    {
        if (node is null || node.IsOtherAggregate)
        {
            return;
        }
        try
        {
            if (_taggedPaths.Contains(node.Path))
            {
                await _services.Visualization.SetTagAsync(node.Path, null, CancellationToken.None);
                _taggedPaths.Remove(node.Path);
                StatusText = $"已移除标签：{node.Name}";
            }
            else
            {
                await _services.Visualization.SetTagAsync(node.Path, DefaultTag, CancellationToken.None);
                _taggedPaths.Add(node.Path);
                StatusText = $"已标记「{DefaultTag}」：{node.Name}";
            }
            if (ReferenceEquals(SelectedNode, node))
            {
                SelectedNodeTag = _taggedPaths.Contains(node.Path) ? DefaultTag : null;
            }
        }
        catch (Exception ex)
        {
            StatusText = $"标签保存失败：{ex.Message}";
        }
    }

    private async Task ExportAsync()
    {
        if (_filteredRoot is null)
        {
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "导出扫描快照",
            Filter = "JSON 快照 (*.json)|*.json",
            FileName = $"sysmgr-vis-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        try
        {
            await _services.Visualization.ExportSnapshotAsync(_filteredRoot, dialog.FileName, CancellationToken.None);
            StatusText = $"快照已导出：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
        }
    }
}
