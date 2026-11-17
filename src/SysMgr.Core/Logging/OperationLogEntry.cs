namespace SysMgr.Core.Logging;

/// <summary>
/// 操作历史条目（TDD §4.4 history.jsonl 公开格式，TRUST-04 第 1 层证据）。
/// 每行一条 JSON；追加写入（append-only）；永不自动清理。
/// Status: Success / Failed / RolledBack。
/// </summary>
public sealed record OperationLogEntry(
    Guid OpId,
    DateTimeOffset Ts,
    string ModuleId,
    string FrId,
    string Kind,            // ActionKind 字符串名
    string? Target,
    string? OriginalValue,
    string? NewValue,
    string Status,
    long DurationMs,
    string? Error);
