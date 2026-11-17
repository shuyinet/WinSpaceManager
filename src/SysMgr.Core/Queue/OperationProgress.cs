namespace SysMgr.Core.Queue;

/// <summary>
/// 破坏性操作进度（TDD §2.2）。Stage: Snapshot / Executing / Committing / RollingBack / Done。
/// CurrentActionIndex/TotalActions 用于 TRUST-01 直播面板展示"正在执行第 N 步"。
/// </summary>
public sealed record OperationProgress(
    Guid PlanId,
    OperationStage Stage,
    int CurrentActionIndex,
    int TotalActions,
    string? CurrentActionDescription);

public enum OperationStage
{
    Snapshot,
    Executing,
    Committing,
    RollingBack,
    Done
}
