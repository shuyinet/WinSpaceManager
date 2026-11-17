using SysMgr.Core.Config;

namespace SysMgr.App.ViewModels;

/// <summary>应用级设置（持久化到 config.json 的 "app" 节）。</summary>
public sealed record AppSettingsData(
    string Theme = "Dark",
    int LargeFileThresholdMb = 100,
    int RollbackRetentionDays = 30,
    bool TelemetryEnabled = false,
    string ExcludePaths = "");

/// <summary>设置页 ViewModel：加载/保存全局偏好与帮助信息。</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private const string Section = "app";
    private readonly IConfigStore _config;

    private int _largeFileThresholdMb = 100;
    private int _rollbackRetentionDays = 30;
    private bool _telemetryEnabled;
    private string _excludePaths = string.Empty;
    private string _statusText = "更改后点击保存";

    public SettingsViewModel(SysMgrServices services)
    {
        _config = services.Config;
        SaveCommand = new RelayCommand(_ => Save(), _ => true);
    }

    public int LargeFileThresholdMb
    {
        get => _largeFileThresholdMb;
        set => Set(ref _largeFileThresholdMb, value);
    }

    public int RollbackRetentionDays
    {
        get => _rollbackRetentionDays;
        set => Set(ref _rollbackRetentionDays, value);
    }

    public bool TelemetryEnabled
    {
        get => _telemetryEnabled;
        set => Set(ref _telemetryEnabled, value);
    }

    public string ExcludePaths
    {
        get => _excludePaths;
        set => Set(ref _excludePaths, value);
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public RelayCommand SaveCommand { get; }

    /// <summary>从配置中心加载已保存的偏好。</summary>
    public async Task LoadAsync()
    {
        var saved = await _config.GetAsync<AppSettingsData>(Section, CancellationToken.None);
        if (saved is null)
        {
            return;
        }

        LargeFileThresholdMb = saved.LargeFileThresholdMb;
        RollbackRetentionDays = saved.RollbackRetentionDays;
        TelemetryEnabled = saved.TelemetryEnabled;
        ExcludePaths = saved.ExcludePaths ?? string.Empty;
    }

    private async void Save()
    {
        var data = new AppSettingsData(
            "Dark",
            Math.Clamp(LargeFileThresholdMb, 1, 1_000_000),
            Math.Clamp(RollbackRetentionDays, 1, 3650),
            TelemetryEnabled,
            NormalizeExcludes(ExcludePaths));

        await _config.UpdateAsync(Section, data, CancellationToken.None);
        ExcludePaths = data.ExcludePaths;
        StatusText = $"已保存 · {DateTime.Now:HH:mm:ss}";
    }

    private static string NormalizeExcludes(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var lines = raw
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join("\n", lines);
    }
}
