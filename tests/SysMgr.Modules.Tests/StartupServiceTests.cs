using SysMgr.Core.Operations;
using SysMgr.Core.Safety;
using SysMgr.Core.SystemAccess;
using SysMgr.Modules.Startup;
using SysMgr.Modules.Startup.Errors;
using SysMgr.Modules.Tests.Fakes;
using Xunit;

namespace SysMgr.Modules.Tests;

/// <summary>
/// 启动项管理模块测试（FR-STARTUP-01）。
/// 注册表用 FakeRegistry 预置 Run 值与 StartupApproved 二进制状态；
/// 计划任务用 FakeTaskQuery；启动文件夹用临时目录经 ctor 注入。
/// </summary>
public class StartupServiceTests : IDisposable
{
    private const string ApprovedHkcuRun =
        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedHklmRun =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedHklmRun32 =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string ApprovedHkcuFolder =
        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    private const string ApprovedHklmFolder =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    private const string ApprovedHkcuStartupTask =
        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupTask";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "startup_test_" + Guid.NewGuid().ToString("N"));

    private readonly FakeRegistry _registry = new();
    private readonly FakeTaskQuery _tasks = new();

    public StartupServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>folder 缺省传空串跳过真实启动文件夹扫描，保证测试确定性。</summary>
    private StartupService CreateService(
        FakeModuleContext? ctx = null,
        string? userFolder = "",
        string? commonFolder = "")
    {
        var service = new StartupService(_registry, _tasks, userFolder, commonFolder);
        service.InitializeAsync(ctx ?? new FakeModuleContext(), CancellationToken.None)
            .GetAwaiter().GetResult();
        return service;
    }

    private string WriteSizedFile(string relative, long bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(bytes); // SetLength 瞬建逻辑大小，不占实际磁盘
        }
        return path;
    }

    private static byte[] EnabledBytes() => new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    private static byte[] DisabledBytes() => new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    // ---------------- 扫描 ----------------

    [Fact]
    public async Task Scan_RunKeys_ReturnsAllThreeSources()
    {
        _registry.SetValue(RegistryRoots.RunHkcu, "AppUser", @"C:\apps\user.exe");
        _registry.SetValue(RegistryRoots.RunHklm, "AppMachine", @"C:\apps\machine.exe");
        _registry.SetValue(RegistryRoots.RunHklmWow, "AppWow", @"C:\apps\wow.exe");

        var items = await CreateService().ScanAsync(CancellationToken.None);

        Assert.Equal(3, items.Count);
        var user = Assert.Single(items.Where(i => i.Name == "AppUser"));
        Assert.Equal(StartupItemKind.RegistryRun, user.Kind);
        Assert.Equal(@"C:\apps\user.exe", user.Command);
        Assert.True(user.CanDelete);
        Assert.True(user.CanEdit);
        Assert.Single(items.Where(i => i.Name == "AppMachine" && i.Kind == StartupItemKind.RegistryRun));
        Assert.Single(items.Where(i => i.Name == "AppWow" && i.Kind == StartupItemKind.RegistryRun32));
    }

    [Fact]
    public async Task Scan_ApprovedByte03_MarkedDisabled()
    {
        _registry.SetValue(RegistryRoots.RunHkcu, "AppOff", @"C:\apps\off.exe");
        _registry.SetBinaryValue(ApprovedHkcuRun, "AppOff", DisabledBytes());

        var items = await CreateService().ScanAsync(CancellationToken.None);

        Assert.False(Assert.Single(items).IsEnabled);
    }

    [Fact]
    public async Task Scan_ApprovedMissingOrByte02_MarkedEnabled()
    {
        _registry.SetValue(RegistryRoots.RunHkcu, "AppNoState", @"C:\apps\a.exe");
        _registry.SetValue(RegistryRoots.RunHkcu, "AppOn", @"C:\apps\b.exe");
        _registry.SetBinaryValue(ApprovedHkcuRun, "AppOn", EnabledBytes());

        var items = await CreateService().ScanAsync(CancellationToken.None);

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.True(i.IsEnabled));
    }

    [Fact]
    public async Task Scan_StartupFolders_ReturnsLnkItems()
    {
        var userFolder = Path.Combine(_root, "user_startup");
        var commonFolder = Path.Combine(_root, "common_startup");
        Directory.CreateDirectory(userFolder);
        Directory.CreateDirectory(commonFolder);
        File.WriteAllText(Path.Combine(userFolder, "userapp.lnk"), "x");
        File.WriteAllText(Path.Combine(commonFolder, "commonapp.lnk"), "x");
        File.WriteAllText(Path.Combine(userFolder, "ignored.txt"), "x"); // 非 .lnk 不扫描

        var items = await CreateService(userFolder: userFolder, commonFolder: commonFolder)
            .ScanAsync(CancellationToken.None);

        Assert.Equal(2, items.Count);
        var userItem = Assert.Single(items.Where(i => i.Kind == StartupItemKind.StartupFolderUser));
        Assert.Equal("userapp", userItem.Name);
        Assert.False(userItem.CanEdit);
        Assert.Single(items.Where(i => i.Kind == StartupItemKind.StartupFolderCommon));
    }

    [Fact]
    public async Task Scan_Tasks_OnlyLogonTriggerIncluded()
    {
        _tasks.Tasks.Add(new ScheduledTaskInfo(
            "\\LogonTask", "LogonTask", @"C:\apps\task.exe", Enabled: false, HasLogonTrigger: true));
        _tasks.Tasks.Add(new ScheduledTaskInfo(
            "\\DailyTask", "DailyTask", @"C:\apps\daily.exe", Enabled: true, HasLogonTrigger: false));

        var items = await CreateService().ScanAsync(CancellationToken.None);

        var task = Assert.Single(items);
        Assert.Equal(StartupItemKind.ScheduledTaskLogon, task.Kind);
        Assert.Equal("\\LogonTask", task.Source);
        Assert.False(task.IsEnabled);
        Assert.False(task.CanDelete);
        Assert.False(task.CanEdit);
    }

    [Fact]
    public async Task Scan_UwpStartupTasks_FromApprovedKey()
    {
        _registry.SetBinaryValue(ApprovedHkcuStartupTask, "ContosoApp_abc123!App", DisabledBytes());

        var items = await CreateService().ScanAsync(CancellationToken.None);

        var uwp = Assert.Single(items);
        Assert.Equal(StartupItemKind.UwpStartupTask, uwp.Kind);
        Assert.Equal("ContosoApp_abc123!App", uwp.Name);
        Assert.False(uwp.IsEnabled);
        Assert.False(uwp.CanDelete);
    }

    // ---------------- 禁用 / 启用 ----------------

    [Fact]
    public async Task SetEnabled_HklmItem_EnqueuesApprovedBinaryAction()
    {
        var ctx = new FakeModuleContext();
        _registry.SetValue(RegistryRoots.RunHklm, "AppMachine", @"C:\apps\machine.exe");
        _registry.SetBinaryValue(ApprovedHklmRun, "AppMachine", EnabledBytes());
        var service = CreateService(ctx);
        var item = Assert.Single(await service.ScanAsync(CancellationToken.None));

        var result = await service.SetEnabledAsync(item, false, null, CancellationToken.None);

        Assert.True(result.Success);
        var plan = Assert.Single(ctx.Queue.Plans);
        Assert.Equal("startup", plan.ModuleId);
        Assert.Equal("FR-STARTUP-01", plan.FrId);
        Assert.Equal(SafetyLevel.Medium, plan.SafetyLevel);
        Assert.True(plan.PreviewRequired);
        Assert.True(plan.UserConfirmed);
        var action = Assert.Single(plan.Actions);
        Assert.Equal(ActionKind.SetStartupEnabled, action.Kind);
        Assert.Equal(ApprovedHklmRun, action.Key);
        Assert.Equal("AppMachine", action.ValueName);
        Assert.Equal(Convert.ToHexString(EnabledBytes()), action.OriginalValue);
        Assert.Equal(3, Convert.FromHexString(action.NewValue!)[0]);
    }

    [Fact]
    public async Task SetEnabled_HkcuItem_UsesHkcuApprovedKey()
    {
        var ctx = new FakeModuleContext();
        _registry.SetValue(RegistryRoots.RunHkcu, "AppUser", @"C:\apps\user.exe");
        var service = CreateService(ctx);
        var item = Assert.Single(await service.ScanAsync(CancellationToken.None));

        await service.SetEnabledAsync(item, false, null, CancellationToken.None);

        var action = Assert.Single(Assert.Single(ctx.Queue.Plans).Actions);
        Assert.Equal(ApprovedHkcuRun, action.Key);
        Assert.Null(action.OriginalValue); // 原状态缺失
    }

    [Fact]
    public async Task SetEnabled_WowItem_UsesRun32ApprovedKey()
    {
        var ctx = new FakeModuleContext();
        _registry.SetValue(RegistryRoots.RunHklmWow, "AppWow", @"C:\apps\wow.exe");
        var service = CreateService(ctx);
        var item = Assert.Single(await service.ScanAsync(CancellationToken.None));

        await service.SetEnabledAsync(item, false, null, CancellationToken.None);

        var action = Assert.Single(Assert.Single(ctx.Queue.Plans).Actions);
        Assert.Equal(ApprovedHklmRun32, action.Key);
    }

    [Fact]
    public async Task SetEnabled_FolderItem_ValueNameIsLnkFileName()
    {
        var ctx = new FakeModuleContext();
        var userFolder = Path.Combine(_root, "user_startup");
        Directory.CreateDirectory(userFolder);
        File.WriteAllText(Path.Combine(userFolder, "userapp.lnk"), "x");
        var service = CreateService(ctx, userFolder: userFolder);
        var item = Assert.Single(await service.ScanAsync(CancellationToken.None));

        await service.SetEnabledAsync(item, false, null, CancellationToken.None);

        var action = Assert.Single(Assert.Single(ctx.Queue.Plans).Actions);
        Assert.Equal(ActionKind.SetStartupEnabled, action.Kind);
        Assert.Equal(ApprovedHkcuFolder, action.Key);
        Assert.Equal("userapp.lnk", action.ValueName);
    }

    [Fact]
    public async Task SetEnabled_CommonFolderItem_UsesHklmApprovedKey()
    {
        var ctx = new FakeModuleContext();
        var commonFolder = Path.Combine(_root, "common_startup");
        Directory.CreateDirectory(commonFolder);
        File.WriteAllText(Path.Combine(commonFolder, "commonapp.lnk"), "x");
        var service = CreateService(ctx, commonFolder: commonFolder);
        var item = Assert.Single(await service.ScanAsync(CancellationToken.None));

        await service.SetEnabledAsync(item, false, null, CancellationToken.None);

        var action = Assert.Single(Assert.Single(ctx.Queue.Plans).Actions);
        Assert.Equal(ApprovedHklmFolder, action.Key);
    }

    [Fact]
    public async Task SetEnabled_ScheduledTask_EnqueuesTaskAction()
    {
        var ctx = new FakeModuleContext();
        _tasks.Tasks.Add(new ScheduledTaskInfo(
            "\\LogonTask", "LogonTask", @"C:\apps\task.exe", Enabled: true, HasLogonTrigger: true));
        var service = CreateService(ctx);
        var item = Assert.Single(await service.ScanAsync(CancellationToken.None));

        var result = await service.SetEnabledAsync(item, false, null, CancellationToken.None);

        Assert.True(result.Success);
        var action = Assert.Single(Assert.Single(ctx.Queue.Plans).Actions);
        Assert.Equal(ActionKind.SetScheduledTaskEnabled, action.Kind);
        Assert.Equal("\\LogonTask", action.TaskPath);
        Assert.Equal("true", action.OriginalValue);
        Assert.Equal("false", action.NewValue);
    }

    [Fact]
    public async Task SetEnabled_PreviewCancelled_SkipsQueue()
    {
        var ctx = new FakeModuleContext();
        ctx.TrustStub.Confirmed = false;
        _registry.SetValue(RegistryRoots.RunHkcu, "AppUser", @"C:\apps\user.exe");
        var service = CreateService(ctx);
        var item = Assert.Single(await service.ScanAsync(CancellationToken.None));

        var result = await service.SetEnabledAsync(item, false, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(ctx.Queue.Plans);
    }

    // ---------------- 删除 ----------------

    [Fact]
    public async Task Delete_RegistryItem_EnqueuesDeleteRegistry()
    {
        var ctx = new FakeModuleContext();
        _registry.SetValue(RegistryRoots.RunHkcu, "AppUser", @"C:\apps\user.exe");
        var service = CreateService(ctx);
        var item = Assert.Single(await service.ScanAsync(CancellationToken.None));

        var result = await service.DeleteAsync(item, null, CancellationToken.None);

        Assert.True(result.Success);
        var action = Assert.Single(Assert.Single(ctx.Queue.Plans).Actions);
        Assert.Equal(ActionKind.DeleteRegistry, action.Kind);
        Assert.Equal(RegistryRoots.RunHkcu, action.Key);
        Assert.Equal("AppUser", action.ValueName);
    }

    [Fact]
    public async Task Delete_FolderItem_EnqueuesDeleteFile()
    {
        var ctx = new FakeModuleContext();
        var userFolder = Path.Combine(_root, "user_startup");
        Directory.CreateDirectory(userFolder);
        var link = Path.Combine(userFolder, "userapp.lnk");
        File.WriteAllText(link, "x");
        var service = CreateService(ctx, userFolder: userFolder);
        var item = Assert.Single(await service.ScanAsync(CancellationToken.None));

        var result = await service.DeleteAsync(item, null, CancellationToken.None);

        Assert.True(result.Success);
        var action = Assert.Single(Assert.Single(ctx.Queue.Plans).Actions);
        Assert.Equal(ActionKind.DeleteFile, action.Kind);
        Assert.Equal(link, action.Target);
    }

    [Fact]
    public async Task Delete_ScheduledTask_Throws()
    {
        _tasks.Tasks.Add(new ScheduledTaskInfo(
            "\\LogonTask", "LogonTask", @"C:\apps\task.exe", HasLogonTrigger: true));
        var service = CreateService();
        var item = Assert.Single(await service.ScanAsync(CancellationToken.None));

        await Assert.ThrowsAsync<StartupNotSupportedException>(() =>
            service.DeleteAsync(item, null, CancellationToken.None));
    }

    // ---------------- 编辑命令行 ----------------

    [Fact]
    public async Task EditCommand_RegistryItem_EnqueuesSetRegistry()
    {
        var ctx = new FakeModuleContext();
        _registry.SetValue(RegistryRoots.RunHkcu, "AppUser", @"C:\apps\old.exe");
        var service = CreateService(ctx);
        var item = Assert.Single(await service.ScanAsync(CancellationToken.None));

        var result = await service.EditCommandAsync(
            item, @"  D:\new\app.exe --flag  ", null, CancellationToken.None);

        Assert.True(result.Success);
        var action = Assert.Single(Assert.Single(ctx.Queue.Plans).Actions);
        Assert.Equal(ActionKind.SetRegistry, action.Kind);
        Assert.Equal(RegistryRoots.RunHkcu, action.Key);
        Assert.Equal("AppUser", action.ValueName);
        Assert.Equal(@"C:\apps\old.exe", action.OriginalValue);
        Assert.Equal(@"D:\new\app.exe --flag", action.NewValue); // 已 Trim
    }

    [Fact]
    public async Task EditCommand_Blank_Throws()
    {
        _registry.SetValue(RegistryRoots.RunHkcu, "AppUser", @"C:\apps\user.exe");
        var service = CreateService();
        var item = Assert.Single(await service.ScanAsync(CancellationToken.None));

        await Assert.ThrowsAsync<StartupCommandEmptyException>(() =>
            service.EditCommandAsync(item, "   ", null, CancellationToken.None));
    }

    [Fact]
    public async Task EditCommand_UwpItem_Throws()
    {
        _registry.SetBinaryValue(ApprovedHkcuStartupTask, "ContosoApp_abc123!App", EnabledBytes());
        var service = CreateService();
        var item = Assert.Single(await service.ScanAsync(CancellationToken.None));

        await Assert.ThrowsAsync<StartupNotSupportedException>(() =>
            service.EditCommandAsync(item, @"C:\x.exe", null, CancellationToken.None));
    }

    // ---------------- 影响标注 ----------------

    [Fact]
    public async Task Scan_Impact_ClassifiedByExeSize()
    {
        var small = WriteSizedFile("small.exe", 1024);
        var medium = WriteSizedFile("medium.exe", 11L * 1024 * 1024);
        var large = WriteSizedFile("large.exe", 101L * 1024 * 1024);
        _registry.SetValue(RegistryRoots.RunHkcu, "Small", $"\"{small}\" --run");
        _registry.SetValue(RegistryRoots.RunHkcu, "Medium", medium);
        _registry.SetValue(RegistryRoots.RunHkcu, "Large", large);
        _registry.SetValue(RegistryRoots.RunHkcu, "Missing", @"C:\no_such_dir\ghost.exe");

        var items = await CreateService().ScanAsync(CancellationToken.None);

        Assert.Equal(StartupImpact.Low, Assert.Single(items.Where(i => i.Name == "Small")).Impact);
        Assert.Equal(StartupImpact.Medium, Assert.Single(items.Where(i => i.Name == "Medium")).Impact);
        Assert.Equal(StartupImpact.High, Assert.Single(items.Where(i => i.Name == "Large")).Impact);
        Assert.Equal(StartupImpact.Unknown, Assert.Single(items.Where(i => i.Name == "Missing")).Impact);
    }
}
