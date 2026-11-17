using SysMgr.Core.Logging;
using SysMgr.Core.Operations;

namespace SysMgr.Core.Trust;

/// <summary>
/// TRUST 信任层广播器（横切层，TDD §5.1 / FR-TRUST-01/02/03）。
/// 所有模块执行操作时通过它广播；是 TRUST-01 直播 + TRUST-02 预演 + TRUST-03 自我断网的统一入口。
/// </summary>
public interface ITrustBroadcaster
{
    /// <summary>落盘 history.jsonl + 推送 LiveStream（FR-TRUST-01 操作直播）。</summary>
    void Broadcast(OperationLogEntry entry);

    /// <summary>
    /// TRUST-02 操作前预演。任何破坏性操作入队前必须经此方法：
    /// 弹出预演面板逐项展示 actions + 安全等级 + 影响，用户审核点"确认执行"才返回 Confirmed=true。
    /// 安全护栏：plan 中存在 SafetyLevel.Low 项时默认不勾选，需用户展开"高级模式"逐项勾选。
    /// </summary>
    Task<PreviewResult> PreviewAsync(OperationPlan plan, CancellationToken ct);

    /// <summary>
    /// TRUST-03 自我断网开关。开启时给自身程序添加 Windows Firewall 出站阻断规则（按程序路径匹配），
    /// 并访问固定自检端点验证规则已生效；关闭即移除该规则。
    /// </summary>
    Task<bool> SelfTestDisconnectAsync(CancellationToken ct);
}
