using SysMgr.Core.Operations;
using SysMgr.Core.Queue;
using SysMgr.Core.Safety;
using SysMgr.Core.SystemAccess;
using SysMgr.Modules.CSlim.Errors;
using SysMgr.Modules.Internal;

namespace SysMgr.Modules.CSlim;

/// <summary>
/// C 盘瘦身模块实现（ICSlimService，FR-CSLIM-01/02/05/08）。
/// 分析只读；大文件迁移经协调器路由到 Migration；残留清除/保存位置改写经 TRUST-02 → OpQueue。
/// </summary>
public sealed class CSlimService : ICSlimService
{
    private static readonly TimeSpan BigDirTimeout = TimeSpan.FromSeconds(30);

    /// <summary>保存位置类别 → Known Folder ID 映射（V1 仅支持语义明确的四项）。</summary>
    private static readonly Dictionary<SaveLocationCategory, Guid> KnownFolderMap = new()
    {
        [SaveLocationCategory.NewDocuments] = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7"),
        [SaveLocationCategory.NewMusic] = new("4BD8D571-6D19-48D3-BE97-422220080E43"),
        [SaveLocationCategory.NewPhotos] = new("33E28130-4E1E-4676-835A-98395C3BC3BB"),
        [SaveLocationCategory.NewVideos] = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC")
    };

    private readonly IRegistry _registry;
    private readonly IKnownFolder _knownFolders;

    private IModuleContext? _ctx;

    public string ModuleId => "cslim";
    public string DisplayName => "C 盘瘦身";
    public ModuleVersion Version => new(1, 0, "V1");

    public CSlimService(IRegistry registry, IKnownFolder knownFolders)
    {
        _registry = registry;
        _knownFolders = knownFolders;
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
                ["knownFolder"] = "ok"
            }));

    // ---------------- FR-CSLIM-01 分析 ----------------

    /// <inheritdoc />
    public Task<CDriveAnalysis> AnalyzeCDriveAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemDriveRoot = Path.GetPathRoot(systemRoot)!;
        var drive = new DriveInfo(systemDriveRoot);

        var sizes = new Dictionary<CDriveCategory, long>();

        // 1. 系统/保留：Windows 目录（30s 限时降级）+ 根目录系统文件
        var windowsSize = FileUtilities.Measure(systemRoot, BigDirTimeout, out _);
        windowsSize += RootFileSize(systemDriveRoot, "pagefile.sys");
        windowsSize += RootFileSize(systemDriveRoot, "hiberfil.sys");
        windowsSize += RootFileSize(systemDriveRoot, "swapfile.sys");
        sizes[CDriveCategory.SystemReserved] = windowsSize;

        progress?.Report(new ScanProgress(ScanPhase.Sizing, 1, null, null, systemRoot));

        // 2. 已安装应用：清单 EstimatedSize 汇总
        sizes[CDriveCategory.InstalledApps] = SumEstimatedAppSizes();

        // 3. 临时文件：用户 TEMP + Windows\Temp
        var userTemp = Environment.GetEnvironmentVariable("TEMP") ?? string.Empty;
        var tempSize = FileUtilities.Measure(userTemp, BigDirTimeout, out _);
        tempSize += FileUtilities.Measure(Path.Combine(systemRoot, "Temp"), BigDirTimeout, out _);
        sizes[CDriveCategory.TempFiles] = tempSize;

        // 4. 用户文件：profile - AppData 两份 + 用户 TEMP（TEMP 位于 LocalAppData 内）
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userSize = FileUtilities.Measure(profile, BigDirTimeout, out _)
                       - FileUtilities.Measure(localAppData, BigDirTimeout, out _)
                       - FileUtilities.Measure(appData, BigDirTimeout, out _)
                       + FileUtilities.Measure(userTemp, null, out _);
        sizes[CDriveCategory.UserFiles] = Math.Max(0, userSize);

        // 5. 其他兜底
        var used = drive.TotalSize - drive.AvailableFreeSpace;
        var accounted = sizes.Values.Sum();
        sizes[CDriveCategory.Other] = Math.Max(0, used - accounted);

        var categories = Enum.GetValues<CDriveCategory>()
            .Select(c => new CDriveCategorySize(
                c,
                sizes.GetValueOrDefault(c),
                DescribeCategory(c)))
            .ToList();

        var result = new CDriveAnalysis(
            systemDriveRoot,
            drive.TotalSize,
            drive.AvailableFreeSpace,
            categories,
            DateTimeOffset.Now);

        progress?.Report(new ScanProgress(ScanPhase.Done, 0, null, null, null));
        return Task.FromResult(result);
    }

    // ---------------- FR-CSLIM-02 大文件 ----------------

    /// <inheritdoc />
    public async IAsyncEnumerable<LargeFileItem> FindLargeFilesAsync(
        long minSizeBytes,
        IProgress<ScanProgress>? progress,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        var systemRoot = FileUtilities.Normalize(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var systemDriveRoot = Path.GetPathRoot(systemRoot)!;

        var exclusions = BuildExclusions(systemDriveRoot);

        var pending = new Stack<string>();
        pending.Push(systemDriveRoot);
        var count = 0;

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            if (exclusions.Any(e => FileUtilities.IsUnder(dir, e)))
            {
                continue;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                files = Array.Empty<string>();
            }
            foreach (var file in files)
            {
                if (IsExcludedRootFile(file, exclusions))
                {
                    continue;
                }
                long size;
                try
                {
                    size = new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (size < minSizeBytes)
                {
                    continue;
                }
                count++;
                var lastWrite = SafeLastWrite(file);
                yield return new LargeFileItem(
                    file, size, lastWrite, ClassifyByExtension(file));
            }

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                subDirs = Array.Empty<string>();
            }
            foreach (var sub in subDirs)
            {
                try
                {
                    if (File.GetAttributes(sub).HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                pending.Push(sub);
            }
        }

        progress?.Report(new ScanProgress(ScanPhase.Done, count, null, null, null));
    }

    /// <inheritdoc />
    public Task<OperationResult> EnqueueLargeFileMigrationAsync(
        IReadOnlyList<LargeFileItem> items,
        string destinationRoot,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("模块尚未初始化");

        if (items.Count == 0)
        {
            throw new CslimItemsEmptyException();
        }

        DriveInfo destination;
        try
        {
            destination = new DriveInfo(FileUtilities.Normalize(destinationRoot));
            if (destination.DriveType != DriveType.Fixed)
            {
                throw new InvalidMigrationTargetException(destinationRoot);
            }
        }
        catch (InvalidMigrationTargetException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            // 路径非法 / 驱动器不存在或未就绪
            throw new InvalidMigrationTargetException(destinationRoot);
        }

        var totalSize = items.Sum(i => i.SizeBytes);
        if (totalSize > destination.AvailableFreeSpace)
        {
            throw new InsufficientDestinationSpaceException();
        }

        var request = new MigrationRequest(
            Guid.NewGuid(),
            MigrationKind.File,
            items.Select(i => i.Path).ToList(),
            FileUtilities.Normalize(destinationRoot),
            CreateJunction: false,
            DeleteSourceAfterVerify: true);

        return ctx.Coordinator.RequestMigrationAsync(request, progress, ct);
    }

    // ---------------- FR-CSLIM-05 Windows 残留 ----------------

    /// <inheritdoc />
    public IReadOnlyList<WindowsResidualItem> DetectWindowsResiduals()
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemDriveRoot = Path.GetPathRoot(systemRoot)!;
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var items = new List<WindowsResidualItem>();

        AddIfExists(items,
            WindowsResidualKind.WindowsOld,
            Path.Combine(systemDriveRoot, "Windows.old"),
            "清理后将无法回退到上一个 Windows 版本");
        AddIfExists(items,
            WindowsResidualKind.UpdateDownload,
            Path.Combine(systemRoot, "SoftwareDistribution", "Download"),
            "已下载的更新安装缓存，可安全删除");
        AddIfExists(items,
            WindowsResidualKind.WinSxSBackup,
            Path.Combine(systemRoot, "WinSxS", "Backup"),
            "组件备份，删除后无法卸载部分更新");
        AddIfExists(items,
            WindowsResidualKind.WinREAgent,
            Path.Combine(systemDriveRoot, "$WinREAgent"),
            "更新临时文件，可安全删除");
        AddIfExists(items,
            WindowsResidualKind.DeliveryOptimization,
            Path.Combine(programData, "Microsoft", "Windows", "DeliveryOptimization", "Cache"),
            "传递优化缓存，可安全删除");

        return items;
    }

    /// <inheritdoc />
    public async Task<OperationResult> EnqueueWindowsResidualCleanAsync(
        IReadOnlyList<WindowsResidualItem> items,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("模块尚未初始化");

        if (items.Count == 0)
        {
            throw new CslimItemsEmptyException();
        }

        var actions = items.Select(i => new OperationAction(
            ActionKind.DeleteFile,
            i.Target,
            null, i.SizeBytes, null, null, null, null, null, null, null))
            .ToList();

        var plan = new OperationPlan(
            Guid.NewGuid(),
            "cslim",
            "FR-CSLIM-05",
            $"清理 {items.Count} 项 Windows 更新残留",
            SafetyLevel.Medium,
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

    // ---------------- FR-CSLIM-08 保存位置 ----------------

    /// <inheritdoc />
    public Task<IReadOnlyList<SaveLocationSuggestion>> GetSaveLocationSuggestionsAsync(CancellationToken ct)
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemDriveRoot = Path.GetPathRoot(systemRoot)!;

        var targetDrive = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Where(d => !d.RootDirectory.Name.Equals(systemDriveRoot, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.AvailableFreeSpace)
            .FirstOrDefault();

        var result = new List<SaveLocationSuggestion>();
        foreach (var category in Enum.GetValues<SaveLocationCategory>())
        {
            if (!KnownFolderMap.TryGetValue(category, out var folderId))
            {
                result.Add(new SaveLocationSuggestion(
                    category, systemDriveRoot, targetDrive?.RootDirectory.Name ?? "?",
                    CanApply: false,
                    "该项 V1 暂不支持改写"));
                continue;
            }

            var currentPath = _knownFolders.GetPath(folderId);
            var currentDrive = currentPath is not null
                ? FileUtilities.GetDriveRoot(currentPath)
                : null;
            var canApply = targetDrive is not null
                           && currentDrive?.Equals(systemDriveRoot, StringComparison.OrdinalIgnoreCase) == true;

            result.Add(new SaveLocationSuggestion(
                category,
                currentDrive ?? "?",
                targetDrive?.RootDirectory.Name ?? "?",
                canApply,
                canApply
                    ? $"建议迁移到 {targetDrive!.RootDirectory.Name}"
                    : "当前无需迁移"));
        }
        return Task.FromResult<IReadOnlyList<SaveLocationSuggestion>>(result);
    }

    /// <inheritdoc />
    public async Task<OperationResult> ApplySaveLocationSuggestionsAsync(
        IReadOnlyList<SaveLocationCategory> categories,
        string targetDrive,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("模块尚未初始化");

        if (categories.Count == 0)
        {
            throw new CslimItemsEmptyException();
        }

        var normalizedTarget = FileUtilities.Normalize(targetDrive);
        var actions = new List<OperationAction>();

        foreach (var category in categories)
        {
            if (!KnownFolderMap.TryGetValue(category, out var folderId))
            {
                continue;
            }
            var currentPath = _knownFolders.GetPath(folderId);
            if (currentPath is null)
            {
                continue;
            }
            var leaf = Path.GetFileName(currentPath.TrimEnd(Path.DirectorySeparatorChar));
            var newPath = Path.Combine(normalizedTarget, leaf);

            actions.Add(new OperationAction(
                ActionKind.RedirectKnownFolder,
                null, null, null, null,
                folderId.ToString(), null,
                currentPath,
                newPath,
                null, null));
        }

        if (actions.Count == 0)
        {
            throw new CslimItemsEmptyException();
        }

        var plan = new OperationPlan(
            Guid.NewGuid(),
            "cslim",
            "FR-CSLIM-08",
            $"改写 {actions.Count} 项默认保存位置到 {normalizedTarget}",
            SafetyLevel.Medium,
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

    // ---------------- 私有工具 ----------------

    private long SumEstimatedAppSizes()
    {
        long total = 0;
        foreach (var hive in new[]
                 {
                     RegistryRoots.UninstallHklm,
                     RegistryRoots.UninstallHklmWow,
                     RegistryRoots.UninstallHkcu
                 })
        {
            foreach (var subName in SafeSubKeys(hive))
            {
                var key = Path.Combine(hive, subName);
                var sizeKb = _registry.GetValue(key, "EstimatedSize");
                if (long.TryParse(sizeKb, out var kb))
                {
                    total += kb * 1024;
                }
            }
        }
        return total;
    }

    private static List<string> BuildExclusions(string systemDriveRoot)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new List<string>
        {
            FileUtilities.Normalize(windows),
            FileUtilities.Normalize(Path.Combine(systemDriveRoot, "$Recycle.Bin")),
            FileUtilities.Normalize(Path.Combine(systemDriveRoot, "System Volume Information")),
            FileUtilities.Normalize(Path.Combine(systemDriveRoot, "Recovery"))
        };
    }

    private static bool IsExcludedRootFile(string file, IEnumerable<string> exclusions)
    {
        var leaf = Path.GetFileName(file);
        if (leaf is "pagefile.sys" or "hiberfil.sys" or "swapfile.sys")
        {
            return true;
        }
        var normalized = FileUtilities.Normalize(file);
        return exclusions.Any(e => FileUtilities.IsUnder(normalized, e));
    }

    private static LargeFileKind ClassifyByExtension(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" => LargeFileKind.Video,
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".raw" or ".psd" => LargeFileKind.Image,
            ".doc" or ".docx" or ".pdf" or ".txt" or ".xls" or ".xlsx" or ".ppt" or ".pptx" => LargeFileKind.Document,
            ".exe" or ".msi" or ".zip" or ".7z" or ".rar" or ".iso" => LargeFileKind.Installer,
            ".db" or ".mdb" or ".sqlite" or ".vmdk" or ".vhd" or ".vhdx" => LargeFileKind.Database,
            _ => LargeFileKind.Other
        };
    }

    private static DateTimeOffset SafeLastWrite(string path)
    {
        try
        {
            return new DateTimeOffset(new FileInfo(path).LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static void AddIfExists(
        List<WindowsResidualItem> items,
        WindowsResidualKind kind,
        string path,
        string warning)
    {
        if (Directory.Exists(path))
        {
            items.Add(new WindowsResidualItem(
                kind,
                path,
                FileUtilities.Measure(path, TimeSpan.FromSeconds(5), out _),
                warning));
        }
    }

    private static long RootFileSize(string driveRoot, string fileName)
    {
        var path = Path.Combine(driveRoot, fileName);
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

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

    private static string DescribeCategory(CDriveCategory category) => category switch
    {
        CDriveCategory.SystemReserved => "Windows 系统、系统保留文件",
        CDriveCategory.InstalledApps => "已安装应用",
        CDriveCategory.UserFiles => "用户文档与个人文件",
        CDriveCategory.TempFiles => "临时文件与缓存",
        CDriveCategory.Other => "其他文件",
        _ => category.ToString()
    };
}
