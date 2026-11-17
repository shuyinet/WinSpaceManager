using SysMgr.App.ViewModels;
using SysMgr.App.ViewModels.Navigation;

namespace SysMgr.App.Views;

/// <summary>C 盘瘦身页（FR-CSLIM-01/02/05/08）。</summary>
public partial class CSlimPage : System.Windows.Controls.Page
{
    public CSlimPage(SysMgrServices services)
    {
        InitializeComponent();
        ViewModel = new CSlimViewModel(services);
        DataContext = ViewModel;

        // 首次进入该功能页且尚未分析 → 自动分析 C 盘占用
        Loaded += (_, _) =>
        {
            if (ViewModel.AnalyzeCommand.CanExecute(null) && AutoScanGate.TryClaim("cslim"))
            {
                ViewModel.AnalyzeCommand.Execute(null);
            }
        };
    }

    public CSlimViewModel ViewModel { get; }
}
