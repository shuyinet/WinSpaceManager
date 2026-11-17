using SysMgr.Core.Operations;
using SysMgr.Core.Queue;
using SysMgr.Core.Safety;
using SysMgr.Core.SystemAccess;
using SysMgr.Modules.Startup.Errors;

namespace SysMgr.Modules.Startup;

/// <summary>
/// 启动项管理模块实现（IStartupService，FR-STARTUP-01）。
/// 扫描只读；禁用/启用/删除/编辑经 TRUST-02 预演 + OpQueue，回滚原值由模块预填。
/// 启用状态与任务管理器同口径：StartupApproved 键 REG_BINARY 12 字节，
/// byte0=02 启用 / 03 禁用，值缺失视为启用。
/// </summary>
public sealed class StartupService : IStartupService
{
    private const string StartupApprovedHkcu =
        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string StartupApprovedHklm =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    private const long MediumImpactBytes = 10L * 1024 * 1024;
    private const long HighImpactBytes = 100L * 1024 * 1024;

    private readonly IRegistry _registry;
    private readonly ITaskQuery _tasks;
    private readonly string? _userStartupFolder;
    private readonly string? _commonStartupFolder;

    private IModuleContext? _ctx;

    /// <summary>
    /// folder 参数仅测试注入用；生产缺省取 Environment.SpecialFolder 实际路径，
    /// 传空串表示跳过对应启动文件夹扫描。
    /// </summary>
    public StartupService(
        IRegistry registry,
        ITaskQuery tasks,
        string? userStartupFolder = null,
        string? commonStartupFolder = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _userStartupFolder = userStartupFolder
            ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        _commonStartupFolder = commonStartupFolder
            ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
    }

    public string ModuleId => "startup";
    public string DisplayName => "启动项管理";
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

    // ---------------- FR-STARTUP-01 扫描 ----------------

    /// <inheritdoc />
    public Task<IReadOnlyList<StartupItem>> ScanAsync(CancellationToken ct)
    {
        var items = new List<StartupItem>();

        ScanRunKey(RegistryRoots.RunHkcu, StartupItemKind.RegistryRun, ApprovedHkcu("Run"), items);
        ScanRunKey(RegistryRoots.RunHklm, StartupItemKind.RegistryRun, ApprovedHklm("Run"), items);
        ScanRunKey(RegistryRoots.RunHklmWow, StartupItemKind.RegistryRun32, ApprovedHklm("Run32"), items);
        ct.ThrowIfCancellationRequested();

        ScanStartupFolder(
            _userStartupFolder, StartupItemKind.StartupFolderUser, ApprovedHkcu("StartupFolder"), items);
        ScanStartupFolder(
            _commonStartupFolder, StartupItemKind.StartupFolderCommon, ApprovedHklm("StartupFolder"), items);
        ct.ThrowIfCancellationRequested();

        ScanLogonTasks(items);
        ScanUwpStartupTasks(items);
        return Task.FromResult<IReadOnlyList<StartupItem>>(items);
    }

    private void ScanRunKey(
        string runKey, StartupItemKind kind, string approvedKey, List<StartupItem> items)
    {
        foreach (var value in _registry.EnumerateValues(runKey))
        {
            items.Add(new StartupItem(
                $"{kind}|{runKey}|{value.Name}",
                value.Name,
                value.Data,
                kind,
                runKey,
                value.Name,
                ReadApprovedEnabled(approvedKey, value.Name),
                ClassifyImpact(value.Data),
                CanDelete: true,
                CanEdit: true));
        }
    }

    private void ScanStartupFolder(
        string? folder, StartupItemKind kind, string approvedKey, List<StartupItem> items)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        string[] links;
        try
        {
            links = Directory.GetFiles(folder, "*.lnk");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or DirectoryNotFoundException)
        {
            return;
        }

        foreach (var link in links)
        {
            var fileName = Path.GetFileName(link);
            items.Add(new StartupItem(
                $"{kind}|{link}",
                Path.GetFileNameWithoutExtension(link),
                link,
                kind,
                link,
                null,
                ReadApprovedEnabled(approvedKey, fileName),
                StartupImpact.Unknown, // .lnk 目标不解析，影响标注 Unknown
                CanDelete: true,
                CanEdit: false));
        }
    }

    private void ScanLogonTasks(List<StartupItem> items)
    {
        foreach (var task in _tasks.Enumerate())
        {
            if (!task.HasLogonTrigger)
            {
                continue;
            }

            items.Add(new StartupItem(
                $"{StartupItemKind.ScheduledTaskLogon}|{task.Path}",
                task.Name,
                task.ExecutablePath,
                StartupItemKind.ScheduledTaskLogon,
                task.Path,
                null,
                task.Enabled,
                ClassifyImpact(task.ExecutablePath),
                CanDelete: false,
                CanEdit: false));
        }
    }

    private void ScanUwpStartupTasks(List<StartupItem> items)
    {
        var approvedKey = ApprovedHkcu("StartupTask");
        foreach (var value in _registry.EnumerateValues(approvedKey))
        {
            items.Add(new StartupItem(
                $"{StartupItemKind.UwpStartupTask}|{value.Name}",
                value.Name,
                null, // UWP 项无命令行
                StartupItemKind.UwpStartupTask,
                approvedKey,
                value.Name,
                ReadApprovedEnabled(approvedKey, value.Name),
                StartupImpact.Unknown,
                CanDelete: false,
                CanEdit: false));
        }
    }

    // ---------------- FR-STARTUP-01 禁用/启用 ----------------

    /// <inheritdoc />
    public async Task<OperationResult> SetEnabledAsync(
        StartupItem item,
        bool enabled,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);

        OperationAction action;
        string description;
        if (item.Kind == StartupItemKind.ScheduledTaskLogon)
        {
            action = new OperationAction(
                ActionKind.SetScheduledTaskEnabled,
                null, null, null, null, null, null,
                item.IsEnabled ? "true" : "false",
                enabled ? "true" : "false",
                null,
                item.Source);
            description = $"{(enabled ? "启用" : "禁用")}计划任务启动项 {item.Name}";
        }
        else
        {
            var approvedKey = ApprovedKeyFor(item);
            var valueName = ApprovalValueName(item);
            var original = _registry.GetBinaryValue(approvedKey, valueName);
            action = new OperationAction(
                ActionKind.SetStartupEnabled,
                null, null, null, null,
                approvedKey,
                valueName,
                original is null ? null : Convert.ToHexString(original),
                Convert.ToHexString(BuildApprovalBytes(enabled)),
                null, null);
            description = $"{(enabled ? "启用" : "禁用")}启动项 {item.Name}";
        }

        return await EnqueueSingleAsync(action, description, progress, ct);
    }

    // ---------------- FR-STARTUP-01 删除 / 编辑 ----------------

    /// <inheritdoc />
    public async Task<OperationResult> DeleteAsync(
        StartupItem item,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.CanDelete)
        {
            throw new StartupNotSupportedException("删除", item.Name);
        }

        var action = item.Kind switch
        {
            StartupItemKind.RegistryRun or StartupItemKind.RegistryRun32 => new OperationAction(
                ActionKind.DeleteRegistry,
                null, null, null, null,
                item.Source,
                item.ValueName,
                null, null, null, null),
            StartupItemKind.StartupFolderUser or StartupItemKind.StartupFolderCommon
                => new OperationAction(
                    ActionKind.DeleteFile,
                    item.Source,
                    null, null, null, null, null, null, null, null, null),
            _ => throw new StartupNotSupportedException("删除", item.Name)
        };

        return await EnqueueSingleAsync(action, $"删除启动项 {item.Name}", progress, ct);
    }

    /// <inheritdoc />
    public async Task<OperationResult> EditCommandAsync(
        StartupItem item,
        string newCommand,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.CanEdit)
        {
            throw new StartupNotSupportedException("编辑命令行", item.Name);
        }
        if (string.IsNullOrWhiteSpace(newCommand))
        {
            throw new StartupCommandEmptyException();
        }

        var action = new OperationAction(
            ActionKind.SetRegistry,
            null, null, null, null,
            item.Source,
            item.ValueName,
            _registry.GetValue(item.Source, item.ValueName),
            newCommand.Trim(),
            null, null);

        return await EnqueueSingleAsync(action, $"编辑启动项命令行 {item.Name}", progress, ct);
    }

    private async Task<OperationResult> EnqueueSingleAsync(
        OperationAction action,
        string description,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("模块尚未初始化");

        var plan = new OperationPlan(
            Guid.NewGuid(),
            ModuleId,
            "FR-STARTUP-01",
            description,
            SafetyLevel.Medium,
            new[] { action },
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

    // ---------------- StartupApproved 状态 ----------------

    private bool ReadApprovedEnabled(string approvedKey, string valueName)
    {
        var bytes = _registry.GetBinaryValue(approvedKey, valueName);
        // 缺失=启用（任务管理器口径）；byte0=03 禁用，其余（02 等）启用
        return bytes is not { Length: > 0 } || bytes[0] != 3;
    }

    /// <summary>生成与任务管理器同格式的 12 字节状态值；禁用时 bytes4-11 写 FILETIME。</summary>
    internal static byte[] BuildApprovalBytes(bool enabled)
    {
        var bytes = new byte[12];
        bytes[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled)
        {
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTime()).CopyTo(bytes, 4);
        }
        return bytes;
    }

    private static string ApprovedKeyFor(StartupItem item) => item.Kind switch
    {
        StartupItemKind.RegistryRun => IsHkcu(item.Source) ? ApprovedHkcu("Run") : ApprovedHklm("Run"),
        StartupItemKind.RegistryRun32 => ApprovedHklm("Run32"),
        StartupItemKind.StartupFolderUser => ApprovedHkcu("StartupFolder"),
        StartupItemKind.StartupFolderCommon => ApprovedHklm("StartupFolder"),
        StartupItemKind.UwpStartupTask => ApprovedHkcu("StartupTask"),
        _ => throw new StartupNotSupportedException("状态切换", item.Name)
    };

    private static string ApprovalValueName(StartupItem item)
        => item.Kind is StartupItemKind.StartupFolderUser or StartupItemKind.StartupFolderCommon
            ? Path.GetFileName(item.Source) // 启动文件夹项值名=.lnk 文件名
            : item.ValueName ?? item.Name;

    private static string ApprovedHkcu(string leaf) => StartupApprovedHkcu + "\\" + leaf;

    private static string ApprovedHklm(string leaf) => StartupApprovedHklm + "\\" + leaf;

    private static bool IsHkcu(string keyPath)
        => keyPath.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase);

    // ---------------- 影响标注（启发式） ----------------

    private static StartupImpact ClassifyImpact(string? command)
    {
        var exe = ExtractExePath(command);
        if (exe is null)
        {
            return StartupImpact.Unknown;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(exe);
            if (!File.Exists(expanded))
            {
                return StartupImpact.Unknown;
            }
            var size = new FileInfo(expanded).Length;
            return size > HighImpactBytes ? StartupImpact.High
                : size > MediumImpactBytes ? StartupImpact.Medium
                : StartupImpact.Low;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return StartupImpact.Unknown;
        }
    }

    /// <summary>从命令行解析目标 exe（支持引号包裹与裸路径两种写法）。</summary>
    internal static string? ExtractExePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var c = command.Trim();
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }
        var space = c.IndexOf(' ');
        return space > 0 ? c[..space] : c;
    }
}
