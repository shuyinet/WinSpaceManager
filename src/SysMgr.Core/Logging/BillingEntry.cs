namespace SysMgr.Core.Logging;

/// <summary>
/// AI 计费明细条目（TDD §2.6 / FR-AI-08）。每次云端 AI 调用 token 明细落盘到 ai_billing.jsonl。
/// 保留 2 年。V3 落地，V1 仅占位。
/// </summary>
public sealed record BillingEntry(
    DateTimeOffset Ts,
    string CallType,        // 调用类型
    int InputTokens,
    int OutputTokens,
    decimal DeductedTokens,  // 扣费 token
    decimal RemainingBalance,
    string? PlanId);          // 套餐抵扣 ID
