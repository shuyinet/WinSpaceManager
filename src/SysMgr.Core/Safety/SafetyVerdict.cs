namespace SysMgr.Core.Safety;

/// <summary>
/// 安全等级判定结论（TDD §2.3）。Reason 给出判定原因文本，Evidence 列出引用证据路径/键名。
/// </summary>
public sealed record SafetyVerdict(SafetyLevel Level, string Reason, IReadOnlyList<string> Evidence);
