namespace SysMgr.Core.SystemAccess;

/// <summary>
/// 进程查询端口（L4 抽象，TDD §6.3 ProcessFacade）。实现位于 SysMgr.Infra。
/// </summary>
public interface IProcessQuery
{
    /// <summary>枚举全部进程（含映像路径；权限不足的系统进程路径可能为 null）。</summary>
    IReadOnlyCollection<ProcessInfo> Enumerate();

    /// <summary>结束进程；entireProcessTree=true 时连带子进程。</summary>
    void Kill(int processId, bool entireProcessTree);
}

/// <summary>进程信息。</summary>
public sealed record ProcessInfo(int Id, string ProcessName, string? ExecutablePath);
