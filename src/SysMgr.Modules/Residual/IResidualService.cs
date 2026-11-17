using SysMgr.Core.Queue;

namespace SysMgr.Modules.Residual;

/// <summary>
/// 残留清除模块服务（FR-RESIDUAL-01/02/03，TDD §3.2 / §1.2 命名空间 SysMgr.Modules.Residual）。
/// 方法链：ScanResidualAsync（五维度残留扫描 + 安全分级）→ EnqueueCleanupAsync（勾选项入队清除）。
///
/// 清除执行（FR-RESIDUAL-02）：按 ResidualCategory 映射 OperationAction（File→DeleteFile、
/// Registry→DeleteRegistry、Service→StopService、ScheduledTask→DeleteScheduledTask、
/// Startup→DeleteRegistry（Run 键值）/ DeleteFile（启动文件夹 .lnk））构造 OperationPlan，
/// 走 TRUST-02 预演（逐项展示 Reason/Evidence，Low 项默认不勾选）→ OpQueue 串行执行。
/// 文件/注册表删除前由 OpQueue 触发 IRollbackEngine 影子备份（Copy 策略）。
///
/// 联动（FR-RESIDUAL-03）：Uninstall 卸载完成后经 IModuleCoordinator.TriggerResidualScanAsync
/// 路由到本模块自动扫描，摘要（残留项数 + 预计释放空间）经 TRUST-01 直播推送 UI。
///
/// V1 不落地：驱动 .sys 文件删除（仅 StopService，V1.5）、UWP 应用残留（V1.5）、
/// 无目标软件的全盘孤儿扫描页（V1.5）。
/// </summary>
public interface IResidualService : IModuleService
{
    /// <summary>
    /// 扫描指定软件的残留（FR-RESIDUAL-01）。
    /// 五维度：注册表（Uninstall 孤儿键 / App Paths / Shell Extensions / CLSID / Run 启动项）、
    /// 文件系统（%ProgramFiles% 等六根目录）、服务与驱动、计划任务、启动项。
    /// 每个候选 Emit 前经 ISafetyClassifier.Classify 打级；流式返回避免大集合 OOM。
    /// </summary>
    IAsyncEnumerable<ResidualItem> ScanResidualAsync(
        ResidualScanTarget target,
        ResidualScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken ct);

    /// <summary>
    /// 提交残留清除任务（FR-RESIDUAL-02）。
    /// DryRun=true 仅返回预览不入队；否则构造 OperationPlan 走 TRUST-02 预演 → OpQueue 串行执行。
    /// 单项失败不阻塞其他项（与 FR-UNINSTALL-05 批量语义一致）。
    /// </summary>
    Task<OperationResult> EnqueueCleanupAsync(
        IReadOnlyList<ResidualItem> items,
        ResidualCleanupOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken ct);
}
