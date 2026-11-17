using SysMgr.Core.Common;

namespace SysMgr.Core.Network;

/// <summary>
/// 非白名单出站请求异常（TDD §2.4）。理论上不存在非白名单出站点（无其他出站入口），
/// 但保留兜底：通过 ETW System.Network 监听，命中非白名单远端 IP 即落盘 WARN 日志并抛此异常。
/// </summary>
public sealed class UnauthorizedNetworkAccessException : SysMgrException
{
    public UnauthorizedNetworkAccessException(WhitelistScope scope, Uri endpoint)
        : base($"未经授权的联网请求：scope={scope}, endpoint={endpoint}. 非白名单出站已被拦截。")
    {
        Scope = scope;
        Endpoint = endpoint;
    }

    public WhitelistScope Scope { get; }
    public Uri Endpoint { get; }
}
