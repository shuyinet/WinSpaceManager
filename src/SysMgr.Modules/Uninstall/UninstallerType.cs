namespace SysMgr.Modules.Uninstall;

/// <summary>
/// 卸载程序类型（FR-UNINSTALL-03 静默参数识别）：
/// <list type="bullet">
/// <item>Msi: Windows Installer，静默参数 /qn</item>
/// <item>InnoSetup: 静默参数 /VERYSILENT /NORESTART</item>
/// <item>Nsis: 静默参数 /S</item>
/// <item>BasicExe: 普通 exe，无通用静默参数</item>
/// <item>None: UninstallString 缺失或损坏（FR-UNINSTALL-04 强制卸载入口启用）</item>
/// <item>Unknown: 未识别</item>
/// </list>
/// </summary>
public enum UninstallerType
{
    Msi,
    InnoSetup,
    Nsis,
    BasicExe,
    None,
    Unknown
}
