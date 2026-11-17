using SysMgr.Core.Queue;

namespace SysMgr.Modules.Startup;

/// <summary>
/// 启动项管理模块服务（FR-STARTUP-01，requirements §3.8，TDD 命名空间 SysMgr.Modules.Startup）。
///
/// - ScanAsync：扫描注册表 Run 键（HKLM/HKCU/WOW6432Node）、用户/公用启动文件夹、
///   计划任务登录触发项与 UWP 自启动项；启用状态与任务管理器同口径（StartupApproved）。
///   扫描为只读操作，不经 OpQueue。
/// - SetEnabledAsync：禁用/启用（六类均支持）。注册表/文件夹/UWP 项写 StartupApproved
///   REG_BINARY，计划任务经 schtasks /change；原状态预填供回滚。
/// - DeleteAsync：删除（仅注册表 Run 值与启动文件夹 .lnk；计划任务删除属 FR-SERVICE-02）。
/// - EditCommandAsync：编辑命令行（仅注册表 Run 项）。
/// 所有修改走 TRUST-02 预演 + OpQueue + 影子备份。
/// </summary>
public interface IStartupService : IModuleService
{
    /// <summary>扫描全部来源的启动项（FR-STARTUP-01，只读）。</summary>
    Task<IReadOnlyList<StartupItem>> ScanAsync(CancellationToken ct);

    /// <summary>禁用/启用启动项（FR-STARTUP-01，走回滚）。</summary>
    Task<OperationResult> SetEnabledAsync(
        StartupItem item,
        bool enabled,
        IProgress<OperationProgress>? progress,
        CancellationToken ct);

    /// <summary>删除启动项（FR-STARTUP-01，仅 <see cref="StartupItem.CanDelete"/> 项，走回滚）。</summary>
    Task<OperationResult> DeleteAsync(
        StartupItem item,
        IProgress<OperationProgress>? progress,
        CancellationToken ct);

    /// <summary>编辑命令行（FR-STARTUP-01，仅 <see cref="StartupItem.CanEdit"/> 项，走回滚）。</summary>
    Task<OperationResult> EditCommandAsync(
        StartupItem item,
        string newCommand,
        IProgress<OperationProgress>? progress,
        CancellationToken ct);
}
