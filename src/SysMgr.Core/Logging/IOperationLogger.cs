namespace SysMgr.Core.Logging;

/// <summary>
/// 操作历史与直播日志（横切层，TDD §2.6 / FR-TRUST-01 / FR-MONITOR-02）。
/// 职责：分级日志落盘、操作历史落盘 JSONL（可回放）、TRUST-01 操作直播面板广播。
/// history.jsonl 永不自动清理（用户主动才行）；追加写入（append-only）；第三方可写解析工具核验（TRUST-04 第 1 层证据）。
/// </summary>
public interface IOperationLogger
{
    /// <summary>落盘 history.jsonl 并推送 LiveStream。</summary>
    void LogOperation(OperationLogEntry entry);

    /// <summary>TRUST-01 直播面板订阅源。UI 订阅后实时接收；"暂停滚动"仅暂停渲染，不停止落盘与广播。</summary>
    IObservable<OperationLogEntry> LiveStream { get; }
}
