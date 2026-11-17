namespace SysMgr.Core.Rollback;

/// <summary>
/// 回滚历史条目（TDD §2.1 EnumerateHistoryAsync 返回）。对应 &lt;OpId&gt;\meta.json 的投影。
/// Status: Active（已快照未提交）/ Committed（已提交，可 LRU 清理）/ RolledBack（已回滚）/ FailedNeedsManual（回滚失败，跳过 LRU）。
/// </summary>
public sealed record RollbackEntry(
    Guid OpId,
    DateTimeOffset CreatedAt,
    string ModuleId,
    string? FrId,
    string Description,
    RollbackStrategy Strategy,
    long TotalSizeBytes,
    SnapshotStatus Status);

/// <summary>快照状态（TDD §4.3 meta.json status 字段）。</summary>
public enum SnapshotStatus
{
    Active,
    Committed,
    RolledBack,
    FailedNeedsManual
}
