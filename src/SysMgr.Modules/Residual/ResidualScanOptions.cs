namespace SysMgr.Modules.Residual;

/// <summary>
/// 残留扫描选项（FR-RESIDUAL-01）。null 类别列表表示全维度扫描。
/// </summary>
public sealed record ResidualScanOptions(
    IReadOnlyList<ResidualCategory>? Categories,   // null = 五维度全扫
    string[]? AdditionalKeywords);                 // 用户补充关键词（如软件曾用名）
