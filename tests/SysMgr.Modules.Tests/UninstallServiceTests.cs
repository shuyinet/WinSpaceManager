using SysMgr.Core.Operations;
using SysMgr.Core.Safety;
using SysMgr.Core.SystemAccess;
using SysMgr.Modules.Tests.Fakes;
using SysMgr.Modules;
using SysMgr.Modules.Uninstall;
using SysMgr.Modules.Uninstall.Errors;
using Xunit;

namespace SysMgr.Modules.Tests;

public class UninstallServiceTests
{
    private const string HklmApp =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SutApp";
    private const string HkcuApp =
        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SutUserApp";

    private static async Task<List<T>> Drain<T>(
        IAsyncEnumerable<T> source, CancellationToken ct = default)
    {
        var list = new List<T>();
        await foreach (var x in source.WithCancellation(ct))
        {
            list.Add(x);
        }
        return list;
    }

    private static UninstallOptions Options(
        UninstallMode mode = UninstallMode.Standard,
        bool kill = false,
        string? customArgs = null)
        => new(mode, kill, customArgs);

    [Fact]
    public async Task Scan_ListsAppsFromAllHives_SkipsNameless()
    {
        var registry = new FakeRegistry();
        registry.SetValue(HklmApp, "DisplayName", "Sut App");
        registry.SetValue(HkcuApp, "DisplayName", "Sut User App");
        registry.SetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Nameless",
            "Publisher", "Acme");

        var service = new UninstallService(
            registry, new FakeProcessQuery(), new FakeServiceQuery(), new FakeTaskQuery());

        var items = await Drain(service.ScanInstalledAsync(null, CancellationToken.None));

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Source == SoftwareSource.RegistryX64);
        Assert.Contains(items, i => i.Source == SoftwareSource.RegistryUser);
    }

    [Fact]
    public async Task Scan_RecentInstallDate_NotLongUnused()
    {
        var registry = new FakeRegistry();
        registry.SetValue(HklmApp, "DisplayName", "Sut App");
        registry.SetValue(
            HklmApp, "InstallDate", DateTimeOffset.Now.AddDays(-10).ToString("yyyyMMdd"));

        var service = new UninstallService(
            registry, new FakeProcessQuery(), new FakeServiceQuery(), new FakeTaskQuery());

        var item = Assert.Single(await Drain(service.ScanInstalledAsync(null, CancellationToken.None)));
        Assert.False(item.IsLongUnused);
    }

    [Fact]
    public async Task Scan_OldInstallNoPrefetchNoStartup_LongUnused()
    {
        var registry = new FakeRegistry();
        registry.SetValue(HklmApp, "DisplayName", "Sut App");
        registry.SetValue(
            HklmApp, "InstallDate", DateTimeOffset.Now.AddDays(-200).ToString("yyyyMMdd"));
        registry.SetValue(HklmApp, "UninstallString", @"C:\Sut\zqxsutapp.exe");

        var service = new UninstallService(
            registry, new FakeProcessQuery(), new FakeServiceQuery(), new FakeTaskQuery());

        var item = Assert.Single(await Drain(service.ScanInstalledAsync(null, CancellationToken.None)));
        Assert.True(item.IsLongUnused);
    }

    [Fact]
    public async Task GetCommand_Msi_ClassifiedAndSilentCapable()
    {
        var registry = new FakeRegistry();
        registry.SetValue(
            HklmApp,
            "UninstallString",
            "msiexec.exe /i{11111111-1111-1111-1111-111111111111}");
        var item = new UninstallItem(
            Guid.NewGuid(), "Sut App", null, null, null, null, null,
            registry.GetValue(HklmApp, "UninstallString"),
            SoftwareSource.RegistryX64, false, false, null, false);

        var service = new UninstallService(
            registry, new FakeProcessQuery(), new FakeServiceQuery(), new FakeTaskQuery());

        var command = await service.GetUninstallCommandAsync(item, CancellationToken.None);

        Assert.Equal(UninstallerType.Msi, command.UninstallerType);
        Assert.True(command.IsSilentSupported);
        Assert.Equal("/qn /norestart", command.SilentArgs);
    }

    [Fact]
    public async Task Enqueue_Standard_BuildsUninstallerPlanAndRegistersResidualTarget()
    {
        using var ws = new TempWorkspace();
        var registry = new FakeRegistry();
        var uninstaller = ws.CreateFile("uninst.exe");
        registry.SetValue(HklmApp, "DisplayName", "Sut App");
        registry.SetValue(HklmApp, "Publisher", "Acme");
        registry.SetValue(HklmApp, "UninstallString", uninstaller);

        var ctx = new FakeModuleContext();
        var service = new UninstallService(
            registry, new FakeProcessQuery(), new FakeServiceQuery(), new FakeTaskQuery());
        await service.InitializeAsync(ctx, CancellationToken.None);
        var item = Assert.Single(await Drain(service.ScanInstalledAsync(null, CancellationToken.None)));

        var result = await service.EnqueueUninstallAsync(item, Options(), null, CancellationToken.None);

        Assert.True(result.Success);
        var plan = Assert.Single(ctx.Queue.Plans);
        Assert.Equal("FR-UNINSTALL-02", plan.FrId);
        Assert.Equal(SafetyLevel.Medium, plan.SafetyLevel);
        var action = Assert.Single(plan.Actions);
        Assert.Equal(ActionKind.RunExternalUninstaller, action.Kind);

        var registered = Assert.Single(ctx.Recorder.Targets);
        Assert.Equal(plan.PlanId, registered.Key);
        Assert.Equal("Sut App", registered.Value.DisplayName);
        Assert.Equal("Acme", registered.Value.Publisher);
        Assert.True(ctx.Recorder.ResidualTriggered);
    }

    [Fact]
    public async Task Enqueue_StandardWithCustomArgs_AppendedToTarget()
    {
        var registry = new FakeRegistry();
        registry.SetValue(HklmApp, "DisplayName", "Sut App");
        registry.SetValue(HklmApp, "UninstallString", @"C:\Sut\setup.exe");

        var ctx = new FakeModuleContext();
        var service = new UninstallService(
            registry, new FakeProcessQuery(), new FakeServiceQuery(), new FakeTaskQuery());
        await service.InitializeAsync(ctx, CancellationToken.None);
        var item = Assert.Single(await Drain(service.ScanInstalledAsync(null, CancellationToken.None)));

        await service.EnqueueUninstallAsync(
            item, Options(customArgs: "--foo"), null, CancellationToken.None);

        var action = Assert.Single(ctx.Queue.Plans).Actions.Single();
        Assert.EndsWith("--foo", action.Target);
    }

    [Fact]
    public async Task Enqueue_StandardWithoutUninstallString_Throws()
    {
        var registry = new FakeRegistry();
        registry.SetValue(HklmApp, "DisplayName", "Sut App");

        var service = new UninstallService(
            registry, new FakeProcessQuery(), new FakeServiceQuery(), new FakeTaskQuery());
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);
        var item = Assert.Single(await Drain(service.ScanInstalledAsync(null, CancellationToken.None)));

        await Assert.ThrowsAsync<UninstallerNotFoundException>(
            () => service.EnqueueUninstallAsync(item, Options(), null, CancellationToken.None));
    }

    [Fact]
    public async Task Enqueue_ForceWhenUninstallerAvailable_Throws()
    {
        var registry = new FakeRegistry();
        registry.SetValue(HklmApp, "DisplayName", "Sut App");
        registry.SetValue(HklmApp, "UninstallString", @"C:\Sut\setup.exe");

        var service = new UninstallService(
            registry, new FakeProcessQuery(), new FakeServiceQuery(), new FakeTaskQuery());
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);
        var item = Assert.Single(await Drain(service.ScanInstalledAsync(null, CancellationToken.None)));

        await Assert.ThrowsAsync<UninstallerAvailableException>(
            () => service.EnqueueUninstallAsync(item, Options(UninstallMode.Force), null, CancellationToken.None));
    }

    [Fact]
    public async Task Enqueue_ForceNoUninstaller_BuildsLowPlanWithFileAndRegistry()
    {
        using var ws = new TempWorkspace();
        var installDir = ws.CreateDirectory("install");
        ws.CreateFile("install/app.exe", "abc");

        var registry = new FakeRegistry();
        registry.SetValue(HklmApp, "DisplayName", "Sut App");
        registry.SetValue(HklmApp, "InstallLocation", installDir);

        var ctx = new FakeModuleContext();
        var service = new UninstallService(
            registry, new FakeProcessQuery(), new FakeServiceQuery(), new FakeTaskQuery());
        await service.InitializeAsync(ctx, CancellationToken.None);
        var item = Assert.Single(await Drain(service.ScanInstalledAsync(null, CancellationToken.None)));

        await service.EnqueueUninstallAsync(
            item, Options(UninstallMode.Force), null, CancellationToken.None);

        var plan = Assert.Single(ctx.Queue.Plans);
        Assert.Equal("FR-UNINSTALL-04", plan.FrId);
        Assert.Equal(SafetyLevel.Low, plan.SafetyLevel);
        Assert.Equal(
            new[] { ActionKind.DeleteFile, ActionKind.DeleteRegistry },
            plan.Actions.Select(a => a.Kind));
        Assert.Equal(installDir, plan.Actions[0].Target);
    }

    [Fact]
    public async Task Enqueue_RunningProcessWithoutKill_Throws()
    {
        using var ws = new TempWorkspace();
        var installDir = ws.CreateDirectory("install");
        var runningExe = Path.Combine(installDir, "app.exe");
        var registry = new FakeRegistry();
        registry.SetValue(HklmApp, "DisplayName", "Sut App");
        registry.SetValue(HklmApp, "InstallLocation", installDir);

        var processes = new FakeProcessQuery();
        processes.Processes.Add(new ProcessInfo(4242, "app", runningExe));

        var service = new UninstallService(
            registry, processes, new FakeServiceQuery(), new FakeTaskQuery());
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);
        var item = Assert.Single(await Drain(service.ScanInstalledAsync(null, CancellationToken.None)));

        var ex = await Assert.ThrowsAsync<SoftwareRunningException>(
            () => service.EnqueueUninstallAsync(item, Options(), null, CancellationToken.None));
        Assert.Contains("app", ex.RunningProcesses);
    }

    [Fact]
    public async Task Enqueue_RunningProcessWithKill_IncludesKillAction()
    {
        using var ws = new TempWorkspace();
        var installDir = ws.CreateDirectory("install");
        var runningExe = Path.Combine(installDir, "app.exe");
        var registry = new FakeRegistry();
        registry.SetValue(HklmApp, "DisplayName", "Sut App");
        registry.SetValue(HklmApp, "InstallLocation", installDir);
        registry.SetValue(HklmApp, "UninstallString", @"C:\Sut\uninstall.exe");

        var processes = new FakeProcessQuery();
        processes.Processes.Add(new ProcessInfo(4242, "app", runningExe));

        var ctx = new FakeModuleContext();
        var service = new UninstallService(
            registry, processes, new FakeServiceQuery(), new FakeTaskQuery());
        await service.InitializeAsync(ctx, CancellationToken.None);
        var item = Assert.Single(await Drain(service.ScanInstalledAsync(null, CancellationToken.None)));

        await service.EnqueueUninstallAsync(
            item, Options(UninstallMode.Standard, kill: true), null, CancellationToken.None);

        var plan = Assert.Single(ctx.Queue.Plans);
        Assert.Equal(ActionKind.KillProcess, plan.Actions[0].Kind);
        Assert.Equal("4242", plan.Actions[0].OriginalValue);
    }

    [Fact]
    public async Task Enqueue_Uwp_Throws()
    {
        var item = new UninstallItem(
            Guid.NewGuid(), "Sut Uwp", null, null, null, null, null, null,
            SoftwareSource.Uwp, false, false, null, false);
        var service = new UninstallService(
            new FakeRegistry(), new FakeProcessQuery(), new FakeServiceQuery(), new FakeTaskQuery());
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        await Assert.ThrowsAsync<UwpNotSupportedV1Exception>(
            () => service.EnqueueUninstallAsync(item, Options(), null, CancellationToken.None));
    }

    [Fact]
    public async Task Enqueue_SilentMode_Throws()
    {
        var item = new UninstallItem(
            Guid.NewGuid(), "Sut App", null, null, null, null, null, @"C:\Sut\a.exe",
            SoftwareSource.RegistryX64, false, false, null, false);
        var service = new UninstallService(
            new FakeRegistry(), new FakeProcessQuery(), new FakeServiceQuery(), new FakeTaskQuery());
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => service.EnqueueUninstallAsync(item, Options(UninstallMode.Silent), null, CancellationToken.None));
    }

    [Fact]
    public async Task Enqueue_PreviewCancelled_ReturnsFalseAndSkipsQueue()
    {
        var registry = new FakeRegistry();
        registry.SetValue(HklmApp, "DisplayName", "Sut App");
        registry.SetValue(HklmApp, "UninstallString", @"C:\Sut\setup.exe");

        var ctx = new FakeModuleContext();
        ctx.TrustStub.Confirmed = false;
        var service = new UninstallService(
            registry, new FakeProcessQuery(), new FakeServiceQuery(), new FakeTaskQuery());
        await service.InitializeAsync(ctx, CancellationToken.None);
        var item = Assert.Single(await Drain(service.ScanInstalledAsync(null, CancellationToken.None)));

        var result = await service.EnqueueUninstallAsync(item, Options(), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(ctx.Queue.Plans);
    }
}
