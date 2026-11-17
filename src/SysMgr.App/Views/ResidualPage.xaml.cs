using SysMgr.App.ViewModels;

namespace SysMgr.App.Views;

/// <summary>残留清除页（FR-RESIDUAL-01/02）。</summary>
public partial class ResidualPage : System.Windows.Controls.Page
{
    public ResidualPage(SysMgrServices services)
    {
        InitializeComponent();
        ViewModel = new ResidualViewModel(services);
        DataContext = ViewModel;
    }

    public ResidualViewModel ViewModel { get; }
}
