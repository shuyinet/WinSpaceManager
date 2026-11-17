namespace SysMgr.Core.SystemAccess;

/// <summary>
/// 目录联接（junction）管理端口（L4 抽象，FR-MIGRATE-01）。实现位于 SysMgr.Infra（reparse point）。
/// </summary>
public interface ILinkManager
{
    /// <summary>在 junctionPath 创建指向 targetDirectory 的目录联接（父目录须存在）。</summary>
    void CreateJunction(string junctionPath, string targetDirectory);

    /// <summary>路径是否存在（文件/目录/联接均可）。</summary>
    bool PathExists(string path);

    /// <summary>删除联接本身（不删除其指向的目标内容）。</summary>
    void DeleteLink(string path);
}
