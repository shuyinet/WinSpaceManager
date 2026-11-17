namespace SysMgr.Modules.Uninstall;

/// <summary>
/// 软件清单来源（FR-UNINSTALL-01）：
/// <list type="bullet">
/// <item>RegistryX64: HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*</item>
/// <item>RegistryX86: HKLM\SOFTWARE\WOW6432Node\...\Uninstall\*（32 位软件）</item>
/// <item>RegistryUser: HKCU\...\Uninstall\*（用户级安装）</item>
/// <item>Uwp: 商店应用（V1 仅扫描展示，卸载走 UwpDeploymentFacade V1.5）</item>
/// <item>WindowsFeature: Windows 可选功能/系统组件（标记 IsSystemComponent，默认折叠隐藏）</item>
/// </list>
/// </summary>
public enum SoftwareSource
{
    RegistryX64,
    RegistryX86,
    RegistryUser,
    Uwp,
    WindowsFeature
}
