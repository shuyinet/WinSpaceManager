using System.Windows;
using System.Windows.Input;
using SysMgr.App.ViewModels;
using SysMgr.App.ViewModels.Navigation;

namespace SysMgr.App.Views;

/// <summary>垃圾清理页（FR-CLEAN-01/02/06）。</summary>
public partial class CleanupPage : System.Windows.Controls.Page
{
    private Window? _hostWindow;

    public CleanupPage(SysMgrServices services)
    {
        InitializeComponent();
        ViewModel = new CleanupViewModel(services);
        DataContext = ViewModel;

        Loaded += OnLoaded;
    }

    public CleanupViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null)
        {
            _hostWindow.SizeChanged -= OnHostSizeChanged;
            _hostWindow.SizeChanged += OnHostSizeChanged;
        }

        ApplyViewportHeight();

        // 首次进入该功能页且尚未扫描 → 自动扫描
        if (ViewModel.ScanCommand.CanExecute(null) && AutoScanGate.TryClaim("cleanup"))
        {
            ViewModel.ScanCommand.Execute(null);
        }
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e) => ApplyViewportHeight();

    /// <summary>
    /// 页面被放置在无限高度的内容宿主里时，ScrollViewer 拿不到可视高度会退化成不可滚动，
    /// 这里显式把高度约束到窗口剩余可视区域，滚轮才能滚动整页。
    /// </summary>
    private void ApplyViewportHeight()
    {
        if (_hostWindow is null || !IsLoaded)
        {
            return;
        }

        var origin = PageScroll.TransformToAncestor(_hostWindow).Transform(new Point(0, 0));
        var available = _hostWindow.ActualHeight - origin.Y - 24;
        PageScroll.Height = Math.Max(200, available);
    }

    /// <summary>鼠标滚轮滚动页面（避免被上层导航容器截获后页面纹丝不动）。</summary>
    private void PageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scroll = (System.Windows.Controls.ScrollViewer)sender;
        if (scroll.ScrollableHeight <= 0)
        {
            return;
        }

        var step = Math.Sign(e.Delta) * 56.0;
        scroll.ScrollToVerticalOffset(Math.Clamp(scroll.VerticalOffset - step, 0, scroll.ScrollableHeight));
        e.Handled = true;
    }
}
