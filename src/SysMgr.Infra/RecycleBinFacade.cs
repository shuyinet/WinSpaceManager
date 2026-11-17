using SysMgr.Core.SystemAccess;
using SysMgr.Infra.Native;

namespace SysMgr.Infra;

/// <summary>回收站实现（IRecycleBin 端口）。</summary>
public sealed class RecycleBinFacade : IRecycleBin
{
    /// <inheritdoc />
    public long GetTotalSizeBytes() => Shell32Native.QueryRecycleBin().SizeBytes;

    /// <inheritdoc />
    public void Empty() => Shell32Native.EmptyRecycleBin();
}
