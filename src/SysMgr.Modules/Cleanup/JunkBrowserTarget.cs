namespace SysMgr.Modules.Cleanup;

/// <summary>
/// 浏览器清理目标（FR-CLEAN-02）：指定浏览器 + 要清理的数据类型（JunkKind 中的 Browser* 项）。
/// "仅清理缓存，保留登录态"预设 = 仅含 BrowserCache 的目标列表。
/// </summary>
public sealed record JunkBrowserTarget(
    JunkBrowser Browser,
    IReadOnlyList<JunkKind> DataKinds);
