using SysMgr.Core.SystemAccess;
using SysMgr.Infra.Native;

namespace SysMgr.Infra;

/// <summary>Known Folder 实现（IKnownFolder 端口）。</summary>
public sealed class KnownFolderFacade : IKnownFolder
{
    /// <inheritdoc />
    public string? GetPath(Guid folderId) => Shell32Native.GetKnownFolderPath(folderId);

    /// <inheritdoc />
    public void SetPath(Guid folderId, string path)
        => Shell32Native.SetKnownFolderPath(folderId, path);
}
