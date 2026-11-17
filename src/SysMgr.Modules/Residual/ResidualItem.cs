using SysMgr.Core.Safety;

namespace SysMgr.Modules.Residual;

/// <summary>
/// 残留项（FR-RESIDUAL-01 扫描产出）。
/// Verdict 来自 ISafetyClassifier（全局统一口径）：High=建议删除（默认勾选）/ Medium=需人工判断 /
/// Low=可能影响其他软件（默认不勾选）。Reason/Evidence 在 TRUST-02 预演面板逐项展示。
/// Target 语义按 Category：File=文件/目录路径；Registry=注册表键路径；Service=服务名；
/// ScheduledTask=任务路径；Startup=Run 键路径（ValueName 非空）或启动文件夹 .lnk 路径（ValueName 空）。
/// </summary>
public sealed record ResidualItem(
    Guid ItemId,
    Guid SoftwareId,
    ResidualCategory Category,
    string Target,
    string? ValueName,           // 注册表值名（null 表示整个键；Startup 为 Run 键值名）
    SafetyVerdict Verdict,       // 安全等级判定结论（Level + Reason + Evidence）
    long? EstimatedSizeBytes,    // 文件/目录大小；目录估算超时降级 null；其他类别 null
    string MatchedBy,            // 命中的关键词（哪个词匹配到该项，供人工判断参考）
    string Description);         // UI 展示描述（如"启动项：Foo Tray（HKCU Run）"）
