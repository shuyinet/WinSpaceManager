using System.Diagnostics;
using System.Security.Cryptography;
using SysMgr.Core.Operations;
using SysMgr.Core.Queue;
using SysMgr.Core.SystemAccess;

namespace SysMgr.Infra;

/// <summary>
/// Windows 动作执行器（IOperationExecutor 端口实现）。
/// 队列只编排；本类按 ActionKind 把动作落到文件/注册表/服务/任务/进程等 Facade。
/// </summary>
public sealed class WindowsOperationExecutor : IOperationExecutor
{
    private readonly IRegistry _registry;
    private readonly IServiceQuery _services;
    private readonly ITaskQuery _tasks;
    private readonly IRecycleBin _recycleBin;
    private readonly ILinkManager _links;
    private readonly IProcessQuery _processes;
    private readonly IKnownFolder _knownFolders;

    /// <summary>回收站虚拟目标（与 Cleanup 模块约定，队列执行时识别）。</summary>
    public const string RecycleBinTarget = "$RecycleBin";

    public WindowsOperationExecutor(
        IRegistry registry,
        IServiceQuery services,
        ITaskQuery tasks,
        IRecycleBin recycleBin,
        ILinkManager links,
        IProcessQuery processes,
        IKnownFolder knownFolders)
    {
        _registry = registry;
        _services = services;
        _tasks = tasks;
        _recycleBin = recycleBin;
        _links = links;
        _processes = processes;
        _knownFolders = knownFolders;
    }

    /// <inheritdoc />
    public Task ExecuteAsync(
        OperationAction action,
        IProgress<OperationProgress>? progress,
        CancellationToken ct) => action.Kind switch
    {
        ActionKind.DeleteFile => DeleteFileAsync(action, ct),
        ActionKind.Purge => PurgeAsync(action, ct),
        ActionKind.MoveFile => MoveFileAsync(action, ct),
        ActionKind.DeleteRegistry => DeleteRegistryAsync(action),
        ActionKind.SetRegistry => SetRegistry(action),
        ActionKind.StopService => StopService(action),
        ActionKind.DisableService => DisableService(action),
        ActionKind.DisableStartup => DisableStartup(action),
        ActionKind.DeleteScheduledTask => DeleteScheduledTask(action),
        ActionKind.KillProcess => KillProcessAsync(action),
        ActionKind.RunExternalUninstaller => RunExternalUninstallerAsync(action, ct),
        ActionKind.CreateJunction => CreateJunction(action),
        ActionKind.RedirectKnownFolder => RedirectKnownFolder(action),
        ActionKind.SetStartupEnabled => SetStartupEnabled(action),
        ActionKind.SetScheduledTaskEnabled => SetScheduledTaskEnabled(action),
        ActionKind.CopyFile => CopyFileAsync(action, ct),
        ActionKind.Shred => Shred(action),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private Task DeleteFileAsync(OperationAction action, CancellationToken ct)
    {
        if (RecycleBinTarget.Equals(action.Target, StringComparison.Ordinal))
        {
            _recycleBin.Empty();
            return Task.CompletedTask;
        }

        if (Directory.Exists(action.Target))
        {
            Directory.Delete(action.Target, recursive: true);
        }
        else if (File.Exists(action.Target))
        {
            ct.ThrowIfCancellationRequested();
            File.Delete(action.Target);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 垃圾清理：尽最大努力删除，跳过被进程占用/无权限的文件与目录；
    /// 未删掉的部分由 UI 重新测量并以「剩余体积」展示，不会让整批清理失败。
    /// </summary>
    private Task PurgeAsync(OperationAction action, CancellationToken ct)
    {
        if (RecycleBinTarget.Equals(action.Target, StringComparison.Ordinal))
        {
            _recycleBin.Empty();
            return Task.CompletedTask;
        }

        var target = action.Target;
        if (string.IsNullOrWhiteSpace(target))
        {
            return Task.CompletedTask;
        }

        if (Directory.Exists(target))
        {
            PurgeDirectory(target, ct);
        }
        else if (File.Exists(target))
        {
            TryDeleteFile(target, ct);
        }
        return Task.CompletedTask;
    }

    private static void PurgeDirectory(string root, CancellationToken ct)
    {
        var pending = new Stack<string>();
        var visited = new List<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            visited.Add(dir);

            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                TryDeleteFile(file, ct);
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var child in children)
            {
                pending.Push(child);
            }
        }

        // 自底向上删除空目录；被占用文件所在目录删除失败则保留
        foreach (var dir in visited.OrderByDescending(d => d.Length))
        {
            try
            {
                Directory.Delete(dir, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 目录非空或被占用，保留
            }
        }
    }

    private static void TryDeleteFile(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 文件正被其他进程使用，跳过并保留（后续由剩余体积统计展示）
        }
    }

    private async Task MoveFileAsync(OperationAction action, CancellationToken ct)
    {
        try
        {
            await CopyAndVerifyAsync(action, ct);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                $"复制或校验权限不足：源 {action.Target}；目标 {action.NewPath}。请检查目录权限及安全软件拦截。", ex);
        }
        var source = action.Target!;
        var attributes = File.GetAttributes(source);
        try
        {
            // Windows refuses to delete read-only files even when elevated.
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(source, attributes & ~FileAttributes.ReadOnly);
            File.Delete(source);
        }
        catch (Exception ex)
        {
            if (File.Exists(source))
                File.SetAttributes(source, attributes);
            if (ex is UnauthorizedAccessException)
                throw new UnauthorizedAccessException(
                    $"副本已校验，但无法删除源文件：{source}。请检查文件权限或退出占用该文件的软件。", ex);
            throw;
        }
    }

    private Task CopyFileAsync(OperationAction action, CancellationToken ct)
        => CopyAndVerifyAsync(action, ct);

    /// <summary>
    /// 复制 + SHA-256 校验核心（MoveFile/CopyFile 共用）。
    /// 目标先落 .partial，校验通过才改名，防半成品被误用。
    /// </summary>
    private async Task CopyAndVerifyAsync(OperationAction action, CancellationToken ct)
    {
        var source = action.Target ?? throw new InvalidOperationException("缺少源路径");
        var destination = action.NewPath ?? throw new InvalidOperationException("缺少目标路径");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partialPath = destination + ".partial";

        await using (var src = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var dst = File.Create(partialPath))
        {
            await src.CopyToAsync(dst, ct);
        }

        // 校验目标哈希
        if (!string.IsNullOrEmpty(action.Sha256))
        {
            var actual = await ComputeSha256Async(partialPath, ct);
            if (!string.Equals(actual, action.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partialPath);
                throw new IOException($"文件校验失败：{source}");
            }
        }

        File.Move(partialPath, destination, overwrite: false);
    }

    private Task DeleteRegistryAsync(OperationAction action)
    {
        if (action.Key is null)
        {
            throw new InvalidOperationException("DeleteRegistry 缺少键路径");
        }
        if (action.ValueName is not null)
        {
            _registry.DeleteValue(action.Key, action.ValueName);
        }
        else
        {
            _registry.DeleteKeyTree(action.Key);
        }
        return Task.CompletedTask;
    }

    private Task SetRegistry(OperationAction action)
    {
        if (action.Key is null || action.NewValue is null)
        {
            throw new InvalidOperationException("SetRegistry 缺少键路径或新值");
        }
        _registry.SetValue(action.Key, action.ValueName ?? string.Empty, action.NewValue);
        return Task.CompletedTask;
    }

    private Task StopService(OperationAction action)
    {
        _services.Stop(action.ServiceName ?? throw new InvalidOperationException("StopService 缺少服务名"));
        return Task.CompletedTask;
    }

    private Task DisableService(OperationAction action)
    {
        var name = action.ServiceName ?? throw new InvalidOperationException("DisableService 缺少服务名");
        _services.Stop(name);
        // Start=4 (Disabled)
        _registry.SetValue($@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{name}", "Start", "4");
        return Task.CompletedTask;
    }

    private Task DisableStartup(OperationAction action)
    {
        // V1 与 Run 键值删除同语义（目标=键路径，ValueName 携带值名）
        if (action.Target is null)
        {
            throw new InvalidOperationException("DisableStartup 缺少启动项目标");
        }
        _registry.DeleteValue(action.Target, action.ValueName ?? string.Empty);
        return Task.CompletedTask;
    }

    private Task DeleteScheduledTask(OperationAction action)
    {
        _tasks.DeleteTask(action.TaskPath ?? throw new InvalidOperationException("DeleteScheduledTask 缺少任务路径"));
        return Task.CompletedTask;
    }

    private Task KillProcessAsync(OperationAction action)
    {
        var target = action.Target ?? throw new InvalidOperationException("KillProcess 缺少目标");
        var byPath = target.Contains(Path.DirectorySeparatorChar) || target.Contains(Path.AltDirectorySeparatorChar);

        var victims = _processes.Enumerate()
            .Where(p => byPath
                ? p.ExecutablePath is not null
                  && p.ExecutablePath.StartsWith(target, StringComparison.OrdinalIgnoreCase)
                : p.ProcessName.Equals(target, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var victim in victims)
        {
            _processes.Kill(victim.Id, entireProcessTree: true);
        }
        return Task.CompletedTask;
    }

    private async Task RunExternalUninstallerAsync(OperationAction action, CancellationToken ct)
    {
        var commandLine = action.Target ?? throw new InvalidOperationException("缺少卸载命令行");

        // 经 cmd /c 兼容 "MsiExec.exe /X{...}" 等完整命令行写法
        var psi = new ProcessStartInfo("cmd.exe", $"/d /s /c \"{commandLine}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = false
        };
        using var process = Process.Start(psi)!;
        using var registration = ct.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 已退出
            }
        });

        await process.WaitForExitAsync(ct);
        // Windows Installer reports successful removal requiring a restart with these codes.
        if (process.ExitCode is not (0 or 1641 or 3010))
        {
            throw new InvalidOperationException($"卸载程序退出码：{process.ExitCode}");
        }
    }

    private Task CreateJunction(OperationAction action)
    {
        if (action.Target is null || action.NewPath is null)
        {
            throw new InvalidOperationException("CreateJunction 缺少源或目标路径");
        }
        _links.CreateJunction(action.Target, action.NewPath);
        return Task.CompletedTask;
    }

    private Task RedirectKnownFolder(OperationAction action)
    {
        if (action.Key is null || !Guid.TryParse(action.Key, out var folderId))
        {
            throw new InvalidOperationException("RedirectKnownFolder 缺少合法的 Known Folder ID");
        }
        if (action.NewValue is null)
        {
            throw new InvalidOperationException("RedirectKnownFolder 缺少新路径");
        }
        _knownFolders.SetPath(folderId, action.NewValue);
        return Task.CompletedTask;
    }

    private Task SetStartupEnabled(OperationAction action)
    {
        // Key=StartupApproved 键路径，ValueName=启动项名，NewValue=12 字节 REG_BINARY 十六进制
        if (action.Key is null || action.NewValue is null)
        {
            throw new InvalidOperationException("SetStartupEnabled 缺少键路径或新状态");
        }
        _registry.SetBinaryValue(
            action.Key, action.ValueName ?? string.Empty, Convert.FromHexString(action.NewValue));
        return Task.CompletedTask;
    }

    private Task SetScheduledTaskEnabled(OperationAction action)
    {
        if (action.TaskPath is null || action.NewValue is null)
        {
            throw new InvalidOperationException("SetScheduledTaskEnabled 缺少任务路径或新状态");
        }
        _tasks.SetTaskEnabled(action.TaskPath, bool.Parse(action.NewValue));
        return Task.CompletedTask;
    }

    private Task Shred(OperationAction action)
    {
        // FR-SHRED-01/02：清只读/隐藏属性强制删除；按 ShredPasses 多遍覆写后删除，不可回滚
        if (action.Target is null)
        {
            throw new InvalidOperationException("Shred 缺少目标");
        }
        if (Directory.Exists(action.Target))
        {
            // 目录粉碎由模块层展开为逐文件 action；执行器拒绝裸目录目标
            throw new InvalidOperationException("Shred 不接受目录目标，须逐文件入队");
        }
        if (!File.Exists(action.Target))
        {
            return Task.CompletedTask;
        }

        // 强制删除前置：清只读/隐藏/系统属性
        var attributes = File.GetAttributes(action.Target);
        if ((attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
        {
            File.SetAttributes(action.Target, attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
        }

        var length = new FileInfo(action.Target).Length;
        if (length > 0)
        {
            var passes = action.ShredPasses is >= 1 ? action.ShredPasses.Value : 1;
            var buffer = new byte[(int)Math.Min(length, 1024 * 1024)];
            for (var pass = 0; pass < passes; pass++)
            {
                FillPassBuffer(buffer, pass, passes);
                using (var stream = new FileStream(
                    action.Target, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    var written = 0L;
                    while (written < length)
                    {
                        var chunk = (int)Math.Min(buffer.Length, length - written);
                        stream.Write(buffer, 0, chunk);
                        written += chunk;
                    }
                    stream.Flush(true);
                }
            }
        }
        File.Delete(action.Target);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 覆写数据模式（FR-SHRED-02）：1 遍=零写（快速）；
    /// 3 遍=0x00/0xFF/随机；7 遍参考 DoD 5220.22-M=0xF6/0x00/0xFF/随机/0x00/0xFF/随机。
    /// 末遍恒为随机。
    /// </summary>
    private static void FillPassBuffer(byte[] buffer, int pass, int totalPasses)
    {
        if (totalPasses >= 7)
        {
            switch (pass)
            {
                case 0: Array.Fill(buffer, (byte)0xF6); return;
                case 1: Array.Fill(buffer, (byte)0x00); return;
                case 2: Array.Fill(buffer, (byte)0xFF); return;
                case 4: Array.Fill(buffer, (byte)0x00); return;
                case 5: Array.Fill(buffer, (byte)0xFF); return;
                default: RandomNumberGenerator.Fill(buffer); return;
            }
        }
        if (totalPasses == 3)
        {
            switch (pass)
            {
                case 0: Array.Fill(buffer, (byte)0x00); return;
                case 1: Array.Fill(buffer, (byte)0xFF); return;
                default: RandomNumberGenerator.Fill(buffer); return;
            }
        }
        // 快速粉碎：1 遍零写
        Array.Fill(buffer, (byte)0x00);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var stream = File.OpenRead(path);
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash);
    }
}
