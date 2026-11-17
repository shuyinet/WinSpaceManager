namespace SysMgr.Modules.Cleanup;

/// <summary>
/// 支持的浏览器（FR-CLEAN-02）。IE 已退役不支持。
/// 各浏览器数据目录从 %LocalAppData%/%AppData% 对应厂商目录读取，规则外置在 rules\cleanup_targets.json。
/// </summary>
public enum JunkBrowser
{
    Edge,      // Microsoft Edge（Chromium 内核）
    Chrome,    // Google Chrome
    Firefox,   // Mozilla Firefox（数据存储结构与 Chromium 系不同，规则单独配置）
    Brave,     // Brave（Chromium 系）
    Opera      // Opera（Chromium 系）
}
