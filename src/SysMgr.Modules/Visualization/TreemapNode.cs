namespace SysMgr.Modules.Visualization;

/// <summary>
/// 磁盘可视化展示分类（FR-VIS-01 着色口径）。目录条目取其内部文件的占优类型；
/// 混合目录与未知类型归入 <see cref="Other"/>。
/// </summary>
public enum VisFileKind
{
    Video,       // 视频（mp4/mkv/avi/mov/wmv/flv 等）
    Image,       // 图片（jpg/png/gif/bmp/raw/psd 等）
    Document,    // 文档（doc/pdf/txt/xlsx/pptx 等）
    Installer,   // 安装包（exe/msi/zip/7z/rar/iso 等）
    Database,    // 数据库/镜像（db/mdb/sqlite/vmdk/vhd 等）
    Other        // 其他 / 混合目录
}

/// <summary>
/// Treemap 节点（FR-VIS-01）。树形结构：目录含 Children；文件为叶子。
/// 占父节点 <see cref="TotalBytes"/> 不到约 0.8% 的文件子项并入伪节点
/// "&lt;其余 N 项&gt;"（IsOtherAggregate=true），控制树规模；目录子项始终保留下钻。
/// </summary>
public sealed record TreemapNode(
    string Name,
    string Path,
    bool IsDirectory,
    long TotalBytes,
    VisFileKind DominantKind,
    IReadOnlyList<TreemapNode> Children,
    long DirectFileBytes,
    int FileCount,
    bool IsOtherAggregate,
    DateTimeOffset LastWriteTime,
    bool IsHidden);
