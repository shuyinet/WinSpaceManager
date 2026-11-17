using SysMgr.Core.Queue;
using SysMgr.Modules.Residual;

namespace SysMgr.Modules;

/// <summary>
/// 模块间协作协议（TDD §3.3）。模块间不允许直接 new 对方 Service，统一走协调器。
/// 实现位于 L1 启动初始化阶段，DI 注入所有模块引用；模块通过 IModuleContext.Coordinator 拿到引用。
/// </summary>
public interface IModuleCoordinator
{
    /// <summary>卸载完成后触发残留扫描（FR-RESIDUAL-03 软件关联残留）。</summary>
    Task TriggerResidualScanAsync(Guid uninstallOpId);

    /// <summary>
    /// 登记卸载计划与软件目标的关联（FR-RESIDUAL-03）。
    /// OperationPlan 不携带软件元数据，卸载模块在入队前登记；
    /// TriggerResidualScanAsync 据此构造残留扫描目标。
    /// </summary>
    void RegisterUninstallTarget(Guid uninstallOpId, ResidualScanTarget target);

    /// <summary>
    /// 发起文件/文件夹迁移请求（TDD §1.3：CSlim 大文件迁移经此路由到 Migration 模块）。
    /// 迁移走复制 + 校验 + 回滚；返回 OpQueue 执行结果。
    /// </summary>
    Task<OperationResult> RequestMigrationAsync(
        MigrationRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken ct);

    /// <summary>关键变更后触发时间机器配置快照（FR-TM-01，V2 落地，V1 占位）。</summary>
    Task TriggerTimelineSnapshotAsync();

    /// <summary>AI 诊断入口（FR-AI-01，V3 落地，V1 占位）。</summary>
    Task TriggerAiDiagnosisAsync(string query);
}
