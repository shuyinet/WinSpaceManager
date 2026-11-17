namespace SysMgr.Core.Operations;

/// <summary>
/// 单个操作动作（TDD §4.2 actions 数组项）。按 Kind 携带不同字段，未用字段留 null。
/// Shred 类不走回滚（FR-SHRED-02 粉碎后不可恢复）。
/// </summary>
public sealed record OperationAction(
    ActionKind Kind,
    string? Target,         // DeleteFile/MoveFile/Shred 的文件路径；MoveFile 目标路径用 NewPath
    string? NewPath,        // MoveFile 的新路径
    long? SizeBytes,        // DeleteFile/MoveFile/Shred 的文件大小（用于回滚策略判定）
    string? Sha256,         // DeleteFile/MoveFile 的校验值
    string? Key,            // DeleteRegistry/SetRegistry 的注册表键路径
    string? ValueName,      // SetRegistry 的值名（null 表示整个键）
    string? OriginalValue,  // SetRegistry 的原值（回滚用）
    string? NewValue,       // SetRegistry 的新值
    string? ServiceName,    // StopService/DisableService 的服务名
    string? TaskPath,       // DeleteScheduledTask 的任务路径
    int? ShredPasses = null); // Shred 覆写遍数（FR-SHRED-02：1=快速零写，3/7=安全覆写）；null 按 1 处理
