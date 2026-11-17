using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SysMgr.App.Screenshot;

/// <summary>
/// 屏幕 / 窗口捕获底层（纯 P/Invoke + WPF 位图，不依赖 System.Drawing）。
/// 坐标一律按物理像素处理；调用方负责把 DIP 选区换算成物理像素。
/// </summary>
public static class ScreenCapture
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const uint GA_ROOT = 2;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>虚拟屏幕物理像素范围（多显示器合并后的总范围）。</summary>
    public static Int32Rect VirtualScreenBounds()
    {
        var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        return new Int32Rect(x, y, Math.Max(w, 1), Math.Max(h, 1));
    }

    /// <summary>截取整个虚拟屏幕（物理像素）。</summary>
    public static BitmapSource CaptureScreen()
    {
        var bounds = VirtualScreenBounds();
        return CaptureScreenRegion(new Int32Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
    }

    /// <summary>按物理像素坐标截取屏幕区域。</summary>
    public static BitmapSource CaptureScreenRegion(Int32Rect region)
    {
        var width = Math.Max(region.Width, 1);
        var height = Math.Max(region.Height, 1);

        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var previous = SelectObject(memoryDc, bitmap);

        try
        {
            BitBlt(memoryDc, 0, 0, width, height, screenDc, region.X, region.Y, SRCCOPY);
            return ToBitmapSource(bitmap);
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// 截取指定窗口。优先 PrintWindow（可截被遮挡窗口），失败或全黑时回退到屏幕区域。
    /// windowRect 为屏幕物理像素坐标。
    /// </summary>
    public static BitmapSource CaptureWindow(IntPtr hwnd, Int32Rect windowRect)
    {
        var width = Math.Max(windowRect.Width, 1);
        var height = Math.Max(windowRect.Height, 1);

        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var previous = SelectObject(memoryDc, bitmap);

        try
        {
            var ok = PrintWindow(hwnd, memoryDc, PW_RENDERFULLCONTENT);
            var result = ToBitmapSource(bitmap);
            if (ok && !IsNearlyBlank(result))
            {
                return result;
            }
        }
        catch (Exception)
        {
            // PrintWindow 对部分 UWP / 受保护窗口会失败，走屏幕回退
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        return CaptureScreenRegion(windowRect);
    }

    /// <summary>屏幕点（物理像素）命中的顶层窗口。</summary>
    public static IntPtr WindowFromScreenPoint(int x, int y)
    {
        var hwnd = WindowFromPoint(new POINT { X = x, Y = y });
        return hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GA_ROOT);
    }

    /// <summary>当前鼠标的屏幕物理像素坐标（覆盖层里比 PointToScreen 更可靠）。</summary>
    public static (int X, int Y) GetCursorPosition()
        => GetCursorPos(out var point) ? (point.X, point.Y) : (0, 0);

    /// <summary>窗口在屏幕上的物理像素范围（优先 DWM 真实边界，去掉阴影）。</summary>
    public static Int32Rect GetWindowRect(IntPtr hwnd)
    {
        try
        {
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var dwm, Marshal.SizeOf<RECT>()) == 0
                && dwm.Width > 0 && dwm.Height > 0)
            {
                return new Int32Rect(dwm.Left, dwm.Top, dwm.Width, dwm.Height);
            }
        }
        catch (DllNotFoundException)
        {
            // 老系统没有 dwmapi，忽略
        }

        return GetWindowRectNative(hwnd, out var rect)
            ? new Int32Rect(rect.Left, rect.Top, rect.Width, rect.Height)
            : Int32Rect.Empty;
    }

    /// <summary>枚举当前可见、有标题、非最小化的顶层窗口。</summary>
    public static IReadOnlyList<IntPtr> EnumerateTopLevelWindows()
    {
        var result = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || GetWindowTextLength(hwnd) == 0)
            {
                return true;
            }

            var rect = GetWindowRect(hwnd);
            if (rect.Width < 40 || rect.Height < 40)
            {
                return true;
            }

            result.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>窗口标题（用于页面提示）。</summary>
    public static string GetWindowTitle(IntPtr hwnd)
    {
        var buffer = new StringBuilder(512);
        var length = GetWindowText(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }

    /// <summary>从已截好的整屏位图中裁剪指定物理像素区域。</summary>
    public static BitmapSource Crop(BitmapSource source, Int32Rect region)
    {
        var x = Math.Clamp(region.X, 0, Math.Max(source.PixelWidth - 1, 0));
        var y = Math.Clamp(region.Y, 0, Math.Max(source.PixelHeight - 1, 0));
        var w = Math.Clamp(region.Width, 1, source.PixelWidth - x);
        var h = Math.Clamp(region.Height, 1, source.PixelHeight - y);
        var cropped = new CroppedBitmap(source, new Int32Rect(x, y, w, h));
        cropped.Freeze();
        return cropped;
    }

    /// <summary>放大镜：截取屏幕某点周围的小块并放大显示（区域选择时使用）。</summary>
    public static BitmapSource Magnify(BitmapSource screen, Int32Rect region, int zoom = 6)
    {
        var cropped = Crop(screen, region);
        var transformed = new TransformedBitmap(cropped, new ScaleTransform(zoom, zoom));
        transformed.Freeze();
        return transformed;
    }

    private static BitmapSource ToBitmapSource(IntPtr hBitmap)
    {
        var source = Imaging.CreateBitmapSourceFromHBitmap(
            hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

        // CreateBitmapSourceFromHBitmap 不接管句柄生命周期，复制一份后再释放由调用方负责
        var copy = new WriteableBitmap(source);
        copy.Freeze();
        return copy;
    }

    private static bool IsNearlyBlank(BitmapSource source)
    {
        try
        {
            var converted = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            var stride = converted.PixelWidth * 4;
            var buffer = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(buffer, stride, 0);

            // 每 400 个像素采样一次，全黑/全白视为截空
            var step = Math.Max(4, buffer.Length / 400 / 4 * 4);
            for (var i = 0; i + 3 < buffer.Length; i += step)
            {
                if (buffer[i] > 8 || buffer[i + 1] > 8 || buffer[i + 2] > 8)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool GetWindowRectWin32(IntPtr hwnd, out RECT rect)
        => GetWindowRectNative(hwnd, out rect);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(
        IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int srcX, int srcY, uint rop);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool GetWindowRectNative(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
