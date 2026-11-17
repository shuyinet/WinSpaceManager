using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace SysMgr.App.Screenshot;

/// <summary>
/// 全局快捷键（RegisterHotKey）。单个实例只注册一个热键，释放时务必注销。
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x5357;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;

    private HwndSource? _source;
    private IntPtr _hwnd;

    public event Action? Pressed;

    /// <summary>当前是否已成功占用热键。</summary>
    public bool IsRegistered { get; private set; }

    /// <summary>注册 Ctrl+Alt+X 全局热键；被其他程序占用时返回 false。</summary>
    public bool Register(Window window)
    {
        Unregister();

        _hwnd = new WindowInteropHelper(window).Handle;
        if (_hwnd == IntPtr.Zero)
        {
            return false;
        }

        IsRegistered = RegisterHotKey(_hwnd, HotkeyId, ModControl | ModAlt | ModNoRepeat, 0x58);
        if (!IsRegistered)
        {
            return false;
        }

        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        return true;
    }

    public void Unregister()
    {
        if (IsRegistered)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            IsRegistered = false;
        }

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }

    public void Dispose() => Unregister();

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }

        return IntPtr.Zero;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
