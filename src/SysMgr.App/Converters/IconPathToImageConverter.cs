using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SysMgr.App.Converters;

/// <summary>
/// 软件图标路径（exe/ico/dll）→ 32×32 图标图像；解析失败返回 null，界面不显示图标。
/// 结果按路径缓存，避免 DataGrid 滚动/刷新时重复调用 Shell。
/// </summary>
public sealed class IconPathToImageConverter : IValueConverter
{
    private const uint ShgfiIcon = 0x0000_0100;
    private const uint ShgfiLargeIcon = 0x0000_0000;

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Load(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>提取文件关联图标（含可执行文件的嵌入图标）。</summary>
    public static ImageSource? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var cached))
            {
                return cached;
            }
        }

        ImageSource? image = null;
        try
        {
            if (File.Exists(path))
            {
                var info = new ShFileInfo();
                var result = SHGetFileInfo(
                    path,
                    0,
                    ref info,
                    (uint)Marshal.SizeOf<ShFileInfo>(),
                    ShgfiIcon | ShgfiLargeIcon);
                if (result != IntPtr.Zero && info.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        var source = Imaging.CreateBitmapSourceFromHIcon(
                            info.hIcon,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        source.Freeze();
                        image = source;
                    }
                    finally
                    {
                        DestroyIcon(info.hIcon);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            image = null;
        }

        lock (Gate)
        {
            Cache[path] = image;
        }

        return image;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref ShFileInfo psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
