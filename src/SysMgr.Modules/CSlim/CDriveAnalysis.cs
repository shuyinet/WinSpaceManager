namespace SysMgr.Modules.CSlim;

/// <summary>
/// 单个分类的占用量（FR-CSLIM-01）。
/// </summary>
public sealed record CDriveCategorySize(
    CDriveCategory Category,
    long SizeBytes,
    string Description);

/// <summary>
/// C 盘占用分析结果（FR-CSLIM-01）。
/// 各分类 SizeBytes 之和 + FreeBytes 可能与 TotalBytes 有少量口径误差（跨分类的硬链接/占用文件），
/// 误差归入 Other，UI 不做强制对账。
/// </summary>
public sealed record CDriveAnalysis(
    string DriveRoot,
    long TotalBytes,
    long FreeBytes,
    IReadOnlyList<CDriveCategorySize> Categories,
    DateTimeOffset AnalyzedAt);
