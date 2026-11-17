namespace SysMgr.Core.Safety;

/// <summary>扫描结果项类型（TDD §2.3）。</summary>
public enum ItemType
{
    File,
    Registry,
    Service,
    ScheduledTask
}

/// <summary>
/// 扫描结果项基类（TDD §2.3 / §3.4）。所有扫描模块返回 IAsyncEnumerable&lt;ScannedItem&gt;。
/// 子类携带各自特有字段；ISafetyClassifier.Classify 接收基类引用。
/// </summary>
public abstract record ScannedItem(string Path, ItemType Type);

/// <summary>文件项（含大小，用于回滚策略判定与空间统计）。</summary>
public record FileItem(string Path, long Size) : ScannedItem(Path, ItemType.File);

/// <summary>注册表项（ValueName=null 表示整个键）。</summary>
public record RegistryItem(string KeyPath, string? ValueName) : ScannedItem(KeyPath, ItemType.Registry);

/// <summary>服务项。</summary>
public record ServiceItem(string ServiceName) : ScannedItem(ServiceName, ItemType.Service);

/// <summary>计划任务项。</summary>
public record ScheduledTaskItem(string TaskPath) : ScannedItem(TaskPath, ItemType.ScheduledTask);
