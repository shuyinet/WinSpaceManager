using System.Text.Json;
using SysMgr.Core.Operations;
using SysMgr.Core.Queue;
using SysMgr.Core.Safety;
using SysMgr.Modules.Internal;
using SysMgr.Modules.Visualization.Errors;

namespace SysMgr.Modules.Visualization;

/// <summary>
/// 磁盘空间可视化模块实现（IVisualizationService，FR-VIS-01/02/03）。
/// 扫描只读；删除/迁移经 TRUST-02 预演 + OpQueue/协调器；标签持久化到 config.json。
/// </summary>
public sealed class VisualizationService : IVisualizationService
{
    /// <summary>子项占父节点比例低于该值时并入"&lt;其余 N 项&gt;"（FR-VIS-01 聚合口径）。</summary>
    private const double AggregateRatio = 0.008;

    private const string TagsSection = "vis_tags";

    private IModuleContext? _ctx;

    public string ModuleId => "visualization";
    public string DisplayName => "磁盘可视化";
    public ModuleVersion Version => new(1, 5, "V1.5");

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
            new Dictionary<string, string> { ["scanner"] = "ok" }));

    // ---------------- FR-VIS-01 扫描 ----------------

    /// <inheritdoc />
    public Task<TreemapNode> ScanAsync(
        string rootPath,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var normalized = FileUtilities.Normalize(rootPath);

        if (File.Exists(normalized))
        {
            return Task.FromResult(BuildFileNode(normalized));
        }
        if (!Directory.Exists(normalized))
        {
            throw new InvalidScanTargetException(rootPath);
        }

        var scanned = 0;
        var node = ScanDirectory(normalized, progress, ref scanned, ct);
        node = node with { Name = normalized };
        progress?.Report(new ScanProgress(ScanPhase.Done, scanned, null, null, null));
        return Task.FromResult(node);
    }

    private TreemapNode ScanDirectory(
        string dir,
        IProgress<ScanProgress>? progress,
        ref int scanned,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var childNodes = new List<TreemapNode>();
        var kindBytes = new Dictionary<VisFileKind, long>();
        long directBytes = 0;
        var fileCount = 0;

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
            ct.ThrowIfCancellationRequested();
            long size;
            DateTimeOffset lastWrite;
            bool hidden;
            try
            {
                var info = new FileInfo(file);
                size = info.Length;
                lastWrite = new DateTimeOffset(info.LastWriteTimeUtc);
                hidden = IsHiddenOrSystem(info.Attributes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var kind = ClassifyByExtension(file);
            kindBytes[kind] = kindBytes.GetValueOrDefault(kind) + size;
            directBytes += size;
            fileCount++;
            scanned++;

            childNodes.Add(new TreemapNode(
                Path.GetFileName(file),
                file,
                false,
                size,
                kind,
                Array.Empty<TreemapNode>(),
                size,
                1,
                false,
                lastWrite,
                hidden));
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
            ct.ThrowIfCancellationRequested();
            FileAttributes attrs;
            try
            {
                attrs = File.GetAttributes(sub);
                if (attrs.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var child = ScanDirectory(sub, progress, ref scanned, ct);
            child = child with
            {
                IsHidden = IsHiddenOrSystem(attrs)
            };
            childNodes.Add(child);
            kindBytes[child.DominantKind] =
                kindBytes.GetValueOrDefault(child.DominantKind) + child.TotalBytes;

            scanned++;
            if (scanned % 500 == 0)
            {
                progress?.Report(new ScanProgress(ScanPhase.Sizing, scanned, null, null, sub));
            }
        }

        return Compose(dir, childNodes, directBytes, fileCount, kindBytes);
    }

    /// <summary>按占比聚合并构造目录节点（根/子目录共用；根名称由调用方改写）。</summary>
    private static TreemapNode Compose(
        string dir,
        List<TreemapNode> children,
        long directBytes,
        int fileCount,
        Dictionary<VisFileKind, long> kindBytes)
    {
        var total = children.Sum(c => c.TotalBytes);
        var threshold = (long)(total * AggregateRatio);

        var kept = new List<TreemapNode>();
        var restBytes = 0L;
        var restCount = 0;
        foreach (var child in children)
        {
            // 目录始终保留以便下钻；文件叶过小则聚合
            if (child.IsDirectory || child.TotalBytes >= threshold)
            {
                kept.Add(child);
            }
            else
            {
                restBytes += child.TotalBytes;
                restCount++;
            }
        }

        if (restCount > 0)
        {
            kept.Add(new TreemapNode(
                $"<其余 {restCount} 项>",
                dir,
                false,
                restBytes,
                VisFileKind.Other,
                Array.Empty<TreemapNode>(),
                restBytes,
                restCount,
                true,
                DateTimeOffset.MinValue,
                false));
        }

        kept.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));

        var dominant = kindBytes.Count == 0
            ? VisFileKind.Other
            : kindBytes.MaxBy(kv => kv.Value).Key;

        DateTimeOffset lastWrite;
        var hidden = false;
        try
        {
            var info = new DirectoryInfo(dir);
            lastWrite = new DateTimeOffset(info.LastWriteTimeUtc);
            hidden = IsHiddenOrSystem(info.Attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lastWrite = DateTimeOffset.MinValue;
        }

        return new TreemapNode(
            Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)),
            dir,
            true,
            total,
            dominant,
            kept,
            directBytes,
            fileCount,
            false,
            lastWrite,
            hidden);
    }

    private static TreemapNode BuildFileNode(string file)
    {
        long size;
        DateTimeOffset lastWrite;
        var hidden = false;
        try
        {
            var info = new FileInfo(file);
            size = info.Length;
            lastWrite = new DateTimeOffset(info.LastWriteTimeUtc);
            hidden = IsHiddenOrSystem(info.Attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            size = 0;
            lastWrite = DateTimeOffset.MinValue;
        }

        return new TreemapNode(
            Path.GetFileName(file),
            file,
            false,
            size,
            ClassifyByExtension(file),
            Array.Empty<TreemapNode>(),
            size,
            1,
            false,
            lastWrite,
            hidden);
    }

    // ---------------- FR-VIS-02 筛选 ----------------

    /// <inheritdoc />
    public TreemapNode FilterTree(
        TreemapNode root,
        string? nameOrExtension,
        long minSizeBytes,
        DateTimeOffset? modifiedAfter,
        bool excludeHidden)
    {
        var hasFilter = !string.IsNullOrWhiteSpace(nameOrExtension)
                        || minSizeBytes > 0
                        || modifiedAfter is not null
                        || excludeHidden;
        if (!hasFilter)
        {
            return root;
        }

        var filtered = FilterNode(root, nameOrExtension, minSizeBytes, modifiedAfter, excludeHidden);
        return filtered ?? root with
        {
            Children = Array.Empty<TreemapNode>(),
            TotalBytes = 0,
            DirectFileBytes = 0,
            FileCount = 0
        };
    }

    private static TreemapNode? FilterNode(
        TreemapNode node,
        string? text,
        long minSizeBytes,
        DateTimeOffset? modifiedAfter,
        bool excludeHidden)
    {
        if (excludeHidden && node.IsHidden && !node.IsOtherAggregate)
        {
            return null;
        }

        if (!node.IsDirectory)
        {
            // 聚合伪节点无法按名称/日期判定，启用文本/大小/日期筛选即不展示
            if (node.IsOtherAggregate)
            {
                var onlyHiddenFilter = string.IsNullOrWhiteSpace(text)
                                       && minSizeBytes <= 0
                                       && modifiedAfter is null;
                return onlyHiddenFilter ? node : null;
            }
            if (!MatchesText(node, text))
            {
                return null;
            }
            if (minSizeBytes > 0 && node.TotalBytes < minSizeBytes)
            {
                return null;
            }
            if (modifiedAfter is not null && node.LastWriteTime < modifiedAfter.Value)
            {
                return null;
            }
            return node;
        }

        var keptChildren = new List<TreemapNode>();
        var directKeptBytes = 0L;
        var fileCount = 0;
        foreach (var child in node.Children)
        {
            var kept = FilterNode(child, text, minSizeBytes, modifiedAfter, excludeHidden);
            if (kept is not null)
            {
                keptChildren.Add(kept);
                if (!kept.IsDirectory)
                {
                    directKeptBytes += kept.TotalBytes;
                    fileCount += kept.FileCount;
                }
            }
        }

        // 目录自身名称命中时保留整棵子树（常见用法：搜目录名定位）
        var selfMatch = !string.IsNullOrWhiteSpace(text)
                        && MatchesText(node, text)
                        && minSizeBytes <= 0
                        && modifiedAfter is null;
        if (selfMatch)
        {
            return node;
        }
        if (keptChildren.Count == 0)
        {
            return null;
        }

        var total = keptChildren.Sum(c => c.TotalBytes);
        return node with
        {
            Children = keptChildren,
            TotalBytes = total,
            DirectFileBytes = directKeptBytes,
            FileCount = fileCount
        };
    }

    private static bool MatchesText(TreemapNode node, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }
        var t = text.Trim();
        if (node.Name.Contains(t, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var ext = t.StartsWith('.') ? t : "." + t;
        return node.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- FR-VIS-02 删除 / 迁移 ----------------

    /// <inheritdoc />
    public async Task<OperationResult> EnqueueDeleteAsync(
        IReadOnlyList<string> paths,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("模块尚未初始化");
        if (paths.Count == 0)
        {
            throw new VisItemsEmptyException();
        }

        var actions = paths.Select(p => new OperationAction(
            ActionKind.DeleteFile,
            p,
            null,
            SafeSize(p), null, null, null, null, null, null, null)).ToList();

        var plan = new OperationPlan(
            Guid.NewGuid(),
            ModuleId,
            "FR-VIS-02",
            $"删除 {paths.Count} 项磁盘对象",
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

    /// <inheritdoc />
    public Task<OperationResult> EnqueueMigrationAsync(
        IReadOnlyList<string> paths,
        string destinationRoot,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("模块尚未初始化");
        if (paths.Count == 0)
        {
            throw new VisItemsEmptyException();
        }

        DriveInfo destination;
        try
        {
            destination = new DriveInfo(FileUtilities.Normalize(destinationRoot));
            if (destination.DriveType != DriveType.Fixed)
            {
                throw new InvalidScanTargetException(destinationRoot);
            }
        }
        catch (InvalidScanTargetException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            throw new InvalidScanTargetException(destinationRoot);
        }

        var request = new MigrationRequest(
            Guid.NewGuid(),
            MigrationKind.File,
            paths.ToList(),
            FileUtilities.Normalize(destinationRoot),
            CreateJunction: false,
            DeleteSourceAfterVerify: true);

        return ctx.Coordinator.RequestMigrationAsync(request, progress, ct);
    }

    // ---------------- FR-VIS-03 标签 / 导出 ----------------

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> GetTagsAsync(CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("模块尚未初始化");
        var tags = await ctx.Config.GetAsync<Dictionary<string, string>>(TagsSection, ct);
        return tags ?? new Dictionary<string, string>();
    }

    /// <inheritdoc />
    public async Task SetTagAsync(string path, string? tag, CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("模块尚未初始化");
        var tags = new Dictionary<string, string>(await GetTagsAsync(ct));
        if (string.IsNullOrWhiteSpace(tag))
        {
            tags.Remove(path);
        }
        else
        {
            tags[path] = tag.Trim();
        }
        await ctx.Config.UpdateAsync(TagsSection, tags, ct);
    }

    /// <inheritdoc />
    public async Task ExportSnapshotAsync(TreemapNode root, string filePath, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var payload = new
        {
            exportedAt = DateTimeOffset.Now,
            app = "sysmgr",
            kind = "vis_snapshot",
            root
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        await File.WriteAllTextAsync(filePath, json, ct);
    }

    // ---------------- 私有工具 ----------------

    internal static VisFileKind ClassifyByExtension(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" => VisFileKind.Video,
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".raw" or ".psd" => VisFileKind.Image,
            ".doc" or ".docx" or ".pdf" or ".txt" or ".xls" or ".xlsx" or ".ppt" or ".pptx" => VisFileKind.Document,
            ".exe" or ".msi" or ".zip" or ".7z" or ".rar" or ".iso" => VisFileKind.Installer,
            ".db" or ".mdb" or ".sqlite" or ".vmdk" or ".vhd" or ".vhdx" => VisFileKind.Database,
            _ => VisFileKind.Other
        };
    }

    private static bool IsHiddenOrSystem(FileAttributes attrs)
        => (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    private static long SafeSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
