using SysMgr.Core.Logging;
using SysMgr.Core.Operations;
using SysMgr.Core.Queue;
using SysMgr.Core.Safety;
using SysMgr.Core.SystemAccess;
using SysMgr.Modules.Internal;
using SysMgr.Modules.Residual.Errors;

namespace SysMgr.Modules.Residual;

/// <summary>
/// 残留清除模块实现（IResidualService，FR-RESIDUAL-01/02/03）。
/// 五维度扫描，候选项统一经 ISafetyClassifier 打级；清除经 TRUST-02 预演 → OpQueue。
/// </summary>
public sealed class ResidualService : IResidualService
{
    private const int MinKeywordLength = 3;
    private const int DirSizeTimeoutMs = 5_000;
    private const int ClsidScanTimeoutMs = 10_000;

    /// <summary>关键词停用词（与 rules/residual_stopwords.json 同源的 V1 内置基准）。</summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Update", "Setup", "Microsoft", "Runtime", "Tools", "Installer", "Patch",
        "Driver", "Windows", "Component", "Helper", "Service", "Application",
        "App", "Software", "System", "Install", "Uninstall"
    };

    private readonly IRegistry _registry;
    private readonly IServiceQuery _services;
    private readonly ITaskQuery _tasks;

    private IModuleContext? _ctx;

    public string ModuleId => "residual";
    public string DisplayName => "残留清除";
    public ModuleVersion Version => new(1, 0, "V1");

    public ResidualService(IRegistry registry, IServiceQuery services, ITaskQuery tasks)
    {
        _registry = registry;
        _services = services;
        _tasks = tasks;
    }

    /// <inheritdoc />
    public Task InitializeAsync(IModuleContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ModuleHealthReport> HealthCheckAsync(CancellationToken ct)
        => Task.FromResult(new ModuleHealthReport(
            ModuleId,
            ModuleHealthStatus.Ok,
            null,
            new Dictionary<string, string>
            {
                ["registry"] = "ok",
                ["service"] = "ok",
                ["task"] = "ok"
            }));

    /// <inheritdoc />
    public async IAsyncEnumerable<ResidualItem> ScanResidualAsync(
        ResidualScanTarget target,
        ResidualScanOptions options,
        IProgress<ScanProgress>? progress,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        var keywords = BuildKeywords(target, options);
        var categories = options.Categories ?? Enum.GetValues<ResidualCategory>();
        var scannedCount = 0;

        if (categories.Contains(ResidualCategory.Registry))
        {
            foreach (var item in ScanRegistry(target, keywords, ct))
            {
                scannedCount++;
                progress?.Report(new ScanProgress(
                    ScanPhase.Enumerating, scannedCount, null, null, item.Target));
                yield return item with { SoftwareId = target.SoftwareId };
            }
        }

        if (!ct.IsCancellationRequested && categories.Contains(ResidualCategory.File))
        {
            foreach (var item in ScanFiles(keywords, ct))
            {
                scannedCount++;
                progress?.Report(new ScanProgress(
                    ScanPhase.Enumerating, scannedCount, null, null, item.Target));
                yield return item with { SoftwareId = target.SoftwareId };
            }
        }

        if (!ct.IsCancellationRequested && categories.Contains(ResidualCategory.Service))
        {
            foreach (var item in ScanServices(keywords))
            {
                scannedCount++;
                yield return item with { SoftwareId = target.SoftwareId };
            }
        }

        if (!ct.IsCancellationRequested && categories.Contains(ResidualCategory.ScheduledTask))
        {
            foreach (var item in ScanTasks(keywords))
            {
                scannedCount++;
                yield return item with { SoftwareId = target.SoftwareId };
            }
        }

        progress?.Report(new ScanProgress(ScanPhase.Done, scannedCount, null, null, null));
    }

    /// <inheritdoc />
    public async Task<OperationResult> EnqueueCleanupAsync(
        IReadOnlyList<ResidualItem> items,
        ResidualCleanupOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("模块尚未初始化");

        if (items.Count == 0)
        {
            throw new ResidualItemsEmptyException();
        }

        if (options.DryRun)
        {
            // 仅扫描不删除：返回预览报告，不构造计划、不触达队列
            return new OperationResult(Guid.Empty, true, TimeSpan.Zero, null, null);
        }

        var actions = items.Select(BuildAction).ToList();
        // 详设 02 §FR-RESIDUAL-02：含任一 Low 项则整体 Low（High=0 < Low=2，数值上取大）
        var safety = items
            .Select(i => i.Verdict.Level)
            .Aggregate((a, b) => a > b ? a : b);

        var plan = new OperationPlan(
            Guid.NewGuid(),
            "residual",
            "FR-RESIDUAL-02",
            $"清除 {items.Count} 项软件残留",
            safety,
            actions,
            PreviewRequired: true,
            UserConfirmed: false);

        var preview = await ctx.Trust.PreviewAsync(plan, ct);
        if (!preview.Confirmed)
        {
            return new OperationResult(plan.PlanId, false, TimeSpan.Zero, "用户取消预演", null);
        }

        var confirmedPlan = plan with { UserConfirmed = true };
        return await ctx.OpQueue.EnqueueAsync(confirmedPlan, progress, ct);
    }

    // ---------------- 维度扫描 ----------------

    private IEnumerable<ResidualItem> ScanRegistry(
        ResidualScanTarget target,
        IReadOnlyList<string> keywords,
        CancellationToken ct)
    {
        // 1. Uninstall 孤儿键
        foreach (var hive in new[]
                 {
                     RegistryRoots.UninstallHklm,
                     RegistryRoots.UninstallHklmWow,
                     RegistryRoots.UninstallHkcu
                 })
        {
            foreach (var subName in SafeSubKeys(hive))
            {
                ct.ThrowIfCancellationRequested();
                var key = Path.Combine(hive, subName);
                var name = _registry.GetValue(key, "DisplayName");
                if (name is not null
                    && TryMatchWord(name, keywords, out var matched))
                {
                    yield return Emit(
                        ResidualCategory.Registry, key, null, null, matched,
                        new RegistryItem(key, null),
                        $"注册表残留：{name}");
                }
            }
        }

        // 2. App Paths
        foreach (var exeName in SafeSubKeys(RegistryRoots.AppPaths))
        {
            var bare = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? exeName[..^4]
                : exeName;
            if (TryMatchWord(bare, keywords, out var matched))
            {
                var key = Path.Combine(RegistryRoots.AppPaths, exeName);
                yield return Emit(
                    ResidualCategory.Registry, key, null, null, matched,
                    new RegistryItem(key, null),
                    $"App Paths：{exeName}");
            }
        }

        // 3. Run 启动项（注册表形态）
        foreach (var runKey in new[] { RegistryRoots.RunHkcu, RegistryRoots.RunHklm })
        {
            IReadOnlyCollection<RegistryValueInfo> values;
            try
            {
                values = _registry.EnumerateValues(runKey);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var value in values)
            {
                if (value.Data is not null
                    && TryMatchSubstring(value.Data, keywords, out var matched))
                {
                    yield return Emit(
                        ResidualCategory.Startup, runKey, value.Name, null, matched,
                        new RegistryItem(runKey, value.Name),
                        $"启动项：{value.Name}（{DescribeRunKey(runKey)}）");
                }
            }
        }

        // 4. CLSID / Shell Extensions（限时 10s 降级）
        var deadline = DateTime.UtcNow.AddMilliseconds(ClsidScanTimeoutMs);
        var clsidRoot = Path.Combine(RegistryRoots.Hklm, "SOFTWARE", "Classes", "CLSID");
        foreach (var clsidName in SafeSubKeys(clsidRoot))
        {
            if (DateTime.UtcNow > deadline)
            {
                break;
            }
            var clsidKey = Path.Combine(clsidRoot, clsidName);
            var inprocKey = Path.Combine(clsidKey, "InprocServer32");
            var inproc = SafeGetValue(inprocKey, null);
            if (inproc is null)
            {
                continue;
            }
            var locationHit = target.InstallLocation is not null
                && inproc.StartsWith(target.InstallLocation, StringComparison.OrdinalIgnoreCase);
            TryMatchSubstring(inproc, keywords, out var matched);
            if (locationHit || matched.Length > 0)
            {
                yield return Emit(
                    ResidualCategory.Registry, clsidKey, null, null,
                    locationHit ? target.InstallLocation! : matched,
                    new RegistryItem(clsidKey, null),
                    $"Shell 扩展：{clsidName}");
            }
        }
    }

    private IEnumerable<ResidualItem> ScanFiles(IReadOnlyList<string> keywords, CancellationToken ct)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetEnvironmentVariable("PUBLIC")
        };

        // 1. 六根目录顶层子目录
        foreach (var root in roots
                     .Where(r => !string.IsNullOrEmpty(r))
                     .Where(Directory.Exists)
                     .Select(r => r!))
        {
            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var dir in dirs)
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(dir);
                if (!TryMatchWord(name, keywords, out var matched))
                {
                    continue;
                }
                var size = FileUtilities.Measure(
                    dir, TimeSpan.FromMilliseconds(DirSizeTimeoutMs), out _);
                yield return Emit(
                    ResidualCategory.File, dir, null, size, matched,
                    new FileItem(dir, size),
                    $"目录残留：{name}");
            }
        }

        // 2. 开始菜单快捷方式（Startup 文件形态）
        var startMenuRoots = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                "Programs")
        };
        foreach (var menuRoot in startMenuRoots.Where(Directory.Exists))
        {
            foreach (var lnk in SafeEnumerateFiles(menuRoot, "*.lnk"))
            {
                var name = Path.GetFileNameWithoutExtension(lnk);
                if (TryMatchWord(name, keywords, out var matched))
                {
                    yield return Emit(
                        ResidualCategory.Startup, lnk, null, null, matched,
                        new FileItem(lnk, 0),
                        $"开始菜单快捷方式：{name}");
                }
            }
        }
    }

    private IEnumerable<ResidualItem> ScanServices(IReadOnlyList<string> keywords)
    {
        foreach (var service in _services.EnumerateServices())
        {
            var matched = (string?)null;
            if (TryMatchWord(service.Name, keywords, out var byName))
            {
                matched = byName;
            }
            else if (TryMatchWord(service.DisplayName, keywords, out var byDisplay))
            {
                matched = byDisplay;
            }
            else if (service.PathName is not null
                     && TryMatchSubstring(service.PathName, keywords, out var byPath))
            {
                matched = byPath;
            }

            if (matched is not null)
            {
                yield return Emit(
                    ResidualCategory.Service, service.Name, null, null, matched,
                    new ServiceItem(service.Name),
                    $"服务：{service.DisplayName}");
            }
        }
    }

    private IEnumerable<ResidualItem> ScanTasks(IReadOnlyList<string> keywords)
    {
        foreach (var task in _tasks.Enumerate())
        {
            var matched = (string?)null;
            if (TryMatchWord(task.Name, keywords, out var byName))
            {
                matched = byName;
            }
            else if (task.ExecutablePath is not null
                     && TryMatchSubstring(task.ExecutablePath, keywords, out var byPath))
            {
                matched = byPath;
            }

            if (matched is not null)
            {
                yield return Emit(
                    ResidualCategory.ScheduledTask, task.Path, null, null, matched,
                    new ScheduledTaskItem(task.Path),
                    $"计划任务：{task.Name}");
            }
        }
    }

    // ---------------- 关键词与匹配 ----------------

    private IReadOnlyList<string> BuildKeywords(
        ResidualScanTarget target,
        ResidualScanOptions options)
    {
        var keywords = new List<string> { target.DisplayName.Trim() };

        if (!string.IsNullOrWhiteSpace(target.Publisher))
        {
            keywords.Add(target.Publisher.Trim());
        }
        if (!string.IsNullOrWhiteSpace(target.InstallLocation))
        {
            keywords.Add(Path.GetFileName(
                target.InstallLocation.TrimEnd(Path.DirectorySeparatorChar)));
        }
        if (options.AdditionalKeywords is not null)
        {
            keywords.AddRange(options.AdditionalKeywords.Where(k => !string.IsNullOrWhiteSpace(k)));
        }

        var filtered = keywords
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(k => !Stopwords.Contains(k))
            .Where(k => k.Length >= MinKeywordLength)
            .OrderByDescending(k => k.Length)
            .ToList();

        if (filtered.Count == 0)
        {
            throw new ResidualTargetInvalidException(target);
        }
        return filtered;
    }

    private static bool TryMatchWord(
        string name,
        IReadOnlyList<string> keywords,
        out string matched)
    {
        foreach (var keyword in keywords)
        {
            if (MatchWordBoundary(name, keyword))
            {
                matched = keyword;
                return true;
            }
        }
        matched = string.Empty;
        return false;
    }

    private static bool TryMatchSubstring(
        string text,
        IReadOnlyList<string> keywords,
        out string matched)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                matched = keyword;
                return true;
            }
        }
        matched = string.Empty;
        return false;
    }

    private static bool MatchWordBoundary(string name, string keyword)
    {
        if (name.Equals(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (name.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
        {
            var next = name[keyword.Length];
            return next is ' ' or '_' or '-' or '.';
        }
        return false;
    }

    // ---------------- 输出与动作映射 ----------------

    private ResidualItem Emit(
        ResidualCategory category,
        string target,
        string? valueName,
        long? sizeBytes,
        string matchedBy,
        ScannedItem scanned,
        string description)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("模块尚未初始化");
        var verdict = ctx.Safety.Classify(scanned);
        return new ResidualItem(
            Guid.NewGuid(),
            Guid.Empty,
            category,
            target,
            valueName,
            verdict,
            sizeBytes,
            matchedBy,
            description);
    }

    private static OperationAction BuildAction(ResidualItem item) => item.Category switch
    {
        ResidualCategory.File => new OperationAction(
            ActionKind.DeleteFile, item.Target, null,
            item.EstimatedSizeBytes, null, null, null, null, null, null, null),

        ResidualCategory.Registry => new OperationAction(
            ActionKind.DeleteRegistry, null, null, null, null,
            item.Target, item.ValueName, null, null, null, null),

        ResidualCategory.Service => new OperationAction(
            ActionKind.StopService, null, null, null, null, null, null, null, null,
            item.Target, null),

        ResidualCategory.ScheduledTask => new OperationAction(
            ActionKind.DeleteScheduledTask, null, null, null, null, null, null, null, null, null,
            item.Target),

        ResidualCategory.Startup => item.ValueName is not null
            ? new OperationAction(
                ActionKind.DeleteRegistry, null, null, null, null,
                item.Target, item.ValueName, null, null, null, null)
            : new OperationAction(
                ActionKind.DeleteFile, item.Target, null, null, null, null, null, null, null, null, null),

        _ => throw new ArgumentOutOfRangeException(nameof(item))
    };

    // ---------------- 安全封装 ----------------

    private IReadOnlyCollection<string> SafeSubKeys(string key)
    {
        try
        {
            return _registry.EnumerateSubKeyNames(key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private string? SafeGetValue(string key, string? valueName)
    {
        try
        {
            return _registry.GetValue(key, valueName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            // 扫描期间键可能已不存在或无权限读取，按"无此值"降级
            return null;
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string DescribeRunKey(string runKey) => runKey switch
    {
        _ when runKey.Equals(RegistryRoots.RunHkcu, StringComparison.Ordinal) => "HKCU Run",
        _ when runKey.Equals(RegistryRoots.RunHklm, StringComparison.Ordinal) => "HKLM Run",
        _ => runKey
    };
}
