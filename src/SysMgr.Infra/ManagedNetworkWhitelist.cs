using System.Net.Http;
using SysMgr.Core.Network;

namespace SysMgr.Infra;

/// <summary>
/// 联网白名单网关实现（INetworkWhitelist，TDD §2.4 V1 子集）。
/// 全部出站由托管 <see cref="HttpClient"/> 发出：scope 未启用或目标主机不在该 scope 的
/// 允许后缀清单内，一律抛 <see cref="UnauthorizedNetworkAccessException"/> 并中止请求。
/// V1 无部署的远端服务，默认允许后缀清单为空（任何请求都会被拦截）。
/// </summary>
public sealed class ManagedNetworkWhitelist : INetworkWhitelist
{
    private readonly HttpClient _http;
    private readonly HashSet<WhitelistScope> _enabled;
    private readonly Dictionary<WhitelistScope, string[]> _allowedHostSuffixes;

    /// <summary>初始化白名单网关。</summary>
    /// <param name="httpClient">托管 HttpClient；为 null 时使用默认实例。</param>
    /// <param name="enabledScopes">启用的 scope；为 null 时启用 V1 范围（UpdateCheck、TrustSelfTest）。</param>
    /// <param name="allowedHostSuffixes">各 scope 允许的主机后缀；未提供的 scope 视为空清单。</param>
    public ManagedNetworkWhitelist(
        HttpClient? httpClient = null,
        IEnumerable<WhitelistScope>? enabledScopes = null,
        IReadOnlyDictionary<WhitelistScope, string[]>? allowedHostSuffixes = null)
    {
        _http = httpClient ?? new HttpClient();
        _enabled = new HashSet<WhitelistScope>(
            enabledScopes
            ?? new[] { WhitelistScope.UpdateCheck, WhitelistScope.TrustSelfTest });
        _allowedHostSuffixes = allowedHostSuffixes is null
            ? new Dictionary<WhitelistScope, string[]>()
            : new Dictionary<WhitelistScope, string[]>(allowedHostSuffixes);
    }

    /// <inheritdoc />
    public bool IsAllowed(WhitelistScope scope) => _enabled.Contains(scope);

    /// <inheritdoc />
    public async Task<HttpResponseMessage> RequestAsync(
        WhitelistScope scope,
        Uri endpoint,
        HttpRequestMessage req,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(req);

        if (!IsAllowed(scope) || !HostMatches(scope, endpoint))
        {
            throw new UnauthorizedNetworkAccessException(scope, endpoint);
        }

        req.RequestUri = endpoint;
        return await _http.SendAsync(req, ct);
    }

    private bool HostMatches(WhitelistScope scope, Uri endpoint)
    {
        if (!_allowedHostSuffixes.TryGetValue(scope, out var suffixes))
        {
            return false;
        }
        var host = endpoint.Host;
        return suffixes.Any(suffix =>
            host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
    }
}
