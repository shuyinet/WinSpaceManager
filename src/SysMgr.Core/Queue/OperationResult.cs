namespace SysMgr.Core.Queue;

/// <summary>
/// 破坏性操作执行结果。Success=true 表示 actions 全部执行成功并已 Commit；
/// Success=false 表示执行失败已触发回滚（回滚成功/失败见 RollbackResult，由广播附带）。
/// </summary>
public sealed record OperationResult(
    Guid PlanId,
    bool Success,
    TimeSpan Duration,
    string? Error,
    Guid? RollbackOpId);
