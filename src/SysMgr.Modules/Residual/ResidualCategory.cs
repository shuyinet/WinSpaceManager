namespace SysMgr.Modules.Residual;

/// <summary>
/// 残留项类别（FR-RESIDUAL-01 扫描维度）。
/// Startup 为扫描维度标记，落盘时按底层形态映射：注册表 Run 键值（Target=Run 键路径 + ValueName=值名）
/// 或启动文件夹快捷方式（Target=.lnk 路径，ValueName=null），清除时分别映射 DeleteRegistry / DeleteFile。
/// </summary>
public enum ResidualCategory
{
    Registry,
    File,
    Service,
    ScheduledTask,
    Startup
}
