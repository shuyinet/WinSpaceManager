using SysMgr.Core.Operations;

namespace SysMgr.Core.Queue;

/// <summary>
/// 破坏性操作全局串行队列（横切层，TDD §2.2 / §4.3）。
/// 职责：所有破坏性操作（删除/迁移/注册表修改/服务变更/粉碎）全局串行执行；非破坏性扫描可并行。
/// 实现约束：内部 Channel&lt;QueueItem&gt; 单消费者任务，容量无上限；Snapshot → ExecutePlan → Commit/Rollback → Broadcast。
/// 一旦进入 Snapshot 阶段即不可取消（避免半完成态）；Execute 阶段可取消但会触发 Rollback。
/// </summary>
public interface IDestructiveOpQueue
{
    /// <summary>入队破坏性操作计划，返回可 await 的任务（完成即操作结束）。</summary>
    Task<OperationResult> EnqueueAsync(OperationPlan plan, IProgress<OperationProgress>? progress, CancellationToken ct);
}
