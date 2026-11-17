using System.Collections.ObjectModel;
using System.Windows.Media;
using SysMgr.Modules;
using SysMgr.Modules.Cleanup;

namespace SysMgr.App.ViewModels;

/// <summary>
/// 仪表盘首页 ViewModel。聚合扫描：C 盘分析 + 垃圾统计 + 软件清单 → 健康分与状态卡片。
/// </summary>
public sealed class DashboardViewModel : ViewModelBase
{
    private readonly SysMgrServices _services;
    private bool _isScanning;
    private int _healthScore;
    private string _statusLine = "尚未体检 · 点击「一键体检」全面扫描系统状态";
    private string _rating = "未体检";
    private int _riskCount;
    private long _recoverableBytes;
    private int _softwareCount;
    private long _cDriveFreeBytes;
    private double _scanPercent;
    private string _scanPhase = string.Empty;
    private bool _hasResult;

    public DashboardViewModel(SysMgrServices services)
    {
        _services = services;
        QuickScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsScanning);
        NavigateCommand = new RelayCommand(p => Navigate((QuickEntry)p!));
    }

    /// <summary>请求导航到目标页面（页面层订阅）。</summary>
    public event Action<Type>? NavigateRequested;

    public ObservableCollection<QuickEntry> Entries { get; } = new()
    {
        new("软件卸载", "卸载不需要的应用", Wpf.Ui.Controls.SymbolRegular.AppsList24, typeof(Views.UninstallPage)),
        new("C盘清理", "清理系统盘垃圾与缓存", Wpf.Ui.Controls.SymbolRegular.Broom24, typeof(Views.CleanupPage)),
        new("大文件搬家", "大文件安全迁移", Wpf.Ui.Controls.SymbolRegular.ArrowMove24, typeof(Views.MigrationPage)),
        new("磁盘分析", "可视化空间占用", Wpf.Ui.Controls.SymbolRegular.DataUsage24, typeof(Views.VisualizationPage)),
        new("启动项管理", "管理开机自启动", Wpf.Ui.Controls.SymbolRegular.Rocket24, typeof(Views.StartupPage)),
        new("文件粉碎", "多遍覆写彻底删除", Wpf.Ui.Controls.SymbolRegular.DocumentDismiss24, typeof(Views.ShredPage))
    };

    public bool IsScanning { get => _isScanning; private set { if (Set(ref _isScanning, value)) { QuickScanCommand.RaiseCanExecuteChanged(); OnPropertyChanged(nameof(CenterDisplay)); } } }

    public int HealthScore { get => _healthScore; private set { if (Set(ref _healthScore, value)) { OnPropertyChanged(nameof(ScoreBrush)); OnPropertyChanged(nameof(ScoreGlow)); OnPropertyChanged(nameof(CenterDisplay)); } } }

    public string StatusLine { get => _statusLine; private set => Set(ref _statusLine, value); }
    public string Rating { get => _rating; private set => Set(ref _rating, value); }
    public int RiskCount { get => _riskCount; private set { if (Set(ref _riskCount, value)) OnPropertyChanged(nameof(RiskDisplay)); } }
    public long RecoverableBytes { get => _recoverableBytes; private set { if (Set(ref _recoverableBytes, value)) OnPropertyChanged(nameof(RecoverableDisplay)); } }
    public int SoftwareCount { get => _softwareCount; private set { if (Set(ref _softwareCount, value)) OnPropertyChanged(nameof(SoftwareDisplay)); } }
    public long CDriveFreeBytes { get => _cDriveFreeBytes; private set { if (Set(ref _cDriveFreeBytes, value)) OnPropertyChanged(nameof(CDriveFreeDisplay)); } }
    public double ScanPercent { get => _scanPercent; private set { if (Set(ref _scanPercent, value)) OnPropertyChanged(nameof(CenterDisplay)); } }
    public string ScanPhase { get => _scanPhase; private set => Set(ref _scanPhase, value); }
    public bool HasResult
    {
        get => _hasResult;
        private set
        {
            if (Set(ref _hasResult, value))
            {
                OnPropertyChanged(nameof(RiskDisplay));
                OnPropertyChanged(nameof(RecoverableDisplay));
                OnPropertyChanged(nameof(SoftwareDisplay));
                OnPropertyChanged(nameof(CDriveFreeDisplay));
            }
        }
    }

    public AsyncRelayCommand QuickScanCommand { get; }
    public RelayCommand NavigateCommand { get; }

    /// <summary>圆环中心：扫描中显示进度百分比，否则显示健康分。</summary>
    public string CenterDisplay => IsScanning ? $"{Math.Round(ScanPercent)}" : HealthScore.ToString();

    /// <summary>状态卡数值：未体检显示占位符。</summary>
    public string RiskDisplay => HasResult ? RiskCount.ToString() : "—";
    public string RecoverableDisplay => HasResult ? Converters.BytesFormatter.Format(RecoverableBytes) : "—";
    public string SoftwareDisplay => HasResult ? SoftwareCount.ToString() : "—";
    public string CDriveFreeDisplay => HasResult ? Converters.BytesFormatter.Format(CDriveFreeBytes) : "—";

    /// <summary>健康分对应画刷（评级配色）。</summary>
    public Brush ScoreBrush => new SolidColorBrush(_healthScore switch
    {
        >= 85 => Color.FromRgb(0x23, 0xE3, 0xA0),
        >= 70 => Color.FromRgb(0x18, 0xC0, 0xFF),
        >= 55 => Color.FromRgb(0xFF, 0xB2, 0x3E),
        _ => Color.FromRgb(0xFF, 0x4D, 0x62)
    });

    public Color ScoreGlow => ((SolidColorBrush)ScoreBrush).Color;

    public void Navigate(QuickEntry entry) => NavigateRequested?.Invoke(entry.TargetPage);

    private async Task ScanAsync()
    {
        IsScanning = true;
        Rating = "正在体检";
        ScanPercent = 0;

        var analysisTask = Task.Run(async () =>
        {
            ScanPhase = "正在分析 C 盘空间分布…";
            var p = new Progress<ScanProgress>(_ => ScanPercent = Math.Max(ScanPercent, 30));
            return await _services.CSlim.AnalyzeCDriveAsync(p, CancellationToken.None);
        });

        var junkTask = Task.Run(async () =>
        {
            ScanPhase = "正在扫描系统垃圾与浏览痕迹…";
            var p = new Progress<ScanProgress>(_ => ScanPercent = Math.Max(ScanPercent, 55));
            var count = 0;
            var bytes = 0L;
            await foreach (var item in _services.Cleanup.ScanJunkAsync(
                new JunkScope(null, null), p, CancellationToken.None))
            {
                count++;
                bytes += item.EstimatedSizeBytes;
            }
            return (Count: count, Bytes: bytes);
        });

        var softwareTask = Task.Run(async () =>
        {
            ScanPhase = "正在枚举已安装软件…";
            var p = new Progress<ScanProgress>(_ => ScanPercent = Math.Max(ScanPercent, 80));
            var count = 0;
            await foreach (var _ in _services.Uninstall.ScanInstalledAsync(p, CancellationToken.None))
            {
                count++;
            }
            return count;
        });

        try
        {
            await Task.WhenAll(analysisTask, junkTask, softwareTask);

            var analysis = analysisTask.Result;
            var junk = junkTask.Result;
            var software = softwareTask.Result;

            CDriveFreeBytes = analysis.FreeBytes;
            RiskCount = junk.Count;
            RecoverableBytes = junk.Bytes;
            SoftwareCount = software;

            // 健康分算法（V1 启发式）：垃圾项 + C 盘压力
            var score = 100;
            score -= Math.Min(30, junk.Count * 2);
            var usedRatio = analysis.TotalBytes <= 0 ? 0
                : 1 - (double)analysis.FreeBytes / analysis.TotalBytes;
            if (usedRatio > 0.92) score -= 20;
            else if (usedRatio > 0.85) score -= 10;
            score = Math.Max(35, score);

            HealthScore = score;
            Rating = score switch
            {
                >= 85 => "状态优秀",
                >= 70 => "状态良好",
                >= 55 => "需要注意",
                _ => "风险较高"
            };
            StatusLine = $"{junk.Count} 项待处理 · 可释放 {Converters.BytesFormatter.Format(junk.Bytes)} · C 盘剩余 {Converters.BytesFormatter.Format(analysis.FreeBytes)}";
            ScanPercent = 100;
            HasResult = true;
        }
        catch (Exception ex)
        {
            StatusLine = $"体检失败：{ex.Message}";
        }
        finally
        {
            IsScanning = false;
            ScanPhase = string.Empty;
        }
    }
}

/// <summary>仪表盘快捷入口。</summary>
public sealed record QuickEntry(
    string Title,
    string Subtitle,
    Wpf.Ui.Controls.SymbolRegular Icon,
    Type TargetPage);
