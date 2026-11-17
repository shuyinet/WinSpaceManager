using System.Collections.ObjectModel;
using SysMgr.Modules.Shred;
using SysMgr.Modules.Shred.Errors;

namespace SysMgr.App.ViewModels;

/// <summary>
/// 文件粉碎页 ViewModel（FR-SHRED-01/02）。
/// 添加文件/文件夹（含拖拽）→ AnalyzeAsync 展示递归统计 → 选档位 → 粉碎
/// （服务层经 TRUST-02 预演 + OpQueue，粉碎不可回滚，页面文案明确警示）。
/// </summary>
public sealed class ShredViewModel : ViewModelBase
{
    private readonly SysMgrServices _services;
    private bool _isBusy;
    private string _statusText = "添加或拖入要粉碎的文件/文件夹";
    private ShredTargetRow? _selectedItem;
    private int _selectedPasses = 1;

    public ShredViewModel(SysMgrServices services)
    {
        _services = services;
        RemoveCommand = new RelayCommand(_ => RemoveSelected(), _ => !IsBusy && SelectedItem is not null);
        ClearCommand = new RelayCommand(_ => ClearAll(), _ => !IsBusy && Targets.Count > 0);
        ShredCommand = new AsyncRelayCommand(_ => ShredAsync(), _ => !IsBusy && Targets.Count > 0);
    }

    public ObservableCollection<ShredTargetRow> Targets { get; } = new();

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                RemoveCommand.RaiseCanExecuteChanged();
                ClearCommand.RaiseCanExecuteChanged();
                ShredCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public ShredTargetRow? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (Set(ref _selectedItem, value))
            {
                RemoveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>覆写遍数（1/3/7，绑定三档单选）。</summary>
    public int SelectedPasses
    {
        get => _selectedPasses;
        set
        {
            if (Set(ref _selectedPasses, value))
            {
                OnPropertyChanged(nameof(ModeQuick));
                OnPropertyChanged(nameof(ModeSecure3));
                OnPropertyChanged(nameof(ModeSecure7));
            }
        }
    }

    public bool ModeQuick { get => SelectedPasses == 1; set { if (value) SelectedPasses = 1; } }
    public bool ModeSecure3 { get => SelectedPasses == 3; set { if (value) SelectedPasses = 3; } }
    public bool ModeSecure7 { get => SelectedPasses == 7; set { if (value) SelectedPasses = 7; } }

    public RelayCommand RemoveCommand { get; }
    public RelayCommand ClearCommand { get; }
    public AsyncRelayCommand ShredCommand { get; }

    /// <summary>添加路径并立即分析（文件选择器/文件夹选择器/拖拽共用入口）。</summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        IsBusy = true;
        try
        {
            foreach (var path in paths)
            {
                if (Targets.Any(t => t.Target.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue; // 已在列表中，跳过
                }

                var analyzed = await _services.Shred.AnalyzeAsync(new[] { path }, CancellationToken.None);
                foreach (var target in analyzed)
                {
                    Targets.Add(new ShredTargetRow(target));
                }
            }
            RefreshStatus();
        }
        catch (ShredException ex)
        {
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            StatusText = $"添加失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RemoveSelected()
    {
        if (SelectedItem is not null)
        {
            Targets.Remove(SelectedItem);
            RefreshStatus();
        }
    }

    private void ClearAll()
    {
        Targets.Clear();
        RefreshStatus();
    }

    private async Task ShredAsync()
    {
        var mode = SelectedPasses switch
        {
            3 => ShredMode.Secure3,
            7 => ShredMode.Secure7,
            _ => ShredMode.Quick
        };
        var paths = Targets.Select(t => t.Target.Path).ToList();

        IsBusy = true;
        StatusText = "正在粉碎…";
        try
        {
            var result = await _services.Shred.ShredAsync(paths, mode, null, CancellationToken.None);
            if (result.Success)
            {
                var count = Targets.Sum(t => t.Target.FileCount);
                Targets.Clear();
                StatusText = $"已粉碎 {count} 个文件，数据不可恢复";
            }
            else
            {
                StatusText = $"未完成：{result.Error}";
            }
        }
        catch (ShredException ex)
        {
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            StatusText = $"粉碎异常：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            ClearCommand.RaiseCanExecuteChanged();
            ShredCommand.RaiseCanExecuteChanged();
        }
    }

    private void RefreshStatus()
    {
        if (Targets.Count == 0)
        {
            StatusText = "添加或拖入要粉碎的文件/文件夹";
            return;
        }
        var files = Targets.Sum(t => t.Target.FileCount);
        var bytes = Targets.Sum(t => t.Target.TotalBytes);
        StatusText = $"待粉碎 {files} 个文件 · 共 {Converters.BytesFormatter.Format(bytes)}";
        ClearCommand.RaiseCanExecuteChanged();
        ShredCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>粉碎目标行。</summary>
public sealed class ShredTargetRow(ShredTarget target)
{
    public ShredTarget Target { get; } = target;

    public string KindText => Target.IsDirectory ? "文件夹" : "文件";
    public string SizeText => Converters.BytesFormatter.Format(Target.TotalBytes);
    public string CountText => Target.IsDirectory ? $"{Target.FileCount} 个文件" : "—";
}
