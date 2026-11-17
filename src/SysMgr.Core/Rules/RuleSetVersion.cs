namespace SysMgr.Core.Rules;

/// <summary>规则集版本（TDD §2.5）。</summary>
public sealed record RuleSetVersion(int Major, int Minor, int Patch, DateTimeOffset PublishedAt);
