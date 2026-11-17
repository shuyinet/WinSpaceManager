using SysMgr.App.ViewModels;

namespace SysMgr.App.Views;

/// <summary>历史与回滚页。</summary>
public partial class HistoryPage : System.Windows.Controls.Page
{
    public HistoryPage(SysMgrServices services)
    {
        InitializeComponent();
        ViewModel = new HistoryViewModel(services);
        DataContext = ViewModel;
        Loaded += (_, _) => ViewModel.RefreshCommand.Execute(null);
    }

    public HistoryViewModel ViewModel { get; }
}
