using System.Collections.ObjectModel;
using SysMgr.Core.Rollback;

namespace SysMgr.App.ViewModels;

/// <summary>
/// 历史与回滚页 ViewModel。枚举 IRollbackEngine 历史，提供一键回滚。
/// </summary>
public sealed class HistoryViewModel : ViewModelBase
{
    private readonly SysMgrServices _services;
    private bool _isBusy;
    private string _statusText = string.Empty;
    private RollbackEntry? _selected;

    public HistoryViewModel(SysMgrServices services)
    {
        _services = services;
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !IsBusy);
        RollbackCommand = new AsyncRelayCommand(_ => RollbackAsync(), _ => !IsBusy && Selected is not null);
    }

    public ObservableCollection<RollbackEntry> Entries { get; } = new();

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                RollbackCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public RollbackEntry? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                RollbackCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RollbackCommand { get; }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        Entries.Clear();
        try
        {
            await foreach (var entry in _services.Rollback.EnumerateHistoryAsync(CancellationToken.None))
            {
                Entries.Add(entry);
            }
            StatusText = $"共 {Entries.Count} 条历史记录";
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RollbackAsync()
    {
        if (Selected is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = "正在回滚…";
            var result = await _services.Rollback.RollbackAsync(Selected.OpId, CancellationToken.None);
            StatusText = result.Success
                ? "回滚成功"
                : $"回滚失败：{string.Join("；", result.Conflicts)}";
        }
        catch (Exception ex)
        {
            StatusText = $"回滚异常：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
