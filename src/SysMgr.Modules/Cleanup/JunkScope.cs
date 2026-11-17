namespace SysMgr.Modules.Cleanup;

/// <summary>
/// 垃圾扫描范围（TDD §3.2 ScanJunkAsync(scope) 的 scope）。
/// SystemKinds=null 表示扫描全部系统垃圾类型；BrowserTargets=null 表示扫描全部已安装浏览器。
/// 未安装的浏览器自动跳过（数据目录不存在即视为未安装）。
/// </summary>
public sealed record JunkScope(
    IReadOnlyList<JunkKind>? SystemKinds,           // null = 全部系统/应用垃圾类型
    IReadOnlyList<JunkBrowserTarget>? BrowserTargets); // null = 五浏览器 × 用户默认数据类型
