namespace SysMgr.Core.SystemAccess;

/// <summary>
/// 回收站端口（L4 抽象）。实现位于 SysMgr.Infra（SHEmptyRecycleBin / SHQueryRecycleBin）。
/// </summary>
public interface IRecycleBin
{
    /// <summary>各盘回收站总大小（字节）。</summary>
    long GetTotalSizeBytes();

    /// <summary>清空全部回收站（不弹系统确认框）。</summary>
    void Empty();
}
