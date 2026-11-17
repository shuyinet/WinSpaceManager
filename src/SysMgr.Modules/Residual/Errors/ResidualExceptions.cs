using SysMgr.Core.Common;

namespace SysMgr.Modules.Residual.Errors;

/// <summary>残留模块异常基类（02-residual.md §6）。</summary>
public class ResidualException : SysMgrException
{
    public ResidualException(string message) : base(message) { }
    public ResidualException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>扫描目标无法构造有效关键词（02-residual.md §4.1，RES-001）。</summary>
public sealed class ResidualTargetInvalidException : ResidualException
{
    public ResidualTargetInvalidException(ResidualScanTarget target)
        : base($"无法识别该软件的特征，请补充关键词后重试：{target.DisplayName}") { }
}

/// <summary>未选择任何残留项即提交清除（RES-002）。</summary>
public sealed class ResidualItemsEmptyException : ResidualException
{
    public ResidualItemsEmptyException()
        : base("请先勾选要清除的残留项") { }
}

/// <summary>影子备份失败，对应项已跳过未删除（FR-RESIDUAL-02）。</summary>
public sealed class ShadowBackupFailedException : ResidualException
{
    /// <summary>备份失败条目数。</summary>
    public int FailedCount { get; }

    public ShadowBackupFailedException(int failedCount)
        : base($"{failedCount} 项备份失败已跳过，未删除以保护可恢复性")
    {
        FailedCount = failedCount;
    }
}

/// <summary>服务停止失败（FR-RESIDUAL-01 服务维度）。</summary>
public sealed class ServiceStopFailedException : ResidualException
{
    public ServiceStopFailedException(string serviceName, Exception innerException)
        : base($"服务停止失败（可能被其他程序使用），已跳过：{serviceName}", innerException) { }
}

/// <summary>计划任务删除失败（FR-RESIDUAL-01 任务维度）。</summary>
public sealed class ScheduledTaskDeleteFailedException : ResidualException
{
    public ScheduledTaskDeleteFailedException(string taskPath, Exception innerException)
        : base($"计划任务删除失败，请以管理员身份重试：{taskPath}", innerException) { }
}

/// <summary>文件被占用导致删除失败（FR-RESIDUAL-02）。</summary>
public sealed class FileInUseException : ResidualException
{
    public FileInUseException(string path, Exception innerException)
        : base($"部分文件被占用已回滚，请关闭相关程序后重试：{path}", innerException) { }
}
