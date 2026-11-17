namespace SysMgr.Core.Safety;

/// <summary>
/// 安全分级器实现（§3.0 全局统一口径）。
/// 判定顺序：①命中系统关键路径前缀 → Low；②路径形态明确为可重建的临时/缓存/孤儿注册表项 → High；
/// ③其余 → Medium。模块与 UI 不允许绕过此入口。
/// 关键路径前缀由组合层从 rules\safety_critical_paths.json 注入。
/// </summary>
public sealed class SafetyClassifier : ISafetyClassifier
{
    private readonly IReadOnlyList<string> _criticalPrefixes;

    /// <summary>High 判定的路径段关键词（小写比较）。</summary>
    private static readonly string[] ReclaimableSegments =
    [
        "temp", "tmp", "cache", "caches", "code cache", "gpucache", "thumbcache",
        "reportqueue", "reportarchive",
        // 应用/系统缓存（对标同类清理工具的「应用程序 / 缓存文件」）
        "package cache", "solutionpackages", "officefilecache", "inetcache",
        "deliveryoptimization", "fntcache", "fontcache", ".nuget",
        "addons", "wns", "qqguild", "minibrowser", "qqgametempest", "logs"
    ];

    public SafetyClassifier(IEnumerable<string> criticalPathPrefixes)
    {
        ArgumentNullException.ThrowIfNull(criticalPathPrefixes);
        _criticalPrefixes = NormalizePrefixes(criticalPathPrefixes);
    }

    /// <inheritdoc />
    public SafetyVerdict Classify(ScannedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var path = item.Path;

        // ① 系统关键路径
        foreach (var prefix in _criticalPrefixes)
        {
            if (IsPathOrUnder(path, prefix))
            {
                return new SafetyVerdict(
                    SafetyLevel.Low,
                    "位于系统关键路径，删除可能导致系统或其他软件异常",
                    [$"关键路径前缀：{prefix}"]);
            }
        }

        // ② 明确可重建形态
        if (item is FileItem)
        {
            if (LooksReclaimable(path))
            {
                return new SafetyVerdict(
                    SafetyLevel.High,
                    "临时或缓存文件，系统与软件可自动重建",
                    [path]);
            }
        }
        else if (item is RegistryItem)
        {
            // 调用方（Residual）仅在确认属目标软件残留时才送分类；
            // Uninstall/App Paths/Run 键形态均为可重建孤儿项
            return new SafetyVerdict(
                SafetyLevel.High,
                "软件私有注册表残留，重装软件或重新配置即可恢复",
                [path]);
        }

        // ③ 默认中风险，交由人工判断
        return new SafetyVerdict(
            SafetyLevel.Medium,
            "引用关系不完全确定，删除可能影响个别软件功能",
            [path]);
    }

    private static bool LooksReclaimable(string path)
    {
        var segments = path.Split(
            ['/', '\\'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var segment in segments)
        {
            if (Array.IndexOf(ReclaimableSegments, segment.ToLowerInvariant()) >= 0)
            {
                return true;
            }
        }
        // 文件名形态：thumbcache_*.db、*.dmp（WER/转储）
        var fileName = segments.Length > 0 ? segments[^1].ToLowerInvariant() : string.Empty;
        return fileName.StartsWith("thumbcache_", StringComparison.Ordinal)
               || fileName.EndsWith(".dmp", StringComparison.Ordinal);
    }

    /// <summary>path 是否等于 prefix 或位于 prefix 目录树内（大小写不敏感）。</summary>
    private static bool IsPathOrUnder(string path, string prefix)
    {
        var normalizedPath = EnsureTrailingSlash(path);
        var normalizedPrefix = EnsureTrailingSlash(prefix);
        return normalizedPath.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSlash(string value)
    {
        var trimmed = value.TrimEnd('/', '\\');
        return trimmed + Path.DirectorySeparatorChar;
    }

    private static IReadOnlyList<string> NormalizePrefixes(IEnumerable<string> prefixes)
    {
        return prefixes
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().TrimEnd('/', '\\'))
            .ToList();
    }
}
