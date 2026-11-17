using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using SysMgr.App.Screenshot;

namespace SysMgr.App.ViewModels;

/// <summary>延迟选项。</summary>
public sealed record DelayOption(int Seconds, string Label);

/// <summary>最近截图条目（缩略图 + 文件名 + 时间）。</summary>
public sealed class ScreenshotThumb
{
    public ScreenshotThumb(string filePath, BitmapSource? image)
    {
        FilePath = filePath;
        FileName = System.IO.Path.GetFileName(filePath);
        TimeText = File.GetLastWriteTime(filePath).ToString("MM-dd HH:mm");
        Image = image;
    }

    public string FilePath { get; }
    public string FileName { get; }
    public string TimeText { get; }
    public BitmapSource? Image { get; }
}

/// <summary>
/// 截图页 ViewModel：区域 / 全屏 / 窗口三种截法，
/// 延迟、全局快捷键、保存目录设置，以及最近截图缩略图墙。
/// </summary>
public sealed class ScreenshotViewModel : ViewModelBase
{
    private const int MaxRecent = 18;

    private readonly ScreenshotService _service;
    private string _statusText;
    private bool _isCapturing;

    public ScreenshotViewModel(ScreenshotService service)
    {
        _service = service;
        _statusText = service.IsHotkeyActive
            ? "按 Ctrl + Alt + X 随时截取区域，或从上方选择一种截图方式"
            : "从上方选择一种截图方式开始";

        CaptureRegionCommand = new AsyncRelayCommand(
            _ => CaptureAsync(ScreenshotMode.Region), _ => !IsCapturing);
        CaptureFullScreenCommand = new AsyncRelayCommand(
            _ => CaptureAsync(ScreenshotMode.FullScreen), _ => !IsCapturing);
        CaptureWindowCommand = new AsyncRelayCommand(
            _ => CaptureAsync(ScreenshotMode.Window), _ => !IsCapturing);
        OpenSaveDirectoryCommand = new RelayCommand(_ => OpenSaveDirectory());
        RefreshRecentCommand = new RelayCommand(_ => LoadRecent());
        OpenRecentCommand = new RelayCommand(p => OpenRecent(p as ScreenshotThumb));

        LoadRecent();
    }

    private bool _attached;

    /// <summary>页面显示时订阅服务事件。</summary>
    public void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        _service.Saved += OnSaved;
        _service.Failed += OnFailed;
        LoadRecent();
    }

    /// <summary>页面离开时解除订阅，避免服务长期持有旧 ViewModel。</summary>
    public void Detach()
    {
        if (!_attached)
        {
            return;
        }

        _attached = false;
        _service.Saved -= OnSaved;
        _service.Failed -= OnFailed;
    }

    public IReadOnlyList<DelayOption> DelayOptions { get; } = new[]
    {
        new DelayOption(0, "无延迟"),
        new DelayOption(1, "1 秒"),
        new DelayOption(2, "2 秒"),
        new DelayOption(3, "3 秒"),
        new DelayOption(5, "5 秒")
    };

    public ObservableCollection<ScreenshotThumb> Recent { get; } = new();

    public AsyncRelayCommand CaptureRegionCommand { get; }
    public AsyncRelayCommand CaptureFullScreenCommand { get; }
    public AsyncRelayCommand CaptureWindowCommand { get; }
    public RelayCommand OpenSaveDirectoryCommand { get; }
    public RelayCommand RefreshRecentCommand { get; }
    public RelayCommand OpenRecentCommand { get; }

    /// <summary>操作状态提示。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>正在截图（用于禁用三个入口，避免重入）。</summary>
    public bool IsCapturing
    {
        get => _isCapturing;
        private set
        {
            if (!Set(ref _isCapturing, value))
            {
                return;
            }

            CaptureRegionCommand.RaiseCanExecuteChanged();
            CaptureFullScreenCommand.RaiseCanExecuteChanged();
            CaptureWindowCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>保存目录（不存在时回落默认目录）。</summary>
    public string SaveDirectory
    {
        get
        {
            var dir = _service.Settings.SaveDirectory;
            return string.IsNullOrWhiteSpace(dir) ? ScreenshotService.DefaultSaveDirectory : dir;
        }
    }

    /// <summary>延迟秒数（写入后立即持久化）。</summary>
    public int DelaySeconds
    {
        get => _service.Settings.DelaySeconds;
        set
        {
            if (_service.Settings.DelaySeconds == value)
            {
                return;
            }

            _ = _service.SetDelayAsync(value);
            OnPropertyChanged();
            StatusText = value <= 0
                ? "已关闭延迟，截图将立即开始"
                : $"已设置 {value} 秒延迟，方便先打开要截的界面";
        }
    }

    /// <summary>全局快捷键开关。</summary>
    public bool HotkeyEnabled
    {
        get => _service.Settings.EnableHotkey;
        set
        {
            if (_service.Settings.EnableHotkey == value)
            {
                return;
            }

            _ = ApplyHotkeyAsync(value);
        }
    }

    /// <summary>快捷键显示文本。</summary>
    public string HotkeyText => HotkeyEnabled ? "Ctrl + Alt + X" : "未启用";

    /// <summary>最近截图数量提示。</summary>
    public string RecentSummary => Recent.Count == 0
        ? "还没有保存过截图"
        : $"最近 {Recent.Count} 张截图";

    public bool HasRecent => Recent.Count > 0;

    private async Task ApplyHotkeyAsync(bool enabled)
    {
        var ok = await _service.SetHotkeyEnabledAsync(enabled);
        OnPropertyChanged(nameof(HotkeyEnabled));
        OnPropertyChanged(nameof(HotkeyText));
        StatusText = ok
            ? "快捷键已启用：Ctrl + Alt + X 直接区域截图"
            : enabled
                ? "快捷键注册失败，可能已被其他程序占用"
                : "快捷键已关闭";
    }

    /// <summary>更改保存目录并刷新最近截图。</summary>
    public async Task ChangeSaveDirectoryAsync(string directory)
    {
        await _service.SetSaveDirectoryAsync(directory);
        OnPropertyChanged(nameof(SaveDirectory));
        StatusText = $"截图将保存到：{directory}";
        LoadRecent();
    }

    private async Task CaptureAsync(ScreenshotMode mode)
    {
        IsCapturing = true;
        StatusText = mode switch
        {
            ScreenshotMode.Region => "区域截图：拖动鼠标框选范围，Esc 取消",
            ScreenshotMode.Window => "窗口截图：移动鼠标高亮窗口后点击，Esc 取消",
            _ => "全屏截图：正在抓取整个屏幕…"
        };

        try
        {
            var accepted = await _service.CaptureAsync(mode);
            StatusText = accepted
                ? "截图完成，可在编辑器里标注并保存（Ctrl+S）"
                : "已取消截图";
        }
        finally
        {
            IsCapturing = false;
        }
    }

    private void OnSaved(string path)
    {
        StatusText = $"已保存：{System.IO.Path.GetFileName(path)}";
        LoadRecent();
    }

    private void OnFailed(string message) => StatusText = message;

    /// <summary>读取保存目录下最新截图并生成缩略图。</summary>
    public void LoadRecent()
    {
        Recent.Clear();

        try
        {
            var dir = SaveDirectory;
            if (Directory.Exists(dir))
            {
                var files = Directory
                    .EnumerateFiles(dir, "*.png")
                    .Concat(Directory.EnumerateFiles(dir, "*.jpg"))
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(MaxRecent);

                foreach (var file in files)
                {
                    Recent.Add(new ScreenshotThumb(file.FullName, LoadThumb(file.FullName)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or ArgumentException)
        {
            StatusText = $"读取最近截图失败：{ex.Message}";
        }

        OnPropertyChanged(nameof(RecentSummary));
        OnPropertyChanged(nameof(HasRecent));
    }

    private static BitmapSource? LoadThumb(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 320;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or ArgumentException
                                       or FileFormatException)
        {
            return null;
        }
    }

    private void OpenSaveDirectory()
    {
        try
        {
            var dir = SaveDirectory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or ArgumentException)
        {
            StatusText = $"无法打开目录：{ex.Message}";
        }
    }

    private void OpenRecent(ScreenshotThumb? thumb)
    {
        if (thumb is null || !File.Exists(thumb.FilePath))
        {
            LoadRecent();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(thumb.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            StatusText = $"无法打开截图：{ex.Message}";
        }
    }
}
