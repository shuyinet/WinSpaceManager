using System.Text.Json;
using SysMgr.Core.Operations;
using SysMgr.Core.SystemAccess;

namespace SysMgr.Core.Rollback;

/// <summary>
/// 回滚引擎实现（TDD §2.1 / FR-ROLLBACK-01/02/03/04）。
/// 快照布局：<c>&lt;rollbackRoot&gt;/&lt;opId&gt;/</c> 下 meta.json（元数据）、manifest.json（捕获清单）、
/// files/（文件副本）、reg/（注册表树 JSON）。
/// V1 策略：可恢复项一律 Copy（注册表递归读为 JSON 重建）；不可恢复动作（Shred/KillProcess/
/// RunExternalUninstaller 等）走 PathRecordOnly 仅记录。VssShadow 策略在后续版本引入。
/// LRU 空间管理在批次 2（06-rollback.md）实现，V1 不自动清理快照。
/// </summary>
public sealed class FileRollbackEngine : IRollbackEngine
{
    private readonly string _rollbackRoot;
    private readonly IRegistry _registry;
    private readonly IKnownFolder? _knownFolder;
    private readonly ITaskQuery? _tasks;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public FileRollbackEngine(string rollbackRoot, IRegistry registry)
        : this(rollbackRoot, registry, knownFolder: null)
    {
    }

    public FileRollbackEngine(
        string rollbackRoot,
        IRegistry registry,
        IKnownFolder? knownFolder,
        ITaskQuery? tasks = null)
    {
        _rollbackRoot = rollbackRoot ?? throw new ArgumentNullException(nameof(rollbackRoot));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _knownFolder = knownFolder;
        _tasks = tasks;
    }

    /// <inheritdoc />
    public async Task<SnapshotHandle> CreateSnapshotAsync(OperationPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var snapshotDir = Path.Combine(_rollbackRoot, plan.PlanId.ToString("N"));
        Directory.CreateDirectory(snapshotDir);

        var manifest = new SnapshotManifest
        {
            CreatedAt = DateTimeOffset.Now,
            ModuleId = plan.ModuleId,
            FrId = plan.FrId,
            Description = plan.Description,
            Actions = new List<ActionCapture>()
        };

        try
        {
            for (var i = 0; i < plan.Actions.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var action = plan.Actions[i];
                var capture = await CaptureAsync(
                    i, action, snapshotDir, plan.DestinationBackedRollback, ct);
                manifest.Actions.Add(capture);
            }

            manifest.TotalSizeBytes = manifest.Actions.Sum(a => a.SizeBytes);
            manifest.Status = SnapshotStatus.Active;
            await WriteJsonAsync(Path.Combine(snapshotDir, "manifest.json"), manifest, ct);
        }
        catch
        {
            // 快照失败（如磁盘空间不足）时清理半成品，避免残留文件继续占用磁盘
            TryDeleteDirectory(snapshotDir);
            throw;
        }

        var strategy = manifest.Actions.Any(a => a.Restorable)
            ? RollbackStrategy.Copy
            : RollbackStrategy.PathRecordOnly;

        return new SnapshotHandle(plan.PlanId, snapshotDir, strategy);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // 清理残留失败不应覆盖原始异常
        }
    }

    /// <inheritdoc />
    public async Task CommitAsync(SnapshotHandle handle, CancellationToken ct)
    {
        var manifest = await LoadManifestAsync(handle, ct);
        manifest.Status = SnapshotStatus.Committed;
        await WriteJsonAsync(Path.Combine(handle.SnapshotDir, "manifest.json"), manifest, ct);
    }

    /// <inheritdoc />
    public async Task<RollbackResult> RollbackAsync(Guid opId, CancellationToken ct)
    {
        var snapshotDir = Path.Combine(_rollbackRoot, opId.ToString("N"));
        var manifestPath = Path.Combine(snapshotDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return new RollbackResult(false, null, ["未找到对应快照，无法回滚"]);
        }

        var manifest = await ReadJsonAsync<SnapshotManifest>(manifestPath, ct);
        var conflicts = new List<string>();

        // 倒序恢复（后执行的动作先恢复）
        for (var i = manifest.Actions.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            var capture = manifest.Actions[i];
            RestoreCapture(capture, snapshotDir, conflicts);
        }

        var restorableCount = manifest.Actions.Count(a => a.Restorable);
        manifest.Status = conflicts.Count > 0 && conflicts.Count == restorableCount
            ? SnapshotStatus.FailedNeedsManual
            : SnapshotStatus.RolledBack;
        await WriteJsonAsync(manifestPath, manifest, ct);

        string? reportPath = null;
        if (conflicts.Count > 0)
        {
            reportPath = Path.Combine(snapshotDir, $"rollback_failed_{DateTimeOffset.Now:yyyyMMddHHmmss}.txt");
            await File.WriteAllLinesAsync(reportPath, conflicts, ct);
        }

        return new RollbackResult(
            conflicts.Count == 0 || manifest.Status == SnapshotStatus.RolledBack,
            reportPath,
            conflicts);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RollbackEntry> EnumerateHistoryAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(_rollbackRoot))
        {
            yield break;
        }

        foreach (var dir in Directory.EnumerateDirectories(_rollbackRoot))
        {
            ct.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }
            var manifest = await ReadJsonAsync<SnapshotManifest>(manifestPath, ct);
            var opId = Guid.ParseExact(Path.GetFileName(dir)!, "N");
            yield return new RollbackEntry(
                opId,
                manifest.CreatedAt,
                manifest.ModuleId,
                manifest.FrId,
                manifest.Description,
                manifest.Actions.Any(a => a.Restorable)
                    ? RollbackStrategy.Copy
                    : RollbackStrategy.PathRecordOnly,
                manifest.TotalSizeBytes,
                manifest.Status);
        }
    }

    private async Task<ActionCapture> CaptureAsync(
        int actionIndex,
        OperationAction action,
        string snapshotDir,
        bool destinationBacked,
        CancellationToken ct)
    {
        var capture = new ActionCapture
        {
            ActionIndex = actionIndex,
            Kind = action.Kind.ToString(),
            Restorable = false,
            SizeBytes = 0
        };

        switch (action.Kind)
        {
            case ActionKind.MoveFile when destinationBacked:
                // 搬家类操作：源内容在目标处已有校验通过的副本，
                // 回滚直接从目标搬回，绝不在源卷（通常是待腾空的 C 盘）再复制一份等量数据
                CaptureMoveByDestination(action, capture);
                break;

            case ActionKind.DeleteFile when destinationBacked:
                if (Directory.Exists(action.Target))
                {
                    CapturePathOnly(action, capture);
                }
                else
                {
                    CaptureFile(action, capture, snapshotDir);
                }
                break;

            case ActionKind.DeleteFile:
            case ActionKind.MoveFile:
                CaptureFile(action, capture, snapshotDir);
                break;

            case ActionKind.DeleteRegistry:
                await CaptureRegistryTreeAsync(action, capture, snapshotDir, ct);
                break;

            case ActionKind.SetRegistry:
                CaptureRegistryValue(action, capture);
                break;

            case ActionKind.RedirectKnownFolder:
                CaptureKnownFolderRedirect(action, capture);
                break;

            case ActionKind.SetStartupEnabled:
                CaptureStartupToggle(action, capture);
                break;

            case ActionKind.SetScheduledTaskEnabled:
                CaptureScheduledTaskToggle(action, capture);
                break;

            case ActionKind.StopService:
            case ActionKind.DisableService:
            case ActionKind.DisableStartup:
            case ActionKind.DeleteScheduledTask:
            case ActionKind.KillProcess:
            case ActionKind.RunExternalUninstaller:
            case ActionKind.CreateJunction:
            case ActionKind.CopyFile:
            case ActionKind.Shred:
            case ActionKind.Purge:
                // 不可逆或无状态可恢复，仅记录（垃圾清理不复制文件，避免占用等量磁盘空间）
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }

        return capture;
    }

    private void CapturePathOnly(OperationAction action, ActionCapture capture)
    {
        var path = action.Target;
        if (path is null || !PathExists(path))
        {
            return;
        }

        // 只记录路径：目录内容由同批 MoveFile 记录负责恢复，回滚时仅重建空目录
        capture.OriginalPath = path;
        capture.IsDirectory = Directory.Exists(path);
        capture.SizeBytes = 0;
        capture.Restorable = true;
    }

    private void CaptureMoveByDestination(OperationAction action, ActionCapture capture)
    {
        if (action.Target is null || action.NewPath is null)
        {
            return;
        }

        // 源内容已在目标副本中（复制时已做 SHA-256 校验），回滚从目标搬回即可
        capture.OriginalPath = action.Target;
        capture.RestoreFromPath = action.NewPath;
        capture.SizeBytes = 0;
        capture.Restorable = true;
    }

    private void CaptureFile(OperationAction action, ActionCapture capture, string snapshotDir)
    {
        var source = action.Target;
        if (source is null || !PathExists(source))
        {
            // 源已不存在，无可恢复内容（操作本身对它无影响）
            return;
        }

        var isDirectory = Directory.Exists(source);
        var storeDir = Path.Combine(snapshotDir, "files", capture.ActionIndex.ToString());
        Directory.CreateDirectory(storeDir);

        long size;
        string leafName;
        if (isDirectory)
        {
            leafName = new DirectoryInfo(source).Name;
            CopyDirectory(source, Path.Combine(storeDir, leafName));
            size = DirSize(source);
        }
        else
        {
            leafName = Path.GetFileName(source);
            Directory.CreateDirectory(Path.Combine(storeDir));
            File.Copy(source, Path.Combine(storeDir, leafName), overwrite: true);
            size = new FileInfo(source).Length;
        }

        capture.OriginalPath = source;
        capture.StoredPath = Path.Combine("files", capture.ActionIndex.ToString(), leafName);
        capture.IsDirectory = isDirectory;
        capture.SizeBytes = size;
        capture.Restorable = true;
    }

    private async Task CaptureRegistryTreeAsync(
        OperationAction action,
        ActionCapture capture,
        string snapshotDir,
        CancellationToken ct)
    {
        if (action.Key is null || !_registry.KeyExists(action.Key))
        {
            return;
        }

        var tree = ReadRegistryNode(action.Key);
        var regDir = Path.Combine(snapshotDir, "reg");
        Directory.CreateDirectory(regDir);
        var fileName = capture.ActionIndex + ".json";
        await WriteJsonAsync(Path.Combine(regDir, fileName), tree, ct);

        capture.OriginalPath = action.Key;
        capture.ValueName = action.ValueName;
        capture.StoredPath = Path.Combine("reg", fileName);
        capture.SizeBytes = new FileInfo(Path.Combine(regDir, fileName)).Length;
        capture.Restorable = true;
    }

    private void CaptureRegistryValue(OperationAction action, ActionCapture capture)
    {
        if (action.Key is null)
        {
            return;
        }

        capture.OriginalPath = action.Key;
        capture.ValueName = action.ValueName;
        capture.OriginalValue = _registry.GetValue(action.Key, action.ValueName);
        capture.Restorable = true;
    }

    private void CaptureKnownFolderRedirect(OperationAction action, ActionCapture capture)
    {
        // Key=Known Folder ID GUID；OriginalValue 由模块在改写前填入当前路径
        if (action.Key is null)
        {
            return;
        }

        capture.OriginalPath = action.Key;
        capture.OriginalValue = action.OriginalValue;
        capture.Restorable = true;
    }

    private void CaptureStartupToggle(OperationAction action, ActionCapture capture)
    {
        // Key=StartupApproved 键路径；OriginalValue 由模块预填原 REG_BINARY 十六进制（null=原本无记录）
        if (action.Key is null)
        {
            return;
        }

        capture.OriginalPath = action.Key;
        capture.ValueName = action.ValueName;
        capture.OriginalValue = action.OriginalValue;
        capture.Restorable = true;
    }

    private void CaptureScheduledTaskToggle(OperationAction action, ActionCapture capture)
    {
        // TaskPath=任务路径；OriginalValue 由模块预填原启用状态（"true"/"false"）
        if (action.TaskPath is null)
        {
            return;
        }

        capture.OriginalPath = action.TaskPath;
        capture.OriginalValue = action.OriginalValue;
        capture.Restorable = true;
    }

    private RegistryNodeDto ReadRegistryNode(string keyPath)
    {
        var node = new RegistryNodeDto
        {
            Name = keyPath,
            Values = _registry
                .EnumerateValues(keyPath)
                .Select(v => new RegistryValueDto(v.Name, v.Data))
                .ToList(),
            SubKeys = new List<RegistryNodeDto>()
        };
        foreach (var subName in _registry.EnumerateSubKeyNames(keyPath))
        {
            node.SubKeys.Add(ReadRegistryNode(Path.Combine(keyPath, subName)));
        }
        return node;
    }

    private void RestoreCapture(ActionCapture capture, string snapshotDir, List<string> conflicts)
    {
        if (!capture.Restorable)
        {
            return;
        }

        if (capture.StoredPath is not null && capture.OriginalPath is not null)
        {
            var storedFull = Path.Combine(snapshotDir, capture.StoredPath);
            if (capture.IsDirectory)
            {
                if (Directory.Exists(capture.OriginalPath))
                {
                    conflicts.Add($"目标目录已存在，跳过恢复：{capture.OriginalPath}");
                }
                else
                {
                    CopyDirectory(storedFull, capture.OriginalPath);
                }
            }
            else if (capture.StoredPath.StartsWith("reg" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                RestoreRegistryTree(storedFull, conflicts);
            }
            else
            {
                if (File.Exists(capture.OriginalPath))
                {
                    conflicts.Add($"目标文件已存在，跳过恢复：{capture.OriginalPath}");
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(capture.OriginalPath)!);
                    File.Copy(storedFull, capture.OriginalPath);
                }
            }
        }
        else if (capture.RestoreFromPath is not null && capture.OriginalPath is not null)
        {
            // 搬家回滚：从目标副本搬回源位置（源卷无需预留等量空间）
            if (PathExists(capture.OriginalPath))
            {
                conflicts.Add($"目标位置已存在，跳过恢复：{capture.OriginalPath}");
            }
            else if (File.Exists(capture.RestoreFromPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(capture.OriginalPath)!);
                File.Move(capture.RestoreFromPath, capture.OriginalPath);
            }
            else
            {
                conflicts.Add($"副本已不存在，无法恢复：{capture.OriginalPath}");
            }
        }
        else if (capture.Kind == ActionKind.DeleteFile.ToString()
                 && capture.IsDirectory
                 && capture.OriginalPath is not null)
        {
            // 仅重建被删除的空目录（其内容由 MoveFile 记录逐文件恢复）
            if (!Directory.Exists(capture.OriginalPath))
            {
                Directory.CreateDirectory(capture.OriginalPath);
            }
        }
        else if (capture.Kind == ActionKind.SetRegistry.ToString()
                 && capture.OriginalPath is not null)
        {
            // 原值恢复（原值为 null 表示原本不存在，删除当前值）
            if (capture.OriginalValue is null)
            {
                _registry.DeleteValue(capture.OriginalPath, capture.ValueName ?? string.Empty);
            }
            else
            {
                _registry.SetValue(capture.OriginalPath, capture.ValueName ?? string.Empty, capture.OriginalValue);
            }
        }
        else if (capture.Kind == ActionKind.SetStartupEnabled.ToString()
                 && capture.OriginalPath is not null)
        {
            // StartupApproved 原状态恢复（原值为 null 表示原本无记录，删除当前值）
            if (capture.OriginalValue is null)
            {
                _registry.DeleteValue(capture.OriginalPath, capture.ValueName ?? string.Empty);
            }
            else
            {
                _registry.SetBinaryValue(
                    capture.OriginalPath,
                    capture.ValueName ?? string.Empty,
                    Convert.FromHexString(capture.OriginalValue));
            }
        }
        else if (capture.Kind == ActionKind.SetScheduledTaskEnabled.ToString()
                 && capture.OriginalPath is not null)
        {
            // 计划任务状态回滚：恢复原启用状态；未注入 ITaskQuery（如旧测试）则记为冲突
            if (_tasks is null)
            {
                conflicts.Add($"无法自动恢复计划任务状态（缺少 ITaskQuery）：{capture.OriginalPath}");
            }
            else if (capture.OriginalValue is not null)
            {
                _tasks.SetTaskEnabled(capture.OriginalPath, bool.Parse(capture.OriginalValue));
            }
        }
        else if (capture.Kind == ActionKind.RedirectKnownFolder.ToString()
                 && capture.OriginalPath is not null)
        {
            // Known Folder 回滚：恢复原路径；未注入 IKnownFolder（如旧测试）则记为冲突
            if (_knownFolder is null)
            {
                conflicts.Add($"无法自动恢复 Known Folder（缺少 IKnownFolder）：{capture.OriginalPath}");
            }
            else if (capture.OriginalValue is not null
                     && Guid.TryParse(capture.OriginalPath, out var folderId))
            {
                _knownFolder.SetPath(folderId, capture.OriginalValue);
            }
        }
    }

    private void RestoreRegistryTree(string storedJsonPath, List<string> conflicts)
    {
        var tree = ReadJsonAsync<RegistryNodeDto>(storedJsonPath, CancellationToken.None)
            .GetAwaiter().GetResult();
        WriteRegistryNode(tree, conflicts);
    }

    private void WriteRegistryNode(RegistryNodeDto node, List<string> conflicts)
    {
        if (!_registry.KeyExists(node.Name))
        {
            _registry.CreateKey(node.Name);
        }
        else
        {
            conflicts.Add($"注册表键已存在，合并写入：{node.Name}");
        }
        foreach (var value in node.Values)
        {
            if (value.Data is not null)
            {
                _registry.SetValue(node.Name, value.Name, value.Data);
            }
        }
        foreach (var sub in node.SubKeys)
        {
            WriteRegistryNode(sub, conflicts);
        }
    }

    private async Task<SnapshotManifest> LoadManifestAsync(SnapshotHandle handle, CancellationToken ct)
    {
        return await ReadJsonAsync<SnapshotManifest>(Path.Combine(handle.SnapshotDir, "manifest.json"), ct);
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<T>(text)!;
    }

    private async Task WriteJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), ct);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private static long DirSize(string directory)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            total += new FileInfo(file).Length;
        }
        return total;
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>快照清单（manifest.json 持久化模型）。</summary>
    private sealed class SnapshotManifest
    {
        public DateTimeOffset CreatedAt { get; set; }
        public string ModuleId { get; set; } = string.Empty;
        public string? FrId { get; set; }
        public string Description { get; set; } = string.Empty;
        public long TotalSizeBytes { get; set; }
        public SnapshotStatus Status { get; set; }
        public List<ActionCapture> Actions { get; set; } = new();
    }

    /// <summary>单个动作的捕获记录。</summary>
    private sealed class ActionCapture
    {
        public int ActionIndex { get; set; }
        public string Kind { get; set; } = string.Empty;
        public bool Restorable { get; set; }
        public long SizeBytes { get; set; }
        public string? OriginalPath { get; set; }
        public string? StoredPath { get; set; }
        public string? RestoreFromPath { get; set; }
        public string? ValueName { get; set; }
        public string? OriginalValue { get; set; }
        public bool IsDirectory { get; set; }
    }

    private sealed record RegistryNodeDto
    {
        public string Name { get; init; } = string.Empty;
        public List<RegistryValueDto> Values { get; init; } = new();
        public List<RegistryNodeDto> SubKeys { get; init; } = new();
    }

    private sealed record RegistryValueDto(string Name, string? Data);
}
