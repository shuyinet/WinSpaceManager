using SysMgr.Core.Queue;

namespace SysMgr.Modules.Shred;

/// <summary>
/// 文件粉碎模块服务（FR-SHRED-01/02，requirements §3.10，TDD 命名空间 SysMgr.Modules.Shred）。
///
/// - AnalyzeAsync：只读分析。文件/目录（递归统计文件数与总大小，含只读/隐藏），
///   逐个经全局安全分级器打级，系统关键路径（Low）一律拒绝。
/// - ShredAsync：逐文件构造 Shred action（ShredPasses=档位遍数），
///   经 TRUST-02 预演确认后入 OpQueue 串行执行；粉碎不可回滚（PathRecordOnly，仅记录）。
/// FR-SHRED-03 右键菜单仅安装版提供，V1.5 不落地。
/// </summary>
public interface IShredService : IModuleService
{
    /// <summary>分析待粉碎目标（FR-SHRED-01，只读；目录递归统计供确认展示）。</summary>
    Task<IReadOnlyList<ShredTarget>> AnalyzeAsync(IEnumerable<string> paths, CancellationToken ct);

    /// <summary>粉碎文件/目录（FR-SHRED-01/02，走预演 + OpQueue；不可回滚）。</summary>
    Task<OperationResult> ShredAsync(
        IEnumerable<string> paths,
        ShredMode mode,
        IProgress<OperationProgress>? progress,
        CancellationToken ct);
}
