using SysMgr.Core.Operations;
using SysMgr.Core.Queue;

namespace SysMgr.Core.Tests.Fakes;

/// <summary>
/// 内存版动作执行器（测试用 IOperationExecutor 假实现）。
/// 按顺序记录被执行动作；FailAt 索引或 ExceptionFactory 配置失败场景。
/// </summary>
public sealed class FakeOperationExecutor : IOperationExecutor
{
    private readonly List<OperationAction> _executed = [];
    private readonly int? _failAt;
    private readonly Exception _exception;

    public FakeOperationExecutor(int? failAt = null, Exception? exception = null)
    {
        _failAt = failAt;
        _exception = exception ?? new InvalidOperationException("模拟执行失败");
    }

    public IReadOnlyList<OperationAction> ExecutedActions => _executed;

    public Task ExecuteAsync(
        OperationAction action,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_failAt is not null && _executed.Count == _failAt)
        {
            throw _exception;
        }
        _executed.Add(action);
        return Task.CompletedTask;
    }
}
