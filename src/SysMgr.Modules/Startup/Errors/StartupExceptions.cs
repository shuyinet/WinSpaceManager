using SysMgr.Core.Common;

namespace SysMgr.Modules.Startup.Errors;

/// <summary>启动项管理模块异常基类。</summary>
public class StartupException : SysMgrException
{
    public StartupException(string message) : base(message) { }
    public StartupException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>该项不支持此操作（如计划任务/UWP 项不可删除或编辑命令行）。</summary>
public sealed class StartupNotSupportedException : StartupException
{
    public StartupNotSupportedException(string operation, string itemName)
        : base($"启动项不支持{operation}：{itemName}") { }
}

/// <summary>编辑命令行时提交了空白命令。</summary>
public sealed class StartupCommandEmptyException : StartupException
{
    public StartupCommandEmptyException()
        : base("命令行不能为空") { }
}
