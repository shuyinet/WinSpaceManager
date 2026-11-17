namespace SysMgr.Modules;

/// <summary>
/// 功能模块统一基类（TDD §3.1）。所有 L2 模块实现此接口，由 DI 容器在启动时统一初始化。
/// 模块间通过 IModuleContext 拿横切层句柄，通过 IModuleCoordinator 跨模块协作，不直接 new 对方。
/// </summary>
public interface IModuleService
{
    /// <summary>模块标识，如 "uninstall" / "residual" / "cleanup"。</summary>
    string ModuleId { get; }

    /// <summary>显示名，如"软件卸载"。</summary>
    string DisplayName { get; }

    /// <summary>版本（对齐 requirements §6 版本规划：V1/V1.5/V2/V3）。</summary>
    ModuleVersion Version { get; }

    /// <summary>加载规则、注册菜单、初始化内存索引。启动时 DI 容器统一调用。</summary>
    Task InitializeAsync(IModuleContext ctx, CancellationToken ct);

    /// <summary>体检模块调度（FR-CHECK-01 并行触发各模块健康检查）。</summary>
    Task<ModuleHealthReport> HealthCheckAsync(CancellationToken ct);
}
