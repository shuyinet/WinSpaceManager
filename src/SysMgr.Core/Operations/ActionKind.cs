namespace SysMgr.Core.Operations;

/// <summary>
/// 操作动作类型（TDD §4.2 ActionKind 枚举，V1 详设批次 1 扩展）。
/// 除 Shred 外所有 Kind 的 OperationPlan.PreviewRequired 强制为 true（TRUST-02 预演硬约束）。
/// Shred / KillProcess / RunExternalUninstaller 不可回滚（PathRecordOnly 策略，仅记录）。
/// </summary>
public enum ActionKind
{
    DeleteFile,
    MoveFile,
    DeleteRegistry,
    SetRegistry,
    StopService,
    DisableService,
    DisableStartup,
    DeleteScheduledTask,
    Shred,
    KillProcess,              // FR-UNINSTALL-02 强制结束进程及其子进程
    RunExternalUninstaller,   // FR-UNINSTALL-02 调用软件自身 UninstallString
    CreateJunction,           // FR-MIGRATE-01 在源位置创建指向新路径的目录联接
    RedirectKnownFolder,      // FR-CSLIM-08 经 SHSetKnownFolderPath 改写默认保存位置
    CopyFile,                 // FR-MIGRATE-01 仅复制副本并校验，保留源文件
    SetStartupEnabled,        // FR-STARTUP-01 写 StartupApproved REG_BINARY 启用/禁用启动项
    SetScheduledTaskEnabled,  // FR-STARTUP-01 启用/禁用计划任务（保留定义，不删除）
    Purge                     // FR-CLEAN-01/02 清理可重建垃圾：直接删除，不生成文件快照（不可回滚）
}
