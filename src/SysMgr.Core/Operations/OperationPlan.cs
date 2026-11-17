using SysMgr.Core.Safety;

namespace SysMgr.Core.Operations;

/// <summary>
/// 操作计划（TDD §4.2，跨模块协议核心）。
/// 所有破坏性操作入队前必须构造此对象；是 TRUST-02 预演、回滚快照、TRUST-01 直播、AI 操作建议的统一数据载体。
/// previewRequired=true 时必须经 ITrustBroadcaster.PreviewAsync 用户确认后才置 userConfirmed=true 入队。
/// </summary>
public sealed record OperationPlan(
    Guid PlanId,
    string ModuleId,
    string FrId,
    string Description,
    SafetyLevel SafetyLevel,
    IReadOnlyList<OperationAction> Actions,
    bool PreviewRequired,
    bool UserConfirmed,
    bool DestinationBackedRollback = false);
