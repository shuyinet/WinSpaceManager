using System.Windows;
using SysMgr.App.ViewModels;
using SysMgr.App.ViewModels.Navigation;
using Wpf.Ui.Controls;

namespace SysMgr.App.Views;

/// <summary>启动项管理页（FR-STARTUP-01）。</summary>
public partial class StartupPage : System.Windows.Controls.Page
{
    public StartupPage(SysMgrServices services)
    {
        InitializeComponent();
        ViewModel = new StartupViewModel(services);
        DataContext = ViewModel;

        // 首次进入该功能页且尚未扫描 → 自动扫描
        Loaded += (_, _) =>
        {
            if (ViewModel.ScanCommand.CanExecute(null) && AutoScanGate.TryClaim("startup"))
            {
                ViewModel.ScanCommand.Execute(null);
            }
        };
    }

    public StartupViewModel ViewModel { get; }

    /// <summary>行内开关切换 → 服务层禁用/启用（失败时 ViewModel 自动还原开关）。</summary>
    private async void ItemToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { DataContext: StartupItemRow row } toggle)
        {
            await ViewModel.ToggleAsync(row, toggle.IsChecked == true);
        }
    }
}
