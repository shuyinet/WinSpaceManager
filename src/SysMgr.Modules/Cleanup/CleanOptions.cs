namespace SysMgr.Modules.Cleanup;

/// <summary>
/// 垃圾清理选项（FR-CLEAN-02）。
/// KillRunningBrowsers=true 时先结束目标浏览器进程及其子进程再清理（避免文件占用失败）；
/// false 且检测到浏览器在运行则中止并抛 BrowserRunningException。默认 false（安全开关）。
/// </summary>
public sealed record CleanOptions(bool KillRunningBrowsers);
