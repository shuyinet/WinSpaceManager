namespace SysMgr.Core.Safety;

/// <summary>
/// 安全等级（§3.0 全模块统一口径）：
/// <list type="bullet">
/// <item>High：建议处理，默认勾选（软件私有残留、不被引用、可重建）。</item>
/// <item>Medium：需人工判断，默认不勾（可能被其他软件共用）。</item>
/// <item>Low：可能影响系统/其他软件，默认不勾且需强确认（系统关键路径、被系统服务引用）。</item>
/// </list>
/// </summary>
public enum SafetyLevel
{
    High,
    Medium,
    Low
}
