using System.Collections.ObjectModel;
using SysMgr.Core.Queue;
using SysMgr.Modules;
using SysMgr.Modules.CSlim;

namespace SysMgr.App.ViewModels;

/// <summary>
/// C 盘瘦身页 ViewModel（FR-CSLIM-01/02/05/08）。
/// 分析 → 大文件扫描/迁移 → Windows 残留检测/清除 → 保存位置建议。
/// </summary>
public sealed class CSlimViewModel : ViewModelBase
{
    private readonly SysMgrServices _services;
    private CancellationTokenSource? _scanCts;
    private bool _isScanning;
    private string _statusText = "点击「分析 C 盘」查看空间分布";
    private CDriveAnalysis? _analysis;
    private long _minSizeMb = 100;
    private string _destinationRoot = string.Empty;

    public CSlimViewModel(SysMgrServices services)
    {
        _services = services;
        AnalyzeCommand = new AsyncRelayCommand(_ => AnalyzeAsync(), _ => !IsScanning);
        ScanLargeFilesCommand = new AsyncRelayCommand(_ => ScanLargeAsync(), _ => !IsScanning);
        MigrateCommand = new AsyncRelayCommand(_ => MigrateAsync(), _ => !IsScanning && LargeFiles.Any(f => f.IsSelected) && !string.IsNullOrWhiteSpace(DestinationRoot));
        CleanWindowsResidualCommand = new AsyncRelayCommand(_ => CleanWindowsResidualAsync(), _ => !IsScanning && WindowsResiduals.Any(w => w.IsSelected));
        LoadSaveLocationCommand = new AsyncRelayCommand(_ => LoadSaveLocationsAsync(), _ => !IsScanning);
        ApplySaveLocationCommand = new AsyncRelayCommand(_ => ApplySaveLocationsAsync(), _ => !IsScanning && SaveLocations.Any(s => s.IsSelected && s.Suggestion.CanApply));
        CancelCommand = new RelayCommand(_ => _scanCts?.Cancel(), _ => IsScanning);
    }

    public ObservableCollection<CDriveCategorySize> AnalysisCategories { get; } = new();
    public ObservableCollection<SelectableLargeFile> LargeFiles { get; } = new();
    public ObservableCollection<SelectableWindowsResidual> WindowsResiduals { get; } = new();
    public ObservableCollection<SelectableSaveLocation> SaveLocations { get; } = new();

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (Set(ref _isScanning, value))
            {
                AnalyzeCommand.RaiseCanExecuteChanged();
                ScanLargeFilesCommand.RaiseCanExecuteChanged();
                MigrateCommand.RaiseCanExecuteChanged();
                CleanWindowsResidualCommand.RaiseCanExecuteChanged();
                LoadSaveLocationCommand.RaiseCanExecuteChanged();
                ApplySaveLocationCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public CDriveAnalysis? Analysis
    {
        get => _analysis;
        private set
        {
            if (Set(ref _analysis, value))
            {
                OnPropertyChanged(nameof(UsedPercent));
                OnPropertyChanged(nameof(FreePercent));
            }
        }
    }

    public long MinSizeMb { get => _minSizeMb; set => Set(ref _minSizeMb, value); }
    public string DestinationRoot { get => _destinationRoot; set => Set(ref _destinationRoot, value); }

    public double UsedPercent
        => Analysis is null || Analysis.TotalBytes <= 0 ? 0
            : (Analysis.TotalBytes - Analysis.FreeBytes) * 100.0 / Analysis.TotalBytes;

    public double FreePercent
        => Analysis is null || Analysis.TotalBytes <= 0 ? 0
            : Analysis.FreeBytes * 100.0 / Analysis.TotalBytes;

    public AsyncRelayCommand AnalyzeCommand { get; }
    public AsyncRelayCommand ScanLargeFilesCommand { get; }
    public AsyncRelayCommand MigrateCommand { get; }
    public AsyncRelayCommand CleanWindowsResidualCommand { get; }
    public AsyncRelayCommand LoadSaveLocationCommand { get; }
    public AsyncRelayCommand ApplySaveLocationCommand { get; }
    public RelayCommand CancelCommand { get; }

    private async Task AnalyzeAsync()
    {
        IsScanning = true;
        AnalysisCategories.Clear();
        _scanCts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(p =>
            StatusText = $"分析中… {p.Phase} ({p.ScannedCount})");
        try
        {
            var result = await _services.CSlim.AnalyzeCDriveAsync(progress, _scanCts.Token);
            Analysis = result;
            AnalysisCategories.Clear();
            foreach (var cat in result.Categories)
            {
                AnalysisCategories.Add(cat);
            }
            StatusText = $"{result.DriveRoot} 总计 {Converters.BytesFormatter.Format(result.TotalBytes)}，可用 {Converters.BytesFormatter.Format(result.FreeBytes)}";

            // FR-CSLIM-05：进入页面时自动检测 Windows 残留
            DetectWindowsResiduals();
        }
        catch (OperationCanceledException)
        {
            StatusText = "分析已取消";
        }
        finally
        {
            IsScanning = false;
            _scanCts.Dispose();
            _scanCts = null;
        }
    }

    private async Task ScanLargeAsync()
    {
        IsScanning = true;
        LargeFiles.Clear();
        _scanCts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(p =>
            StatusText = $"扫描大文件中… 已发现 {p.ScannedCount} 项");
        try
        {
            var min = MinSizeMb * 1024 * 1024;
            await foreach (var item in _services.CSlim.FindLargeFilesAsync(min, progress, _scanCts.Token))
            {
                LargeFiles.Add(new SelectableLargeFile(item, true));
            }
            StatusText = $"大文件共 {LargeFiles.Count} 项";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"已取消，已加载 {LargeFiles.Count} 项";
        }
        finally
        {
            IsScanning = false;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    private async Task MigrateAsync()
    {
        var items = LargeFiles.Where(f => f.IsSelected).Select(f => f.Item).ToList();
        if (items.Count == 0 || string.IsNullOrWhiteSpace(DestinationRoot))
        {
            return;
        }

        StatusText = $"正在迁移 {items.Count} 个大文件…";
        var progress = new Progress<OperationProgress>(p =>
            StatusText = $"迁移中：{p.Stage} ({p.CurrentActionIndex}/{p.TotalActions})");
        try
        {
            var result = await _services.CSlim.EnqueueLargeFileMigrationAsync(
                items, DestinationRoot.Trim(), progress, CancellationToken.None);
            StatusText = result.Success ? "迁移完成" : $"迁移失败：{result.Error}（已回滚）";
            if (result.Success)
            {
                foreach (var vm in LargeFiles.Where(f => f.IsSelected).ToList())
                {
                    LargeFiles.Remove(vm);
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"迁移异常：{ex.Message}";
        }
    }

    private void DetectWindowsResiduals()
    {
        WindowsResiduals.Clear();
        foreach (var item in _services.CSlim.DetectWindowsResiduals())
        {
            WindowsResiduals.Add(new SelectableWindowsResidual(item, false));
        }
    }

    private async Task CleanWindowsResidualAsync()
    {
        var items = WindowsResiduals.Where(w => w.IsSelected).Select(w => w.Item).ToList();
        if (items.Count == 0)
        {
            return;
        }

        StatusText = "正在清除 Windows 残留…";
        var progress = new Progress<OperationProgress>(p =>
            StatusText = $"清除中：{p.Stage} ({p.CurrentActionIndex}/{p.TotalActions})");
        try
        {
            var result = await _services.CSlim.EnqueueWindowsResidualCleanAsync(
                items, progress, CancellationToken.None);
            StatusText = result.Success ? "清除完成" : $"清除失败：{result.Error}（已回滚）";
            if (result.Success)
            {
                foreach (var vm in WindowsResiduals.Where(w => w.IsSelected).ToList())
                {
                    WindowsResiduals.Remove(vm);
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"清除异常：{ex.Message}";
        }
    }

    private async Task LoadSaveLocationsAsync()
    {
        IsScanning = true;
        SaveLocations.Clear();
        try
        {
            var list = await _services.CSlim.GetSaveLocationSuggestionsAsync(CancellationToken.None);
            foreach (var s in list)
            {
                SaveLocations.Add(new SelectableSaveLocation(s, s.CanApply));
            }
            StatusText = $"已加载 {SaveLocations.Count} 条保存位置建议";
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败：{ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task ApplySaveLocationsAsync()
    {
        var selected = SaveLocations.Where(s => s.IsSelected && s.Suggestion.CanApply).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var targetDrive = selected[0].Suggestion.SuggestedDrive;
        var cats = selected.Select(s => s.Suggestion.Category).ToList();
        StatusText = "正在应用保存位置…";
        var progress = new Progress<OperationProgress>(p =>
            StatusText = $"应用中：{p.Stage} ({p.CurrentActionIndex}/{p.TotalActions})");
        try
        {
            var result = await _services.CSlim.ApplySaveLocationSuggestionsAsync(
                cats, targetDrive, progress, CancellationToken.None);
            StatusText = result.Success ? "保存位置已更新" : $"部分失败：{result.Error}";
        }
        catch (Exception ex)
        {
            StatusText = $"应用异常：{ex.Message}";
        }
    }
}

public sealed class SelectableLargeFile : ViewModelBase
{
    private bool _isSelected;
    public SelectableLargeFile(LargeFileItem item, bool isSelected) { Item = item; _isSelected = isSelected; }
    public LargeFileItem Item { get; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed class SelectableWindowsResidual : ViewModelBase
{
    private bool _isSelected;
    public SelectableWindowsResidual(WindowsResidualItem item, bool isSelected) { Item = item; _isSelected = isSelected; }
    public WindowsResidualItem Item { get; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed class SelectableSaveLocation : ViewModelBase
{
    private bool _isSelected;
    public SelectableSaveLocation(SaveLocationSuggestion suggestion, bool isSelected) { Suggestion = suggestion; _isSelected = isSelected; }
    public SaveLocationSuggestion Suggestion { get; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}
