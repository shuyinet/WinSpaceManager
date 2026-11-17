using SysMgr.Core.Queue;

namespace SysMgr.Modules.Visualization;

/// <summary>
/// 磁盘空间可视化模块服务（FR-VIS-01/02/03，requirements §3.5，TDD 命名空间
/// SysMgr.Modules.Visualization）。
///
/// - FR-VIS-01 ScanAsync：递归扫描盘符/目录，产出 TreemapNode 树（面积=大小、颜色=占优类型）。
///   扫描为只读操作，不经 OpQueue。
/// - FR-VIS-02 FilterTree：按名称/扩展名/大小阈值/日期过滤已扫描树（纯函数，UI 即时应用）。
///   EnqueueDeleteAsync / EnqueueMigrationAsync：右键删除走 TRUST-02 预演 + OpQueue + 影子备份；
///   迁移经 IModuleCoordinator.RequestMigrationAsync 路由到 Migration 模块。
/// - FR-VIS-03 SetTagAsync/GetTagsAsync：标签（"待处理"等）持久化到 config.json "vis_tags" 节。
///   ExportSnapshotAsync：扫描结果导出 JSON 快照，便于二次查看或传给 IT 协助。
/// </summary>
public interface IVisualizationService : IModuleService
{
    /// <summary>
    /// 递归扫描 rootPath 生成 Treemap 树（FR-VIS-01）。
    /// 不跟随 reparse point，拒绝访问目录跳过并继续；占父节点 &lt;1% 的子项聚合为"&lt;其余 N 项&gt;"。
    /// </summary>
    Task<TreemapNode> ScanAsync(
        string rootPath,
        IProgress<ScanProgress>? progress,
        CancellationToken ct);

    /// <summary>
    /// 过滤已扫描树（FR-VIS-02）。nameOrExtension 为空、minSizeBytes 为 0、
    /// modifiedAfter 为空即不启用对应条件；excludeHidden 过滤隐藏/系统属性的文件与目录。
    /// 目录保留以维持树结构（自身名称命中时保留整棵子树）。
    /// </summary>
    TreemapNode FilterTree(
        TreemapNode root,
        string? nameOrExtension,
        long minSizeBytes,
        DateTimeOffset? modifiedAfter,
        bool excludeHidden);

    /// <summary>
    /// 删除勾选项（FR-VIS-02 右键删除，走回滚）。
    /// 构造 DeleteFile actions 走 TRUST-02 预演 → OpQueue，删除前影子备份。
    /// </summary>
    Task<OperationResult> EnqueueDeleteAsync(
        IReadOnlyList<string> paths,
        IProgress<OperationProgress>? progress,
        CancellationToken ct);

    /// <summary>
    /// 迁移勾选项到目标目录（FR-VIS-02 右键迁移）。
    /// 构造 File 类 MigrationRequest 经协调器路由到 Migration 模块（复制+校验+回滚）。
    /// </summary>
    Task<OperationResult> EnqueueMigrationAsync(
        IReadOnlyList<string> paths,
        string destinationRoot,
        IProgress<OperationProgress>? progress,
        CancellationToken ct);

    /// <summary>读取全部标签（FR-VIS-03）。键为完整路径，值为标签文本。</summary>
    Task<IReadOnlyDictionary<string, string>> GetTagsAsync(CancellationToken ct);

    /// <summary>设置/清除标签（FR-VIS-03）。tag 为 null 或空白表示清除。</summary>
    Task SetTagAsync(string path, string? tag, CancellationToken ct);

    /// <summary>导出扫描快照 JSON（FR-VIS-03）。目录不存在时创建；覆盖同名文件。</summary>
    Task ExportSnapshotAsync(TreemapNode root, string filePath, CancellationToken ct);
}
