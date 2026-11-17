using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using SysMgr.App.ViewModels;
using Wpf.Ui.Controls;

namespace SysMgr.App.Views;

/// <summary>仪表盘首页。</summary>
public partial class DashboardPage
{
    private readonly DashboardViewModel _viewModel;

    public DashboardPage(SysMgrServices services)
    {
        InitializeComponent();
        _viewModel = new DashboardViewModel(services);
        DataContext = _viewModel;
        _viewModel.NavigateRequested += OnNavigate;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (_viewModel.HasResult || !_viewModel.QuickScanCommand.CanExecute(null)) return;
        await Task.Delay(500);
        _viewModel.QuickScanCommand.Execute(null);
    }

    private void OnNavigate(Type pageType)
    {
        var nav = FindNavigation(this);
        nav?.Navigate(pageType);
    }

    /// <summary>沿可视化树向上找到承载本页的 NavigationView。</summary>
    internal static NavigationView? FindNavigation(DependencyObject start)
    {
        var current = start;
        while (current is not null and not NavigationView)
        {
            current = VisualTreeHelper.GetParent(current);
        }
        return current as NavigationView;
    }
}
