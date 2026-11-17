using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using SysMgr.Core.Config;

namespace SysMgr.App.Screenshot;

/// <summary>截图方式。</summary>
public enum ScreenshotMode
{
    Region,
    FullScreen,
    Window
}

/// <summary>截图设置（持久化到 config.json 的 screenshot 节）。</summary>
public sealed class ScreenshotSettings
{
    public int DelaySeconds { get; set; }
    public string SaveDirectory { get; set; } = string.Empty;
    public bool EnableHotkey { get; set; } = true;
}

/// <summary>
/// 截图流程编排：延迟 → 隐藏主窗口 → 抓屏 → 选区覆盖层 → 打开编辑器。
/// 同时负责全局快捷键 Ctrl+Alt+X 区域截图。
/// </summary>
public sealed class ScreenshotService
{
    private const string ConfigSection = "screenshot";

    private readonly IConfigStore _config;
    private readonly Window _owner;
    private readonly GlobalHotkey _hotkey = new();

    private bool _capturing;
    private bool _initialized;

    public ScreenshotService(IConfigStore config, Window owner)
    {
        _config = config;
        _owner = owner;
        _hotkey.Pressed += OnHotkeyPressed;
    }

    /// <summary>当前设置。</summary>
    public ScreenshotSettings Settings { get; private set; } = new();

    /// <summary>截图保存成功（参数为文件路径）。</summary>
    public event Action<string>? Saved;

    /// <summary>最近一次失败原因（供页面显示）。</summary>
    public event Action<string>? Failed;

    public bool IsHotkeyActive => _hotkey.IsRegistered;

    /// <summary>默认保存目录：图片\截图。</summary>
    public static string DefaultSaveDirectory
    {
        get
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            var root = string.IsNullOrWhiteSpace(pictures)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : pictures;
            return Path.Combine(root, "截图");
        }
    }

    /// <summary>读取设置并按需注册全局热键（应用启动时调用一次）。</summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            Settings = await _config.GetAsync<ScreenshotSettings>(ConfigSection, CancellationToken.None)
                       ?? new ScreenshotSettings();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            Settings = new ScreenshotSettings();
        }

        if (string.IsNullOrWhiteSpace(Settings.SaveDirectory))
        {
            Settings.SaveDirectory = DefaultSaveDirectory;
        }

        if (Settings.EnableHotkey)
        {
            Settings.EnableHotkey = _hotkey.Register(_owner);
        }
    }

    /// <summary>切换全局快捷键，返回是否生效。</summary>
    public async Task<bool> SetHotkeyEnabledAsync(bool enabled)
    {
        Settings.EnableHotkey = enabled && _hotkey.Register(_owner);
        if (!enabled)
        {
            _hotkey.Unregister();
        }

        await SaveSettingsAsync();
        return Settings.EnableHotkey;
    }

    /// <summary>更新保存目录并持久化。</summary>
    public async Task SetSaveDirectoryAsync(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Settings.SaveDirectory = directory;
        await SaveSettingsAsync();
    }

    /// <summary>更新延迟秒数并持久化。</summary>
    public async Task SetDelayAsync(int seconds)
    {
        Settings.DelaySeconds = Math.Clamp(seconds, 0, 10);
        await SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await _config.UpdateAsync(ConfigSection, Settings, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 设置写失败不影响截图本身
        }
    }

    /// <summary>
    /// 执行一次截图。返回 false 表示用户取消或出错。
    /// </summary>
    public async Task<bool> CaptureAsync(ScreenshotMode mode)
    {
        if (_capturing)
        {
            return false;
        }

        _capturing = true;
        try
        {
            if (Settings.DelaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(Settings.DelaySeconds));
            }

            var screen = await CaptureScreenWithOwnerHiddenAsync();

            if (mode == ScreenshotMode.FullScreen)
            {
                OpenEditor(screen);
                return true;
            }

            var overlay = new ScreenshotOverlayWindow(screen, mode == ScreenshotMode.Window);
            var accepted = overlay.ShowDialog() == true;

            if (!accepted)
            {
                return false;
            }

            if (mode == ScreenshotMode.Window)
            {
                var hwnd = overlay.SelectedWindow;
                if (hwnd == IntPtr.Zero)
                {
                    return false;
                }

                var bounds = ScreenCapture.GetWindowRect(hwnd);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    Failed?.Invoke("无法获取窗口位置，请改用区域截图");
                    return false;
                }

                OpenEditor(ScreenCapture.CaptureWindow(hwnd, bounds));
                return true;
            }

            if (overlay.Result is { } region)
            {
                OpenEditor(region);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Failed?.Invoke($"截图失败：{ex.Message}");
            return false;
        }
        finally
        {
            _capturing = false;
        }
    }

    private async Task<BitmapSource> CaptureScreenWithOwnerHiddenAsync()
    {
        var wasMinimized = _owner.WindowState == WindowState.Minimized;
        _owner.Hide();

        try
        {
            // 给系统一点时间重绘桌面，避免截到正在消失的窗口
            await Task.Delay(220);
            return ScreenCapture.CaptureScreen();
        }
        finally
        {
            _owner.Show();
            if (wasMinimized)
            {
                _owner.WindowState = WindowState.Minimized;
            }
        }
    }

    private void OpenEditor(BitmapSource bitmap)
    {
        var editor = new ScreenshotEditorWindow(bitmap, Settings.SaveDirectory);
        editor.Saved += path => Saved?.Invoke(path);
        editor.Show();
        // 主窗刚被 Show() 激活过，编辑器需要再抢一次前台，否则可能被压在下面
        editor.Topmost = true;
        editor.Topmost = false;
        editor.Activate();
    }

    private async void OnHotkeyPressed()
    {
        // 热键在应用最小化时也要能用：截图时临时显示主窗再隐藏
        await CaptureAsync(ScreenshotMode.Region);
    }
}
