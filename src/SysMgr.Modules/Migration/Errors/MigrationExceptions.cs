using SysMgr.Core.Common;

namespace SysMgr.Modules.Migration.Errors;

/// <summary>文件迁移模块异常基类（05-migration.md §6）。</summary>
public class MigrationException : SysMgrException
{
    public MigrationException(string message) : base(message) { }
    public MigrationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>迁移预检存在阻断项（FR-MIGRATE-01）。</summary>
public sealed class MigrationPrecheckFailedException : MigrationException
{
    /// <summary>阻断性问题清单。</summary>
    public IReadOnlyList<string> Issues { get; }

    public MigrationPrecheckFailedException(IReadOnlyList<string> issues)
        : base("迁移条件不满足：" + string.Join("；", issues))
    {
        Issues = issues;
    }
}

/// <summary>复制后 SHA-256 校验不一致（FR-MIGRATE-01）。</summary>
public sealed class MigrationVerifyFailedException : MigrationException
{
    public MigrationVerifyFailedException(string source)
        : base($"文件校验失败，已中止迁移以保证数据一致：{source}") { }
}

/// <summary>目录联接创建失败（FR-MIGRATE-01）。</summary>
public sealed class JunctionCreateFailedException : MigrationException
{
    public JunctionCreateFailedException(string path, Exception innerException)
        : base($"路径联接创建失败，已回滚迁移；文件副本保留在目标位置：{path}", innerException) { }
}

/// <summary>源文件删除被占用（非阻断，FR-MIGRATE-01）。</summary>
public sealed class MigrationSourceInUseException : MigrationException
{
    /// <summary>未能删除的条目数。</summary>
    public int InUseCount { get; }

    public MigrationSourceInUseException(int inUseCount)
        : base($"{inUseCount} 个文件被占用未能删除，关闭程序后可手动清理源目录")
    {
        InUseCount = inUseCount;
    }
}
