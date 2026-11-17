using SysMgr.Core.Common;

namespace SysMgr.Modules.Uninstall.Errors;

/// <summary>卸载模块异常基类（01-uninstall.md §6）。</summary>
public class UninstallException : SysMgrException
{
    public UninstallException(string message) : base(message) { }
    public UninstallException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>软件正在运行且未选择强制结束进程（FR-UNINSTALL-02）。</summary>
public sealed class SoftwareRunningException : UninstallException
{
    /// <summary>占用中的进程名。</summary>
    public IReadOnlyList<string> RunningProcesses { get; }

    public SoftwareRunningException(string installLocation, IReadOnlyList<string> runningProcesses)
        : base($"软件正在运行，请先关闭或开启强制结束进程：{installLocation}")
    {
        RunningProcesses = runningProcesses;
    }
}

/// <summary>标准模式下未找到可用卸载程序（FR-UNINSTALL-04 强制卸载入口）。</summary>
public sealed class UninstallerNotFoundException : UninstallException
{
    public UninstallerNotFoundException(string softwareName)
        : base($"未找到卸载程序，建议使用强制卸载：{softwareName}") { }
}

/// <summary>强制模式但软件自带卸载程序正常（FR-UNINSTALL-04）。</summary>
public sealed class UninstallerAvailableException : UninstallException
{
    public UninstallerAvailableException(string softwareName)
        : base($"该软件自带卸载程序，建议使用标准卸载：{softwareName}") { }
}

/// <summary>外部卸载程序超时未退出（FR-UNINSTALL-02）。</summary>
public sealed class ExternalUninstallerTimeoutException : UninstallException
{
    public ExternalUninstallerTimeoutException(string softwareName, TimeSpan timeout)
        : base($"卸载程序 {timeout.TotalSeconds:F0}s 无响应，可手动处理或转强制卸载：{softwareName}") { }
}

/// <summary>强制卸载的回滚快照创建失败（FR-UNINSTALL-04，中止以保护可恢复性）。</summary>
public sealed class ForceUninstallSnapshotException : UninstallException
{
    public ForceUninstallSnapshotException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>V1 不支持 UWP 应用卸载（V1.5 落地）。</summary>
public sealed class UwpNotSupportedV1Exception : UninstallException
{
    public UwpNotSupportedV1Exception()
        : base("UWP 应用卸载将在 V1.5 支持") { }
}
