namespace SysMgr.Core.SystemAccess;

/// <summary>
/// Windows Known Folder 端口（L4 抽象，FR-CSLIM-08）。实现位于 SysMgr.Infra（SHGet/SetKnownFolderPath）。
/// FolderId 为系统 Known Folder GUID（如 FOLDERID_Documents）。
/// </summary>
public interface IKnownFolder
{
    /// <summary>读取 Known Folder 当前完整路径；不存在/未重定向时返回 null。</summary>
    string? GetPath(Guid folderId);

    /// <summary>改写 Known Folder 路径（系统负责建目录与后续重定向）。</summary>
    void SetPath(Guid folderId, string path);
}
