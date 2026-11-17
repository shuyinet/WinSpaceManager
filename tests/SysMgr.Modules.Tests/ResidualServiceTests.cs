using SysMgr.Core.Operations;
using SysMgr.Core.Safety;
using SysMgr.Core.SystemAccess;
using SysMgr.Modules.Tests.Fakes;
using SysMgr.Modules.Residual;
using SysMgr.Modules.Residual.Errors;
using Xunit;

namespace SysMgr.Modules.Tests;

public class ResidualServiceTests : IDisposable
{
    private const string Keyword = "Zqxsutres";

    private readonly string _localDir;
    private string? _lnk;

    public ResidualServiceTests()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _localDir = Path.Combine(local, Keyword);
        Directory.CreateDirectory(_localDir);
        File.WriteAllText(Path.Combine(_localDir, "f.txt"), "data");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_localDir))
            {
                Directory.Delete(_localDir, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        try
        {
            if (_lnk is not null && File.Exists(_lnk))
            {
                File.Delete(_lnk);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static ResidualScanTarget Target(string? installLocation = null)
        => new(Guid.NewGuid(), Keyword, null, installLocation);

    private static ResidualScanOptions Options(
        IReadOnlyList<ResidualCategory>? categories = null,
        string[]? extras = null)
        => new(categories, extras);

    private static async Task<List<ResidualItem>> Scan(
        ResidualService service,
        ResidualScanTarget target,
        ResidualScanOptions options)
    {
        var items = new List<ResidualItem>();
        await foreach (var item in service.ScanResidualAsync(
                           target, options, null, CancellationToken.None))
        {
            items.Add(item);
        }
        return items;
    }

    [Fact]
    public async Task Scan_OrphanUninstallKey_FoundRegistry()
    {
        var registry = new FakeRegistry();
        var key = RegistryRoots.UninstallHkcu + @"\Orphan";
        registry.SetValue(key, "DisplayName", Keyword);

        var service = new ResidualService(
            registry, new FakeServiceQuery(), new FakeTaskQuery());
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        var target = Target();
        var item = Assert.Single(await Scan(
            service, target, Options(new[] { ResidualCategory.Registry })));
        Assert.Equal(ResidualCategory.Registry, item.Category);
        Assert.Equal(key, item.Target);
        Assert.Equal(target.SoftwareId, item.SoftwareId);
    }

    [Fact]
    public async Task Scan_AppPaths_Found()
    {
        var registry = new FakeRegistry();
        var key = RegistryRoots.AppPaths + "\\" + Keyword + ".exe";
        registry.SetValue(key, string.Empty, @"C:\Sut\app.exe");

        var service = new ResidualService(
            registry, new FakeServiceQuery(), new FakeTaskQuery());
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        var items = await Scan(service, Target(), Options(new[] { ResidualCategory.Registry }));
        Assert.Contains(items, i => i.Target == key);
    }

    [Fact]
    public async Task Scan_RunValue_FoundStartupWithValueName()
    {
        var registry = new FakeRegistry();
        registry.SetValue(
            RegistryRoots.RunHkcu, "SutTray", $"\"C:\\Sut\\{Keyword}\\app.exe\"");

        var service = new ResidualService(
            registry, new FakeServiceQuery(), new FakeTaskQuery());
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        var item = Assert.Single(await Scan(service, Target(), Options(new[] { ResidualCategory.Registry })));
        Assert.Equal(ResidualCategory.Startup, item.Category);
        Assert.Equal(RegistryRoots.RunHkcu, item.Target);
        Assert.Equal("SutTray", item.ValueName);
    }

    [Fact]
    public async Task Scan_FileDirectory_FoundWithSize()
    {
        var service = new ResidualService(
            new FakeRegistry(), new FakeServiceQuery(), new FakeTaskQuery());
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        var item = Assert.Single(await Scan(service, Target(), Options(new[] { ResidualCategory.File })));
        Assert.Equal(ResidualCategory.File, item.Category);
        Assert.Equal(_localDir, item.Target);
        Assert.NotNull(item.EstimatedSizeBytes);
        Assert.True(item.EstimatedSizeBytes > 0);
    }

    [Fact]
    public async Task Scan_StartMenuShortcut_FoundStartupFile()
    {
        var menuRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs");
        Directory.CreateDirectory(menuRoot);
        _lnk = Path.Combine(menuRoot, Keyword + ".lnk");
        File.WriteAllText(_lnk, "x");

        var service = new ResidualService(
            new FakeRegistry(), new FakeServiceQuery(), new FakeTaskQuery());
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        var items = await Scan(service, Target(), Options(new[] { ResidualCategory.File }));
        var lnkItem = Assert.Single(items.Where(i => i.Target == _lnk));
        Assert.Equal(ResidualCategory.Startup, lnkItem.Category);
        Assert.Null(lnkItem.ValueName);
    }

    [Fact]
    public async Task Scan_Service_Found()
    {
        var services = new FakeServiceQuery();
        services.Services.Add(new ServiceInfo(
            Keyword, "Sut Service", $@"C:\Sut\{Keyword}\svc.exe", ServiceState.Running));

        var service = new ResidualService(
            new FakeRegistry(), services, new FakeTaskQuery());
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        var item = Assert.Single(await Scan(service, Target(), Options(new[] { ResidualCategory.Service })));
        Assert.Equal(ResidualCategory.Service, item.Category);
        Assert.Equal(Keyword, item.Target);
    }

    [Fact]
    public async Task Scan_Task_Found()
    {
        var tasks = new FakeTaskQuery();
        tasks.Tasks.Add(new ScheduledTaskInfo(
            $@"\Sut\{Keyword}", Keyword, $@"C:\Sut\{Keyword}\task.exe"));

        var service = new ResidualService(
            new FakeRegistry(), new FakeServiceQuery(), tasks);
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        var item = Assert.Single(await Scan(service, Target(), Options(new[] { ResidualCategory.ScheduledTask })));
        Assert.Equal(ResidualCategory.ScheduledTask, item.Category);
        Assert.Equal($@"\Sut\{Keyword}", item.Target);
    }

    [Fact]
    public async Task Scan_CategoryFilter_OnlyReturnsRequested()
    {
        var services = new FakeServiceQuery();
        services.Services.Add(new ServiceInfo(
            Keyword, "Sut Service", null, ServiceState.Running));

        var service = new ResidualService(
            new FakeRegistry(), services, new FakeTaskQuery());
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        var items = await Scan(service, Target(), Options(new[] { ResidualCategory.Service }));
        Assert.Single(items);
        Assert.Equal(ResidualCategory.Service, items[0].Category);
    }

    [Fact]
    public async Task Scan_AdditionalKeyword_MatchesOtherNames()
    {
        var registry = new FakeRegistry();
        var key = RegistryRoots.UninstallHkcu + @"\Legacy";
        registry.SetValue(key, "DisplayName", "Legacy Name");

        var service = new ResidualService(
            registry, new FakeServiceQuery(), new FakeTaskQuery());
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        // 主关键词不匹配，用户补充 "Legacy" 命中
        var items = await Scan(
            service,
            Target(),
            Options(new[] { ResidualCategory.Registry }, new[] { "Legacy" }));
        Assert.Contains(items, i => i.Target == key);
    }

    [Fact]
    public async Task Scan_StopwordOnlyTarget_Throws()
    {
        var service = new ResidualService(
            new FakeRegistry(), new FakeServiceQuery(), new FakeTaskQuery());
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        await Assert.ThrowsAsync<ResidualTargetInvalidException>(
            () => Scan(
                service,
                new ResidualScanTarget(Guid.NewGuid(), "Update", null, null),
                Options(new[] { ResidualCategory.Registry })));
    }

    [Fact]
    public async Task Enqueue_EmptyItems_Throws()
    {
        var service = new ResidualService(
            new FakeRegistry(), new FakeServiceQuery(), new FakeTaskQuery());
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        await Assert.ThrowsAsync<ResidualItemsEmptyException>(
            () => service.EnqueueCleanupAsync(
                Array.Empty<ResidualItem>(),
                new ResidualCleanupOptions(false),
                null,
                CancellationToken.None));
    }

    [Fact]
    public async Task Enqueue_DryRun_ReturnsSuccessWithoutTouchingQueue()
    {
        var ctx = new FakeModuleContext();
        var service = new ResidualService(
            new FakeRegistry(), new FakeServiceQuery(), new FakeTaskQuery());
        await service.InitializeAsync(ctx, CancellationToken.None);

        var item = new ResidualItem(
            Guid.NewGuid(), Guid.NewGuid(), ResidualCategory.File, "C:\\x",
            null, new SafetyVerdict(SafetyLevel.High, "r", Array.Empty<string>()),
            0, Keyword, "d");

        var result = await service.EnqueueCleanupAsync(
            new[] { item },
            new ResidualCleanupOptions(DryRun: true),
            null,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(ctx.Queue.Plans);
    }

    [Fact]
    public async Task Enqueue_PreviewCancelled_FalseAndSkipsQueue()
    {
        var ctx = new FakeModuleContext();
        ctx.TrustStub.Confirmed = false;
        var service = new ResidualService(
            new FakeRegistry(), new FakeServiceQuery(), new FakeTaskQuery());
        await service.InitializeAsync(ctx, CancellationToken.None);

        var item = new ResidualItem(
            Guid.NewGuid(), Guid.NewGuid(), ResidualCategory.File, "C:\\x",
            null, new SafetyVerdict(SafetyLevel.High, "r", Array.Empty<string>()),
            0, Keyword, "d");

        var result = await service.EnqueueCleanupAsync(
            new[] { item },
            new ResidualCleanupOptions(false),
            null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(ctx.Queue.Plans);
    }

    [Fact]
    public async Task Enqueue_Confirmed_MapsActionsAndPlanSafetyIsLowest()
    {
        var registry = new FakeRegistry();
        var orphanKey = RegistryRoots.UninstallHkcu + @"\Orphan";
        registry.SetValue(orphanKey, "DisplayName", Keyword);
        registry.SetValue(
            RegistryRoots.RunHkcu, "SutTray", $"\"C:\\Sut\\{Keyword}\\app.exe\"");

        var services = new FakeServiceQuery();
        services.Services.Add(new ServiceInfo(
            Keyword, "Sut Service", null, ServiceState.Running));

        var tasks = new FakeTaskQuery();
        tasks.Tasks.Add(new ScheduledTaskInfo($@"\Sut\{Keyword}", Keyword, null));

        var ctx = new FakeModuleContext();
        // 服务项打 Low，其余 High → 整体 Low
        ctx.SafetyStub.Level = SafetyLevel.High;
        var original = ctx.Safety;
        ctx.Safety = new ServiceLowClassifier();

        var service = new ResidualService(registry, services, tasks);
        await service.InitializeAsync(ctx, CancellationToken.None);

        var items = await Scan(service, Target(), Options());

        await service.EnqueueCleanupAsync(
            items,
            new ResidualCleanupOptions(false),
            null,
            CancellationToken.None);

        var plan = Assert.Single(ctx.Queue.Plans);
        Assert.True(plan.UserConfirmed);
        Assert.Equal(SafetyLevel.Low, plan.SafetyLevel);

        var kinds = plan.Actions.Select(a => a.Kind).ToList();
        Assert.Contains(ActionKind.DeleteRegistry, kinds);
        Assert.Contains(ActionKind.StopService, kinds);
        Assert.Contains(ActionKind.DeleteScheduledTask, kinds);

        var stopAction = plan.Actions.Single(a => a.Kind == ActionKind.StopService);
        Assert.Equal(Keyword, stopAction.ServiceName);

        var taskAction = plan.Actions.Single(a => a.Kind == ActionKind.DeleteScheduledTask);
        Assert.Equal($@"\Sut\{Keyword}", taskAction.TaskPath);
    }

    private sealed class ServiceLowClassifier : ISafetyClassifier
    {
        public SafetyVerdict Classify(ScannedItem item)
            => new(
                item.Type == ItemType.Service ? SafetyLevel.Low : SafetyLevel.High,
                "测试",
                Array.Empty<string>());
    }
}
