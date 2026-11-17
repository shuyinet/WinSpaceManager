namespace SysMgr.Modules.Startup;

/// <summary>启动项来源类别（FR-STARTUP-01，requirements §3.8）。</summary>
public enum StartupItemKind
{
    /// <summary>注册表 Run 键（64 位，HKLM/HKCU）。</summary>
    RegistryRun,

    /// <summary>注册表 Run 键（32 位 WOW6432Node，HKLM）。</summary>
    RegistryRun32,

    /// <summary>当前用户启动文件夹 .lnk。</summary>
    StartupFolderUser,

    /// <summary>公用启动文件夹 .lnk。</summary>
    StartupFolderCommon,

    /// <summary>计划任务登录触发项（仅禁用/启用，删除属 FR-SERVICE-02）。</summary>
    ScheduledTaskLogon,

    /// <summary>UWP 自启动项（仅禁用/启用，经 StartupApproved\StartupTask）。</summary>
    UwpStartupTask
}

/// <summary>开机耗时影响标注（启发式：目标 exe 大小分档）。</summary>
public enum StartupImpact
{
    /// <summary>无法判定（目标无法解析/计划任务无命令行/UWP 项）。</summary>
    Unknown,

    /// <summary>目标 exe ≤10MB。</summary>
    Low,

    /// <summary>目标 exe 10~100MB。</summary>
    Medium,

    /// <summary>目标 exe &gt;100MB。</summary>
    High
}

/// <summary>
/// 启动项（FR-STARTUP-01 扫描结果）。
/// Source：注册表项=Run 键路径；文件夹项=.lnk 完整路径；计划任务=任务路径；UWP=StartupApproved 键路径。
/// ValueName：注册表/UWP 项的值名；其余为 null。
/// </summary>
public sealed record StartupItem(
    string Id,
    string Name,
    string? Command,
    StartupItemKind Kind,
    string Source,
    string? ValueName,
    bool IsEnabled,
    StartupImpact Impact,
    bool CanDelete,
    bool CanEdit);
