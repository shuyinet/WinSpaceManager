namespace SysMgr.Modules.Cleanup;

/// <summary>
/// 垃圾项类型（FR-CLEAN-01/02，扫描与勾选的最小粒度）。
/// 前 7 项为系统垃圾（FR-CLEAN-01）；Browser* 为浏览器隐私数据（FR-CLEAN-02），
/// 具体落在哪个浏览器由 JunkItem.Browser 标识。
/// </summary>
public enum JunkKind
{
    // ---- FR-CLEAN-01 系统临时文件 ----
    Temp,                 // %TEMP%（当前用户临时目录）
    WindowsTemp,          // %SystemRoot%\Temp
    Prefetch,             // C:\Windows\Prefetch（系统可重建，删除后短期内启动性能略降，默认不勾）
    ThumbnailCache,       // 缩略图缓存（资源管理器自动重建）
    WindowsErrorReporting,// Windows 错误报告（WER）队列与归档
    MemoryDump,           // 内存转储（Minidump/MEMORY.DMP）
    RecycleBin,           // 回收站（清空走 SHEmptyRecycleBin，非普通文件删除）

    // ---- FR-CLEAN-01 应用与系统缓存（对标同类工具的「应用程序 / 缓存文件」分类）----
    PackageCache,          // 安装包缓存 C:\ProgramData\Package Cache（VS/VC++ 安装器缓存）
    AppCache,              // 第三方应用缓存（QQ/腾讯系等，可重新下载）
    AppLogs,               // 系统与应用日志（Windows\Logs 等）
    OfficeCache,           // Office 文档缓存（OfficeFileCache）
    WpsBackup,             // WPS 老版本备份与日志
    NuGetCache,            // NuGet 包缓存（%USERPROFILE%\.nuget\packages）
    WindowsUpdateCache,    // Windows 更新下载缓存（SoftwareDistribution\Download）
    DeliveryOptimization,  // 传递优化缓存
    FontCache,             // 字体缓存（系统自动重建）
    IECache,               // IE / 系统网络缓存（INetCache）
    TerminalServicesCache, // 远程桌面缓存

    // ---- FR-CLEAN-02 浏览器隐私数据 ----
    BrowserCache,         // 缓存（默认勾选，"仅清理缓存保留登录态"预设唯一项）
    BrowserCookies,       // Cookie（默认不勾，清理后退出登录态）
    BrowserHistory,       // 历史记录（默认不勾）
    BrowserDownloads,     // 下载记录（默认不勾，仅清记录不删已下载文件）
    BrowserFormData,      // 表单自动填充数据（默认不勾）
    BrowserPasswords      // 已保存密码（默认不勾，FR 明确要求）
}
