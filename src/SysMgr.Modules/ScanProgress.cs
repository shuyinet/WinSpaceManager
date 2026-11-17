namespace SysMgr.Modules;

/// <summary>
/// 扫描进度（TDD §3.4 IProgress&lt;ScanProgress&gt;）。
/// Phase: Enumerating（枚举目录/注册表）/ Sizing（计算大小）/ Classifying（安全分级）/ Done。
/// EstimatedTotal=null 表示尚未能预估总数。
/// </summary>
public sealed record ScanProgress(
    ScanPhase Phase,
    int ScannedCount,
    int? EstimatedTotal,
    long? EstimatedFreeBytes,
    string? CurrentPath);

public enum ScanPhase
{
    Enumerating,
    Sizing,
    Classifying,
    Done
}
