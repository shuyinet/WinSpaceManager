using System.Collections.ObjectModel;
using System.Linq;
using SysMgr.Core.Queue;
using SysMgr.Core.Safety;
using SysMgr.Modules;
using SysMgr.Modules.Residual;

namespace SysMgr.App.ViewModels;

/// <summary>
/// 残留清除页 ViewModel（FR-RESIDUAL-01/02）。
/// 用户输入软件关键词 → 五维度残留扫描 → 按安全等级默认勾选 → 勾选项入队清除（TRUST-02 预演）。
/// </summary>
public sealed class ResidualViewModel : ViewModelBase
{
    private readonly SysMgrServices _services;
    private CancellationTokenSource? _scanCts;
    private bool _isScanning;
    private string _statusText = "输入软件名称后点「扫描残留」";
    private string _softwareName = string.Empty;

    public ResidualViewModel(SysMgrServices services)
    {
        _services = services;
        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsScanning && !string.IsNullOrWhiteSpace(SoftwareName));
        CancelCommand = new RelayCommand(_ => _scanCts?.Cancel(), _ => IsScanning);
        CleanCommand = new AsyncRelayCommand(_ => CleanAsync(), _ => !IsScanning && Items.Any(i => i.IsSelected));
    }

    public ObservableCollection<SelectableResidualItem> Items { get; } = new();

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (Set(ref _isScanning, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
                CleanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public string SoftwareName
    {
        get => _softwareName;
        set
        {
            if (Set(ref _softwareName, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand CleanCommand { get; }

    /// <summary>供卸载页联动：直接带目标触发扫描。</summary>
    public async Task ScanForAsync(ResidualScanTarget target)
    {
        SoftwareName = target.DisplayName;
        await ScanCoreAsync(target);
    }

    private Task ScanAsync()
        => ScanCoreAsync(new ResidualScanTarget(Guid.NewGuid(), SoftwareName.Trim(), null, null));

    private async Task ScanCoreAsync(ResidualScanTarget target)
    {
        IsScanning = true;
        Items.Clear();
        _scanCts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(p =>
            StatusText = $"扫描中… 已发现 {p.ScannedCount} 项 {p.CurrentPath ?? string.Empty}");
        try
        {
            var options = new ResidualScanOptions(null, null);
            await foreach (var item in _services.Residual.ScanResidualAsync(target, options, progress, _scanCts.Token))
            {
                Items.Add(new SelectableResidualItem(item, item.Verdict.Level == SafetyLevel.High));
            }
            var totalBytes = Items.Sum(i => i.Item.EstimatedSizeBytes ?? 0);
            StatusText = $"共 {Items.Count} 项，预计释放 {Converters.BytesFormatter.Format(totalBytes)}";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"已取消，已加载 {Items.Count} 项";
        }
        finally
        {
            IsScanning = false;
            _scanCts.Dispose();
            _scanCts = null;
        }
    }

    private async Task CleanAsync()
    {
        var selected = Items.Where(i => i.IsSelected).Select(i => i.Item).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        StatusText = $"正在清除 {selected.Count} 项残留…";
        var progress = new Progress<OperationProgress>(p =>
            StatusText = $"清除中：{p.Stage} ({p.CurrentActionIndex}/{p.TotalActions})");
        try
        {
            var result = await _services.Residual.EnqueueCleanupAsync(
                selected, new ResidualCleanupOptions(false), progress, CancellationToken.None);
            StatusText = result.Success ? $"清除完成：{selected.Count} 项" : $"清除失败：{result.Error}（已回滚）";
            if (result.Success)
            {
                foreach (var vm in Items.Where(i => i.IsSelected).ToList())
                {
                    Items.Remove(vm);
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"清除异常：{ex.Message}";
        }
    }
}

/// <summary>带勾选状态的残留项包装（UI 绑定用）。</summary>
public sealed class SelectableResidualItem : ViewModelBase
{
    private bool _isSelected;

    public SelectableResidualItem(ResidualItem item, bool isSelected)
    {
        Item = item;
        _isSelected = isSelected;
    }

    public ResidualItem Item { get; }

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}
