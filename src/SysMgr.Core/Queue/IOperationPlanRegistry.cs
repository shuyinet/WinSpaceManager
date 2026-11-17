using SysMgr.Core.Operations;

namespace SysMgr.Core.Queue;

/// <summary>
/// 已执行计划回溯端口。由 OpQueue 实现，协调器（如卸载后联动残留扫描）
/// 据此取回 opId 对应的计划，避免在事件链中重复传参。
/// </summary>
public interface IOperationPlanRegistry
{
    /// <summary>按 opId 查找计划；不存在返回 null。</summary>
    OperationPlan? Find(Guid opId);
}
