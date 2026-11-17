using System.IO;

namespace SysMgr.Modules.Uninstall;

/// <summary>
/// 已安装软件条目（FR-UNINSTALL-01 软件清单展示）。
/// 长期未用采用复合判定（不依赖 NTFS Last Access Time，因其默认禁用不可靠）：
/// IsLongUnused = Prefetch 最后执行时间 + 注册表 InstallDate + 是否仍存在启动项，三项均无近 90 天活动即标记。
/// </summary>
public sealed record UninstallItem(
    Guid SoftwareId,
    string DisplayName,
    string? Publisher,
    string? DisplayVersion,
    DateTimeOffset? InstallDate,
    long? EstimatedSizeBytes,
    string? InstallLocation,
    string? UninstallString,
    SoftwareSource Source,
    bool IsSystemComponent,      // 系统组件默认折叠隐藏（FR-01）
    bool IsLongUnused,            // 长期未用标记（FR-01，阈值可配默认 90 天）
    DateTimeOffset? LastUsedTime, // Prefetch *.pf 最后写入时间
    bool HasStartupEntry,         // 是否仍存在启动项（FR-01 复合判定第 3 项）
    string? IconPath = null)      // 可用于提取图标的程序/图标文件（DisplayIcon 优先）
{
    // Name-based hints, deliberately limited to recognizable shared runtime products.
    public bool IsRuntimeLibrary =>
        (DisplayName.Contains("Visual C++", StringComparison.OrdinalIgnoreCase)
            && DisplayName.Contains("Redistributable", StringComparison.OrdinalIgnoreCase))
        || DisplayName.Contains(".NET Runtime", StringComparison.OrdinalIgnoreCase)
        || DisplayName.Contains(".NET Desktop Runtime", StringComparison.OrdinalIgnoreCase)
        || DisplayName.Contains("ASP.NET Core", StringComparison.OrdinalIgnoreCase)
        || DisplayName.Contains("WebView2 Runtime", StringComparison.OrdinalIgnoreCase)
        || DisplayName.Contains("Java(TM)", StringComparison.OrdinalIgnoreCase)
        || DisplayName.Contains("Java Runtime", StringComparison.OrdinalIgnoreCase);

    public string CategoryText => IsRuntimeLibrary ? "运行时库" : IsSystemComponent ? "系统组件" : "应用软件";

    /// <summary>
    /// 安装盘符（如 "C:"）。优先取 InstallLocation；缺失时退化为从 UninstallString 解析，
    /// 两者都没有（如部分 Store 应用）返回 null。
    /// </summary>
    public string? InstallDrive =>
        ResolveDrive(InstallLocation) ?? ResolveDrive(UninstallString);

    /// <summary>列表展示用名称：把「 - 」分隔处断行，长名称更易读。</summary>
    public string DisplayNameLines => DisplayName.Replace(" - ", Environment.NewLine);

    /// <summary>可跳转的安装目录：优先 InstallLocation，退化为图标程序所在目录。</summary>
    public string? OpenableFolder
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(InstallLocation) && Directory.Exists(InstallLocation))
            {
                return InstallLocation;
            }

            if (!string.IsNullOrWhiteSpace(IconPath)
                && Path.GetDirectoryName(IconPath) is { } iconFolder
                && Directory.Exists(iconFolder))
            {
                return iconFolder;
            }

            return null;
        }
    }

    /// <summary>是否可打开安装目录（无目录时按钮置灰）。</summary>
    public bool CanOpenFolder => OpenableFolder is not null;

    /// <summary>列表展示文本：「C 盘」/「未知」。</summary>
    public string InstallDriveText => InstallDrive is { } drive
        ? $"{drive.TrimEnd(':')} 盘"
        : "未知";

    /// <summary>
    /// 使用频率（与「长期未用」同口径，阈值 90 天）：今天 / 三个月内 / 超过三个月，
    /// 无 Prefetch 记录时为「—」（不代表没使用过）。
    /// </summary>
    public string UsageFrequencyText => UsageRank switch
    {
        0 => "今天",
        1 => "三个月内",
        2 => "超过三个月",
        _ => "—"
    };

    /// <summary>使用频率排序键：0 今天、1 三个月内、2 超过三个月、3 未知。</summary>
    public int UsageRank
    {
        get
        {
            if (LastUsedTime is not { } lastUsed)
            {
                return 3;
            }

            var days = (DateTimeOffset.Now - lastUsed).TotalDays;
            if (days < 1)
            {
                return 0;
            }

            return days <= 90 ? 1 : 2;
        }
    }

    private static string? ResolveDrive(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        string value;
        try
        {
            value = Environment.ExpandEnvironmentVariables(rawPath.Trim().Trim('"'));
        }
        catch (ArgumentException)
        {
            return null;
        }

        // UninstallString 可能带参数或引号，取第一个「盘符:\」/「盘符:/」形态的前缀
        for (var i = 0; i + 2 < value.Length; i++)
        {
            if (char.IsLetter(value[i])
                && value[i + 1] == ':'
                && (value[i + 2] == '\\' || value[i + 2] == '/'))
            {
                return $"{char.ToUpperInvariant(value[i])}:";
            }
        }

        return null;
    }
}
