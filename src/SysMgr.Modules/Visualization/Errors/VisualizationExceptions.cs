using SysMgr.Core.Common;

namespace SysMgr.Modules.Visualization.Errors;

/// <summary>磁盘可视化模块异常基类。</summary>
public class VisualizationException : SysMgrException
{
    public VisualizationException(string message) : base(message) { }
    public VisualizationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>扫描目标不存在或不可访问。</summary>
public sealed class InvalidScanTargetException : VisualizationException
{
    public InvalidScanTargetException(string target)
        : base($"扫描目标不可用：{target}") { }
}

/// <summary>未选择任何项即提交（删除/迁移/导出）。</summary>
public sealed class VisItemsEmptyException : VisualizationException
{
    public VisItemsEmptyException()
        : base("请先选择要处理的项目") { }
}
