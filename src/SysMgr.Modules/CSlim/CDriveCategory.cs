namespace SysMgr.Modules.CSlim;

/// <summary>
/// C 盘占用分类（FR-CSLIM-01）。
/// </summary>
public enum CDriveCategory
{
    SystemReserved,   // 系统/保留空间（Windows 目录、系统保留分区、pagefile/hiberfil/swapfile）
    InstalledApps,    // 已安装应用（Program Files 两目录 + Uninstall 清单 EstimatedSize）
    UserFiles,        // 用户文件（用户 profile 中文档/桌面/下载/媒体等非缓存部分）
    TempFiles,        // 临时文件（与 cleanup_targets 规则同源的临时/缓存目录汇总）
    Other             // 其他（未归入上述分类的剩余占用，兜底项）
}
