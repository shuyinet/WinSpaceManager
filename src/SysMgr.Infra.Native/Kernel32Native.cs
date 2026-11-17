using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SysMgr.Infra.Native;

/// <summary>
/// Kernel32.dll P/Invoke 与目录联接（junction）支持（TDD §6.4：所有 [DllImport] 必须落在此命名空间）。
/// Junction 通过给空目录设置 IO_REPARSE_TAG_MOUNT_POINT 重解析点实现，无需管理员特权。
/// </summary>
public static class Kernel32Native
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    /// <summary>
    /// 创建目录联接。junctionPath 必须尚不存在（父目录存在）；targetDirectory 必须已存在。
    /// </summary>
    public static void CreateJunction(string junctionPath, string targetDirectory)
    {
        Directory.CreateDirectory(junctionPath);

        // NT 命名空间前缀是单反斜杠 "\??\"（等价于 \DosDevices\），写成 "\\??\" 会让联接无法被解析
        var substituteName = @"\??\" + Path.GetFullPath(targetDirectory).TrimEnd('\\');
        var printName = Path.GetFullPath(targetDirectory).TrimEnd('\\');

        var subBytes = Encoding.Unicode.GetBytes(substituteName);
        var printBytes = Encoding.Unicode.GetBytes(printName);

        // ByValArray 要求实例数组长度与 SizeConst 完全一致，否则 StructureToPtr 会抛
        // "length of an embedded array instance does not match the declared length in the layout"
        var pathBuffer = new byte[16 * 1024];
        Buffer.BlockCopy(subBytes, 0, pathBuffer, 0, subBytes.Length);
        // 子名 null 终止后的位置放打印名
        var printOffset = subBytes.Length + sizeof(char);
        Buffer.BlockCopy(printBytes, 0, pathBuffer, printOffset, printBytes.Length);

        var data = new ReparseDataBuffer
        {
            ReparseTag = IoReparseTagMountPoint,
            ReparseDataLength = (ushort)(8 + subBytes.Length + sizeof(char)
                                            + printBytes.Length + sizeof(char)),
            Reserved = 0,
            SubstituteNameOffset = 0,
            SubstituteNameLength = (ushort)subBytes.Length,
            PrintNameOffset = (ushort)printOffset,
            PrintNameLength = (ushort)printBytes.Length,
            PathBuffer = pathBuffer
        };

        // 缓冲区按结构体实际大小分配（供 StructureToPtr 写入），
        // 但交给 DeviceIoControl 的长度只到 ReparseDataLength 为止。
        var inBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<ReparseDataBuffer>());
        var inSize = 8 + data.ReparseDataLength;
        SafeFileHandle? handle = null;
        try
        {
            Marshal.StructureToPtr(data, inBuffer, false);

            handle = CreateFileW(
                junctionPath,
                GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw new IOException(
                    $"打开目录失败：{junctionPath}，Win32 错误 {Marshal.GetLastWin32Error()}");
            }

            if (!DeviceIoControl(handle, FsctlSetReparsePoint, inBuffer, inSize,
                                 IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                throw new IOException(
                    $"设置重解析点失败，Win32 错误 {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBuffer);
            handle?.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReparseDataBuffer
    {
        public uint ReparseTag;
        public ushort ReparseDataLength;
        public ushort Reserved;
        public ushort SubstituteNameOffset;
        public ushort SubstituteNameLength;
        public ushort PrintNameOffset;
        public ushort PrintNameLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16 * 1024)]
        public byte[] PathBuffer;
    }
}
