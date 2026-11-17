using SysMgr.Core.SystemAccess;
using SysMgr.Infra.Native;

namespace SysMgr.Infra;

/// <summary>目录联接管理实现（ILinkManager 端口）。</summary>
public sealed class LinkManager : ILinkManager
{
    /// <inheritdoc />
    public void CreateJunction(string junctionPath, string targetDirectory)
        => Kernel32Native.CreateJunction(junctionPath, targetDirectory);

    /// <inheritdoc />
    public bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <inheritdoc />
    public void DeleteLink(string path)
    {
        // Directory.Delete 对 reparse point 只删除联接本身，不触碰目标内容
        if (Directory.Exists(path))
        {
            Directory.Delete(path);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
