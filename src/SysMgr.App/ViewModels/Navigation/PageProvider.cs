using SysMgr.App.Views;

namespace SysMgr.App.ViewModels.Navigation;

/// <summary>
/// NavigationView 页面工厂（WPF-UI 4.x IServiceProvider 模式）。
/// 按 TargetPageType new 页面并注入组合根服务，页面内部再建各自 ViewModel。
/// </summary>
public sealed class PageProvider : IServiceProvider
{
    private readonly SysMgrServices _services;

    public PageProvider(SysMgrServices services) => _services = services;

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(DashboardPage)) return new DashboardPage(_services);
        if (serviceType == typeof(UninstallPage)) return new UninstallPage(_services);
        if (serviceType == typeof(ResidualPage)) return new ResidualPage(_services);
        if (serviceType == typeof(CSlimPage)) return new CSlimPage(_services);
        if (serviceType == typeof(CleanupPage)) return new CleanupPage(_services);
        if (serviceType == typeof(MigrationPage)) return new MigrationPage(_services);
        if (serviceType == typeof(SoftwareMovePage)) return new SoftwareMovePage(_services);
        if (serviceType == typeof(ScreenshotPage)) return new ScreenshotPage();
        if (serviceType == typeof(VisualizationPage)) return new VisualizationPage(_services);
        if (serviceType == typeof(StartupPage)) return new StartupPage(_services);
        if (serviceType == typeof(ShredPage)) return new ShredPage(_services);
        if (serviceType == typeof(LiveStreamPage)) return new LiveStreamPage(_services);
        if (serviceType == typeof(HistoryPage)) return new HistoryPage(_services);
        if (serviceType == typeof(SettingsPage)) return new SettingsPage(_services);
        return null;
    }
}
