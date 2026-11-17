using System.ComponentModel;
using System.Diagnostics;
using SysMgr.Core.SystemAccess;

namespace SysMgr.Infra;

/// <summary>
/// 进程查询实现（IProcessQuery 端口）。
/// </summary>
public sealed class ProcessQueryFacade : IProcessQuery
{
    /// <inheritdoc />
    public IReadOnlyCollection<ProcessInfo> Enumerate()
    {
        return Process.GetProcesses()
            .Select(p =>
            {
                string? path = null;
                try
                {
                    path = p.MainModule?.FileName;
                }
                catch (Win32Exception)
                {
                    // 系统进程无权限读取模块
                }
                catch (InvalidOperationException)
                {
                    // 枚举期间进程已退出
                }
                return new ProcessInfo(p.Id, p.ProcessName, path);
            })
            .ToList();
    }

    /// <inheritdoc />
    public void Kill(int processId, bool entireProcessTree)
    {
        using var process = Process.GetProcessById(processId);
        process.Kill(entireProcessTree);
    }
}
