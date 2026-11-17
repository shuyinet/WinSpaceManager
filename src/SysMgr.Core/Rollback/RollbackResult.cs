namespace SysMgr.Core.Rollback;

/// <summary>
/// 回滚结果（TDD §2.1）。失败时 FailureReportPath 指向 rollback_failed_&lt;ts&gt;.txt（FR-ROLLBACK-04），
/// Conflicts 列出目标环境冲突项（路径被占用 / 注册表被修改等）。
/// </summary>
public sealed record RollbackResult(bool Success, string? FailureReportPath, IReadOnlyList<string> Conflicts);
