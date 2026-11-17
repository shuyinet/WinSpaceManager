namespace SysMgr.Core.Rollback;

/// <summary>
/// 快照句柄（TDD §2.1）。OpId 与 OperationPlan.PlanId 一致；SnapshotDir 为快照根目录。
/// </summary>
public sealed record SnapshotHandle(Guid OpId, string SnapshotDir, RollbackStrategy Strategy);
