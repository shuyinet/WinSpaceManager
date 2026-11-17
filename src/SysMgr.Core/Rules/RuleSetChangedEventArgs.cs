namespace SysMgr.Core.Rules;

/// <summary>规则集热更新事件参数（TDD §2.5）。</summary>
public sealed class RuleSetChangedEventArgs : EventArgs
{
    public RuleSetChangedEventArgs(RuleSetKind kind, RuleSetVersion oldVersion, RuleSetVersion newVersion)
    {
        Kind = kind;
        OldVersion = oldVersion;
        NewVersion = newVersion;
    }

    public RuleSetKind Kind { get; }
    public RuleSetVersion OldVersion { get; }
    public RuleSetVersion NewVersion { get; }
}
