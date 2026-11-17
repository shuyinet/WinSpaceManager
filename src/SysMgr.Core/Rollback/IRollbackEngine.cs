using SysMgr.Core.Operations;

namespace SysMgr.Core.Rollback;

/// <summary>
/// 回滚引擎（横切层，TDD §2.1 / FR-ROLLBACK-01/02/03/04）。
/// 职责：破坏性操作执行前生成快照、执行后落盘元数据、失败时回滚、空间 LRU 管理、失败兜底报告。
/// 详设（伪代码 + LRU 实现 + 失败兜底）见批次 2 docs/design/06-rollback.md。
/// </summary>
public interface IRollbackEngine
{
    /// <summary>为待执行的操作计划生成快照（文件副本/.reg/JSON），返回快照句柄。调用者必须先 Snapshot 再 Execute。</summary>
    Task<SnapshotHandle> CreateSnapshotAsync(OperationPlan plan, CancellationToken ct);

    /// <summary>操作执行成功后调用，落盘 committed.json 标记快照可被 LRU 清理。</summary>
    Task CommitAsync(SnapshotHandle handle, CancellationToken ct);

    /// <summary>操作执行失败或用户主动回滚时调用。先校验目标环境未冲突，再恢复。</summary>
    Task<RollbackResult> RollbackAsync(Guid opId, CancellationToken ct);

    /// <summary>枚举所有可回滚操作（用于历史与回滚界面）。</summary>
    IAsyncEnumerable<RollbackEntry> EnumerateHistoryAsync(CancellationToken ct);
}
