using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using SysMgr.App.Converters;
using SysMgr.Core.Queue;
using SysMgr.Modules;
using SysMgr.Modules.Migration;
using SysMgr.Modules.Uninstall;

namespace SysMgr.App.ViewModels;

/// <summary>
/// 软件搬家页 ViewModel：扫描 C 盘已安装软件 → 勾选 → 整体搬到其他盘。
/// 搬家沿用迁移引擎（Folder 模式 + CreateJunction）：
/// 复制 → SHA-256 校验 → 删除源目录 → 在原位置创建目录联接，软件仍可按原路径启动。
/// 任一步失败自动回滚，软件保持原状。
/// </summary>
public sealed class SoftwareMoveViewModel : ViewModelBase
{
    private const string DestinationFolderName = "软件搬家";

    /// <summary>会话级扫描缓存：同一会话内回到本页不重复扫描与统计体积。</summary>
    private static List<SoftwareMoveEntry>? _sessionCache;

    private readonly SysMgrServices _services;
    private CancellationTokenSource? _scanCts;
    private string _destinationPath;
    private string _statusText = "准备扫描可搬家的软件…";
    private string _filter = string.Empty;
    private bool _isScanning;
    private bool _isMoving;
    private bool _showRecords;
    private bool _hasScanned;

    public SoftwareMoveViewModel(SysMgrServices services)
    {
        _services = services;
        _destinationPath = BuildDefaultDestination();

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = MatchesFilter;

        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsBusy);
        CancelScanCommand = new RelayCommand(_ => _scanCts?.Cancel(), _ => IsScanning);
        MoveCommand = new AsyncRelayCommand(_ => MoveSelectedAsync(), _ => !IsBusy && SelectedCount > 0);
        MoveSingleCommand = new AsyncRelayCommand(
            p => MoveSingleAsync(p as SoftwareMoveItemViewModel),
            p => !IsBusy && p is SoftwareMoveItemViewModel);
        ToggleRecordsCommand = new RelayCommand(_ => ShowRecords = !ShowRecords);
    }

    public ObservableCollection<SoftwareMoveItemViewModel> Items { get; } = new();
    public ObservableCollection<SoftwareMoveRecordViewModel> Records { get; } = new();

    /// <summary>过滤后的列表视图（支持搜索与点列头排序）。</summary>
    public ICollectionView ItemsView { get; }

    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public AsyncRelayCommand MoveCommand { get; }
    public AsyncRelayCommand MoveSingleCommand { get; }
    public RelayCommand ToggleRecordsCommand { get; }

    public bool IsBusy => _isScanning || _isMoving;

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (Set(ref _isScanning, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                RefreshCommands();
            }
        }
    }

    public bool IsMoving
    {
        get => _isMoving;
        private set
        {
            if (Set(ref _isMoving, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                RefreshCommands();
            }
        }
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public string DestinationPath
    {
        get => _destinationPath;
        set => Set(ref _destinationPath, value);
    }

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

    public bool ShowRecords { get => _showRecords; private set => Set(ref _showRecords, value); }

    public bool HasScanned { get => _hasScanned; private set => Set(ref _hasScanned, value); }

    public bool HasItems => Items.Count > 0;

    public bool IsEmpty => Items.Count == 0;

    public long TotalBytes => Items.Sum(i => i.SizeBytes);

    /// <summary>页头文案：「发现可搬家软件共 12 款 · 6.32 GB」。</summary>
    public string HeaderText => Items.Count == 0
        ? "发现可搬家软件"
        : $"发现可搬家软件共 {Items.Count} 款 · {BytesFormatter.Format(TotalBytes) ?? "0 B"}";

    public int SelectedCount => Items.Count(i => i.IsSelected);

    public long SelectedBytes => Items.Where(i => i.IsSelected).Sum(i => i.SizeBytes);

    public string SelectedText => BytesFormatter.Format(SelectedBytes) ?? "0 B";

    /// <summary>页面首次进入时调用：命中会话缓存则直接展示，否则扫描。</summary>
    public async Task EnsureLoadedAsync()
    {
        if (HasScanned || IsBusy)
        {
            return;
        }

        if (_sessionCache is { } cached)
        {
            ApplyEntries(cached);
            StatusText = cached.Count == 0
                ? "没有发现可以搬家的软件"
                : $"扫描完成，共 {cached.Count} 款软件可搬家";
            return;
        }

        await ScanAsync();
    }

    public async Task ScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;
        Items.Clear();
        HasScanned = false;
        NotifyListChanged();
        _scanCts = new CancellationTokenSource();

        try
        {
            StatusText = "正在扫描已安装软件…";
            var progress = new Progress<ScanProgress>(p =>
            {
                if (IsScanning)
                {
                    StatusText = $"正在扫描已安装软件… 已发现 {p.ScannedCount} 项";
                }
            });

            var candidates = new List<UninstallItem>();
            await foreach (var item in _services.Uninstall.ScanInstalledAsync(progress, _scanCts.Token))
            {
                if (IsMovable(item))
                {
                    candidates.Add(item);
                }
            }

            // 注册表里的 EstimatedSize 经常缺失或不准，这里按安装目录实测；
            // 多个安装目录并行统计，避免逐个等待拖慢扫描
            var total = candidates.Count;
            var entries = new SoftwareMoveEntry?[total];
            var measured = 0;
            IProgress<int> sizeProgress = new Progress<int>(n =>
                StatusText = $"正在统计安装体积（{n}/{total}）…");

            await Parallel.ForEachAsync(
                Enumerable.Range(0, total),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = _scanCts.Token },
                async (i, token) =>
                {
                    var item = candidates[i];
                    var folder = item.InstallLocation!.Trim().Trim('"');
                    var size = await Task.Run(() => MeasureDirectory(folder), token);
                    entries[i] = new SoftwareMoveEntry(
                        item.DisplayName,
                        item.Publisher,
                        item.DisplayVersion,
                        folder,
                        size,
                        item.IconPath);
                    sizeProgress.Report(Interlocked.Increment(ref measured));
                });

            var result = entries.Where(e => e is not null).Select(e => e!).ToList();
            result.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
            _sessionCache = result;
            ApplyEntries(result);
            StatusText = result.Count == 0
                ? "没有发现可以搬家的软件"
                : $"扫描完成，共 {result.Count} 款软件可搬家，合计 {BytesFormatter.Format(TotalBytes) ?? "0 B"}";
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
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    private void ApplyEntries(IReadOnlyList<SoftwareMoveEntry> entries)
    {
        Items.Clear();
        foreach (var entry in entries)
        {
            var item = new SoftwareMoveItemViewModel(entry);
            item.PropertyChanged += OnItemPropertyChanged;
            Items.Add(item);
        }

        HasScanned = true;
        NotifyListChanged();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SoftwareMoveItemViewModel.IsSelected))
        {
            OnSelectionChanged();
        }
    }

    private void NotifyListChanged()
    {
        ItemsView.Refresh();
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(HeaderText));
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedText));
        MoveCommand.RaiseCanExecuteChanged();
    }

    private void RefreshCommands()
    {
        ScanCommand.RaiseCanExecuteChanged();
        CancelScanCommand.RaiseCanExecuteChanged();
        MoveCommand.RaiseCanExecuteChanged();
        MoveSingleCommand.RaiseCanExecuteChanged();
    }

    private bool MatchesFilter(object obj)
        => obj is SoftwareMoveItemViewModel item
           && (string.IsNullOrWhiteSpace(Filter)
               || item.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)
               || (item.Publisher?.Contains(Filter, StringComparison.OrdinalIgnoreCase) ?? false)
               || item.InstallPath.Contains(Filter, StringComparison.OrdinalIgnoreCase));

    private Task MoveSelectedAsync()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusText = "请先勾选要搬家的软件";
            return Task.CompletedTask;
        }

        return MoveAsync(selected);
    }

    private Task MoveSingleAsync(SoftwareMoveItemViewModel? item)
        => item is null ? Task.CompletedTask : MoveAsync(new[] { item });

    private async Task MoveAsync(IReadOnlyList<SoftwareMoveItemViewModel> targets)
    {
        if (IsMoving)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(DestinationPath))
        {
            StatusText = "请先选择搬家的目标位置";
            return;
        }

        IsMoving = true;
        var total = targets.Count;
        var moved = new List<SoftwareMoveItemViewModel>();
        long freedBytes = 0;

        try
        {
            Directory.CreateDirectory(DestinationPath);

            for (var i = 0; i < total; i++)
            {
                var item = targets[i];
                StatusText = $"正在搬家（{i + 1}/{total}）：{item.Name}";

                if (IsRunningUnder(item.InstallPath))
                {
                    MessageBox.Show(
                        $"「{item.Name}」正在运行，文件被占用无法搬家。\n\n请先退出该软件后重试，已搬走的软件不受影响。",
                        "软件正在运行", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
                }

                var destination = Path.Combine(DestinationPath, MakeFolderName(item.Name, i));
                var request = new MigrationRequest(
                    Guid.NewGuid(),
                    MigrationKind.Folder,
                    new[] { item.InstallPath },
                    destination,
                    CreateJunction: true,
                    DeleteSourceAfterVerify: true);

                var progress = new Progress<OperationProgress>(p =>
                    StatusText = $"{item.Name}：{p.Stage}（{p.CurrentActionIndex}/{p.TotalActions}）");

                var result = await _services.Migration.EnqueueMigrationAsync(
                    request, progress, CancellationToken.None);

                if (!result.Success)
                {
                    StatusText = $"「{item.Name}」搬家失败：{result.Error ?? "未知原因"}";
                    MessageBox.Show(
                        $"「{item.Name}」搬家未完成，请查看下方执行及回滚结果。\n\n原因：{result.Error ?? "未知原因"}",
                        "软件搬家", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
                }

                moved.Add(item);
                freedBytes += item.SizeBytes;
                Records.Insert(0, new SoftwareMoveRecordViewModel(
                    DateTime.Now, item.Name, item.SizeText, destination));
            }
        }
        catch (Exception ex)
        {
            StatusText = $"搬家异常：{ex.Message}";
            MessageBox.Show($"软件搬家失败：{ex.Message}", "软件搬家",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RemoveItems(moved);
            IsMoving = false;

            if (moved.Count > 0)
            {
                var freedText = BytesFormatter.Format(freedBytes) ?? "0 B";
                StatusText = $"搬家完成：{moved.Count} 款软件，释放 {freedText}";
                MessageBox.Show(
                    $"已搬走 {moved.Count} 款软件，释放 {freedText}。\n目标位置：{DestinationPath}\n\n"
                    + "原安装目录已保留为目录联接，快捷方式与注册表无需修改。",
                    "搬家完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private void RemoveItems(IReadOnlyList<SoftwareMoveItemViewModel> moved)
    {
        if (moved.Count == 0)
        {
            return;
        }

        var paths = moved.Select(i => i.InstallPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _sessionCache = _sessionCache?.Where(e => !paths.Contains(e.InstallPath)).ToList();

        foreach (var item in moved)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
            Items.Remove(item);
        }

        NotifyListChanged();
    }

    /// <summary>可搬家：C 盘、有真实安装目录、非系统组件、且尚未是目录联接。</summary>
    private static bool IsMovable(UninstallItem item)
    {
        if (item.IsSystemComponent
            || string.IsNullOrWhiteSpace(item.InstallLocation)
            || item.InstallDrive is not { } drive
            || !drive.Equals("C:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var folder = item.InstallLocation.Trim().Trim('"');
        if (!Directory.Exists(folder))
        {
            return false;
        }

        try
        {
            return !File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>递归统计目录体积（跳过联接目录，避免环路）。</summary>
    private static long MeasureDirectory(string root)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 统计期间文件被删除/无权限，跳过
                    }
                }

                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (!File.GetAttributes(sub).HasFlag(FileAttributes.ReparsePoint))
                    {
                        pending.Push(sub);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 目录不可读，跳过
            }
        }

        return total;
    }

    /// <summary>是否有进程的可执行文件位于该安装目录下（搬家前提示先退出软件）。</summary>
    private static bool IsRunningUnder(string folder)
    {
        var root = folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path)
                    && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // 受保护的系统进程读不到路径，忽略
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    /// <summary>目标子目录名：去掉非法字符，重名时追加序号。</summary>
    private string MakeFolderName(string name, int index)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = $"软件 {index + 1}";
        }

        var candidate = cleaned;
        for (var i = 1; Directory.Exists(Path.Combine(DestinationPath, candidate)); i++)
        {
            candidate = $"{cleaned} ({i})";
        }

        return candidate;
    }

    private static string BuildDefaultDestination()
    {
        try
        {
            var target = DriveInfo.GetDrives()
                .Where(d => d.IsReady
                            && d.DriveType == DriveType.Fixed
                            && !string.Equals(d.Name[..1], "C", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.AvailableFreeSpace)
                .FirstOrDefault();

            var root = target is null ? @"C:\" : target.RootDirectory.FullName;
            return Path.Combine(root, DestinationFolderName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.Combine(@"C:\", DestinationFolderName);
        }
    }
}

/// <summary>可搬家软件条目（扫描结果快照）。</summary>
public sealed record SoftwareMoveEntry(
    string Name,
    string? Publisher,
    string? Version,
    string InstallPath,
    long SizeBytes,
    string? IconPath);

/// <summary>可搬家软件行。</summary>
public sealed class SoftwareMoveItemViewModel : ViewModelBase
{
    private bool _isSelected;

    public SoftwareMoveItemViewModel(SoftwareMoveEntry entry)
    {
        Name = entry.Name;
        Publisher = entry.Publisher;
        Version = entry.Version;
        InstallPath = entry.InstallPath;
        SizeBytes = entry.SizeBytes;
        IconPath = entry.IconPath;
        SizeText = BytesFormatter.Format(entry.SizeBytes) ?? "0 B";
    }

    public string Name { get; }
    public string? Publisher { get; }
    public string? Version { get; }
    public string InstallPath { get; }
    public long SizeBytes { get; }
    public string SizeText { get; }
    public string? IconPath { get; }

    /// <summary>名称 + 发布者 + 版本，列表副标题用。</summary>
    public string Subtitle
    {
        get
        {
            var parts = new[] { Publisher, Version }.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            return parts.Length == 0 ? InstallPath : string.Join(" · ", parts);
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

/// <summary>搬家记录（本次运行内）。</summary>
public sealed record SoftwareMoveRecordViewModel(
    DateTime Time,
    string Name,
    string SizeText,
    string Destination)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public string Summary => $"{Name} · {SizeText}";
}
