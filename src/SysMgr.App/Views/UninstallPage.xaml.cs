using SysMgr.App.ViewModels;
using SysMgr.App.ViewModels.Navigation;

namespace SysMgr.App.Views;

/// <summary>软件卸载页（FR-UNINSTALL-01/02/04）。</summary>
public partial class UninstallPage : System.Windows.Controls.Page
{
    public UninstallPage(SysMgrServices services)
    {
        InitializeComponent();
        ViewModel = new UninstallViewModel(services);
        DataContext = ViewModel;

        // 首次进入该功能页且尚未扫描 → 自动扫描
        Loaded += (_, _) =>
        {
            if (ViewModel.ScanCommand.CanExecute(null) && AutoScanGate.TryClaim("uninstall"))
            {
                ViewModel.ScanCommand.Execute(null);
            }
        };
    }

    public UninstallViewModel ViewModel { get; }
}
