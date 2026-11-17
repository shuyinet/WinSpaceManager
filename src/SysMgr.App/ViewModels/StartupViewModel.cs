using System.Collections.ObjectModel;
using SysMgr.Modules.Startup;
using SysMgr.Modules.Startup.Errors;

namespace SysMgr.App.ViewModels;

/// <summary>
/// 启动项管理页 ViewModel（FR-STARTUP-01）。
/// 扫描六类来源 → 列表展示 → 禁用/启用/删除/编辑命令行（TRUST-02 预演）。
/// </summary>
public sealed class StartupViewModel : ViewModelBase
{
    private readonly SysMgrServices _services;
    private bool _isBusy;
    private string _statusText = "点击「扫描启动项」加载全部来源";
    private StartupItemRow? _selectedItem;

    public StartupViewModel(SysMgrServices services)
    {
        _services = services;
        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsBusy);
        DeleteCommand = new AsyncRelayCommand(_ => DeleteAsync(), _ => !IsBusy && SelectedItem?.Item.CanDelete == true);
        EditCommand = new AsyncRelayCommand(_ => EditCommandAsync(), _ => !IsBusy && SelectedItem?.Item.CanEdit == true);
    }

    public ObservableCollection<StartupItemRow> Items { get; } = new();

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                DeleteCommand.RaiseCanExecuteChanged();
                EditCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public StartupItemRow? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (Set(ref _selectedItem, value))
            {
                DeleteCommand.RaiseCanExecuteChanged();
                EditCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand EditCommand { get; }

    private async Task ScanAsync()
    {
        IsBusy = true;
        Items.Clear();
        StatusText = "正在扫描启动项…";
        try
        {
            var items = await _services.Startup.ScanAsync(CancellationToken.None);
            foreach (var item in items)
            {
                Items.Add(new StartupItemRow(item));
            }
            var disabled = Items.Count(i => !i.Item.IsEnabled);
            StatusText = $"共 {Items.Count} 项，已禁用 {disabled} 项";
        }
        catch (Exception ex)
        {
            StatusText = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>行内 ToggleSwitch 触发：禁用/启用（经服务层预演 + 回滚）。</summary>
    public async Task ToggleAsync(StartupItemRow row, bool enabled)
    {
        // 幂等：失败还原开关时会再触发本方法，此时开关值已等于当前状态，直接忽略（防事件回环）
        if (row.Item.IsEnabled == enabled)
        {
            return;
        }

        IsBusy = true;
        StatusText = $"正在{(enabled ? "启用" : "禁用")} {row.Item.Name}…";
        try
        {
            var result = await _services.Startup.SetEnabledAsync(
                row.Item, enabled, null, CancellationToken.None);
            if (result.Success)
            {
                row.RefreshEnabled(enabled);
                StatusText = $"已{(enabled ? "启用" : "禁用")} {row.Item.Name}";
            }
            else
            {
                row.RefreshEnabled(!enabled); // 失败/取消 → 还原开关
                StatusText = $"未生效：{result.Error}";
            }
        }
        catch (Exception ex)
        {
            row.RefreshEnabled(!enabled);
            StatusText = $"操作异常：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        var row = SelectedItem;
        IsBusy = true;
        StatusText = $"正在删除 {row.Item.Name}…";
        try
        {
            var result = await _services.Startup.DeleteAsync(row.Item, null, CancellationToken.None);
            if (result.Success)
            {
                Items.Remove(row);
                StatusText = $"已删除 {row.Item.Name}";
            }
            else
            {
                StatusText = $"删除失败：{result.Error}（已回滚）";
            }
        }
        catch (StartupException ex)
        {
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            StatusText = $"删除异常：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task EditCommandAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        var row = SelectedItem;
        var input = Microsoft.VisualBasic.Interaction.InputBox(
            $"编辑启动项命令行：\n{row.Item.Name}", "编辑命令行", row.Item.Command ?? string.Empty);
        if (string.IsNullOrWhiteSpace(input))
        {
            return; // 用户取消或留空
        }

        IsBusy = true;
        StatusText = $"正在更新 {row.Item.Name} 的命令行…";
        try
        {
            var result = await _services.Startup.EditCommandAsync(
                row.Item, input, null, CancellationToken.None);
            if (result.Success)
            {
                row.RefreshCommand(input.Trim());
                StatusText = $"已更新 {row.Item.Name} 的命令行";
            }
            else
            {
                StatusText = $"编辑失败：{result.Error}（已回滚）";
            }
        }
        catch (StartupException ex)
        {
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            StatusText = $"编辑异常：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>启动项行：开关状态就地刷新，避免整表重扫。</summary>
public sealed class StartupItemRow : ViewModelBase
{
    private bool _isEnabled;
    private string? _command;

    public StartupItemRow(StartupItem item)
    {
        Item = item;
        _isEnabled = item.IsEnabled;
        _command = item.Command;
    }

    public StartupItem Item { get; private set; }

    public bool IsEnabled { get => _isEnabled; private set => Set(ref _isEnabled, value); }
    public string? Command { get => _command; private set => Set(ref _command, value); }

    public string KindText => Item.Kind switch
    {
        StartupItemKind.RegistryRun => "注册表 Run",
        StartupItemKind.RegistryRun32 => "注册表 Run (32 位)",
        StartupItemKind.StartupFolderUser => "启动文件夹（用户）",
        StartupItemKind.StartupFolderCommon => "启动文件夹（公用）",
        StartupItemKind.ScheduledTaskLogon => "计划任务（登录）",
        StartupItemKind.UwpStartupTask => "UWP 应用",
        _ => Item.Kind.ToString()
    };

    public string ImpactText => Item.Impact switch
    {
        StartupImpact.Low => "低",
        StartupImpact.Medium => "中",
        StartupImpact.High => "高",
        _ => "—"
    };

    public void RefreshEnabled(bool enabled)
    {
        // 强制通知：失败还原时字段值未变，也必须让开关弹回真实状态
        _isEnabled = enabled;
        OnPropertyChanged(nameof(IsEnabled));
        Item = Item with { IsEnabled = enabled };
    }

    public void RefreshCommand(string command)
    {
        Command = command;
        Item = Item with { Command = command };
    }
}
