using SysMgr.Core.Common;

namespace SysMgr.Modules.Shred.Errors;

/// <summary>文件粉碎模块异常基类。</summary>
public class ShredException : SysMgrException
{
    public ShredException(string message) : base(message) { }
    public ShredException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>目标位于系统关键路径，禁止粉碎（安全硬护栏）。</summary>
public sealed class ShredCriticalPathException : ShredException
{
    public ShredCriticalPathException(string path)
        : base($"目标位于系统关键路径，禁止粉碎：{path}") { }
}

/// <summary>目标为链接/挂载点，不支持粉碎。</summary>
public sealed class ShredNotSupportedException : ShredException
{
    public ShredNotSupportedException(string path)
        : base($"不支持粉碎链接或挂载点目标：{path}") { }
}

/// <summary>未选择任何可粉碎的文件。</summary>
public sealed class ShredEmptySelectionException : ShredException
{
    public ShredEmptySelectionException()
        : base("未选择任何文件或文件夹") { }
}

/// <summary>目标路径不存在。</summary>
public sealed class ShredTargetNotFoundException : ShredException
{
    public ShredTargetNotFoundException(string path)
        : base($"目标不存在：{path}") { }
}
