using SysMgr.App.ViewModels;

namespace SysMgr.App.Views;

/// <summary>操作直播面板页（FR-TRUST-01）。</summary>
public partial class LiveStreamPage : System.Windows.Controls.Page
{
    public LiveStreamPage(SysMgrServices services)
    {
        InitializeComponent();
        ViewModel = new LiveStreamViewModel(services);
        DataContext = ViewModel;
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public LiveStreamViewModel ViewModel { get; }
}
