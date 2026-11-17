namespace SysMgr.Modules.Residual;

/// <summary>
/// 残留清除选项（FR-RESIDUAL-02）。
/// DryRun=true 时仅返回扫描/预览明细，不构造 OperationPlan、不触达 OpQueue（仅扫描不删除）。
/// </summary>
public sealed record ResidualCleanupOptions(bool DryRun);
