using System.Collections.Generic;

namespace SysMgr.App.ViewModels.Navigation;

/// <summary>
/// 左侧功能切换时的自动扫描闸门：同一会话内每个功能页只自动扫描一次，
/// 之后切换到该功能页遵循用户自己的操作（保留扫描按钮手动触发）。
/// </summary>
public static class AutoScanGate
{
    private static readonly HashSet<string> Scanned = new();

    /// <summary>首次调用返回 true（表示应由本次进入触发自动扫描）。</summary>
    public static bool TryClaim(string key)
    {
        lock (Scanned)
        {
            return Scanned.Add(key);
        }
    }
}
