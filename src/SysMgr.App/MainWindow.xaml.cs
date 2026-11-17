using System.Windows;

namespace SysMgr.App;

/// <summary>
/// 主窗口（L1 统一入口）。品牌标题栏 + WPF-UI NavigationView：
/// 仪表盘 + 五个 V1 模块页 + 操作直播 + 历史与回滚。
/// </summary>
public partial class MainWindow
{
    private readonly ViewModels.MainWindowViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        var services = ((App)Application.Current).Services
            ?? throw new InvalidOperationException("组合根未装配");

        _viewModel = new ViewModels.MainWindowViewModel(services);
        DataContext = _viewModel;

        // 截图服务：全局快捷键需绑定本窗口句柄，故在窗口创建后装配
        var screenshots = new Screenshot.ScreenshotService(services.Config, this);
        ((App)Application.Current).Screenshots = screenshots;
        Loaded += async (_, _) => await screenshots.InitializeAsync();

        // 启动后静默检查新版本，发现则后台下载并在标题栏提示
        Loaded += (_, _) => _viewModel.StartUpdateCheck();

        RootNavigation.SetServiceProvider(new ViewModels.Navigation.PageProvider(services));
        RootNavigation.Loaded += (_, _) => RootNavigation.Navigate(typeof(Views.DashboardPage));
    }

    private async void DisconnectSwitch_Toggled(object sender, RoutedEventArgs e)
        => await _viewModel.ToggleDisconnectAsync();

    private void UpdatePill_Click(object sender, RoutedEventArgs e)
        => _viewModel.UpdateNow();
}
