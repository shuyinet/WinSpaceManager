using System.Security.Cryptography;
using SysMgr.Core.Operations;
using SysMgr.Core.Queue;
using SysMgr.Core.Safety;
using SysMgr.Modules.Internal;
using SysMgr.Modules.Migration.Errors;

namespace SysMgr.Modules.Migration;

/// <summary>
/// 文件迁移引擎实现（IMigrationService，FR-MIGRATE-01）。
/// 预检只读；执行构造 MoveFile/CopyFile + CreateJunction 动作计划，经 TRUST-02 预演 → OpQueue。
/// </summary>
public sealed class MigrationService : IMigrationService
{
    private IModuleContext? _ctx;

    public string ModuleId => "migration";
    public string DisplayName => "大文件搬家";
    public ModuleVersion Version => new(1, 0, "V1");

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
                ["queue"] = "ok"
            }));

    /// <inheritdoc />
    public async Task<MigrationValidation> ValidateAsync(
        MigrationRequest request,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var (_, validation) = await InspectAsync(request, ct);
        progress?.Report(new ScanProgress(ScanPhase.Done, 0, null, null, null));
        return validation;
    }

    /// <inheritdoc />
    public async Task<OperationResult> EnqueueMigrationAsync(
        MigrationRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("模块尚未初始化");

        var (entries, validation) = await InspectAsync(request, ct);
        if (!validation.CanMigrate)
        {
            throw new MigrationPrecheckFailedException(validation.Issues);
        }

        var actions = new List<OperationAction>();

        // 1. 逐文件复制（+校验）；删源与否决定动作类型
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var destination = ResolveConflict(entry.Destination);
            var kind = request.DeleteSourceAfterVerify
                ? ActionKind.MoveFile
                : ActionKind.CopyFile;

            actions.Add(new OperationAction(
                kind,
                entry.Source,
                destination,
                entry.SizeBytes,
                entry.Sha256,
                null, null, null, null, null, null));
        }

        if (request.Kind == MigrationKind.Folder)
        {
            var sourceRoot = request.Sources[0];

            // 2. 删源模式：移除已清空的源目录树（锁定文件残留会导致失败回滚）
            if (request.DeleteSourceAfterVerify)
            {
                actions.Add(new OperationAction(
                    ActionKind.DeleteFile,
                    sourceRoot,
                    null, null, null, null, null, null, null, null, null));
            }

            // 3. 联接必须在目录删除之后、全部复制成功之后
            if (request.CreateJunction)
            {
                actions.Add(new OperationAction(
                    ActionKind.CreateJunction,
                    sourceRoot,
                    request.Destination,
                    null, null, null, null, null, null, null, null));
            }
        }

        var plan = new OperationPlan(
            Guid.NewGuid(),
            "migration",
            "FR-MIGRATE-01",
            $"迁移 {entries.Count} 个文件到 {request.Destination}",
            SafetyLevel.Medium,
            actions,
            PreviewRequired: true,
            UserConfirmed: false,
            // 源内容在目标处已有校验副本，回滚从目标搬回；
            // 避免为待腾空的源卷（通常空间紧张的 C 盘）生成等量回滚快照
            DestinationBackedRollback: true);

        var preview = await ctx.Trust.PreviewAsync(plan, ct);
        if (!preview.Confirmed)
        {
            return new OperationResult(plan.PlanId, false, TimeSpan.Zero, "用户取消预演", null);
        }

        // Bound disk reads: excessive parallelism can reduce throughput on HDDs.
        var hashes = new string?[entries.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, entries.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
            async (i, token) =>
        {
            var entry = entries[i];
            if (entry.Sha256 is not null)
            {
                hashes[i] = entry.Sha256;
                return;
            }
            using var sha = SHA256.Create();
            await using var stream = new FileStream(entry.Source, FileMode.Open,
                FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await sha.ComputeHashAsync(stream, token);
            hashes[i] = Convert.ToHexString(hash);
        });
        // Update the shared action list only after all workers have completed.
        for (var i = 0; i < entries.Count; i++)
        {
            actions[i] = actions[i] with { Sha256 = hashes[i] };
        }

        var confirmedPlan = plan with { UserConfirmed = true };
        return await ctx.OpQueue.EnqueueAsync(confirmedPlan, progress, ct);
    }

    // ---------------- 预检内部 ----------------

    private async Task<(List<MigratedFileEntry> Entries, MigrationValidation Validation)> InspectAsync(
        MigrationRequest request,
        CancellationToken ct)
    {
        var issues = new List<string>();
        var warnings = new List<string>();

        // 1. 构造条目
        List<MigratedFileEntry> entries;
        try
        {
            entries = await Task.Run(() => BuildEntries(request, ct), ct);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            entries = new List<MigratedFileEntry>();
            issues.Add(ex.Message);
            return (entries, new MigrationValidation(
                false, issues, warnings, 0, 0));
        }

        if (entries.Count == 0)
        {
            issues.Add("源位置不存在或为空");
        }

        // 2. 系统关键路径检查
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (entries.Any(e => FileUtilities.IsUnder(e.Source, windows)))
        {
            issues.Add("源包含系统关键路径，禁止迁移");
        }

        // 3. 嵌套关系（仅文件夹）
        if (request.Kind == MigrationKind.Folder)
        {
            var sourceRoot = request.Sources[0];
            if (FileUtilities.IsUnder(request.Destination, sourceRoot)
                || FileUtilities.IsUnder(sourceRoot, request.Destination))
            {
                issues.Add("源与目标存在嵌套关系");
            }
        }

        // 4. 同卷警告
        var sourceDrive = entries.Count > 0 ? FileUtilities.GetDriveRoot(entries[0].Source) : null;
        var destinationDrive = FileUtilities.GetDriveRoot(request.Destination);
        if (sourceDrive is not null && destinationDrive is not null
            && sourceDrive.Equals(destinationDrive, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("源与目标在同一卷，迁移不释放该盘空间");
        }

        // 5. 空间与冲突
        var required = entries.Sum(e => e.SizeBytes);
        long available = 0;
        try
        {
            Directory.CreateDirectory(FileUtilities.Normalize(request.Destination));
            available = new DriveInfo(destinationDrive ?? request.Destination).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            issues.Add($"目标位置不可用：{ex.Message}");
        }
        if (required > available)
        {
            issues.Add("目标卷空间不足");
        }

        foreach (var entry in entries)
        {
            if (File.Exists(entry.Destination))
            {
                warnings.Add($"{entry.Destination} 已存在，将改名避免覆盖");
            }
        }

        var validation = new MigrationValidation(
            issues.Count == 0,
            issues,
            warnings,
            required,
            available);
        return (entries, validation);
    }

    private static List<MigratedFileEntry> BuildEntries(MigrationRequest request, CancellationToken ct)
    {
        if (request.Sources.Count == 0)
        {
            throw new ArgumentException("迁移源为空");
        }

        if (request.Kind == MigrationKind.File)
        {
            return request.Sources
                .Where(File.Exists)
                .Select(source => new MigratedFileEntry(
                    FileUtilities.Normalize(source),
                    Path.Combine(
                        FileUtilities.Normalize(request.Destination),
                        Path.GetFileName(source)),
                    new FileInfo(source).Length,
                    null))
                .ToList();
        }

        // Folder：保留相对目录结构，不跟随 reparse point
        var sourceRoot = FileUtilities.Normalize(request.Sources[0]);
        if (!Directory.Exists(sourceRoot))
        {
            throw new IOException($"源目录不存在：{sourceRoot}");
        }
        var destinationRoot = FileUtilities.Normalize(request.Destination);
        var entries = new List<MigratedFileEntry>();
        var pending = new Stack<string>();
        pending.Push(sourceRoot);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var rel = Path.GetRelativePath(sourceRoot, file);
                entries.Add(new MigratedFileEntry(
                    file,
                    Path.Combine(destinationRoot, rel),
                    new FileInfo(file).Length,
                    null));
            }
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (!File.GetAttributes(sub).HasFlag(FileAttributes.ReparsePoint))
                {
                    pending.Push(sub);
                }
            }
        }
        return entries;
    }

    private static string ResolveConflict(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
