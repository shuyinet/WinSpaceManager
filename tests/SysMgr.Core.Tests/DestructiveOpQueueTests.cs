using SysMgr.Core.Logging;
using SysMgr.Core.Operations;
using SysMgr.Core.Queue;
using SysMgr.Core.Rollback;
using SysMgr.Core.Safety;
using SysMgr.Core.Tests.Fakes;
using Xunit;

namespace SysMgr.Core.Tests;

public sealed class DestructiveOpQueueTests
{
    private static OperationAction Action(ActionKind kind = ActionKind.DeleteFile) => new(
        kind, @"C:\foo", null, 10, null, null, null, null, null, null, null);

    private static OperationPlan Plan(
        bool confirmed = true,
        params OperationAction[] actions) => new(
        Guid.NewGuid(), "cleanup", "FR-CLEAN-01", "测试",
        SafetyLevel.High, actions, PreviewRequired: true, UserConfirmed: confirmed);

    private sealed record Harness(
        DestructiveOpQueue Queue,
        FileRollbackEngine Engine,
        FakeOperationExecutor Executor,
        OperationLogger Logger,
        TempWorkspace Workspace) : IDisposable
    {
        public void Dispose()
        {
            Workspace.Dispose();
        }
    }

    private static Harness CreateHarness(FakeOperationExecutor? executor = null)
    {
        var ws = new TempWorkspace();
        var logger = new OperationLogger(Path.Combine(ws.Root, "history.jsonl"));
        var engine = new FileRollbackEngine(ws.CreateDirectory("rb"), new FakeRegistry());
        var exec = executor ?? new FakeOperationExecutor();
        var queue = new DestructiveOpQueue(engine, exec, logger);
        return new Harness(queue, engine, exec, logger, ws);
    }

    [Fact]
    public async Task 全部动作成功按序执行并提交()
    {
        using var h = CreateHarness();
        var progress = new List<OperationProgress>();

        var result = await h.Queue.EnqueueAsync(
            Plan(true, Action(), Action(ActionKind.DeleteRegistry)),
            new Progress<OperationProgress>(progress.Add),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, h.Executor.ExecutedActions.Count);
        Assert.Equal(ActionKind.DeleteFile, h.Executor.ExecutedActions[0].Kind);
        Assert.Equal(ActionKind.DeleteRegistry, h.Executor.ExecutedActions[1].Kind);
        Assert.Contains(progress, p => p.Stage == OperationStage.Done);

        // 快照已提交
        var entries = new List<RollbackEntry>();
        await foreach (var e in h.Engine.EnumerateHistoryAsync(CancellationToken.None))
        {
            entries.Add(e);
        }
        Assert.Equal(SnapshotStatus.Committed, entries.Single().Status);
    }

    [Fact]
    public async Task 未确认的计划拒绝入队()
    {
        using var h = CreateHarness();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Queue.EnqueueAsync(Plan(confirmed: false, Action()), null, CancellationToken.None));

        Assert.Empty(h.Executor.ExecutedActions);
    }

    [Fact]
    public async Task 动作失败触发回滚并返回失败结果()
    {
        // 第 2 个动作失败
        var executor = new FakeOperationExecutor(failAt: 1);
        using var h = CreateHarness(executor);

        var target = Path.Combine(h.Workspace.Root, "victim.txt");
        File.WriteAllText(target, "x");

        var result = await h.Queue.EnqueueAsync(
            Plan(true,
                 Action(),
                 new OperationAction(ActionKind.DeleteFile, target, null, 1, null,
                                     null, null, null, null, null, null)),
            null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.RollbackOpId);
        Assert.Equal(result.PlanId, result.RollbackOpId);
    }

    [Fact]
    public async Task 执行中取消触发回滚()
    {
        var executor = new FakeOperationExecutor();
        using var h = CreateHarness(executor);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await h.Queue.EnqueueAsync(Plan(true, Action()), null, cts.Token);

        Assert.False(result.Success);
        Assert.Empty(h.Executor.ExecutedActions);
    }

    [Fact]
    public async Task Find回溯已入队计划()
    {
        using var h = CreateHarness();
        var plan = Plan(true, Action());

        await h.Queue.EnqueueAsync(plan, null, CancellationToken.None);

        Assert.Same(plan, h.Queue.Find(plan.PlanId));
    }
}
