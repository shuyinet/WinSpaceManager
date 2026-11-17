using SysMgr.Core.Common;

namespace SysMgr.Modules.CSlim.Errors;

/// <summary>C 盘瘦身模块异常基类（03-cslim.md §6）。</summary>
public class CSlimException : SysMgrException
{
    public CSlimException(string message) : base(message) { }
    public CSlimException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>未选择任何项即提交（FR-CSLIM-02/05）。</summary>
public sealed class CslimItemsEmptyException : CSlimException
{
    public CslimItemsEmptyException()
        : base("请先选择要处理的项目") { }
}

/// <summary>迁移目标不是可写固定盘（FR-CSLIM-02）。</summary>
public sealed class InvalidMigrationTargetException : CSlimException
{
    public InvalidMigrationTargetException(string target)
        : base($"目标位置不可用，请选择本机其他硬盘上的文件夹：{target}") { }
}

/// <summary>目标盘剩余空间不足（FR-CSLIM-02 预检）。</summary>
public sealed class InsufficientDestinationSpaceException : CSlimException
{
    public InsufficientDestinationSpaceException()
        : base("目标盘空间不足，请清理后重试或选择其他盘") { }
}

/// <summary>Windows 残留删除失败（FR-CSLIM-05）。</summary>
public sealed class WindowsResidualDeleteFailedException : CSlimException
{
    public WindowsResidualDeleteFailedException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>Known Folder 保存位置改写失败（FR-CSLIM-08）。</summary>
public sealed class KnownFolderRedirectFailedException : CSlimException
{
    /// <summary>改写失败的类别。</summary>
    public SaveLocationCategory Category { get; }

    public KnownFolderRedirectFailedException(SaveLocationCategory category, Exception innerException)
        : base($"{category} 的保存位置修改失败，已保留原设置", innerException)
    {
        Category = category;
    }
}
