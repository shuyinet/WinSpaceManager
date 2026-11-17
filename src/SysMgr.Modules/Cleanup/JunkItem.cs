using SysMgr.Core.Safety;

namespace SysMgr.Modules.Cleanup;

/// <summary>
/// 垃圾项（FR-CLEAN-01/02/06 扫描产出）。
/// Verdict 来自 ISafetyClassifier（全局统一口径）；Target 为待删文件/目录路径
/// （Kind=RecycleBin 时为虚拟目标 "$RecycleBin"，清空走 SHEmptyRecycleBin）。
/// DefaultSelected 是产品层默认勾选提示（非安全等级）：
/// 系统项 = Verdict.Level==High（Prefetch 为 Medium 默认 false）；
/// 浏览器项 = Kind==BrowserCache（隐私数据默认不勾，FR-CLEAN-02）。
/// </summary>
public sealed record JunkItem(
    Guid ItemId,
    JunkKind Kind,
    JunkBrowser? Browser,             // 浏览器项标识所属浏览器；系统项 null
    string Target,
    SafetyVerdict Verdict,
    long EstimatedSizeBytes,          // 预计释放空间（占用文件跳过，不计入）
    bool DefaultSelected,             // UI 默认是否勾选
    string Description);              // UI 展示描述（如"Edge 缓存""回收站"）
