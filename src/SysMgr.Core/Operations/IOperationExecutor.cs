using SysMgr.Core.Queue;

namespace SysMgr.Core.Operations;

/// <summary>
/// 操作动作执行器端口（L4 抽象）。实现位于 SysMgr.Infra（WindowsOperationExecutor），
/// 内部按 <see cref="OperationAction.Kind"/> 分派到文件/注册表/服务/任务/进程等 Facade。
/// 队列只负责编排（快照 → 执行 → 提交/回滚），不关心动作如何落地。
/// </summary>
public interface IOperationExecutor
{
    /// <summary>执行单个动作；失败抛异常，由队列决定回滚。</summary>
    Task ExecuteAsync(
        OperationAction action,
        IProgress<OperationProgress>? progress,
        CancellationToken ct);
}
