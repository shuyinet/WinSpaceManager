namespace SysMgr.Core.Network;

/// <summary>
/// 联网白名单（横切层，TDD §2.4 / §2.4 联网白名单）。
/// 职责：运行期禁止任何非白名单域名的出站请求；命中白名单的请求需经统一网关；非白名单出站尝试本地拦截并落盘告警。
/// L1-2 层不允许直接 new HttpClient，编译期通过 BannedApiAnalyzer 警告，运行期由 ManagedHttpClient 单例托管全部出站。
/// </summary>
public interface INetworkWhitelist
{
    /// <summary>注册一次联网请求，必须命中白名单类别，否则抛 UnauthorizedNetworkAccessException。</summary>
    Task<HttpResponseMessage> RequestAsync(WhitelistScope scope, Uri endpoint, System.Net.Http.HttpRequestMessage req, CancellationToken ct);

    /// <summary>是否允许某 scope（用户可能在设置中关闭某项）。</summary>
    bool IsAllowed(WhitelistScope scope);
}
