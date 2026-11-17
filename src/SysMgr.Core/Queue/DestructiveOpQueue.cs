using System.Threading.Channels;
using SysMgr.Core.Logging;
using SysMgr.Core.Operations;
using SysMgr.Core.Rollback;

namespace SysMgr.Core.Queue;

/// <summary>
/// 破坏性操作串行队列实现（TDD §2.2/§4.3）。
/// 内部 <see cref="Channel{T}"/> 单消费者：Snapshot（不可取消）→ 逐 action Execute → Commit；
/// 任一步异常/取消 → Rollback。同时实现 <see cref="IOperationPlanRegistry"/> 供协调器回溯计划。
/// </summary>
public sealed class DestructiveOpQueue : IDestructiveOpQueue, IOperationPlanRegistry
{
    private readonly IRollbackEngine _rollback;
    private readonly IOperationExecutor _executor;
    private readonly IOperationLogger _logger;
    private readonly Channel<QueueItem> _channel;

    private readonly Dictionary<Guid, OperationPlan> _planRegistry = new();

    public DestructiveOpQueue(
        IRollbackEngine rollback,
        IOperationExecutor executor,
        IOperationLogger logger)
    {
        _rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _channel = Channel.CreateUnbounded<QueueItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _ = Task.Run(ConsumeAsync);
    }

    /// <inheritdoc />
    public async Task<OperationResult> EnqueueAsync(
        OperationPlan plan,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.PreviewRequired && !plan.UserConfirmed)
        {
            throw new InvalidOperationException(
                "计划要求 TRUST-02 预演，未经用户确认不得入队");
        }

        lock (_planRegistry)
        {
            _planRegistry[plan.PlanId] = plan;
        }

        var item = new QueueItem(plan, progress, ct);
        await _channel.Writer.WriteAsync(item, CancellationToken.None);
        return await item.Completion.Task;
    }

    /// <inheritdoc />
    public OperationPlan? Find(Guid opId)
    {
        lock (_planRegistry)
        {
            return _planRegistry.TryGetValue(opId, out var plan) ? plan : null;
        }
    }

    private async Task ConsumeAsync()
    {
        while (await _channel.Reader.WaitToReadAsync())
        {
            while (_channel.Reader.TryRead(out var item))
            {
                try
                {
                    var result = await ProcessAsync(item);
                    item.Completion.SetResult(result);
                }
                catch (Exception ex)
                {
                    // ProcessAsync 内部已兜底，不应走到这里；防止消费者崩掉
                    item.Completion.SetResult(new OperationResult(
                        item.Plan.PlanId, false, TimeSpan.Zero, ex.Message, item.Plan.PlanId));
                }
            }
        }
    }

    private async Task<OperationResult> ProcessAsync(QueueItem item)
    {
        var plan = item.Plan;
        var progress = item.Progress;
        var ct = item.CancellationToken;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        SnapshotHandle? handle = null;
        try
        {
            // 1. 快照阶段：一旦进入不可取消
            Report(progress, plan, OperationStage.Snapshot, 0, plan.Actions.Count, "生成回滚快照");
            handle = await _rollback.CreateSnapshotAsync(plan, CancellationToken.None);

            // 2. 逐动作执行（此阶段可取消）
            for (var i = 0; i < plan.Actions.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var action = plan.Actions[i];
                Report(progress, plan, OperationStage.Executing, i, plan.Actions.Count,
                       DescribeAction(action));

                var actionSw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await _executor.ExecuteAsync(action, progress, ct);
                    actionSw.Stop();
                    Log(action, plan, "Success", null, actionSw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    actionSw.Stop();
                    Log(action, plan, "Failed", ex.Message, actionSw.ElapsedMilliseconds);
                    throw new IOException($"{DescribeAction(action)}失败：{ex.Message}", ex);
                }
            }

            // 3. 提交
            Report(progress, plan, OperationStage.Committing, plan.Actions.Count, plan.Actions.Count, "提交操作");
            await _rollback.CommitAsync(handle, CancellationToken.None);

            sw.Stop();
            Report(progress, plan, OperationStage.Done, plan.Actions.Count, plan.Actions.Count, "完成");
            return new OperationResult(plan.PlanId, true, sw.Elapsed, null, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            string? rollbackError = null;
            Report(progress, plan, OperationStage.RollingBack, 0, plan.Actions.Count, "执行失败，正在回滚");

            // 快照未建成（通常是快照自身失败）：无状态可回滚
            if (handle is not null)
            {
                try
                {
                    await _rollback.RollbackAsync(plan.PlanId, CancellationToken.None);
                }
                catch (Exception rollbackEx)
                {
                    rollbackError = rollbackEx.Message;
                    _logger.LogOperation(new OperationLogEntry(
                        plan.PlanId, DateTimeOffset.Now, plan.ModuleId, plan.FrId,
                        "Rollback", null, null, null, "Failed", 0, rollbackEx.Message));
                }
            }

            var state = handle is null ? "未执行（快照未完成）"
                : rollbackError is null ? "已执行回滚" : "回滚失败，需人工检查";
            Report(progress, plan, OperationStage.Done, 0, plan.Actions.Count, state);
            var error = rollbackError is null ? ex.Message
                : $"{ex.Message}；回滚失败：{rollbackError}";
            return new OperationResult(plan.PlanId, false, sw.Elapsed, error,
                handle is null ? null : plan.PlanId);
        }
    }

    private static void Report(
        IProgress<OperationProgress>? progress,
        OperationPlan plan,
        OperationStage stage,
        int current,
        int total,
        string description)
    {
        progress?.Report(new OperationProgress(plan.PlanId, stage, current, total, description));
    }

    private void Log(
        OperationAction action,
        OperationPlan plan,
        string status,
        string? error,
        long durationMs)
    {
        _logger.LogOperation(new OperationLogEntry(
            plan.PlanId,
            DateTimeOffset.Now,
            plan.ModuleId,
            plan.FrId,
            action.Kind.ToString(),
            action.Target ?? action.Key ?? action.ServiceName ?? action.TaskPath,
            action.OriginalValue,
            action.NewValue,
            status,
            durationMs,
            error));
    }

    private static string DescribeAction(OperationAction action) => action.Kind switch
    {
        ActionKind.DeleteFile => $"删除 {action.Target}",
        ActionKind.Purge => $"清理 {action.Target}",
        ActionKind.MoveFile => $"迁移 {action.Target} → {action.NewPath}",
        ActionKind.DeleteRegistry => $"删除注册表 {action.Key}",
        ActionKind.SetRegistry => $"写入注册表 {action.Key}",
        ActionKind.StopService => $"停止服务 {action.ServiceName}",
        ActionKind.DisableService => $"禁用服务 {action.ServiceName}",
        ActionKind.DisableStartup => $"禁用启动项 {action.Target}",
        ActionKind.DeleteScheduledTask => $"删除计划任务 {action.TaskPath}",
        ActionKind.Shred => $"粉碎 {action.Target}",
        ActionKind.KillProcess => $"结束进程 {action.Target}",
        ActionKind.RunExternalUninstaller => $"运行卸载程序 {action.Target}",
        ActionKind.CreateJunction => $"创建联接 {action.Target} → {action.NewPath}",
        ActionKind.RedirectKnownFolder => $"改写保存位置 {action.NewValue}",
        ActionKind.SetStartupEnabled => $"切换启动项状态 {action.ValueName}",
        ActionKind.SetScheduledTaskEnabled => $"切换计划任务状态 {action.TaskPath}",
        ActionKind.CopyFile => $"复制副本 {action.Target} → {action.NewPath}",
        _ => action.Kind.ToString()
    };

    private sealed record QueueItem(
        OperationPlan Plan,
        IProgress<OperationProgress>? Progress,
        CancellationToken CancellationToken)
    {
        public TaskCompletionSource<OperationResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
