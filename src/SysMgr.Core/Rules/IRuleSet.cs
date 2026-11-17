namespace SysMgr.Core.Rules;

/// <summary>规则集标记接口（TDD §2.5）。每种外置规则实现此接口，由 IRuleSetManager 泛型加载。</summary>
public interface IRuleSet
{
    RuleSetKind Kind { get; }
    RuleSetVersion Version { get; }
}
