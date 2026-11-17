using SysMgr.Core.Queue;

namespace SysMgr.Modules.CSlim;

/// <summary>
/// C 盘瘦身模块服务（FR-CSLIM-01/02/05/08，TDD §3.2 / §1.2 命名空间 SysMgr.Modules.CSlim）。
///
/// - FR-CSLIM-01 AnalyzeCDriveAsync：分析 C 盘空间分布（系统/保留、已装应用、用户文件、临时、其他）。
/// - FR-CSLIM-02 FindLargeFilesAsync + EnqueueLargeFileMigrationAsync：定位大文件（默认 ≥100MB，
///   系统关键目录默认排除且不可取消），迁移经 IModuleCoordinator.RequestMigrationAsync 路由到
///   Migration 模块（复制+校验+回滚），本模块不直接持有 IMigrationService。
/// - FR-CSLIM-05 EnqueueWindowsResidualCleanAsync：Windows.old/更新残留检测与清除，强制风险提示
///   走 TRUST-02 预演，删除经 OpQueue + 影子备份。
/// - FR-CSLIM-08 GetSaveLocationSuggestions / ApplySaveLocationSuggestionsAsync：默认保存位置建议，
///   仅建议不自动修改，一键应用需用户确认。
///
/// V1 不落地：FR-CSLIM-03 休眠文件管理 / FR-CSLIM-04 虚拟内存转移 / FR-CSLIM-06 系统还原点管理 /
/// FR-CSLIM-07 用户文件夹位置迁移（均 V1.5；07 依赖 Migration 文件夹迁移成熟度与 Known Folder 改写联动）。
/// </summary>
public interface ICSlimService : IModuleService
{
    /// <summary>
    /// 一键分析 C 盘空间分布（FR-CSLIM-01）。
    /// 各分类统计含降级策略（目录估算超时跳过，误差计入 Other），不阻塞产出结果。
    /// </summary>
    Task<CDriveAnalysis> AnalyzeCDriveAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken ct);

    /// <summary>
    /// 扫描 C 盘大文件（FR-CSLIM-02）。
    /// 排除系统关键目录（硬编码默认排除清单，设置中可见不可取消）；流式返回避免大集合 OOM。
    /// </summary>
    IAsyncEnumerable<LargeFileItem> FindLargeFilesAsync(
        long minSizeBytes,
        IProgress<ScanProgress>? progress,
        CancellationToken ct);

    /// <summary>
    /// 提交大文件迁移（FR-CSLIM-02）。
    /// 构造 File 类 MigrationRequest（目标目录=destinationRoot，保留文件名）经协调器路由到
    /// Migration 模块；TRUST-02 预演在迁移模块内完成。单项失败不阻塞。
    /// </summary>
    Task<OperationResult> EnqueueLargeFileMigrationAsync(
        IReadOnlyList<LargeFileItem> items,
        string destinationRoot,
        IProgress<OperationProgress>? progress,
        CancellationToken ct);

    /// <summary>
    /// 检测 Windows 更新/升级残留（FR-CSLIM-05，UI 进入页面时调用）。
    /// 存在即返回（含强制提示文案），不存在不列入。
    /// </summary>
    IReadOnlyList<WindowsResidualItem> DetectWindowsResiduals();

    /// <summary>
    /// 提交 Windows 残留清除（FR-CSLIM-05）。
    /// 构造 DeleteFile actions 走 TRUST-02 预演（Windows.old 强制"无法回退版本"提示）→ OpQueue，
    /// 删除前影子备份；单项失败不阻塞。
    /// </summary>
    Task<OperationResult> EnqueueWindowsResidualCleanAsync(
        IReadOnlyList<WindowsResidualItem> items,
        IProgress<OperationProgress>? progress,
        CancellationToken ct);

    /// <summary>
    /// 获取默认保存位置建议（FR-CSLIM-08）。实时读取系统当前关联卷并按剩余固定盘给出建议。
    /// </summary>
    Task<IReadOnlyList<SaveLocationSuggestion>> GetSaveLocationSuggestionsAsync(CancellationToken ct);

    /// <summary>
    /// 一键应用保存位置建议（FR-CSLIM-08）。
    /// 通过 Known Folder API 将所选类别改写到 targetDrive，TRUST-02 预演确认后执行；
    /// 失败类别逐项报告不影响其他类别。
    /// </summary>
    Task<OperationResult> ApplySaveLocationSuggestionsAsync(
        IReadOnlyList<SaveLocationCategory> categories,
        string targetDrive,
        IProgress<OperationProgress>? progress,
        CancellationToken ct);
}
