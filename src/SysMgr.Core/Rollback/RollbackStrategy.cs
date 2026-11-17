namespace SysMgr.Core.Rollback;

/// <summary>
/// 回滚策略（TDD §2.1 大文件策略决策表）：
/// <list type="bullet">
/// <item>Copy：单文件 &lt; 500MB（阈值可配），复制到 Rollback 目录。</item>
/// <item>VssShadow：单文件 ≥ 500MB 且 VSS 可用，创建卷影副本。</item>
/// <item>PathRecordOnly：单文件 ≥ 500MB 但 VSS 不可用，仅记录原路径 + SHA-256。</item>
/// </list>
/// </summary>
public enum RollbackStrategy
{
    Copy,
    VssShadow,
    PathRecordOnly
}
