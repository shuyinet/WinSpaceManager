using SysMgr.Core.Operations;

namespace SysMgr.Core.Trust;

/// <summary>
/// TRUST-02 预演用户交互端口（L1 抽象）。
/// 生产实现位于 SysMgr.App：弹出预演面板逐项展示 actions/等级/影响，等待用户点"确认执行"；
/// 测试可替换为自动确认/拒绝实现。
/// </summary>
public interface IPreviewPrompt
{
    /// <summary>向用户展示计划并等待审核结论。</summary>
    Task<PreviewResult> PromptAsync(OperationPlan plan, CancellationToken ct);
}
