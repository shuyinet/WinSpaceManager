namespace SysMgr.Modules.Uninstall;

/// <summary>
/// 卸载模式：
/// <list type="bullet">
/// <item>Standard: FR-UNINSTALL-02 标准卸载，调用软件自身 UninstallString。</item>
/// <item>Silent: FR-UNINSTALL-03 静默卸载，对支持静默参数的安装包用 /qn /VERYSILENT /S 等。V1 不落地（handoff 范围 01/02/04）。</item>
/// <item>Force: FR-UNINSTALL-04 强制卸载，直接清理安装目录+注册表+服务+计划任务，走回滚快照。</item>
/// </summary>
public enum UninstallMode
{
    Standard,
    Silent,
    Force
}
