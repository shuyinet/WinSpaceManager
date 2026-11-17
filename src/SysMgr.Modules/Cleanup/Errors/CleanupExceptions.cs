using SysMgr.Core.Common;

namespace SysMgr.Modules.Cleanup.Errors;

/// <summary>垃圾清理模块异常基类（04-cleanup.md §6）。</summary>
public class CleanupException : SysMgrException
{
    public CleanupException(string message) : base(message) { }
    public CleanupException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>未选择任何垃圾项即提交（FR-CLEAN-01/02）。</summary>
public sealed class JunkItemsEmptyException : CleanupException
{
    public JunkItemsEmptyException()
        : base("请先勾选要清理的项目") { }
}

/// <summary>勾选浏览器项且浏览器在运行、未选强制结束（FR-CLEAN-02）。</summary>
public sealed class BrowserRunningException : CleanupException
{
    /// <summary>运行中的浏览器进程名。</summary>
    public IReadOnlyList<string> RunningBrowsers { get; }

    public BrowserRunningException(IReadOnlyList<string> runningBrowsers)
        : base("浏览器正在运行，请先关闭或开启强制结束浏览器进程")
    {
        RunningBrowsers = runningBrowsers;
    }
}

/// <summary>回收站清空失败（FR-CLEAN-01）。</summary>
public sealed class RecycleBinEmptyFailedException : CleanupException
{
    public RecycleBinEmptyFailedException(Exception innerException)
        : base("回收站清空失败，请手动清空后重试", innerException) { }
}

/// <summary>浏览器数据被锁定（Firefox SQLite，FR-CLEAN-02）。</summary>
public sealed class BrowserProfileLockedException : CleanupException
{
    public BrowserProfileLockedException(string browser)
        : base($"浏览器数据被锁定，请确认浏览器（含后台进程）已完全退出：{browser}") { }
}

/// <summary>清理规则条目解析异常（04-cleanup.md §6）。</summary>
public sealed class CleanRuleResolveException : CleanupException
{
    public CleanRuleResolveException(string message)
        : base(message) { }
}
