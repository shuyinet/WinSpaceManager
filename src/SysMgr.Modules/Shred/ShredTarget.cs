using SysMgr.Core.Safety;

namespace SysMgr.Modules.Shred;

/// <summary>粉碎档位（FR-SHRED-02，值即覆写遍数）。</summary>
public enum ShredMode
{
    /// <summary>快速粉碎：1 遍零写。</summary>
    Quick = 1,

    /// <summary>安全粉碎：3 遍覆写（0x00 / 0xFF / 随机）。</summary>
    Secure3 = 3,

    /// <summary>安全粉碎：7 遍覆写（参考 DoD 5220.22-M）。</summary>
    Secure7 = 7
}

/// <summary>
/// 粉碎目标分析结果（FR-SHRED-01）。
/// IsDirectory=true 时 FileCount/TotalBytes 为递归统计（含只读/隐藏文件），供确认前展示。
/// Level 为全局安全分级器判定：Low（系统关键路径）一律拒绝粉碎。
/// </summary>
public sealed record ShredTarget(
    string Path,
    bool IsDirectory,
    long FileCount,
    long TotalBytes,
    SafetyLevel Level,
    string Reason);
