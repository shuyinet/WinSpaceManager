using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using SysMgr.App.Converters;
using SysMgr.Core.Queue;
using SysMgr.Modules;
using SysMgr.Modules.Uninstall;
using SysMgr.Modules.Uninstall.Errors;

namespace SysMgr.App.ViewModels;

/// <summary>
/// 软件卸载页 ViewModel（FR-UNINSTALL-01/02/04）。
/// 扫描 → 列表展示 → 选中后选模式（标准/强制）→ 提交卸载（走 TRUST-02 预演 + OpQueue）。
/// </summary>
public sealed class UninstallViewModel : ViewModelBase
{
    private readonly SysMgrServices _services;
    private CancellationTokenSource? _scanCts;
    private bool _isScanning;
    private string _statusText = "点击「扫描已装软件」开始";
    private string _filter = string.Empty;
    private UninstallItem? _selected;
    private bool _killProcesses;
    private bool _showSystemApps;
    private int _categoryIndex;
    private int _sortIndex;

    public int CategoryIndex
    {
        get => _categoryIndex;
        set { if (Set(ref _categoryIndex, value)) { ItemsView.Refresh(); RecomputeDriveStats(); } }
    }

    public int SortIndex
    {
        get => _sortIndex;
        set
        {
            if (!Set(ref _sortIndex, value)) return;
            using (ItemsView.DeferRefresh())
            {
                ItemsView.SortDescriptions.Clear();
                var property = value switch
                {
                    1 => nameof(UninstallItem.EstimatedSizeBytes),
                    2 => nameof(UninstallItem.InstallDate),
                    3 => nameof(UninstallItem.Publisher),
                    _ => nameof(UninstallItem.DisplayName)
                };
                ItemsView.SortDescriptions.Add(new SortDescription(property,
                    value is 1 or 2 ? ListSortDirection.Descending : ListSortDirection.Ascending));
            }
        }
    }
    private string _driveSummaryText = "尚未扫描";

    public UninstallViewModel(SysMgrServices services)
    {
        _services = services;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = MatchesFilter;
        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsScanning);
        CancelScanCommand = new RelayCommand(_ => _scanCts?.Cancel(), _ => IsScanning);
        UninstallStandardCommand = new AsyncRelayCommand(
            _ => UninstallAsync(UninstallMode.Standard), _ => Selected is not null && !IsScanning);
        UninstallForceCommand = new AsyncRelayCommand(
            _ => UninstallAsync(UninstallMode.Force), _ => Selected is not null && !IsScanning);
        OpenFolderCommand = new RelayCommand(p => OpenInstallFolder(p as UninstallItem ?? Selected));
        RowUninstallCommand = new RelayCommand(p =>
        {
            if (p is not UninstallItem item || IsScanning)
            {
                return;
            }

            // 行内按钮不依赖 DataGrid 当前选中行，先同步选中再执行标准卸载
            Selected = item;
            _ = RunUninstallAsync(item, UninstallMode.Standard);
        });
    }

    /// <summary>行内「打开目录」：跳转到安装文件夹（资源管理器）。</summary>
    public RelayCommand OpenFolderCommand { get; }

    /// <summary>行内「卸载」：选中该行并走标准卸载流程。</summary>
    public RelayCommand RowUninstallCommand { get; }

    public ObservableCollection<UninstallItem> Items { get; } = new();

    /// <summary>
    /// 列表视图（过滤 + 排序）。用 ICollectionView 而非 IEnumerable，
    /// 点击列头才能对结果排序（LINQ 迭代器不支持排序）。
    /// </summary>
    public ICollectionView ItemsView { get; }

    private bool MatchesFilter(object obj)
        => obj is UninstallItem item
           && (ShowSystemApps || !item.IsSystemComponent)
           && (CategoryIndex == 0 || (CategoryIndex == 1 ? !item.IsRuntimeLibrary : item.IsRuntimeLibrary))
           && (string.IsNullOrWhiteSpace(Filter)
               || item.DisplayName.Contains(Filter, StringComparison.OrdinalIgnoreCase)
               || (item.Publisher?.Contains(Filter, StringComparison.OrdinalIgnoreCase) ?? false));

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (Set(ref _isScanning, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                CancelScanCommand.RaiseCanExecuteChanged();
                UninstallStandardCommand.RaiseCanExecuteChanged();
                UninstallForceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value))
            {
                ItemsView.Refresh();
            }
        }
    }

    public UninstallItem? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                UninstallStandardCommand.RaiseCanExecuteChanged();
                UninstallForceCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(HasSelected));
            }
        }
    }

    /// <summary>列表是否有数据（空状态占位用）。</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>列表是否为空（空状态显示）。</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>是否有选中项（操作区可见性）。</summary>
    public bool HasSelected => Selected is not null;

    /// <summary>FR-UNINSTALL-02「强制结束进程并继续」开关。</summary>
    public bool KillProcesses { get => _killProcesses; set => Set(ref _killProcesses, value); }

    /// <summary>是否显示系统级组件（默认关闭，避免误卸载）。</summary>
    public bool ShowSystemApps
    {
        get => _showSystemApps;
        set
        {
            if (Set(ref _showSystemApps, value))
            {
                ItemsView.Refresh();
                RecomputeDriveStats();
                OnPropertyChanged(nameof(SystemToggleText));
            }
        }
    }

    /// <summary>各盘软件数量与体积（默认只统计非系统组件，与列表可见范围一致）。</summary>
    public ObservableCollection<DriveStatViewModel> DriveStats { get; } = new();

    /// <summary>顶部汇总文案：「共 120 款 · 45.6 GB」。</summary>
    public string DriveSummaryText { get => _driveSummaryText; private set => Set(ref _driveSummaryText, value); }

    /// <summary>系统组件开关文案，带数量提示。</summary>
    public string SystemToggleText
    {
        get
        {
            var hidden = Items.Count(i => i.IsSystemComponent);
            return hidden == 0 ? "显示系统软件" : $"显示系统软件（{hidden}）";
        }
    }

    private void RecomputeDriveStats()
    {
        var visible = Items.Where(i => MatchesFilter(i)).ToList();

        DriveStats.Clear();
        foreach (var group in visible
                     .GroupBy(i => i.InstallDriveText)
                     .OrderBy(g => g.Key == "未知" ? 1 : 0)
                     .ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            DriveStats.Add(new DriveStatViewModel(
                group.Key,
                group.Count(),
                group.Sum(i => i.EstimatedSizeBytes ?? 0)));
        }

        DriveSummaryText = visible.Count == 0
            ? "尚未扫描"
            : $"共 {visible.Count} 款 · {BytesFormatter.Format(visible.Sum(i => i.EstimatedSizeBytes ?? 0)) ?? "—"}";
    }

    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public AsyncRelayCommand UninstallStandardCommand { get; }
    public AsyncRelayCommand UninstallForceCommand { get; }

    private async Task ScanAsync()
    {
        IsScanning = true;
        Items.Clear();
        RecomputeDriveStats();
        _scanCts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(p =>
        {
            // 扫描结束后仍有排队回调，避免覆盖最终统计文案
            if (IsScanning)
            {
                StatusText = $"扫描中… 已发现 {p.ScannedCount} 项 {p.CurrentPath ?? string.Empty}";
            }
        });
        try
        {
            await foreach (var item in _services.Uninstall.ScanInstalledAsync(progress, _scanCts.Token))
            {
                Items.Add(item);
            }
            StatusText = $"共 {Items.Count} 款软件";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"已取消，已加载 {Items.Count} 项";
        }
        finally
        {
            IsScanning = false;
            _scanCts?.Dispose();
            _scanCts = null;
            RecomputeDriveStats();
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(SystemToggleText));
        }
    }

    private void OpenInstallFolder(UninstallItem? item)
    {
        if (item?.OpenableFolder is not { } folder)
        {
            StatusText = "未找到该软件的安装目录";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")
            {
                UseShellExecute = true
            });
            StatusText = $"已打开 {item.DisplayName} 的安装目录：{folder}";
        }
        catch (Exception ex)
        {
            StatusText = $"打开安装目录失败：{ex.Message}";
        }
    }

    private Task UninstallAsync(UninstallMode mode)
        => Selected is null ? Task.CompletedTask : RunUninstallAsync(Selected, mode);

    private async Task RunUninstallAsync(UninstallItem item, UninstallMode mode)
    {
        StatusText = mode == UninstallMode.Force
            ? $"正在强制卸载 {item.DisplayName}…"
            : $"正在卸载 {item.DisplayName}…";
        var options = new UninstallOptions(mode, KillProcesses, null);
        var progress = new Progress<OperationProgress>(p =>
            StatusText = $"{item.DisplayName}：{p.Stage} ({p.CurrentActionIndex}/{p.TotalActions})");

        try
        {
            var result = await _services.Uninstall.EnqueueUninstallAsync(
                item, options, progress, CancellationToken.None);
            if (result.Success)
            {
                StatusText = $"{item.DisplayName} 卸载完成";
                Items.Remove(item);
                RecomputeDriveStats();
                OnPropertyChanged(nameof(SystemToggleText));
                MessageBox.Show(
                    $"「{item.DisplayName}」已卸载完成。",
                    "卸载完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            StatusText = $"卸载未完成：{result.Error}";
            MessageBox.Show(
                $"「{item.DisplayName}」卸载未完成。外部卸载程序所做的更改可能无法自动恢复，请重新扫描确认安装状态。\n\n原因：{result.Error}",
                "卸载失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (SoftwareRunningException ex)
        {
            StatusText = $"{item.DisplayName} 正在运行，卸载已中止";
            var names = ex.RunningProcesses.Count == 0
                ? string.Empty
                : $"\n\n运行中的进程：{string.Join("、", ex.RunningProcesses)}";
            if (Ask("软件正在运行",
                    $"「{item.DisplayName}」正在运行，需先结束相关进程才能继续。{names}",
                    "结束进程并继续"))
            {
                KillProcesses = true;
                await RunUninstallAsync(item, mode);
            }
        }
        catch (UninstallerNotFoundException)
        {
            StatusText = $"{item.DisplayName} 未找到卸载程序";
            var extra = item.CanOpenFolder
                ? "强制卸载会结束相关进程，并直接删除安装目录、注册表项与服务，可能留下少量残留。"
                : "该软件未登记安装目录，强制卸载只能清理注册信息，文件需要你手动删除。";
            if (Ask("未找到卸载程序",
                    $"未找到「{item.DisplayName}」的卸载程序，无法执行标准卸载。\n\n{extra}",
                    "强制卸载"))
            {
                await RunUninstallAsync(item, UninstallMode.Force);
            }
        }
        catch (ExternalUninstallerTimeoutException)
        {
            StatusText = $"{item.DisplayName} 的卸载程序超时未响应";
            if (Ask("卸载程序无响应",
                    $"「{item.DisplayName}」的卸载程序长时间没有响应，可改用强制卸载。",
                    "强制卸载"))
            {
                await RunUninstallAsync(item, UninstallMode.Force);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"卸载异常：{ex.Message}";
            MessageBox.Show($"「{item.DisplayName}」卸载异常：\n\n{ex.Message}", "卸载异常",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>弹出确认对话框，返回用户是否选择了主操作。</summary>
    private static bool Ask(string title, string message, string primaryText)
    {
        var dialog = new Views.ConfirmDialog(title, message, primaryText)
        {
            Owner = Application.Current?.MainWindow
        };
        return dialog.ShowDialog() == true;
    }
}

/// <summary>单个磁盘的软件统计（数量 + 占用体积）。</summary>
public sealed class DriveStatViewModel
{
    public DriveStatViewModel(string driveText, int count, long bytes)
    {
        DriveText = driveText;
        Count = count;
        Bytes = bytes;
    }

    public string DriveText { get; }

    public int Count { get; }

    public long Bytes { get; }

    public string CountText => $"{Count} 款";

    public string SizeText => BytesFormatter.Format(Bytes) ?? "—";
}
