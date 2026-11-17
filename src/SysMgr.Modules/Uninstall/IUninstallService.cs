using SysMgr.Core.Queue;

namespace SysMgr.Modules.Uninstall;

/// <summary>
/// 软件卸载模块服务（FR-UNINSTALL-01/02/04，TDD §3.2 / §1.2 命名空间 SysMgr.Modules.Uninstall）。
/// 方法链：ScanInstalledAsync（扫描清单）→ GetUninstallCommandAsync（获取卸载命令）→ EnqueueUninstallAsync（提交卸载任务）。
///
/// 执行分流（按 UninstallOptions.Mode）：
/// - Standard/Silent：构造 OperationPlan（actions=[KillProcess?, RunExternalUninstaller]），
///   走 TRUST-02 预演 → OpQueue 串行执行。回滚策略 PathRecordOnly（外部程序已执行不可逆）。
///   卸载完成后通过 IModuleCoordinator.TriggerResidualScanAsync 触发残留扫描（FR-RESIDUAL-03）。
/// - Force：构造 OperationPlan（actions=[KillProcess?, DeleteFile 安装目录, DeleteRegistry 注册表项,
///   StopService 服务, DeleteScheduledTask 任务]），走 TRUST-02 预演 → OpQueue + 回滚快照（Copy 策略）。
///
/// V1 不落地：FR-UNINSTALL-03 静默卸载（Mode=Silent 占位）、FR-UNINSTALL-05 批量卸载、UWP 卸载（V1.5）。
/// </summary>
public interface IUninstallService : IModuleService
{
    /// <summary>
    /// 扫描系统中已安装软件（FR-UNINSTALL-01）。
    /// 来源：注册表 Uninstall 键（HKLM/HKCU/WOW6432Node）+ UWP/商店应用 + Windows 可选功能。
    /// 流式返回避免大集合 OOM；支持即时搜索过滤、按名称/大小/安装日期排序（UI 层）。
    /// </summary>
    IAsyncEnumerable<UninstallItem> ScanInstalledAsync(IProgress<ScanProgress>? progress, CancellationToken ct);

    /// <summary>
    /// 获取卸载命令详情（FR-UNINSTALL-01/02/03）。
    /// 识别 UninstallerType（Msi/InnoSetup/Nsis/BasicExe/None），判定静默参数支持，
    /// 收集强制卸载所需的关联注册表键/服务/计划任务（FR-UNINSTALL-04）。
    /// </summary>
    Task<UninstallCommand> GetUninstallCommandAsync(UninstallItem item, CancellationToken ct);

    /// <summary>
    /// 提交卸载任务（FR-UNINSTALL-02/04）。
    /// 按 Mode 构造 OperationPlan，走 TRUST-02 预演 → OpQueue 串行执行。
    /// 返回 OperationResult；标准卸载完成后内部触发残留扫描。
    /// </summary>
    Task<OperationResult> EnqueueUninstallAsync(
        UninstallItem item,
        UninstallOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken ct);
}
