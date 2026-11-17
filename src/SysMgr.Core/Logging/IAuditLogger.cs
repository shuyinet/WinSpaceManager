namespace SysMgr.Core.Logging;

/// <summary>
/// AI 行为审计日志（横切层，TDD §2.6 / FR-TRUST-04 / FR-AI-07）。
/// ai_trace.jsonl 保留 90 天；ai_billing.jsonl 保留 2 年（§5.3）。V3 落地，V1 仅占位接口。
/// </summary>
public interface IAuditLogger
{
    /// <summary>落盘 ai_trace.jsonl（FR-AI-07）。</summary>
    void AppendAiTrace(AiTraceEntry entry);

    /// <summary>落盘 ai_billing.jsonl（FR-AI-08，V3）。</summary>
    void AppendBilling(BillingEntry entry);
}
