using SysMgr.Core.Safety;

namespace SysMgr.Modules;

/// <summary>
/// 扫描服务统一契约（TDD §3.4）。所有扫描模块返回 IAsyncEnumerable&lt;ScannedItem&gt; 流式输出，避免大集合 OOM。
/// UI 通过 IAsyncEnumerable + CancellationToken 实现"边扫描边显示 + 随时停止"。
/// 注意：扫描不进破坏性操作队列（IDestructiveOpQueue），可并发；扫描结果转成 OperationPlan 后才入队。
/// </summary>
public interface IScanService
{
    IAsyncEnumerable<ScannedItem> ScanAsync(ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken ct);
}
