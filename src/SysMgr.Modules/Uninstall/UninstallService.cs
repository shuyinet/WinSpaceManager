using SysMgr.Core.Operations;
using SysMgr.Core.Queue;
using SysMgr.Core.Safety;
using SysMgr.Core.SystemAccess;
using SysMgr.Modules.Internal;
using SysMgr.Modules.Residual;
using SysMgr.Modules.Uninstall.Errors;

namespace SysMgr.Modules.Uninstall;

/// <summary>
/// 软件卸载模块实现（IUninstallService，FR-UNINSTALL-01/02/04）。
/// 扫描只依赖端口；卸载经 TRUST-02 预演 → OpQueue；成功后经协调器联动残留扫描。
/// </summary>
public sealed class UninstallService : IUninstallService
{
    private const int LongUnusedThresholdDays = 90;

    private readonly IRegistry _registry;
    private readonly IProcessQuery _processes;
    private readonly IServiceQuery _services;
    private readonly ITaskQuery _tasks;

    private IModuleContext? _ctx;

    public string ModuleId => "uninstall";
    public string DisplayName => "软件卸载";
    public ModuleVersion Version => new(1, 0, "V1");

    public UninstallService(
        IRegistry registry,
        IProcessQuery processes,
        IServiceQuery services,
        ITaskQuery tasks)
    {
        _registry = registry;
        _processes = processes;
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
                ["process"] = "ok"
            }));

    /// <inheritdoc />
    public async IAsyncEnumerable<UninstallItem> ScanInstalledAsync(
        IProgress<ScanProgress>? progress,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        var prefetch = LoadPrefetchIndex();
        var runValues = LoadRunValues();
        var scannedCount = 0;

        foreach (var (hive, source) in EnumerateHives())
        {
            foreach (var subName in SafeSubKeys(hive))
            {
                ct.ThrowIfCancellationRequested();
                var key = Path.Combine(hive, subName);
                var displayName = _registry.GetValue(key, "DisplayName");
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                var item = BuildItem(key, displayName, source, prefetch, runValues);
                if (item.EstimatedSizeBytes is null or <= 0)
                {
                    // 部分软件（搜狗输入法、360 系列等）不写 EstimatedSize，按安装目录实测兜底
                    item = await FillSizeFromDiskAsync(item, ct);
                }
                scannedCount++;
                progress?.Report(new ScanProgress(
                    ScanPhase.Enumerating, scannedCount, null, null, key));
                yield return item;
            }
        }

        progress?.Report(new ScanProgress(ScanPhase.Done, scannedCount, null, null, null));
    }

    /// <inheritdoc />
    public Task<UninstallCommand> GetUninstallCommandAsync(UninstallItem item, CancellationToken ct)
    {
        var uninstallKey = FindUninstallKey(item.DisplayName);

        var (type, silentArgs) = ClassifyUninstaller(item.UninstallString);
        var canForce = type == UninstallerType.None;

        var relatedKeys = CollectRelatedRegistryKeys(item, uninstallKey);
        var relatedServices = CollectRelatedServices(item.InstallLocation);

        return Task.FromResult(new UninstallCommand(
            item.SoftwareId,
            item.UninstallString,
            type,
            type != UninstallerType.BasicExe && type != UninstallerType.None && type != UninstallerType.Unknown,
            silentArgs,
            canForce,
            item.InstallLocation,
            relatedKeys,
            relatedServices,
            CollectRelatedTasks(item.InstallLocation)));
    }

    /// <inheritdoc />
    public async Task<OperationResult> EnqueueUninstallAsync(
        UninstallItem item,
        UninstallOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("模块尚未初始化");

        if (item.Source is SoftwareSource.Uwp or SoftwareSource.WindowsFeature)
        {
            throw new UwpNotSupportedV1Exception();
        }
        if (options.Mode == UninstallMode.Silent)
        {
            throw new NotSupportedException("静默卸载 V1.5 支持");
        }

        var cmd = await GetUninstallCommandAsync(item, ct);

        if (options.Mode == UninstallMode.Force && !cmd.CanForceUninstall)
        {
            throw new UninstallerAvailableException(item.DisplayName);
        }

        // 详设 01 §4.2：进程检测先于卸载程序可用性判定
        var runningProcesses = FindRunningProcesses(item.InstallLocation);
        if (runningProcesses.Count > 0 && !options.KillRunningProcesses)
        {
            throw new SoftwareRunningException(
                item.InstallLocation ?? string.Empty,
                runningProcesses.Select(p => p.ProcessName).ToList());
        }

        if (options.Mode == UninstallMode.Standard
            && string.IsNullOrWhiteSpace(cmd.UninstallString))
        {
            throw new UninstallerNotFoundException(item.DisplayName);
        }

        var actions = new List<OperationAction>();
        if (options.KillRunningProcesses && runningProcesses.Count > 0)
        {
            actions.Add(new OperationAction(
                ActionKind.KillProcess,
                item.InstallLocation,
                null, null, null, null, null,
                string.Join(",", runningProcesses.Select(p => p.Id)),
                null, null, null));
        }

        var plan = options.Mode == UninstallMode.Force
            ? BuildForcePlan(item, cmd, actions)
            : BuildStandardPlan(item, cmd, options, actions);

        var preview = await ctx.Trust.PreviewAsync(plan, ct);
        if (!preview.Confirmed)
        {
            return new OperationResult(plan.PlanId, false, TimeSpan.Zero, "用户取消预演", null);
        }

        var confirmedPlan = plan with { UserConfirmed = true };

        ctx.Coordinator.RegisterUninstallTarget(
            confirmedPlan.PlanId,
            new ResidualScanTarget(
                item.SoftwareId,
                item.DisplayName,
                item.Publisher,
                item.InstallLocation));

        var result = await ctx.OpQueue.EnqueueAsync(confirmedPlan, progress, ct);

        if (result.Success)
        {
            try
            {
                await ctx.Coordinator.TriggerResidualScanAsync(confirmedPlan.PlanId);
            }
            catch (Exception)
            {
                // 残留联动扫描是卸载后的附加动作，失败不应把已成功的卸载报成异常
            }
        }
        return result;
    }

    // ---------------- 计划构造 ----------------

    private static OperationPlan BuildStandardPlan(
        UninstallItem item,
        UninstallCommand cmd,
        UninstallOptions options,
        List<OperationAction> actions)
    {
        var target = options.CustomUninstallArgs is null
            ? cmd.UninstallString
            : $"{cmd.UninstallString} {options.CustomUninstallArgs}";

        actions.Add(new OperationAction(
            ActionKind.RunExternalUninstaller,
            target,
            null, null, null, null, null, null,
            options.CustomUninstallArgs ?? cmd.SilentArgs,
            null, null));

        return new OperationPlan(
            Guid.NewGuid(),
            "uninstall",
            "FR-UNINSTALL-02",
            $"标准卸载 {item.DisplayName}",
            SafetyLevel.Medium,
            actions,
            PreviewRequired: true,
            UserConfirmed: false);
    }

    private static OperationPlan BuildForcePlan(
        UninstallItem item,
        UninstallCommand cmd,
        List<OperationAction> actions)
    {
        if (cmd.InstallLocation is not null)
        {
            actions.Add(new OperationAction(
                ActionKind.DeleteFile,
                cmd.InstallLocation,
                null,
                FileUtilities.TryGetSize(cmd.InstallLocation),
                null, null, null, null, null, null, null));
        }
        foreach (var key in cmd.RelatedRegistryKeys)
        {
            actions.Add(new OperationAction(
                ActionKind.DeleteRegistry,
                null, null, null, null,
                key, null, null, null, null, null));
        }
        foreach (var service in cmd.RelatedServices)
        {
            actions.Add(new OperationAction(
                ActionKind.StopService,
                null, null, null, null, null, null, null, null,
                service, null));
        }
        foreach (var task in cmd.RelatedScheduledTasks)
        {
            actions.Add(new OperationAction(
                ActionKind.DeleteScheduledTask,
                null, null, null, null, null, null, null, null, null,
                task));
        }

        return new OperationPlan(
            Guid.NewGuid(),
            "uninstall",
            "FR-UNINSTALL-04",
            $"强制卸载 {item.DisplayName}",
            SafetyLevel.Low,
            actions,
            PreviewRequired: true,
            UserConfirmed: false);
    }

    // ---------------- 扫描辅助 ----------------

    private UninstallItem BuildItem(
        string key,
        string displayName,
        SoftwareSource source,
        IReadOnlyDictionary<string, DateTimeOffset> prefetch,
        IReadOnlyCollection<(string Key, string Name, string? Data)> runValues)
    {
        var publisher = _registry.GetValue(key, "Publisher");
        var version = _registry.GetValue(key, "DisplayVersion");
        var installDate = ParseInstallDate(_registry.GetValue(key, "InstallDate"));
        var estimatedKb = ParseLong(_registry.GetValue(key, "EstimatedSize"));
        var installLocation = TrimToNull(_registry.GetValue(key, "InstallLocation"));
        var uninstallString = TrimToNull(_registry.GetValue(key, "UninstallString"));
        var displayIcon = TrimToNull(_registry.GetValue(key, "DisplayIcon"));
        // 系统级组件默认不展示，避免误卸载：注册表 SystemComponent 标记 + Windows 驱动程序包
        var systemComponent = _registry.GetValue(key, "SystemComponent") == "1"
                              || displayName.StartsWith("Windows 驱动程序包", StringComparison.OrdinalIgnoreCase);

        var iconPath = ResolveIconPath(displayIcon, installLocation, uninstallString);
        var lastUsed = GetLastUsedTime(displayName, uninstallString, iconPath, installLocation, prefetch);
        var hasStartup = HasStartupEntry(displayName, installLocation, runValues);
        var isLongUnused = ClassifyLongUnused(installDate, lastUsed, hasStartup);

        return new UninstallItem(
            DeterministicId(key),
            displayName,
            TrimToNull(publisher),
            TrimToNull(version),
            installDate,
            estimatedKb * 1024,
            installLocation,
            uninstallString,
            source,
            systemComponent,
            isLongUnused,
            lastUsed,
            hasStartup,
            iconPath);
    }

    /// <summary>
    /// 注册表未登记 EstimatedSize 时按安装目录实测体积兜底。
    /// 在后台线程执行并限时 3 秒，避免拖慢扫描或卡住界面；仍测不到则保持为空。
    /// </summary>
    private static async Task<UninstallItem> FillSizeFromDiskAsync(UninstallItem item, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.InstallLocation) || !Directory.Exists(item.InstallLocation))
        {
            return item;
        }

        var location = item.InstallLocation;
        var bytes = await Task.Run(
                () => FileUtilities.Measure(location, TimeSpan.FromSeconds(3), out _),
                ct)
            .ConfigureAwait(false);

        return bytes > 0 ? item with { EstimatedSizeBytes = bytes } : item;
    }

    /// <summary>
    /// 解析用于提取软件图标的文件：DisplayIcon（最常见）→ UninstallString → 安装目录内的主程序。
    /// DisplayIcon 可能形如 `"C:\App\app.exe",0`，需去掉引号与图标序号。
    /// </summary>
    private static string? ResolveIconPath(
        string? displayIcon,
        string? installLocation,
        string? uninstallString)
    {
        var fromDisplayIcon = NormalizeIconCandidate(displayIcon);
        if (fromDisplayIcon is not null)
        {
            return fromDisplayIcon;
        }

        var fromUninstall = NormalizeIconCandidate(uninstallString);
        if (fromUninstall is not null)
        {
            return fromUninstall;
        }

        if (installLocation is null)
        {
            return null;
        }

        try
        {
            if (!Directory.Exists(installLocation))
            {
                return null;
            }

            // 安装目录顶层只有一个可执行文件时才将其作为主程序，避免误取卸载器/更新器图标
            var executables = Directory.GetFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly);
            return executables.Length == 1 ? executables[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? NormalizeIconCandidate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (value.StartsWith('"'))
        {
            var closing = value.IndexOf('"', 1);
            if (closing > 1)
            {
                value = value[1..closing];
            }
        }
        else
        {
            // 去掉 ",0" 形式（含引号包裹在该分支之外的情况）
            var comma = value.IndexOf(',');
            if (comma > 0)
            {
                value = value[..comma];
            }

            // 去掉可执行文件后的命令行参数
            var exeEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeEnd > 0)
            {
                value = value[..(exeEnd + 4)];
            }
        }

        value = value.Trim().Trim('"');
        try
        {
            value = Environment.ExpandEnvironmentVariables(value);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!File.Exists(value))
        {
            return null;
        }

        var extension = Path.GetExtension(value);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            ? value
            : null;
    }

    private static bool ClassifyLongUnused(
        DateTimeOffset? installDate,
        DateTimeOffset? lastUsed,
        bool hasStartup)
    {
        var lastActivity = DateTimeOffset.MinValue;
        if (installDate is { } d && d > lastActivity)
        {
            lastActivity = d;
        }
        if (lastUsed is { } u && u > lastActivity)
        {
            lastActivity = u;
        }
        if (hasStartup)
        {
            lastActivity = DateTimeOffset.Now;
        }
        if (lastActivity == DateTimeOffset.MinValue)
        {
            // 三项全无数据，保守不标记
            return false;
        }
        return DateTimeOffset.Now - lastActivity > TimeSpan.FromDays(LongUnusedThresholdDays);
    }

    /// <summary>
    /// 由 Prefetch 最后执行时间推断最近使用时间。
    /// Prefetch 文件名形如 <c>GIT.EXE-1A2B3C4D.pf</c>，因此按「主程序名.EXE-」前缀匹配。
    /// 主程序候选来自：卸载程序、图标程序、安装目录顶层/ bin 下的可执行文件、名称首词、目录名。
    /// </summary>
    private static DateTimeOffset? GetLastUsedTime(
        string displayName,
        string? uninstallString,
        string? iconPath,
        string? installLocation,
        IReadOnlyDictionary<string, DateTimeOffset> prefetch)
    {
        if (prefetch.Count == 0)
        {
            return null;
        }

        var candidates = CollectExeCandidates(displayName, uninstallString, iconPath, installLocation);
        if (candidates.Count == 0)
        {
            return null;
        }

        DateTimeOffset? latest = null;
        foreach (var key in prefetch.Keys)
        {
            var separator = key.IndexOf(".EXE-", StringComparison.OrdinalIgnoreCase);
            if (separator <= 0)
            {
                continue;
            }

            if (!candidates.Contains(key[..separator]))
            {
                continue;
            }

            var time = prefetch[key];
            if (latest is null || time > latest)
            {
                latest = time;
            }
        }

        return latest;
    }

    private static HashSet<string> CollectExeCandidates(
        string displayName,
        string? uninstallString,
        string? iconPath,
        string? installLocation)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddCandidate(candidates, ExtractExeBaseName(uninstallString));
        AddCandidate(candidates, ExtractExeBaseName(iconPath));

        var firstToken = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        AddCandidate(candidates, firstToken);

        if (string.IsNullOrWhiteSpace(installLocation))
        {
            return candidates;
        }

        AddCandidate(candidates, Path.GetFileName(installLocation.TrimEnd('\\', '/')));

        foreach (var directory in new[]
                 {
                     installLocation,
                     Path.Combine(installLocation, "bin")
                 })
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var exe in Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(exe);
                    if (name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("uninst", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("setup", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("update", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    AddCandidate(candidates, name);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 目录不可读则跳过
            }
        }

        return candidates;
    }

    private static void AddCandidate(HashSet<string> candidates, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            candidates.Add(value.Trim());
        }
    }

    private static string? ExtractExeBaseName(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }
        var firstToken = commandLine.TrimStart('"');
        var endQuote = firstToken.IndexOf('"');
        if (endQuote > 0)
        {
            firstToken = firstToken[..endQuote];
        }
        else
        {
            var space = firstToken.IndexOf(' ');
            if (space > 0)
            {
                firstToken = firstToken[..space];
            }
        }
        var leaf = Path.GetFileName(firstToken);
        var dot = leaf.LastIndexOf('.');
        return dot > 0 ? leaf[..dot].ToUpperInvariant() : leaf.ToUpperInvariant();
    }

    private static bool HasStartupEntry(
        string displayName,
        string? installLocation,
        IEnumerable<(string Key, string Name, string? Data)> runValues)
    {
        return runValues.Any(v =>
            (v.Data is not null && v.Data.Contains(displayName, StringComparison.OrdinalIgnoreCase))
            || (installLocation is not null && v.Data is not null
                && v.Data.StartsWith(installLocation, StringComparison.OrdinalIgnoreCase)));
    }

    private IReadOnlyDictionary<string, DateTimeOffset> LoadPrefetchIndex()
    {
        var prefetchDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
        var index = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(prefetchDir, "*.pf"))
            {
                index[Path.GetFileName(file)] = new DateTimeOffset(File.GetLastWriteTimeUtc(file));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Prefetch 不可读时降级为空索引
        }
        return index;
    }

    private List<(string Key, string Name, string? Data)> LoadRunValues()
    {
        var values = new List<(string, string, string?)>();
        foreach (var runKey in new[] { RegistryRoots.RunHkcu, RegistryRoots.RunHklm })
        {
            try
            {
                foreach (var value in _registry.EnumerateValues(runKey))
                {
                    values.Add((runKey, value.Name, value.Data));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 键不可读跳过
            }
        }
        return values;
    }

    private List<ProcessInfo> FindRunningProcesses(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation))
        {
            return new List<ProcessInfo>();
        }
        return _processes.Enumerate()
            .Where(p => p.ExecutablePath is not null
                        && p.ExecutablePath.StartsWith(installLocation, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // ---------------- 关联项收集 ----------------

    private string? FindUninstallKey(string displayName)
    {
        foreach (var (hive, _) in EnumerateHives())
        {
            foreach (var subName in SafeSubKeys(hive))
            {
                var key = Path.Combine(hive, subName);
                var name = _registry.GetValue(key, "DisplayName");
                if (string.Equals(name?.Trim(), displayName, StringComparison.Ordinal))
                {
                    return key;
                }
            }
        }
        return null;
    }

    private List<string> CollectRelatedRegistryKeys(UninstallItem item, string? uninstallKey)
    {
        var keys = new List<string>();
        if (uninstallKey is not null)
        {
            keys.Add(uninstallKey);
        }

        if (item.Publisher is not null)
        {
            var candidate = Path.Combine(
                RegistryRoots.Hklm, "SOFTWARE", item.Publisher, item.DisplayName);
            if (_registry.KeyExists(candidate))
            {
                keys.Add(candidate);
            }
            var userCandidate = Path.Combine(
                RegistryRoots.Hkcu, "SOFTWARE", item.Publisher, item.DisplayName);
            if (_registry.KeyExists(userCandidate))
            {
                keys.Add(userCandidate);
            }
        }

        try
        {
            foreach (var exeName in _registry.EnumerateSubKeyNames(RegistryRoots.AppPaths))
            {
                var appPathKey = Path.Combine(RegistryRoots.AppPaths, exeName);
                var data = _registry.GetValue(appPathKey, null);
                if (item.InstallLocation is not null && data is not null
                    && data.StartsWith(item.InstallLocation, StringComparison.OrdinalIgnoreCase))
                {
                    keys.Add(appPathKey);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // AppPaths 不可读跳过
        }

        return keys;
    }

    private List<string> CollectRelatedServices(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation))
        {
            return new List<string>();
        }
        return _services.EnumerateServices()
            .Where(s => s.PathName is not null
                        && s.PathName.Contains(installLocation, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Name)
            .ToList();
    }

    private IReadOnlyList<string> CollectRelatedTasks(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation))
            return Array.Empty<string>();

        return _tasks.Enumerate()
            .Where(task => !string.IsNullOrWhiteSpace(task.ExecutablePath)
                && FileUtilities.IsUnder(task.ExecutablePath.Trim('"'), installLocation))
            .Select(task => task.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // ---------------- 静态工具 ----------------

    private static (UninstallerType Type, string? SilentArgs) ClassifyUninstaller(string? uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString))
        {
            return (UninstallerType.None, null);
        }
        if (uninstallString.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            return (UninstallerType.Msi, "/qn /norestart");
        }
        var leaf = Path.GetFileName(uninstallString.Trim('"'));
        if (leaf.StartsWith("unins", StringComparison.OrdinalIgnoreCase))
        {
            return (UninstallerType.InnoSetup, "/VERYSILENT /NORESTART");
        }
        if (leaf.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return (UninstallerType.BasicExe, null);
        }
        return (UninstallerType.Unknown, null);
    }

    private static DateTimeOffset? ParseInstallDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 8)
        {
            return null;
        }
        if (int.TryParse(value.AsSpan(0, 4), out var year)
            && int.TryParse(value.AsSpan(4, 2), out var month)
            && int.TryParse(value.AsSpan(6, 2), out var day)
            && month is >= 1 and <= 12 && day is >= 1 and <= 31)
        {
            return new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);
        }
        return null;
    }

    private static long? ParseLong(string? value)
        => long.TryParse(value, out var result) ? result : null;

    private static string? TrimToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Guid DeterministicId(string key)
    {
        // 键路径稳定 GUID（MD5 16 字节直接构造）
        var hash = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(key.ToLowerInvariant()));
        return new Guid(hash);
    }

    private static IEnumerable<(string Hive, SoftwareSource Source)> EnumerateHives()
    {
        yield return (RegistryRoots.UninstallHklm, SoftwareSource.RegistryX64);
        yield return (RegistryRoots.UninstallHklmWow, SoftwareSource.RegistryX86);
        yield return (RegistryRoots.UninstallHkcu, SoftwareSource.RegistryUser);
    }

    private IReadOnlyCollection<string> SafeSubKeys(string hive)
    {
        try
        {
            return _registry.EnumerateSubKeyNames(hive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
