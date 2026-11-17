namespace SysMgr.Modules.CSlim;

/// <summary>
/// 大文件类型（FR-CSLIM-02 展示分类），按扩展名归类。
/// </summary>
public enum LargeFileKind
{
    Video,       // 视频（mp4/mkv/avi/mov/wmv/flv 等）
    Image,       // 图片（jpg/png/gif/bmp/raw/psd 等）
    Document,    // 文档（doc/pdf/txt/xlsx/pptx 等）
    Installer,   // 安装包（exe/msi/zip/7z/rar/iso 等）
    Database,    // 数据库（db/mdb/sqlite/vmdk/vhd 等数据镜像）
    Other        // 其他
}

/// <summary>
/// 大文件条目（FR-CSLIM-02）。
/// 不使用 NTFS Last Access Time（默认禁用不可靠），时间字段只取最后写入时间。
/// </summary>
public sealed record LargeFileItem(
    string Path,
    long SizeBytes,
    DateTimeOffset LastWriteTime,
    LargeFileKind Kind);
