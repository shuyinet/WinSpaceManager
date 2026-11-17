namespace SysMgr.Core.Logging;

/// <summary>
/// AI 行为审计条目（TDD §2.6 / FR-AI-07）。每次 AI 解析与建议均落盘到 ai_trace.jsonl，格式公开。
/// 保留 90 天。V3 落地，V1 仅占位。
/// </summary>
public sealed record AiTraceEntry(
    DateTimeOffset Ts,
    string InputText,          // 用户输入原文
    string Intent,             // 解析意图
    double Confidence,         // 置信度
    string? GeneratedPlanJson, // 生成的操作计划 JSON
    bool UserAccepted,         // 用户是否采纳
    string? FinalResult);      // 最终执行结果
