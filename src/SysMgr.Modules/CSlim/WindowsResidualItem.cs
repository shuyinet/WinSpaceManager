namespace SysMgr.Modules.CSlim;

/// <summary>
/// Windows 更新/升级残留类型（FR-CSLIM-05）。
/// </summary>
public enum WindowsResidualKind
{
    WindowsOld,           // C:\Windows.old（清理后无法回退上一 Windows 版本）
    UpdateDownload,       // C:\Windows\SoftwareDistribution\Download（已下载更新缓存）
    WinSxSBackup,         // C:\Windows\WinSxS\Backup（组件备份）
    WinREAgent,           // C:\$WinREAgent（功能更新临时恢复文件）
    DeliveryOptimization  // 传递优化缓存（%ProgramData%\Microsoft\Windows\DeliveryOptimization\Cache）
}

/// <summary>
/// Windows 残留项（FR-CSLIM-05）。
/// Warning 为强制提示文案（如 Windows.old 的"无法回退上一版本"），TRUST-02 预演面板必须原样展示。
/// </summary>
public sealed record WindowsResidualItem(
    WindowsResidualKind Kind,
    string Target,
    long SizeBytes,
    string Warning);
