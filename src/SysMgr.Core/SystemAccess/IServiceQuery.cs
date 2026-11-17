namespace SysMgr.Core.SystemAccess;

/// <summary>服务运行状态（简化为三态）。</summary>
public enum ServiceState
{
    Running,
    Stopped,
    Other
}

/// <summary>
/// 服务与驱动查询端口（L4 抽象，TDD §6.3 ServiceControllerFacade）。实现位于 SysMgr.Infra。
/// </summary>
public interface IServiceQuery
{
    /// <summary>枚举服务（V1 包含内核驱动）。</summary>
    IReadOnlyCollection<ServiceInfo> EnumerateServices();

    /// <summary>停止服务（含依赖等待超时由实现封装）。</summary>
    void Stop(string serviceName);
}

/// <summary>服务信息。</summary>
public sealed record ServiceInfo(
    string Name,
    string DisplayName,
    string? PathName,
    ServiceState State);
