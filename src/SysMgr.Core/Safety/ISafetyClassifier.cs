namespace SysMgr.Core.Safety;

/// <summary>
/// 安全等级判定器（横切层，TDD §2.3 / §3.0）。
/// 全局唯一口径，模块不得自定义冲突规则。判定流程：
/// 1. 命中系统关键白名单路径 → Low
/// 2. 被其他已装软件的注册表/快捷方式引用 → Medium（除非明确为软件私有残留）
/// 3. 可由重装或系统机制重建 + 不被系统引用 → High
/// 用户调整约束：仅允许上调不允许下调（避免误把高危项降级后默认勾选删除）。
/// </summary>
public interface ISafetyClassifier
{
    /// <summary>对扫描结果项打安全等级，返回等级 + 原因 + 证据（用于 TRUST-02 预演面板展示）。</summary>
    SafetyVerdict Classify(ScannedItem item);
}
