using System.Net.Http;
using SysMgr.Core.SystemAccess;

namespace SysMgr.Infra;

/// <summary>
/// 连通性探测实现（IConnectivityProbe 端口，FR-TRUST-03）。
/// 短超时直连，任何异常/超时都视为不可达。
/// </summary>
public sealed class HttpConnectivityProbe : IConnectivityProbe
{
    private static readonly HttpClient Client = CreateClient();

    /// <inheritdoc />
    public async Task<bool> IsReachableAsync(Uri endpoint, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await Client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static HttpClient CreateClient()
    {
        var socketHandler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(1)
        };
        return new HttpClient(socketHandler)
        {
            Timeout = TimeSpan.FromSeconds(6)
        };
    }
}
