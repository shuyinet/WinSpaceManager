using SysMgr.Core.Operations;
using SysMgr.Core.Queue;
using SysMgr.Core.Safety;
using SysMgr.Core.SystemAccess;
using SysMgr.Infra;
using SysMgr.Modules.Cleanup.Errors;
using SysMgr.Modules.Internal;

namespace SysMgr.Modules.Cleanup;

/// <summary>
/// 垃圾清理模块实现（ICleanupService，FR-CLEAN-01/02/06）。
/// 系统垃圾与浏览器隐私数据扫描只读；清理经 TRUST-02 预演 → OpQueue；回收站走虚拟目标。
/// </summary>
public sealed class CleanupService : ICleanupService
{
    private readonly IRecycleBin _recycleBin;
    private readonly IProcessQuery _processes;

    private IModuleContext? _ctx;

    public string ModuleId => "cleanup";
    public string DisplayName => "垃圾清理";
    public ModuleVersion Version => new(1, 0, "V1");

    public CleanupService(IRecycleBin recycleBin, IProcessQuery processes)
    {
        _recycleBin = recycleBin;
        _processes = processes;
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
                ["recycleBin"] = "ok",
                ["process"] = "ok"
            }));

    /// <inheritdoc />
    public async IAsyncEnumerable<JunkItem> ScanJunkAsync(
        JunkScope scope,
        IProgress<ScanProgress>? progress,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        var ctx = _ctx ?? throw new InvalidOperationException("模块尚未初始化");
        var emitted = 0;

        // 1. 系统垃圾
        var systemKinds = scope.SystemKinds
            ?? Enum.GetValues<JunkKind>().Where(k => k < JunkKind.BrowserCache).ToList();
        foreach (var kind in systemKinds)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var target in ResolveSystemTargets(kind))
            {
                long size;
                if (kind == JunkKind.RecycleBin)
                {
                    size = _recycleBin.GetTotalSizeBytes();
                }
                else
                {
                    // 大目录（Package Cache/NuGet 等）遍历耗时长，放到后台线程避免界面卡顿
                    size = await Task.Run(() => FileUtilities.TryGetSize(target.Path), ct);
                }
                if (size == 0 && kind != JunkKind.RecycleBin)
                {
                    continue;
                }

                var verdict = kind == JunkKind.RecycleBin
                    ? new SafetyVerdict(SafetyLevel.High, "用户主动清空回收站，语义明确", [target.Path])
                    : ctx.Safety.Classify(new FileItem(target.Path, size));

                var defaultSelected = kind == JunkKind.RecycleBin
                    || verdict.Level == SafetyLevel.High;

                emitted++;
                yield return new JunkItem(
                    Guid.NewGuid(),
                    kind,
                    Browser: null,
                    target.Path,
                    verdict,
                    size,
                    defaultSelected,
                    target.Description);
            }
            progress?.Report(new ScanProgress(
                ScanPhase.Enumerating, emitted, null, null, kind.ToString()));
        }

        // 2. 浏览器隐私数据
        var browserTargets = scope.BrowserTargets
            ?? Enum.GetValues<JunkBrowser>()
                .Select(b => new JunkBrowserTarget(
                    b,
                    new[] { JunkKind.BrowserCache }))
                .ToList();

        foreach (var bt in browserTargets)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var item in ScanBrowser(bt, ctx))
            {
                emitted++;
                yield return item;
            }
        }

        progress?.Report(new ScanProgress(ScanPhase.Done, emitted, null, null, null));
    }

    /// <inheritdoc />
    public async Task<OperationResult> EnqueueCleanAsync(
        IReadOnlyList<JunkItem> items,
        CleanOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("模块尚未初始化");

        if (items.Count == 0)
        {
            throw new JunkItemsEmptyException();
        }

        var actions = new List<OperationAction>();

        // 1. 浏览器运行检测（仅涉及浏览器项）
        var browsers = items
            .Where(i => i.Browser is not null)
            .Select(i => i.Browser!.Value)
            .Distinct()
            .ToList();
        if (browsers.Count > 0)
        {
            var running = FindRunningBrowsers(browsers);
            if (running.Count > 0 && !options.KillRunningBrowsers)
            {
                throw new BrowserRunningException(running);
            }
            if (options.KillRunningBrowsers)
            {
                foreach (var browser in browsers)
                {
                    actions.Add(new OperationAction(
                        ActionKind.KillProcess,
                        ImageNameOf(browser),
                        null, null, null, null, null, null, null, null, null));
                }
            }
        }

        // 2. 垃圾项动作（回收站虚拟目标特化）
        foreach (var item in items)
        {
            var target = item.Kind == JunkKind.RecycleBin
                ? WindowsOperationExecutor.RecycleBinTarget
                : item.Target;
            actions.Add(new OperationAction(
                ActionKind.Purge,
                target,
                null, item.EstimatedSizeBytes, null, null, null, null, null, null, null));
        }

        // 详设 04 §FR-CLEAN-01：含任一 Low 项则整体 Low（High=0 < Low=2，数值上取大）
        var safety = items
            .Select(i => i.Verdict.Level)
            .Aggregate((a, b) => a > b ? a : b);

        var plan = new OperationPlan(
            Guid.NewGuid(),
            "cleanup",
            "FR-CLEAN-01",
            $"清理 {items.Count} 项垃圾，预计释放 {items.Sum(i => i.EstimatedSizeBytes)} 字节",
            safety,
            actions,
            PreviewRequired: true,
            UserConfirmed: false);

        // 3. 预演二次确认（Cookie/密码项面板额外提示隐私影响）
        var preview = await ctx.Trust.PreviewAsync(plan, ct);
        if (!preview.Confirmed)
        {
            return new OperationResult(plan.PlanId, false, TimeSpan.Zero, "用户取消预演", null);
        }

        var confirmedPlan = plan with { UserConfirmed = true };
        return await ctx.OpQueue.EnqueueAsync(confirmedPlan, progress, ct);
    }

    /// <inheritdoc />
    public Task<long> GetRemainingSizeAsync(JunkItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind == JunkKind.RecycleBin)
        {
            return Task.FromResult(_recycleBin.GetTotalSizeBytes());
        }

        return Task.Run(() => FileUtilities.TryGetSize(item.Target), ct);
    }

    // ---------------- 浏览器扫描 ----------------

    private IEnumerable<JunkItem> ScanBrowser(
        JunkBrowserTarget browserTarget,
        IModuleContext ctx)
    {
        var definition = BrowserDefinitions[browserTarget.Browser];

        foreach (var profile in EnumerateProfiles(definition))
        {
            foreach (var kind in browserTarget.DataKinds)
            {
                foreach (var path in ResolveBrowserData(definition, profile, kind))
                {
                    if (!FileUtilities.Normalize(path).Contains(Path.DirectorySeparatorChar))
                    {
                        continue;
                    }
                    var size = FileUtilities.TryGetSize(path);
                    if (size == 0)
                    {
                        continue;
                    }

                    var verdict = ctx.Safety.Classify(new FileItem(path, size));
                    var description = $"{browserTarget.Browser} {DescribeDataKind(kind)}（{profile.Name}）";

                    yield return new JunkItem(
                        Guid.NewGuid(),
                        kind,
                        browserTarget.Browser,
                        path,
                        verdict,
                        size,
                        DefaultSelected: kind == JunkKind.BrowserCache,
                        description);
                }
            }
        }
    }

    private static IEnumerable<ProfileInfo> EnumerateProfiles(BrowserDef definition)
    {
        if (!Directory.Exists(definition.UserDataRoot))
        {
            yield break;
        }

        if (definition.IsFirefox)
        {
            var profilesRoot = Path.Combine(definition.UserDataRoot, "Profiles");
            if (!Directory.Exists(profilesRoot))
            {
                yield break;
            }
            foreach (var dir in Directory.EnumerateDirectories(profilesRoot))
            {
                if (dir.Contains(".default", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new ProfileInfo(Path.GetFileName(dir), dir);
                }
            }
            yield break;
        }

        // Chromium：Default + Profile N
        foreach (var dir in Directory.EnumerateDirectories(definition.UserDataRoot))
        {
            var name = Path.GetFileName(dir);
            if (name == "Default"
                || (name.StartsWith("Profile ", StringComparison.Ordinal)
                    && int.TryParse(name["Profile ".Length..], out _)))
            {
                yield return new ProfileInfo(name, dir);
            }
        }
    }

    private static IEnumerable<string> ResolveBrowserData(
        BrowserDef definition,
        ProfileInfo profile,
        JunkKind kind)
    {
        var root = profile.Path;

        if (definition.IsFirefox)
        {
            return kind switch
            {
                JunkKind.BrowserCache => new[]
                {
                    Path.Combine(root, "cache2"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Mozilla", "Firefox", "Profiles", profile.Name, "cache2")
                },
                JunkKind.BrowserCookies => new[] { Path.Combine(root, "cookies.sqlite") },
                JunkKind.BrowserHistory => new[] { Path.Combine(root, "places.sqlite") },
                JunkKind.BrowserFormData => new[] { Path.Combine(root, "formhistory.sqlite") },
                JunkKind.BrowserPasswords => new[]
                {
                    Path.Combine(root, "logins.json"),
                    Path.Combine(root, "key4.db")
                },
                _ => Array.Empty<string>()
            };
        }

        return kind switch
        {
            JunkKind.BrowserCache => new[]
            {
                Path.Combine(root, "Cache"),
                Path.Combine(root, "Code Cache"),
                Path.Combine(root, "GPUCache"),
                Path.Combine(root, "DawnCache"),
                Path.Combine(root, "GraphiteDawnCache"),
                Path.Combine(root, "ShaderCache"),
                Path.Combine(root, "Service Worker", "CacheStorage")
            },
            JunkKind.BrowserCookies => new[] { Path.Combine(root, "Network", "Cookies") },
            JunkKind.BrowserHistory => new[] { Path.Combine(root, "History") },
            JunkKind.BrowserFormData => new[] { Path.Combine(root, "Web Data") },
            JunkKind.BrowserPasswords => new[]
            {
                Path.Combine(root, "Network", "Login Data"),
                Path.Combine(root, "Login Data")
            },
            _ => Array.Empty<string>()
        };
    }

    // ---------------- 系统目标 ----------------

    private static IReadOnlyList<ScanTarget> EnumerateThumbnailTargets(string localAppData)
    {
        var explorerDir = Path.Combine(
            localAppData, "Microsoft", "Windows", "Explorer");
        if (!Directory.Exists(explorerDir))
        {
            return Array.Empty<ScanTarget>();
        }
        return Directory.EnumerateFiles(explorerDir, "thumbcache_*.db")
            .Concat(Directory.EnumerateFiles(explorerDir, "iconcache_*.db"))
            .Select(file => new ScanTarget(file, "缩略图与图标缓存"))
            .ToList();
    }

    /// <summary>枚举 Office 缓存：SolutionPackages（加载项包缓存）与各版本（14.0/15.0/16.0…）缓存目录。</summary>
    private static IReadOnlyList<ScanTarget> EnumerateOfficeCacheTargets(string localAppData)
    {
        var officeRoot = Path.Combine(localAppData, "Microsoft", "Office");
        if (!Directory.Exists(officeRoot))
        {
            return Array.Empty<ScanTarget>();
        }

        var targets = new List<ScanTarget>();
        var solutionPackages = Path.Combine(officeRoot, "SolutionPackages");
        if (Directory.Exists(solutionPackages))
        {
            targets.Add(new ScanTarget(solutionPackages, "Office 加载项包缓存"));
        }

        foreach (var versionDir in Directory.EnumerateDirectories(officeRoot))
        {
            // 仅处理数字版本目录（16.0 等），跳过 AI/DEM/FeatureFlags 等配置目录
            var name = Path.GetFileName(versionDir);
            if (name.Length == 0 || !char.IsDigit(name[0]))
            {
                continue;
            }
            var cache = Path.Combine(versionDir, "OfficeFileCache");
            if (Directory.Exists(cache))
                targets.Add(new ScanTarget(cache, $"Office 缓存（{name}）"));
        }
        return targets;
    }

    private static IReadOnlyList<ScanTarget> ResolveSystemTargets(JunkKind kind)
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return kind switch
        {
            JunkKind.Temp => new[]
            {
                new ScanTarget(
                    Environment.GetEnvironmentVariable("TEMP") ?? string.Empty,
                    "当前用户临时文件")
            },
            JunkKind.WindowsTemp => new[]
            {
                new ScanTarget(Path.Combine(systemRoot, "Temp"), "Windows 临时文件")
            },
            JunkKind.Prefetch => new[]
            {
                new ScanTarget(Path.Combine(systemRoot, "Prefetch"), "预读取文件（删除后短期启动略慢）")
            },
            JunkKind.ThumbnailCache => EnumerateThumbnailTargets(localAppData),
            JunkKind.WindowsErrorReporting => new[]
            {
                new ScanTarget(
                    Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue"),
                    "Windows 错误报告队列"),
                new ScanTarget(
                    Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"),
                    "Windows 错误报告归档")
            },
            JunkKind.MemoryDump => new[]
            {
                new ScanTarget(Path.Combine(systemRoot, "Minidump"), "小型内存转储"),
                new ScanTarget(Path.Combine(systemRoot, "MEMORY.DMP"), "完整内存转储")
            },
            JunkKind.RecycleBin => new[]
            {
                new ScanTarget(
                    WindowsOperationExecutor.RecycleBinTarget,
                    "回收站")
            },
            JunkKind.PackageCache => new[]
            {
                new ScanTarget(
                    Path.Combine(programData, "Package Cache"),
                    "安装包缓存（用于程序修复/卸载，可重新下载）")
            },
            JunkKind.AppCache => new[]
            {
                new ScanTarget(Path.Combine(localAppData, "D3DSCache"), "DirectX 着色器缓存"),
                new ScanTarget(Path.Combine(localAppData, "NVIDIA", "DXCache"), "NVIDIA DirectX 缓存"),
                new ScanTarget(Path.Combine(localAppData, "NVIDIA", "GLCache"), "NVIDIA OpenGL 缓存"),
                new ScanTarget(Path.Combine(localAppData, "AMD", "DxCache"), "AMD DirectX 缓存"),
                new ScanTarget(Path.Combine(roaming, "Code", "Cache"), "VS Code 网页缓存"),
                new ScanTarget(Path.Combine(roaming, "Code", "CachedData"), "VS Code 编译缓存"),
                new ScanTarget(Path.Combine(roaming, "Code", "Code Cache"), "VS Code 脚本缓存"),
                new ScanTarget(Path.Combine(roaming, "Code", "GPUCache"), "VS Code 图形缓存"),
                new ScanTarget(
                    Path.Combine(localAppData, "Tencent", "QQGuild"),
                    "QQ 频道缓存"),
                new ScanTarget(
                    Path.Combine(localAppData, "Tencent", "MiniBrowser"),
                    "QQ 内置浏览器缓存"),
                new ScanTarget(
                    Path.Combine(localAppData, "Tencent", "QQGameTempest"),
                    "QQ 游戏临时文件")
            },
            JunkKind.AppLogs => new[]
            {
                new ScanTarget(
                    Path.Combine(localAppData, "Microsoft", "Windows", "Logs"),
                    "用户级应用日志")
            },
            JunkKind.OfficeCache => EnumerateOfficeCacheTargets(localAppData),
            JunkKind.WpsBackup => new[]
            {
                new ScanTarget(
                    Path.Combine(roaming, "Kingsoft", "wps", "addons"),
                    "WPS 加载项缓存"),
                new ScanTarget(
                    Path.Combine(roaming, "Kingsoft", "wps", "download"),
                    "WPS 下载缓存"),
                new ScanTarget(
                    Path.Combine(roaming, "Kingsoft", "office6", "backup"),
                    "WPS 老版本备份"),
                new ScanTarget(
                    Path.Combine(roaming, "Kingsoft", "office6", "log"),
                    "WPS 日志"),
                new ScanTarget(
                    Path.Combine(roaming, "Kingsoft", "office6", "wns"),
                    "WPS 推送缓存")
            },
            JunkKind.NuGetCache => new[]
            {
                new ScanTarget(
                    Path.Combine(userProfile, ".nuget", "packages"),
                    "NuGet 包缓存"),
                new ScanTarget(
                    Path.Combine(localAppData, "NuGet", "Cache"),
                    "NuGet 下载缓存")
            },
            JunkKind.WindowsUpdateCache => new[]
            {
                new ScanTarget(
                    Path.Combine(systemRoot, "SoftwareDistribution", "Download"),
                    "Windows 更新下载缓存")
            },
            JunkKind.DeliveryOptimization => new[]
            {
                new ScanTarget(
                    Path.Combine(systemRoot, "SoftwareDistribution", "DeliveryOptimization"),
                    "传递优化缓存"),
                new ScanTarget(
                    Path.Combine(
                        systemRoot, "ServiceProfiles", "NetworkService", "AppData",
                        "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"),
                    "传递优化缓存（网络服务账户）")
            },
            JunkKind.FontCache => new[]
            {
                new ScanTarget(
                    Path.Combine(
                        systemRoot, "ServiceProfiles", "LocalService", "AppData",
                        "Local", "FontCache"),
                    "字体缓存")
            },
            JunkKind.IECache => new[]
            {
                new ScanTarget(
                    Path.Combine(localAppData, "Microsoft", "Windows", "INetCache"),
                    "IE / 系统网络缓存")
            },
            JunkKind.TerminalServicesCache => new[]
            {
                new ScanTarget(
                    Path.Combine(localAppData, "Microsoft", "Terminal Server Client", "Cache"),
                    "远程桌面缓存")
            },
            _ => Array.Empty<ScanTarget>()
        };
    }

    // ---------------- 运行检测 ----------------

    private List<string> FindRunningBrowsers(IEnumerable<JunkBrowser> browsers)
    {
        var imageNames = browsers.ToDictionary(
            b => Path.GetFileNameWithoutExtension(ImageNameOf(b)),
            b => b.ToString());

        return _processes.Enumerate()
            .Select(p => p.ProcessName)
            .Where(imageNames.ContainsKey)
            .Select(n => imageNames[n])
            .Distinct()
            .ToList();
    }

    private static string ImageNameOf(JunkBrowser browser) => browser switch
    {
        JunkBrowser.Edge => "msedge.exe",
        JunkBrowser.Chrome => "chrome.exe",
        JunkBrowser.Firefox => "firefox.exe",
        JunkBrowser.Brave => "brave.exe",
        JunkBrowser.Opera => "opera.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(browser))
    };

    private static string DescribeDataKind(JunkKind kind) => kind switch
    {
        JunkKind.BrowserCache => "缓存",
        JunkKind.BrowserCookies => "Cookie",
        JunkKind.BrowserHistory => "历史记录",
        JunkKind.BrowserDownloads => "下载记录",
        JunkKind.BrowserFormData => "表单数据",
        JunkKind.BrowserPasswords => "已保存密码",
        _ => kind.ToString()
    };

    // ---------------- 定义与内部类型 ----------------

    private static readonly IReadOnlyDictionary<JunkBrowser, BrowserDef> BrowserDefinitions =
        new Dictionary<JunkBrowser, BrowserDef>
        {
            [JunkBrowser.Edge] = new(
                JunkBrowser.Edge,
                "msedge.exe",
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Edge", "User Data"),
                IsFirefox: false),
            [JunkBrowser.Chrome] = new(
                JunkBrowser.Chrome,
                "chrome.exe",
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Google", "Chrome", "User Data"),
                IsFirefox: false),
            [JunkBrowser.Brave] = new(
                JunkBrowser.Brave,
                "brave.exe",
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BraveSoftware", "Brave-Browser", "User Data"),
                IsFirefox: false),
            [JunkBrowser.Opera] = new(
                JunkBrowser.Opera,
                "opera.exe",
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Opera Software", "Opera Stable"),
                IsFirefox: false),
            [JunkBrowser.Firefox] = new(
                JunkBrowser.Firefox,
                "firefox.exe",
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Mozilla", "Firefox"),
                IsFirefox: true)
        };

    private sealed record BrowserDef(
        JunkBrowser Browser,
        string ImageName,
        string UserDataRoot,
        bool IsFirefox);

    private sealed record ProfileInfo(string Name, string Path);

    private sealed record ScanTarget(string Path, string Description);
}
