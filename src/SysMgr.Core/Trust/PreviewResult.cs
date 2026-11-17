namespace SysMgr.Core.Trust;

/// <summary>
/// TRUST-02 预演结果（TDD §5.2）。Confirmed=true 表示用户已审核并确认执行；
/// Conflicts 为预演阶段检测到的环境冲突（如目标路径已被占用），非空时建议阻止执行。
/// </summary>
public sealed record PreviewResult(bool Confirmed, IReadOnlyList<string> Conflicts, string? UserNote);
