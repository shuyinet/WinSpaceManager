namespace SysMgr.Modules.Uninstall;

/// <summary>
/// 卸载命令详情（FR-UNINSTALL-01/02/03）。
/// GetUninstallCommandAsync 返回，UI 据此决定卸载模式（标准/静默/强制）。
/// </summary>
public sealed record UninstallCommand(
    Guid SoftwareId,
    string? UninstallString,      // 注册表 UninstallString 原值
    UninstallerType UninstallerType,
    bool IsSilentSupported,       // 是否支持静默参数（FR-03）
    string? SilentArgs,           // 静默参数（如 /qn、/VERYSILENT、/S）
    bool CanForceUninstall,       // UninstallerType=None 或 UninstallString 损坏时为 true（FR-04 强制卸载入口启用）
    string? InstallLocation,      // 强制卸载要清理的安装目录
    IReadOnlyList<string> RelatedRegistryKeys,  // 强制卸载要清理的注册表键
    IReadOnlyList<string> RelatedServices,      // 强制卸载要停止的服务
    IReadOnlyList<string> RelatedScheduledTasks); // 强制卸载要删除的计划任务
