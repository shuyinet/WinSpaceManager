namespace SysMgr.Core.Common;

/// <summary>
/// sysmgr 统一异常基类（TDD §6.2）。所有 L4 失败转 SysMgrException，子类按场景。
/// 横切层与功能模块均抛此异常族，UI 层统一捕获并展示。
/// </summary>
public abstract class SysMgrException : Exception
{
    protected SysMgrException(string message) : base(message) { }

    protected SysMgrException(string message, Exception innerException) : base(message, innerException) { }
}
