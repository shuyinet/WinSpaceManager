namespace SysMgr.Modules;

/// <summary>
/// 模块健康报告（TDD §3.1 HealthCheckAsync 返回）。
/// Status: Ok（模块可用）/ Degraded（部分能力降级，如某规则集缺失）/ Down（模块不可用）。
/// </summary>
public sealed record ModuleHealthReport(
    string ModuleId,
    ModuleHealthStatus Status,
    string? Message,
    IReadOnlyDictionary<string, string> Dependencies);

public enum ModuleHealthStatus
{
    Ok,
    Degraded,
    Down
}
