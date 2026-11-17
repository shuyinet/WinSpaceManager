using System.Collections.ObjectModel;
using System.Windows.Media;
using SysMgr.App.Converters;
using SysMgr.Core.Queue;
using SysMgr.Modules;
using SysMgr.Modules.Cleanup;
using SysMgr.Modules.Cleanup.Errors;
using Wpf.Ui.Controls;

namespace SysMgr.App.ViewModels;

/// <summary>
/// 垃圾清理页 ViewModel（FR-CLEAN-01/02/06）。
/// 选择范围 → 扫描 → 按「系统垃圾 / 各浏览器」分组展示，组内按垃圾类型聚合为一张卡片（同一类型的多个文件合并计数与小计），
/// 组头给出共/已选小计与全选；勾选项入队清除（TRUST-02 预演）。
/// </summary>
public sealed class CleanupViewModel : ViewModelBase
{
    private const string SystemGroupKey = "system";
    private const string AppCacheGroupKey = "appcache";

    private readonly SysMgrServices _services;
    private readonly Dictionary<string, CleanupGroupViewModel> _groupMap = new();
    private CancellationTokenSource? _scanCts;
    private bool _isScanning;
    private bool _isCleaning;
    private bool _hasScanned;
    private string _statusText = "点击「扫描垃圾」开始按分类汇总";
    private bool _scanSystemJunk = true;
    private bool _scanBrowserCache = true;
    private bool _scanBrowserHistory = false;
    private bool _scanBrowserCookies = false;
    private bool _killRunningBrowsers;

    public CleanupViewModel(SysMgrServices services)
    {
        _services = services;
        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsBusy && HasAnyScope);
        CancelCommand = new RelayCommand(_ => _scanCts?.Cancel(), _ => IsScanning);
        CleanCommand = new AsyncRelayCommand(_ => CleanSelectedAsync(), _ => !IsBusy && SelectedCount > 0);
        OneClickCleanCommand = new AsyncRelayCommand(_ => OneClickCleanAsync(), _ => !IsBusy && DefaultSelectedCount > 0);
    }

    /// <summary>分类分组（系统垃圾在前，浏览器按 Edge/Chrome/Firefox/Brave/Opera 顺序）。</summary>
    public ObservableCollection<CleanupGroupViewModel> Groups { get; } = new();

    private IEnumerable<CleanupTileViewModel> AllTiles => Groups.SelectMany(g => g.Tiles);

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (Set(ref _isScanning, value))
            {
                RaiseBusyCommands();
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    /// <summary>清理执行中（含等待预演/统计剩余空间）。</summary>
    public bool IsCleaning
    {
        get => _isCleaning;
        private set
        {
            if (Set(ref _isCleaning, value))
            {
                RaiseBusyCommands();
            }
        }
    }

    public bool IsBusy => IsScanning || IsCleaning;

    private void RaiseBusyCommands()
    {
        OnPropertyChanged(nameof(IsBusy));
        ScanCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        CleanCommand.RaiseCanExecuteChanged();
        OneClickCleanCommand.RaiseCanExecuteChanged();
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    /// <summary>扫描按钮文案：首次为「扫描垃圾」，扫描过后变「重新扫描」。</summary>
    public string ScanButtonText => _hasScanned ? "重新扫描" : "扫描垃圾";

    /// <summary>无分类可展示时显示占位卡片。</summary>
    public bool IsEmpty => !IsScanning && Groups.Count == 0;

    public string EmptyHintText => _hasScanned ? "已清理完毕，没有更多可清理的垃圾" : "尚未扫描，点击「扫描垃圾」查看分类结果";

    // ---- 顶部汇总 ----
    public string TotalText => BytesFormatter.Format(TotalBytes);
    public string SelectedText => BytesFormatter.Format(SelectedBytes);
    public long TotalBytes => AllTiles.Sum(t => t.TotalBytes);
    public long SelectedBytes => AllTiles.Where(t => t.IsSelected).Sum(t => t.TotalBytes);
    public int SelectedCount => AllTiles.Where(t => t.IsSelected).Sum(t => t.Items.Count(i => i.EstimatedSizeBytes > 0));
    public int DefaultSelectedCount => AllTiles.Where(t => t.DefaultSelected).Sum(t => t.Items.Count(i => i.EstimatedSizeBytes > 0));
    public string CleanButtonText => SelectedCount > 0 ? $"立即清理（{SelectedCount} 项）" : "立即清理";

    public bool ScanSystemJunk { get => _scanSystemJunk; set { if (Set(ref _scanSystemJunk, value)) ScanCommand.RaiseCanExecuteChanged(); } }
    public bool ScanBrowserCache { get => _scanBrowserCache; set { if (Set(ref _scanBrowserCache, value)) ScanCommand.RaiseCanExecuteChanged(); } }
    public bool ScanBrowserHistory { get => _scanBrowserHistory; set { if (Set(ref _scanBrowserHistory, value)) ScanCommand.RaiseCanExecuteChanged(); } }
    public bool ScanBrowserCookies { get => _scanBrowserCookies; set { if (Set(ref _scanBrowserCookies, value)) ScanCommand.RaiseCanExecuteChanged(); } }
    public bool KillRunningBrowsers { get => _killRunningBrowsers; set => Set(ref _killRunningBrowsers, value); }

    private bool HasAnyScope => ScanSystemJunk || ScanBrowserCache || ScanBrowserHistory || ScanBrowserCookies;

    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand CleanCommand { get; }
    public AsyncRelayCommand OneClickCleanCommand { get; }

    private async Task ScanAsync()
    {
        IsScanning = true;
        Groups.Clear();
        _groupMap.Clear();
        _scanCts = new CancellationTokenSource();
        // 扫描结束后就不再接受进度回调，避免异步回调覆盖最终状态文案
        var progress = new Progress<ScanProgress>(p =>
        {
            if (IsScanning)
            {
                StatusText = $"扫描中… {p.Phase} ({p.ScannedCount})";
            }
        });
        try
        {
            var dataKinds = new List<JunkKind>();
            if (ScanBrowserCache) dataKinds.Add(JunkKind.BrowserCache);
            if (ScanBrowserHistory) dataKinds.Add(JunkKind.BrowserHistory);
            if (ScanBrowserCookies) dataKinds.Add(JunkKind.BrowserCookies);

            // 未勾选任何浏览器数据类型 → 空目标列表（不扫浏览器）；否则按已选类型扫全部浏览器
            var browserTargets = dataKinds.Count == 0
                ? new List<JunkBrowserTarget>()
                : Enum.GetValues<JunkBrowser>()
                    .Select(b => new JunkBrowserTarget(b, dataKinds))
                    .ToList();

            var scope = new JunkScope(
                ScanSystemJunk ? null : new List<JunkKind>(),
                browserTargets);

            var scanned = 0;
            await foreach (var item in _services.Cleanup.ScanJunkAsync(scope, progress, _scanCts.Token))
            {
                GetOrCreateGroup(item).AddItem(item);
                scanned++;
            }

            _hasScanned = true;
            OnPropertyChanged(nameof(ScanButtonText));
            OnPropertyChanged(nameof(EmptyHintText));
            OnPropertyChanged(nameof(IsEmpty));
            StatusText = Groups.Count == 0
                ? "未发现可清理的垃圾"
                : $"已扫描 {scanned} 个文件，共 {Groups.Count} 个分类，可释放 {TotalText}";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"已取消，已加载 {AllTiles.Sum(t => t.Items.Count)} 个文件";
        }
        finally
        {
            IsScanning = false;
            RefreshAggregates();
            OnPropertyChanged(nameof(IsEmpty));
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    private async Task CleanSelectedAsync()
    {
        var items = AllTiles.Where(t => t.IsSelected)
            .SelectMany(t => t.Items)
            .Where(i => i.EstimatedSizeBytes > 0)
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        await RunCleanAsync(items, "正在清理");
    }

    /// <summary>FR-CLEAN-06 一键清理：取默认勾选（High 系统项 + 浏览器缓存），仍经 TRUST-02 预演。</summary>
    private async Task OneClickCleanAsync()
    {
        var items = AllTiles.Where(t => t.DefaultSelected)
            .SelectMany(t => t.Items)
            .Where(i => i.EstimatedSizeBytes > 0)
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        await RunCleanAsync(items, "一键清理");
    }

    private CleanupTileViewModel? _cleaningTile;

    private async Task RunCleanAsync(IReadOnlyList<JunkItem> items, string actionLabel)
    {
        if (IsCleaning)
        {
            return;
        }

        IsCleaning = true;
        var tileByItem = BuildTileMap();
        var killBrowsers = KillRunningBrowsers;
        try
        {
            OperationResult? result = null;
            while (result is null)
            {
                // KillProcess 动作排在删除动作之前，进度序号需减去这部分再映射到垃圾项
                var killCount = killBrowsers
                    ? items.Where(i => i.Browser is not null)
                        .Select(i => i.Browser!.Value).Distinct().Count()
                    : 0;
                var finished = false;
                var activeTile = (CleanupTileViewModel?)null;
                SetCleaningTile(tileByItem.GetValueOrDefault(items[0].ItemId));
                StatusText = $"{actionLabel}中…";

                var progress = new Progress<OperationProgress>(p =>
                {
                    if (finished)
                    {
                        return;
                    }

                    switch (p.Stage)
                    {
                        case OperationStage.Snapshot:
                            StatusText = $"{actionLabel}：正在生成回滚快照…";
                            break;
                        case OperationStage.Executing:
                            var index = p.CurrentActionIndex - killCount;
                            if (index < 0 || index >= items.Count)
                            {
                                break;
                            }

                            var tile = tileByItem.GetValueOrDefault(items[index].ItemId);
                            if (!ReferenceEquals(tile, activeTile))
                            {
                                activeTile = tile;
                                SetCleaningTile(tile);
                            }
                            StatusText = $"{actionLabel}中：{tile?.Name ?? "文件"}（{index + 1}/{items.Count}）";
                            break;
                        case OperationStage.RollingBack:
                            StatusText = "清理失败，正在回滚…";
                            break;
                        case OperationStage.Committing:
                        case OperationStage.Done:
                            SetCleaningTile(null);
                            break;
                    }
                });

                try
                {
                    result = await _services.Cleanup.EnqueueCleanAsync(
                        items, new CleanOptions(killBrowsers), progress, CancellationToken.None);
                    finished = true;
                }
                catch (BrowserRunningException ex)
                {
                    finished = true;
                    SetCleaningTile(null);
                    var running = string.Join("、", ex.RunningBrowsers);
                    var answer = System.Windows.MessageBox.Show(
                        $"{running} 正在运行，清理浏览器缓存前需要先结束浏览器进程。\n\n是否结束浏览器并继续清理？",
                        "需要关闭浏览器",
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Question);
                    if (answer != System.Windows.MessageBoxResult.Yes)
                    {
                        StatusText = "已取消：浏览器正在运行";
                        return;
                    }

                    // 用户同意后强制结束进程，并同步界面勾选
                    killBrowsers = true;
                    KillRunningBrowsers = true;
                    StatusText = $"{actionLabel}：正在结束浏览器进程…";
                }
            }

            SetCleaningTile(null);
            if (result.Success)
            {
                StatusText = $"{actionLabel}完成，正在统计剩余空间…";
                var freed = await ApplyRemainingAsync(items, tileByItem);
                StatusText = $"{actionLabel}完成，释放 {BytesFormatter.Format(freed)}";
            }
            else
            {
                StatusText = $"清理失败：{result.Error}（已回滚）";
            }
        }
        catch (Exception ex)
        {
            SetCleaningTile(null);
            StatusText = $"清理异常：{ex.Message}";
        }
        finally
        {
            SetCleaningTile(null);
            IsCleaning = false;
            RefreshAggregates();
        }
    }

    private Dictionary<Guid, CleanupTileViewModel> BuildTileMap()
    {
        var map = new Dictionary<Guid, CleanupTileViewModel>();
        foreach (var tile in AllTiles)
        {
            foreach (var item in tile.Items)
            {
                map[item.ItemId] = tile;
            }
        }
        return map;
    }

    /// <summary>清理完成后重新测量每个垃圾项的剩余体积，卡片继续显示「剩余多少」。</summary>
    private async Task<long> ApplyRemainingAsync(
        IReadOnlyList<JunkItem> items,
        IReadOnlyDictionary<Guid, CleanupTileViewModel> tileByItem)
    {
        var freed = 0L;
        var remaining = new Dictionary<Guid, long>(items.Count);
        foreach (var item in items)
        {
            var left = await _services.Cleanup.GetRemainingSizeAsync(item, CancellationToken.None);
            remaining[item.ItemId] = left;
            freed += Math.Max(0, item.EstimatedSizeBytes - left);
        }

        foreach (var tile in tileByItem.Values.Distinct())
        {
            tile.ApplyRemaining(remaining);
        }

        foreach (var group in Groups)
        {
            group.RefreshSizes();
        }

        RefreshAggregates();
        OnPropertyChanged(nameof(IsEmpty));
        return freed;
    }

    private void SetCleaningTile(CleanupTileViewModel? tile)
    {
        if (ReferenceEquals(_cleaningTile, tile))
        {
            return;
        }

        if (_cleaningTile is not null)
        {
            _cleaningTile.IsCleaning = false;
        }

        _cleaningTile = tile;
        if (tile is not null)
        {
            tile.IsCleaning = true;
        }
    }

    /// <summary>卡片勾选变化 → 刷新分组小计、全选态与顶部汇总。</summary>
    private void NotifySelectionChanged()
        => RefreshAggregates();

    private void RefreshAggregates()
    {
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(DefaultSelectedCount));
        OnPropertyChanged(nameof(CleanButtonText));
        CleanCommand.RaiseCanExecuteChanged();
        OneClickCleanCommand.RaiseCanExecuteChanged();
    }

    private CleanupGroupViewModel GetOrCreateGroup(JunkItem item)
    {
        var isAppCache = item.Browser is null && IsAppCacheKind(item.Kind);
        var key = item.Browser?.ToString()
            ?? (isAppCache ? AppCacheGroupKey : SystemGroupKey);
        if (_groupMap.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var meta = item.Browser is null
            ? (isAppCache ? AppCacheMeta : SystemMeta)
            : BrowserMeta(item.Browser.Value);

        var group = new CleanupGroupViewModel(key, meta.Title, meta.Icon, meta.Accent, NotifySelectionChanged);
        _groupMap[key] = group;
        Groups.Add(group);
        OnPropertyChanged(nameof(IsEmpty));
        return group;
    }

    private static readonly GroupMeta SystemMeta =
        new("系统垃圾", SymbolRegular.Broom24, Color.FromRgb(0x18, 0xC0, 0xFF));

    private static readonly GroupMeta AppCacheMeta =
        new("应用与缓存", SymbolRegular.Storage24, Color.FromRgb(0xF5, 0x9E, 0x0B));

    /// <summary>应用/系统缓存类（对标同类工具「应用程序 / 缓存文件」分组）。</summary>
    private static bool IsAppCacheKind(JunkKind kind) => kind switch
    {
        JunkKind.PackageCache or JunkKind.AppCache or JunkKind.AppLogs or JunkKind.OfficeCache
            or JunkKind.WpsBackup or JunkKind.NuGetCache or JunkKind.WindowsUpdateCache
            or JunkKind.DeliveryOptimization or JunkKind.FontCache or JunkKind.IECache
            or JunkKind.TerminalServicesCache => true,
        _ => false
    };

    private static GroupMeta BrowserMeta(JunkBrowser browser) => browser switch
    {
        JunkBrowser.Edge => new("Edge 浏览器", SymbolRegular.Globe24, Color.FromRgb(0x38, 0xBD, 0xF8)),
        JunkBrowser.Chrome => new("Chrome 浏览器", SymbolRegular.Globe24, Color.FromRgb(0xFB, 0xBF, 0x24)),
        JunkBrowser.Firefox => new("Firefox 浏览器", SymbolRegular.Globe24, Color.FromRgb(0xFB, 0x92, 0x3C)),
        JunkBrowser.Brave => new("Brave 浏览器", SymbolRegular.Globe24, Color.FromRgb(0xFB, 0x71, 0x85)),
        JunkBrowser.Opera => new("Opera 浏览器", SymbolRegular.Globe24, Color.FromRgb(0xF8, 0x71, 0x71)),
        _ => new("浏览器数据", SymbolRegular.Globe24, Color.FromRgb(0x5D, 0xD9, 0xFF))
    };

    /// <summary>垃圾类型 → 卡片名称（同一类型的多份文件聚合成一张卡片）。</summary>
    internal static string TitleFor(JunkKind kind) => kind switch
    {
        JunkKind.Temp => "用户临时文件",
        JunkKind.WindowsTemp => "Windows 临时文件",
        JunkKind.Prefetch => "预读取文件",
        JunkKind.ThumbnailCache => "缩略图与图标缓存",
        JunkKind.WindowsErrorReporting => "Windows 错误报告",
        JunkKind.MemoryDump => "内存转储文件",
        JunkKind.RecycleBin => "回收站",
        JunkKind.PackageCache => "安装包缓存",
        JunkKind.AppCache => "应用缓存",
        JunkKind.AppLogs => "应用日志",
        JunkKind.OfficeCache => "Office 缓存",
        JunkKind.WpsBackup => "WPS 备份与缓存",
        JunkKind.NuGetCache => "NuGet 包缓存",
        JunkKind.WindowsUpdateCache => "Windows 更新缓存",
        JunkKind.DeliveryOptimization => "传递优化缓存",
        JunkKind.FontCache => "字体缓存",
        JunkKind.IECache => "IE / 系统网络缓存",
        JunkKind.TerminalServicesCache => "远程桌面缓存",
        JunkKind.BrowserCache => "缓存",
        JunkKind.BrowserHistory => "历史记录",
        JunkKind.BrowserDownloads => "下载记录",
        JunkKind.BrowserCookies => "Cookie",
        JunkKind.BrowserFormData => "表单数据",
        JunkKind.BrowserPasswords => "已保存密码",
        _ => kind.ToString()
    };

    /// <summary>垃圾类型 → 卡片图标，便于分类墙快速识别。</summary>
    internal static SymbolRegular IconFor(JunkKind kind) => kind switch
    {
        JunkKind.Temp => SymbolRegular.Folder24,
        JunkKind.WindowsTemp => SymbolRegular.FolderOpen24,
        JunkKind.Prefetch => SymbolRegular.Rocket24,
        JunkKind.ThumbnailCache => SymbolRegular.Image24,
        JunkKind.WindowsErrorReporting => SymbolRegular.Warning24,
        JunkKind.MemoryDump => SymbolRegular.Document24,
        JunkKind.RecycleBin => SymbolRegular.Delete24,
        JunkKind.PackageCache => SymbolRegular.Folder24,
        JunkKind.AppCache => SymbolRegular.Storage24,
        JunkKind.AppLogs => SymbolRegular.Document24,
        JunkKind.OfficeCache => SymbolRegular.Document24,
        JunkKind.WpsBackup => SymbolRegular.Folder24,
        JunkKind.NuGetCache => SymbolRegular.Storage24,
        JunkKind.WindowsUpdateCache => SymbolRegular.Storage24,
        JunkKind.DeliveryOptimization => SymbolRegular.Storage24,
        JunkKind.FontCache => SymbolRegular.Document24,
        JunkKind.IECache => SymbolRegular.Globe24,
        JunkKind.TerminalServicesCache => SymbolRegular.Globe24,
        JunkKind.BrowserCache => SymbolRegular.Storage24,
        JunkKind.BrowserHistory => SymbolRegular.History24,
        JunkKind.BrowserDownloads => SymbolRegular.ArrowDownload24,
        JunkKind.BrowserCookies => SymbolRegular.LockClosed24,
        JunkKind.BrowserFormData => SymbolRegular.Document24,
        JunkKind.BrowserPasswords => SymbolRegular.LockClosed24,
        _ => SymbolRegular.Folder24
    };

    private sealed record GroupMeta(string Title, SymbolRegular Icon, Color Accent);
}

/// <summary>垃圾分类分组：标题 + 小计（共/已选）+ 全选 + 折叠 + 卡片墙。</summary>
public sealed class CleanupGroupViewModel : ViewModelBase
{
    private readonly Action _onSelectionChanged;
    private readonly Dictionary<string, CleanupTileViewModel> _tilesByKind = new();
    private bool _isExpanded = true;

    internal CleanupGroupViewModel(string key, string title, SymbolRegular icon, Color accent, Action onSelectionChanged)
    {
        Key = key;
        Title = title;
        Icon = icon;
        _onSelectionChanged = onSelectionChanged;
        AccentBrush = Freeze(new SolidColorBrush(accent));
        AccentSoftBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x3A, accent.R, accent.G, accent.B)));
    }

    public string Key { get; }
    public string Title { get; }
    public SymbolRegular Icon { get; }
    public Brush AccentBrush { get; }
    public Brush AccentSoftBrush { get; }

    public ObservableCollection<CleanupTileViewModel> Tiles { get; } = new();

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public long TotalBytes => Tiles.Sum(t => t.TotalBytes);
    public long SelectedBytes => Tiles.Where(t => t.IsSelected).Sum(t => t.TotalBytes);
    public int SelectedCount => Tiles.Where(t => t.IsSelected).Sum(t => t.Items.Count);

    /// <summary>分组小计文案：共 7.41 GB，已选 7.41 GB。</summary>
    public string SummaryText => $"共 {BytesFormatter.Format(TotalBytes)}，已选 {BytesFormatter.Format(SelectedBytes)}";

    /// <summary>分组内聚合后的文件总数（组头「N 项」）。</summary>
    public string ItemCountText => $"{Tiles.Sum(t => t.Items.Count)} 项";

    /// <summary>分组全选（部分勾选显示半选态）。</summary>
    public bool? IsAllSelected
    {
        get => SelectedCount == 0 ? false : SelectedCount == Tiles.Sum(t => t.Items.Count) ? true : null;
        set
        {
            if (value is null)
            {
                return;
            }

            foreach (var tile in Tiles)
            {
                tile.IsSelected = value.Value;
            }
        }
    }

    /// <summary>同一垃圾类型的多个文件聚合成一张卡片；新卡片按默认勾选提示初始化。</summary>
    internal void AddItem(JunkItem item)
    {
        var kindKey = item.Kind.ToString();
        if (_tilesByKind.TryGetValue(kindKey, out var existing))
        {
            existing.AddItem(item);
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(ItemCountText));
            OnPropertyChanged(nameof(IsAllSelected));
            return;
        }

        var tile = new CleanupTileViewModel(
            item.Kind,
            CleanupViewModel.TitleFor(item.Kind),
            AccentBrush,
            AccentSoftBrush,
            CleanupViewModel.IconFor(item.Kind),
            OnTileChanged);
        tile.AddItem(item);
        _tilesByKind[kindKey] = tile;
        Tiles.Add(tile);
        tile.IsSelected = item.DefaultSelected;

        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ItemCountText));
        OnPropertyChanged(nameof(IsAllSelected));
    }

    /// <summary>清理完成后卡片剩余体积变化 → 刷新分组小计与顶部汇总。</summary>
    internal void RefreshSizes() => OnTileChanged();

    private void OnTileChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(IsAllSelected));
        _onSelectionChanged();
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>垃圾卡片：图标 + 类型名 + 合计大小徽标 + 勾选态（同一类型的多个文件合并为一张卡片）。</summary>
public sealed class CleanupTileViewModel : ViewModelBase
{
    private readonly List<JunkItem> _items = new();
    private readonly Action _onSelectionChanged;
    private bool _isSelected;
    private bool _isCleaning;
    private bool _cleaned;

    internal CleanupTileViewModel(
        JunkKind kind,
        string name,
        Brush accentBrush,
        Brush accentSoftBrush,
        SymbolRegular icon,
        Action onSelectionChanged)
    {
        Kind = kind;
        Name = name;
        AccentBrush = accentBrush;
        AccentSoftBrush = accentSoftBrush;
        Icon = icon;
        _onSelectionChanged = onSelectionChanged;
    }

    public JunkKind Kind { get; }
    public string Name { get; }
    public SymbolRegular Icon { get; }
    public Brush AccentBrush { get; }
    public Brush AccentSoftBrush { get; }

    /// <summary>卡片聚合的全部垃圾项（清理时按项提交）。</summary>
    public IReadOnlyList<JunkItem> Items => _items;

    public long TotalBytes => _items.Sum(i => i.EstimatedSizeBytes);
    public string SizeText => BytesFormatter.Format(TotalBytes);

    /// <summary>清理中显示转圈提示；清理完成后全为空时显示「已清理」。</summary>
    public string MetaText => IsCleaning
        ? "清理中…"
        : _cleaned && TotalBytes == 0
            ? "已清理"
            : $"{_items.Count} 个文件";

    /// <summary>该卡片当前正在被清理（图标位显示 ProgressRing）。</summary>
    public bool IsCleaning
    {
        get => _isCleaning;
        internal set
        {
            if (Set(ref _isCleaning, value))
            {
                OnPropertyChanged(nameof(IsNotCleaning));
                OnPropertyChanged(nameof(MetaText));
            }
        }
    }

    public bool IsNotCleaning => !_isCleaning;

    /// <summary>默认勾选（FR-CLEAN-06 一键清理的取数口径）。</summary>
    public bool DefaultSelected => _items.Count > 0 && _items.All(i => i.DefaultSelected);

    public string TooltipText
    {
        get
        {
            var levels = string.Join("/", _items.Select(i => i.Verdict.Level).Distinct());
            var paths = string.Join("\n", _items.Take(3).Select(i => i.Target));
            var more = _items.Count > 3 ? $"\n… 共 {_items.Count} 个文件" : string.Empty;
            return $"{Name}（{_items.Count} 个文件）\n{paths}{more}\n安全等级：{levels}";
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value))
            {
                _onSelectionChanged();
            }
        }
    }

    internal void AddItem(JunkItem item)
    {
        _items.Add(item);
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(DefaultSelected));
        OnPropertyChanged(nameof(TooltipText));
        OnPropertyChanged(nameof(MetaText));
    }

    /// <summary>清理完成后用实测剩余体积替换各项大小，卡片继续显示剩余多少。</summary>
    internal void ApplyRemaining(IReadOnlyDictionary<Guid, long> remaining)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            if (remaining.TryGetValue(item.ItemId, out var left) && left != item.EstimatedSizeBytes)
            {
                _items[i] = item with { EstimatedSizeBytes = left };
            }
        }

        _cleaned = true;
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(DefaultSelected));
        OnPropertyChanged(nameof(TooltipText));
        OnPropertyChanged(nameof(MetaText));

        // 已无剩余的文件不再参与「已选」，避免重复提交空项
        if (TotalBytes == 0 && IsSelected)
        {
            IsSelected = false;
        }
    }
}
