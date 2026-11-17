using System.Runtime.InteropServices;

namespace SysMgr.Infra.Native;

/// <summary>
/// Shell32.dll P/Invoke 与公开包装（回收站 + Known Folder，TDD §6.4）。
/// </summary>
public static class Shell32Native
{
    private const uint SherbNoConfirmation = 0x00000001;
    private const uint SherbNoProgressUi = 0x00000002;
    private const uint SherbNoSound = 0x00000004;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(
        IntPtr hwnd,
        string? pszRootPath,
        uint dwFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(
        string? pszRootPath,
        ref SHQUERYRBINFO pSHQueryRBInfo);

    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    private struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr pszPath);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHSetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath);

    /// <summary>清空全部回收站（无确认框/进度/声音）；rootPath=null 表示全部盘。</summary>
    public static void EmptyRecycleBin(string? rootPath = null)
    {
        var hr = SHEmptyRecycleBin(IntPtr.Zero, rootPath,
                                   SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);
        // HRESULT_FACILITY_WIN32：0x8007xxxx；回收站为空时返回 ERROR_FILE_NOT_FOUND
        const int ErrorFileNotFound = unchecked((int)0x80070002);
        if (hr != 0 && hr != ErrorFileNotFound)
        {
            throw Marshal.GetExceptionForHR(hr)!;
        }
    }

    /// <summary>查询回收站总大小（字节）与项数。</summary>
    public static (long SizeBytes, long ItemCount) QueryRecycleBin()
    {
        var info = new SHQUERYRBINFO
        {
            cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>()
        };
        var hr = SHQueryRecycleBin(null, ref info);
        if (hr != 0)
        {
            throw Marshal.GetExceptionForHR(hr)!;
        }
        return (info.i64Size, info.i64NumItems);
    }

    /// <summary>读取 Known Folder 路径；系统 API 失败返回 null。</summary>
    public static string? GetKnownFolderPath(Guid folderId)
    {
        var hr = SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out var pathPtr);
        if (hr != 0)
        {
            return null;
        }
        try
        {
            return Marshal.PtrToStringUni(pathPtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    /// <summary>改写 Known Folder 路径。</summary>
    public static void SetKnownFolderPath(Guid folderId, string path)
    {
        var hr = SHSetKnownFolderPath(folderId, 0, IntPtr.Zero, path);
        if (hr != 0)
        {
            throw Marshal.GetExceptionForHR(hr)!;
        }
    }
}
