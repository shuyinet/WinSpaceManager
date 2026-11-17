namespace SysMgr.Core.Rules;

/// <summary>
/// 规则集类别（TDD §2.5 规则集清单）：
/// <list type="bullet">
/// <item>SafetyCriticalPaths: rules\safety_critical_paths.json（§3.0 系统关键路径白名单，V1）</item>
/// <item>CleanupTargets: rules\cleanup_targets.json（§3.6 垃圾清理目标路径，V1）</item>
/// <item>UninstallVendors: rules\uninstall_vendors.json（§3.2 软件厂商特征映射，V1）</item>
/// <item>PopupRules: rules\popup_rules.json（§3.9 弹窗拦截规则，V2）</item>
/// <item>CliYamlRules: rules\*.yaml（§3.15.6 CLI-03 配置即代码，V1.5）</item>
/// </list>
/// </summary>
public enum RuleSetKind
{
    SafetyCriticalPaths,
    CleanupTargets,
    UninstallVendors,
    PopupRules,
    CliYamlRules
}
