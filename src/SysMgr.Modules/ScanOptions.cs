using SysMgr.Core.Safety;

namespace SysMgr.Modules;

/// <summary>
/// 扫描选项（TDD §3.4）。各字段可空，表示"使用模块默认"。
/// ExcludePaths 默认走 §3.4 FR-CSLIM-02 排除清单（safety_critical_paths.json + 模块硬编码）。
/// MinLevel 默认 High（仅返回建议处理的项，中/低级需用户展开高级）。
/// </summary>
public sealed record ScanOptions(
    string? SoftwareId,         // Residual 扫描时绑定软件
    long? MinFileSize,          // CSlim 大文件阈值
    string[]? IncludePaths,
    string[]? ExcludePaths,
    SafetyLevel? MinLevel);
