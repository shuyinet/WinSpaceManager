using SysMgr.Core.Operations;
using SysMgr.Core.Queue;
using SysMgr.Core.Safety;
using SysMgr.Modules.Shred.Errors;

namespace SysMgr.Modules.Shred;

/// <summary>
/// 文件粉碎模块实现（IShredService，FR-SHRED-01/02）。
/// 分析只读；粉碎把目录展开为逐文件 action（目录本身随后由空目录清理动作移除），
/// 经 TRUST-02 预演确认后入 OpQueue。安全硬护栏：盘符根目录、链接/挂载点、
/// 以及分级器判 Low（系统关键路径）的目标一律拒绝。
/// </summary>
public sealed class ShredService : IShredService
{
    private IModuleContext? _ctx;

    public string ModuleId => "shred";
    public string DisplayName => "文件粉碎";
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
            new Dictionary<string, string> { ["executor"] = "ok" }));

    // ---------------- FR-SHRED-01 分析 ----------------

    /// <inheritdoc />
    public Task<IReadOnlyList<ShredTarget>> AnalyzeAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var ctx = _ctx ?? throw new InvalidOperationException("模块尚未初始化");

        var targets = new List<ShredTarget>();
        foreach (var raw in paths)
        {
            ct.ThrowIfCancellationRequested();
            var path = NormalizeAndValidate(raw);

            bool isDirectory = Directory.Exists(path);
            if (!isDirectory && !File.Exists(path))
            {
                throw new ShredTargetNotFoundException(path);
            }
            if (IsReparsePoint(path))
            {
                throw new ShredNotSupportedException(path);
            }

            long fileCount;
            long totalBytes;
            if (isDirectory)
            {
                (fileCount, totalBytes) = MeasureDirectory(path);
            }
            else
            {
                fileCount = 1;
                totalBytes = new FileInfo(path).Length;
            }

            var verdict = ctx.Safety.Classify(new FileItem(path, totalBytes));
            if (verdict.Level == SafetyLevel.Low)
            {
                throw new ShredCriticalPathException(path);
            }

            targets.Add(new ShredTarget(path, isDirectory, fileCount, totalBytes,
                verdict.Level, verdict.Reason));
        }
        return Task.FromResult<IReadOnlyList<ShredTarget>>(targets);
    }

    // ---------------- FR-SHRED-01/02 粉碎 ----------------

    /// <inheritdoc />
    public async Task<OperationResult> ShredAsync(
        IEnumerable<string> paths,
        ShredMode mode,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var ctx = _ctx ?? throw new InvalidOperationException("模块尚未初始化");

        // 复用分析口径：护栏（关键路径/链接/不存在）在 AnalyzeAsync 内统一抛
        var targets = await AnalyzeAsync(paths, ct);

        var files = new List<(string Path, long Size)>();
        var emptyDirs = new List<string>();
        var worst = SafetyLevel.High;
        foreach (var target in targets)
        {
            worst = (SafetyLevel)Math.Max((int)worst, (int)target.Level);
            if (!target.IsDirectory)
            {
                files.Add((target.Path, target.TotalBytes));
                continue;
            }

            foreach (var file in EnumerateFiles(target.Path))
            {
                files.Add((file, new FileInfo(file).Length));
            }
            emptyDirs.Add(target.Path);
        }

        if (files.Count == 0)
        {
            throw new ShredEmptySelectionException();
        }

        var passes = (int)mode;
        var actions = new List<OperationAction>(files.Count + emptyDirs.Count);
        foreach (var (file, size) in files)
        {
            actions.Add(new OperationAction(
                ActionKind.Shred,
                file,
                null, size, null, null, null, null, null, null, null,
                passes));
        }
        // 目录粉碎收尾：文件全碎后移除空目录树（DeleteFile 执行器支持目录）
        foreach (var dir in emptyDirs)
        {
            actions.Add(new OperationAction(
                ActionKind.DeleteFile,
                dir,
                null, null, null, null, null, null, null, null, null));
        }

        var totalBytes = files.Sum(f => f.Size);
        var plan = new OperationPlan(
            Guid.NewGuid(),
            ModuleId,
            "FR-SHRED-01",
            $"粉碎 {files.Count} 个文件（{FormatBytes(totalBytes)}，{ModeText(mode)}）——粉碎后不可恢复，不进入回滚历史",
            worst,
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

    // ---------------- 护栏与统计 ----------------

    /// <summary>规范化路径并拒绝盘符根目录等毁灭性目标。</summary>
    internal static string NormalizeAndValidate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ShredEmptySelectionException();
        }

        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"')));
        var root = Path.GetPathRoot(full);
        if (string.Equals(full.TrimEnd('\\', '/'), root?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            throw new ShredCriticalPathException(full);
        }
        return full;
    }

    private static bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    /// <summary>递归统计目录内文件数与总大小（含只读/隐藏，跳过不可访问项）。</summary>
    internal static (long FileCount, long TotalBytes) MeasureDirectory(string directory)
    {
        long count = 0, bytes = 0;
        foreach (var file in EnumerateFiles(directory))
        {
            count++;
            try
            {
                bytes += new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 文件在统计间隙被占用/删除：大小按 0 计
            }
        }
        return (count, bytes);
    }

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0 // 含只读/隐藏（FR-SHRED-01）
        };
        return Directory.EnumerateFiles(directory, "*", options);
    }

    internal static string ModeText(ShredMode mode) => mode switch
    {
        ShredMode.Quick => "快速粉碎 · 1 遍零写",
        ShredMode.Secure3 => "安全粉碎 · 3 遍覆写",
        ShredMode.Secure7 => "安全粉碎 · 7 遍覆写",
        _ => $"{(int)mode} 遍覆写"
    };

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
