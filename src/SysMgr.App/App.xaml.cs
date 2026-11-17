using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace SysMgr.App;

public partial class App : Application
{
    /// <summary>已装配的 V1 服务集合（供 ViewModel 使用）。</summary>
    public SysMgrServices? Services { get; private set; }

    /// <summary>截图服务（需要在主窗口创建后装配，因为全局快捷键绑定主窗口句柄）。</summary>
    public Screenshot.ScreenshotService? Screenshots { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 深色科技安全风：电光青蓝全局强调色（WPF-UI 所有 accent 控件同步换色）
        ApplicationAccentColorManager.Apply(Color.FromRgb(0x18, 0xC0, 0xFF));

        base.OnStartup(e);
        Services = CompositionRoot.Build();
    }
}
