namespace SysMgr.Core.SystemAccess;

/// <summary>
/// 计划任务查询端口（L4 抽象，TDD §6.3 TaskSchedulerFacade）。实现位于 SysMgr.Infra。
/// </summary>
public interface ITaskQuery
{
    /// <summary>枚举全部计划任务（Path 为 "\Folder\TaskName" 形式）。</summary>
    IReadOnlyCollection<ScheduledTaskInfo> Enumerate();

    /// <summary>按任务路径删除任务。</summary>
    void DeleteTask(string taskPath);

    /// <summary>启用/禁用计划任务（FR-STARTUP-01，schtasks /change）。</summary>
    void SetTaskEnabled(string taskPath, bool enabled);
}

/// <summary>计划任务信息。Enabled 缺省视为启用；HasLogonTrigger 标记含登录触发器。</summary>
public sealed record ScheduledTaskInfo(
    string Path,
    string Name,
    string? ExecutablePath,
    bool Enabled = true,
    bool HasLogonTrigger = false);
