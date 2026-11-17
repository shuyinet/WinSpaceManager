namespace SysMgr.Core.Rules;

/// <summary>
/// 规则集管理器（横切层，TDD §2.5）。
/// 职责：所有外置规则（安全等级路径表、弹窗规则、软件缓存路径表、CLI YAML 规则集）统一加载/热更新/版本管理。
/// 热更新：规则集文件变更后触发 RuleSetChanged 事件，模块订阅刷新内存索引；
/// 运行中扫描使用旧版本完成（避免半路换规则导致结果不一致），下一次扫描使用新版本。
/// 仅 PopupRuleUpdate 允许联网更新（§2.4 白名单第 2 项）。
/// </summary>
public interface IRuleSetManager
{
    Task<TRuleSet> LoadAsync<TRuleSet>(CancellationToken ct) where TRuleSet : IRuleSet;

    /// <summary>检查规则集版本更新（仅 PopupRuleUpdate scope 走联网，其余本地比对版本号）。</summary>
    Task<RuleSetVersion> CheckUpdateAsync(RuleSetKind kind, CancellationToken ct);

    /// <summary>规则集热更新通知。</summary>
    event EventHandler<RuleSetChangedEventArgs>? RuleSetChanged;
}
