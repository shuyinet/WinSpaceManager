namespace SysMgr.Core.SystemAccess;

/// <summary>
/// Windows 防火墙管理端口（L4 抽象，FR-TRUST-03 自我断网）。实现位于 SysMgr.Infra。
/// </summary>
public interface IFirewall
{
    /// <summary>添加按程序路径匹配的出站阻断规则（已存在则无操作）。</summary>
    Task BlockApplicationAsync(string applicationPath, CancellationToken ct);

    /// <summary>移除该程序的出站阻断规则。</summary>
    Task UnblockApplicationAsync(string applicationPath, CancellationToken ct);
}

/// <summary>
/// 连通性探测端口（FR-TRUST-03 自检）。实现位于 SysMgr.Infra，
/// 探测必须绕过缓存直连，以真实网络结果判定阻断是否生效。
/// </summary>
public interface IConnectivityProbe
{
    /// <summary>探测端点是否可达；不可达（异常/超时）返回 false。</summary>
    Task<bool> IsReachableAsync(Uri endpoint, CancellationToken ct);
}
