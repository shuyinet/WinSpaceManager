namespace SysMgr.Modules.Uninstall;

/// <summary>
/// 卸载选项（FR-UNINSTALL-02/04）。
/// Mode 决定走标准/静默/强制流程；KillRunningProcesses 对应 FR-02"强制结束进程并继续"开关（默认 false）。
/// </summary>
public sealed record UninstallOptions(
    UninstallMode Mode,
    bool KillRunningProcesses,    // FR-02 强制结束主进程及其子进程开关，默认 false
    string? CustomUninstallArgs); // 用户自定义卸载参数（静默模式可覆盖 SilentArgs）
