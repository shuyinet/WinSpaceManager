using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using SysMgr.App.Converters;
using SysMgr.Core.Queue;
using SysMgr.Modules;
using SysMgr.Modules.Migration;

namespace SysMgr.App.ViewModels;

/// <summary>
/// 大文件搬家页 ViewModel：扫描常见大文件目录 → 按分类聚合 → 勾选后搬到其他盘。
/// 搬家沿用迁移引擎（TRUST-02 预演 + OpQueue，SHA-256 校验后再删除源文件）。
/// </summary>
public sealed class MigrationViewModel : ViewModelBase
{
    private const string DestinationFolderName = "大文件搬家";

    /// <summary>会话级扫描缓存：同一会话内回到本页不重复全盘扫描。</summary>
    private static List<LargeFileEntry>? _sessionCache;

    private readonly SysMgrServices _services;
    private readonly LargeFileScanner _scanner = new();
    private string _destinationPath;
    private string _statusText = "准备扫描可搬家的大文件…";
    private bool _isScanning;
    private bool _isMigrating;
    private bool _showRecords;
    private bool _hasScanned;
    private long _totalBytes;

    public MigrationViewModel(SysMgrServices services)
    {
        _services = services;
        _destinationPath = BuildDefaultDestination();

        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsBusy);
        MigrateCommand = new AsyncRelayCommand(_ => MigrateSelectedAsync(), _ => !IsBusy && SelectedCount > 0);
        MigrateSingleCommand = new AsyncRelayCommand(
            p => MigrateSingleAsync(p as LargeFileItemViewModel),
            p => !IsBusy && p is LargeFileItemViewModel);
        ToggleRecordsCommand = new RelayCommand(_ => ShowRecords = !ShowRecords);
    }

    public ObservableCollection<LargeFileGroupViewModel> Groups { get; } = new();
    public ObservableCollection<MigrationRecordViewModel> Records { get; } = new();

    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand MigrateCommand { get; }
    public AsyncRelayCommand MigrateSingleCommand { get; }
    public RelayCommand ToggleRecordsCommand { get; }

    public bool IsBusy => _isScanning || _isMigrating;

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

    public bool IsMigrating
    {
        get => _isMigrating;
        private set
        {
            if (Set(ref _isMigrating, value))
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

    public bool ShowRecords { get => _showRecords; private set => Set(ref _showRecords, value); }

    public bool HasScanned { get => _hasScanned; private set => Set(ref _hasScanned, value); }

    public bool HasResults => Groups.Count > 0;

    public long TotalBytes { get => _totalBytes; private set { if (Set(ref _totalBytes, value)) OnPropertyChanged(nameof(TotalText)); } }

    public string TotalText => BytesFormatter.Format(TotalBytes) ?? "0 B";

    public int SelectedCount => Groups.Sum(g => g.Items.Count(i => i.IsSelected));

    public long SelectedBytes => Groups.Sum(g => g.Items.Where(i => i.IsSelected).Sum(i => i.SizeBytes));

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
                ? "没有发现 50MB 以上的大文件"
                : $"扫描完成，共发现 {cached.Count} 个大文件，可释放 {TotalText}";
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
        Groups.Clear();
        TotalBytes = 0;
        HasScanned = false;
        OnPropertyChanged(nameof(HasResults));
        OnSelectionChanged();

        try
        {
            var progress = new Progress<int>(n => StatusText = $"正在扫描 C 盘… 已检查 {n} 个文件");
            var entries = await _scanner.ScanAsync(LargeFileScanner.DefaultMinSizeBytes, progress, CancellationToken.None);
            _sessionCache = entries;
            ApplyEntries(entries);

            StatusText = entries.Count == 0
                ? "没有发现 50MB 以上的大文件"
                : $"扫描完成，共发现 {entries.Count} 个大文件，可释放 {TotalText}";
        }
        catch (Exception ex)
        {
            StatusText = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void ApplyEntries(IReadOnlyList<LargeFileEntry> entries)
    {
        Groups.Clear();

        var order = new[]
        {
            LargeFileCategory.Installer,
            LargeFileCategory.Archive,
            LargeFileCategory.Log,
            LargeFileCategory.Video,
            LargeFileCategory.Image,
            LargeFileCategory.Other
        };
        var titles = new Dictionary<LargeFileCategory, string>
        {
            [LargeFileCategory.Installer] = "安装包",
            [LargeFileCategory.Archive] = "压缩包",
            [LargeFileCategory.Log] = "日志与转储文件",
            [LargeFileCategory.Video] = "视频",
            [LargeFileCategory.Image] = "镜像与虚拟磁盘",
            [LargeFileCategory.Other] = "其他大文件"
        };

        foreach (var category in order)
        {
            var groupEntries = entries.Where(e => e.Category == category).ToList();
            if (groupEntries.Count == 0)
            {
                continue;
            }

            var group = new LargeFileGroupViewModel(titles[category], groupEntries);
            foreach (var item in group.Items)
            {
                item.PropertyChanged += OnItemPropertyChanged;
            }
            Groups.Add(group);
        }

        TotalBytes = entries.Sum(e => e.SizeBytes);
        HasScanned = true;
        OnPropertyChanged(nameof(HasResults));
        OnSelectionChanged();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LargeFileItemViewModel.IsSelected))
        {
            return;
        }

        if (sender is LargeFileItemViewModel item)
        {
            item.Group.NotifySelectionChanged();
        }
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedText));
        MigrateCommand.RaiseCanExecuteChanged();
    }

    private void RefreshCommands()
    {
        ScanCommand.RaiseCanExecuteChanged();
        MigrateCommand.RaiseCanExecuteChanged();
        MigrateSingleCommand.RaiseCanExecuteChanged();
    }

    private Task MigrateSelectedAsync()
    {
        var selected = Groups
            .SelectMany(g => g.Items)
            .Where(i => i.IsSelected)
            .ToList();

        if (selected.Count == 0)
        {
            StatusText = "请先勾选要搬家的文件";
            return Task.CompletedTask;
        }

        return MigrateItemsAsync(selected);
    }

    private Task MigrateSingleAsync(LargeFileItemViewModel? item)
        => item is null ? Task.CompletedTask : MigrateItemsAsync(new[] { item });

    private async Task MigrateItemsAsync(IReadOnlyList<LargeFileItemViewModel> items)
    {
        if (IsMigrating)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(DestinationPath))
        {
            StatusText = "请先选择搬家的目标位置";
            return;
        }

        IsMigrating = true;
        var sizeText = BytesFormatter.Format(items.Sum(i => i.SizeBytes)) ?? "0 B";
        StatusText = $"正在搬家 {items.Count} 个文件（{sizeText}）…";

        try
        {
            Directory.CreateDirectory(DestinationPath);

            var request = new MigrationRequest(
                Guid.NewGuid(),
                MigrationKind.File,
                items.Select(i => i.Path).ToList(),
                DestinationPath,
                CreateJunction: false,
                DeleteSourceAfterVerify: true);

            var progress = new Progress<OperationProgress>(p =>
                StatusText = $"搬家进行中：{p.Stage}（{p.CurrentActionIndex}/{p.TotalActions}）");

            var result = await _services.Migration.EnqueueMigrationAsync(
                request, progress, CancellationToken.None);

            if (!result.Success)
            {
                StatusText = $"搬家失败：{result.Error ?? "未知原因"}";
                MessageBox.Show(
                    $"搬家未完成：{result.Error ?? "未知原因"}\n\n源文件未被删除。",
                    "大文件搬家", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RemoveItems(items);
            Records.Insert(0, new MigrationRecordViewModel(
                DateTime.Now, items.Count, sizeText, DestinationPath));

            StatusText = $"搬家完成：{items.Count} 个文件（{sizeText}）已搬到 {DestinationPath}";
            MessageBox.Show(
                $"已搬走 {items.Count} 个文件，释放 {sizeText}。\n目标位置：{DestinationPath}",
                "搬家完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText = $"搬家异常：{ex.Message}";
            MessageBox.Show($"搬家失败：{ex.Message}", "大文件搬家",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsMigrating = false;
        }
    }

    private void RemoveItems(IReadOnlyList<LargeFileItemViewModel> migrated)
    {
        var paths = migrated.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _sessionCache = _sessionCache?.Where(e => !paths.Contains(e.Path)).ToList();

        foreach (var group in Groups.ToList())
        {
            foreach (var item in group.Items.Where(i => paths.Contains(i.Path)).ToList())
            {
                item.PropertyChanged -= OnItemPropertyChanged;
                group.Items.Remove(item);
            }

            group.NotifySelectionChanged();
            if (group.Items.Count == 0)
            {
                Groups.Remove(group);
            }
        }

        TotalBytes = Groups.SelectMany(g => g.Items).Sum(i => i.SizeBytes);
        OnPropertyChanged(nameof(HasResults));
        OnSelectionChanged();
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

/// <summary>大文件行。</summary>
public sealed class LargeFileItemViewModel : ViewModelBase
{
    private bool _isSelected = true;

    public LargeFileItemViewModel(LargeFileEntry entry, LargeFileGroupViewModel group)
    {
        Path = entry.Path;
        Name = entry.Name;
        SizeBytes = entry.SizeBytes;
        Folder = System.IO.Path.GetDirectoryName(entry.Path) ?? string.Empty;
        SizeText = BytesFormatter.Format(entry.SizeBytes) ?? string.Empty;
        Group = group;
    }

    public string Path { get; }
    public string Name { get; }
    public string Folder { get; }
    public long SizeBytes { get; }
    public string SizeText { get; }
    public LargeFileGroupViewModel Group { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

/// <summary>按分类聚合的分组（可折叠、可整组勾选）。</summary>
public sealed class LargeFileGroupViewModel : ViewModelBase
{
    private bool _isExpanded = true;
    private bool _suppressSync;

    internal LargeFileGroupViewModel(string title, IEnumerable<LargeFileEntry> entries)
    {
        Title = title;
        Items = new ObservableCollection<LargeFileItemViewModel>(
            entries.Select(e => new LargeFileItemViewModel(e, this)));
        ToggleCommand = new RelayCommand(_ => IsExpanded = !IsExpanded);
    }

    public RelayCommand ToggleCommand { get; }

    public string Title { get; }
    public ObservableCollection<LargeFileItemViewModel> Items { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ChevronGlyph));
            }
        }
    }

    public string ChevronGlyph => IsExpanded ? "\uE70D" : "\uE76C";

    public long TotalBytes => Items.Sum(i => i.SizeBytes);
    public string TotalText => BytesFormatter.Format(TotalBytes) ?? "0 B";
    public string CountText => $"{Items.Count} 项";

    public bool IsAllSelected
    {
        get => Items.Count > 0 && Items.All(i => i.IsSelected);
        set
        {
            if (_suppressSync)
            {
                return;
            }

            _suppressSync = true;
            foreach (var item in Items)
            {
                item.IsSelected = value;
            }
            _suppressSync = false;
            OnPropertyChanged();
        }
    }

    internal void NotifySelectionChanged()
    {
        if (!_suppressSync)
        {
            OnPropertyChanged(nameof(IsAllSelected));
        }
    }
}

/// <summary>搬家记录（本次运行内）。</summary>
public sealed record MigrationRecordViewModel(
    DateTime Time,
    int FileCount,
    string SizeText,
    string Destination)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public string Summary => $"{FileCount} 个文件 · {SizeText}";
}
