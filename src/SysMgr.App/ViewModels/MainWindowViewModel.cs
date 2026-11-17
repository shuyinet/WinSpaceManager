using System.Windows;
using SysMgr.App.Updater;

namespace SysMgr.App.ViewModels;

/// <summary>主窗口 ViewModel：品牌信息 + TRUST-03 自我断网开关 + 自动更新提示。</summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly UpdateService _updater = new();
    private bool _isDisconnected;
    private bool _isToggling;
    private bool _updateCheckStarted;
    private bool _isUpdateVisible;
    private bool _isUpdateDownloading;
    private string _updateText = string.Empty;
    private double _updateProgress;

    public MainWindowViewModel(SysMgrServices services)
    {
        Services = services;
        _updater.StatusChanged += OnUpdateStatusChanged;
    }

    public SysMgrServices Services { get; }

    public bool IsDisconnected
    {
        get => _isDisconnected;
        private set => Set(ref _isDisconnected, value);
    }

    /// <summary>标题栏更新提示是否可见（下载中或已就绪）。</summary>
    public bool IsUpdateVisible
    {
        get => _isUpdateVisible;
        private set => Set(ref _isUpdateVisible, value);
    }

    /// <summary>是否正在后台下载（显示进度条）。</summary>
    public bool IsUpdateDownloading
    {
        get => _isUpdateDownloading;
        private set => Set(ref _isUpdateDownloading, value);
    }

    /// <summary>更新提示文案。</summary>
    public string UpdateText
    {
        get => _updateText;
        private set => Set(ref _updateText, value);
    }

    /// <summary>下载进度 0~1。</summary>
    public double UpdateProgress
    {
        get => _updateProgress;
        private set => Set(ref _updateProgress, value);
    }

    /// <summary>启动后触发一次新版本检查（自我断网时跳过）。</summary>
    public void StartUpdateCheck()
    {
        if (_updateCheckStarted)
        {
            return;
        }

        _updateCheckStarted = true;
        _ = CheckUpdatesAsync();
    }

    /// <summary>点击更新：安装包已就绪时运行并按装后重启。</summary>
    public void UpdateNow()
    {
        if (_updater.IsReady)
        {
            _updater.InstallAndRestart();
        }
    }

    /// <summary>
    /// 切换 TRUST-03 自我断网（添加/移除自身 Firewall 阻断规则 + 自检）。
    /// </summary>
    public async Task ToggleDisconnectAsync()
    {
        if (_isToggling)
        {
            return;
        }

        _isToggling = true;
        try
        {
            var verified = await Services.Trust.SelfTestDisconnectAsync(CancellationToken.None);
            IsDisconnected = verified;

            // 恢复联网后补一次更新检查
            if (!verified)
            {
                _ = CheckUpdatesAsync();
            }
        }
        catch
        {
            IsDisconnected = false;
            MessageBox.Show("防火墙规则切换失败，请确认以管理员身份运行。", "TRUST-03",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isToggling = false;
        }
    }

    private async Task CheckUpdatesAsync()
    {
        if (IsDisconnected)
        {
            return;
        }

        await _updater.CheckAndDownloadAsync();
    }

    private void OnUpdateStatusChanged(UpdateStatus status)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => OnUpdateStatusChanged(status));
            return;
        }

        switch (status.Phase)
        {
            case UpdatePhase.Downloading:
                UpdateText = status.Text;
                UpdateProgress = status.Progress;
                IsUpdateDownloading = true;
                IsUpdateVisible = true;
                break;

            case UpdatePhase.Ready:
                UpdateText = status.Text;
                UpdateProgress = 1;
                IsUpdateDownloading = false;
                IsUpdateVisible = true;
                break;

            default:
                // 检查中 / 已最新 / 失败：不打扰用户，保持隐藏
                IsUpdateDownloading = false;
                IsUpdateVisible = false;
                UpdateProgress = 0;
                break;
        }
    }
}
