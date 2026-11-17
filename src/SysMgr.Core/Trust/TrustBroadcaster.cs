using SysMgr.Core.Logging;
using SysMgr.Core.Operations;
using SysMgr.Core.SystemAccess;

namespace SysMgr.Core.Trust;

/// <summary>
/// TRUST 信任层广播器实现（TDD §5.1 / FR-TRUST-01/02/03）。
/// - Broadcast：委托 <see cref="IOperationLogger"/> 落 history.jsonl（同时推送直播）。
/// - PreviewAsync：委托 <see cref="IPreviewPrompt"/> 等待用户审核结论。
/// - SelfTestDisconnectAsync：经 <see cref="IFirewall"/> 添加本程序出站阻断，
///   再用 <see cref="IConnectivityProbe"/> 直连自检端点；不可达即阻断生效（返回 true）。
///   阻断是持续状态，由调用方在关闭自我断网时调防火墙移除规则。
/// </summary>
public sealed class TrustBroadcaster : ITrustBroadcaster
{
    private readonly IOperationLogger _logger;
    private readonly IPreviewPrompt _prompt;
    private readonly IFirewall _firewall;
    private readonly IConnectivityProbe _probe;
    private readonly Uri _selfTestEndpoint;

    public TrustBroadcaster(
        IOperationLogger logger,
        IPreviewPrompt prompt,
        IFirewall firewall,
        IConnectivityProbe probe,
        Uri selfTestEndpoint)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _firewall = firewall ?? throw new ArgumentNullException(nameof(firewall));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _selfTestEndpoint = selfTestEndpoint ?? throw new ArgumentNullException(nameof(selfTestEndpoint));
    }

    /// <inheritdoc />
    public void Broadcast(OperationLogEntry entry) => _logger.LogOperation(entry);

    /// <inheritdoc />
    public Task<PreviewResult> PreviewAsync(OperationPlan plan, CancellationToken ct)
        => _prompt.PromptAsync(plan, ct);

    /// <inheritdoc />
    public async Task<bool> SelfTestDisconnectAsync(CancellationToken ct)
    {
        var applicationPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(applicationPath))
        {
            return false;
        }

        await _firewall.BlockApplicationAsync(applicationPath, ct);

        // 阻断规则包含当前进程：探测应不可达
        var reachable = await _probe.IsReachableAsync(_selfTestEndpoint, ct);
        return !reachable;
    }
}
