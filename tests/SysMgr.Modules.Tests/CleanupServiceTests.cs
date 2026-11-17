using SysMgr.Core.Operations;
using SysMgr.Core.Safety;
using SysMgr.Core.SystemAccess;
using SysMgr.Infra;
using SysMgr.Modules.Tests.Fakes;
using SysMgr.Modules.Cleanup;
using SysMgr.Modules.Cleanup.Errors;
using Xunit;

namespace SysMgr.Modules.Tests;

public class CleanupServiceTests : IDisposable
{
    private readonly string? _oldTemp;
    private readonly string _localAppData;
    private readonly string _appData;
    private readonly List<string> _createdRoots = new();

    public CleanupServiceTests()
    {
        _oldTemp = Environment.GetEnvironmentVariable("TEMP");
        _localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    }

    public void Dispose()
    {
        if (_oldTemp is not null)
        {
            Environment.SetEnvironmentVariable("TEMP", _oldTemp);
        }

        // 逆序删除测试创建的目录
        for (var i = _createdRoots.Count - 1; i >= 0; i--)
        {
            try
            {
                if (Directory.Exists(_createdRoots[i]))
                {
                    Directory.Delete(_createdRoots[i], true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<List<JunkItem>> Scan(
        CleanupService service, JunkScope scope)
    {
        var items = new List<JunkItem>();
        await foreach (var item in service.ScanJunkAsync(scope, null, CancellationToken.None))
        {
            items.Add(item);
        }
        return items;
    }

    private static JunkItem BrowserItem(JunkBrowser browser, JunkKind kind = JunkKind.BrowserCache)
        => new(
            Guid.NewGuid(),
            kind,
            browser,
            "C:\\sut\\" + browser + "\\cache",
            new SafetyVerdict(SafetyLevel.High, "r", Array.Empty<string>()),
            100,
            DefaultSelected: true,
            "d");

    [Fact]
    public async Task Scan_TempTarget_ReportsSizeAndPath()
    {
        using var ws = new TempWorkspace();
        var tempDir = ws.CreateDirectory("temp");
        File.WriteAllText(Path.Combine(tempDir, "junk.tmp"), "junk-content");
        Environment.SetEnvironmentVariable("TEMP", tempDir);

        var ctx = new FakeModuleContext();
        var service = new CleanupService(new FakeRecycleBin(), new FakeProcessQuery());
        await service.InitializeAsync(ctx, CancellationToken.None);

        var scope = new JunkScope(new[] { JunkKind.Temp }, Array.Empty<JunkBrowserTarget>());
        var item = Assert.Single(await Scan(service, scope));

        Assert.Equal(JunkKind.Temp, item.Kind);
        Assert.Equal(tempDir, item.Target);
        Assert.True(item.EstimatedSizeBytes > 0);
    }

    [Fact]
    public async Task Scan_RecycleBin_HighAndDefaultSelected()
    {
        var recycleBin = new FakeRecycleBin { Size = 2048 };
        var ctx = new FakeModuleContext();
        var service = new CleanupService(recycleBin, new FakeProcessQuery());
        await service.InitializeAsync(ctx, CancellationToken.None);

        var scope = new JunkScope(new[] { JunkKind.RecycleBin }, Array.Empty<JunkBrowserTarget>());
        var item = Assert.Single(await Scan(service, scope));

        Assert.Equal(SafetyLevel.High, item.Verdict.Level);
        Assert.True(item.DefaultSelected);
        Assert.Equal(2048, item.EstimatedSizeBytes);
    }

    [Fact]
    public async Task Scan_ChromeCache_FoundBrowserItemAndDefaultSelected()
    {
        // 使用冷门 Profile 编号，避免触碰真实 Chrome 配置
        var profileRoot = Path.Combine(
            _localAppData, "Google", "Chrome", "User Data", "Profile 77");
        var cacheDir = Path.Combine(profileRoot, "Cache");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Combine(cacheDir, "f.tmp"), "x");
        _createdRoots.Add(profileRoot);

        var ctx = new FakeModuleContext();
        var service = new CleanupService(new FakeRecycleBin(), new FakeProcessQuery());
        await service.InitializeAsync(ctx, CancellationToken.None);

        var scope = new JunkScope(
            Array.Empty<JunkKind>(),
            new[]
            {
                new JunkBrowserTarget(JunkBrowser.Chrome, new[] { JunkKind.BrowserCache })
            });
        var items = await Scan(service, scope);

        // 缓存子目录已扩展（Code Cache/DawnCache/ShaderCache 等），真实 Chrome 配置可能贡献多项；
        // 断言测试 Profile 的缓存被收录且全部为可默认勾选的浏览器缓存项
        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.Equal(JunkBrowser.Chrome, i.Browser));
        Assert.All(items, i => Assert.Equal(JunkKind.BrowserCache, i.Kind));
        Assert.All(items, i => Assert.True(i.DefaultSelected));
        Assert.Contains(items, i => i.Target == cacheDir);
    }

    [Fact]
    public async Task Scan_FirefoxCache_Found()
    {
        var profileRoot = Path.Combine(
            _appData, "Mozilla", "Firefox", "Profiles", "zqxsut.default");
        var cacheDir = Path.Combine(profileRoot, "cache2");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Combine(cacheDir, "f.tmp"), "x");
        _createdRoots.Add(profileRoot);

        var ctx = new FakeModuleContext();
        var service = new CleanupService(new FakeRecycleBin(), new FakeProcessQuery());
        await service.InitializeAsync(ctx, CancellationToken.None);

        var scope = new JunkScope(
            Array.Empty<JunkKind>(),
            new[]
            {
                new JunkBrowserTarget(JunkBrowser.Firefox, new[] { JunkKind.BrowserCache })
            });
        var item = Assert.Single(await Scan(service, scope));

        Assert.Equal(JunkBrowser.Firefox, item.Browser);
        Assert.Equal(cacheDir, item.Target);
    }

    [Fact]
    public async Task Enqueue_BrowserRunningWithoutKill_Throws()
    {
        var processes = new FakeProcessQuery();
        processes.Processes.Add(new ProcessInfo(1, "chrome", null));

        var ctx = new FakeModuleContext();
        var service = new CleanupService(new FakeRecycleBin(), processes);
        await service.InitializeAsync(ctx, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<BrowserRunningException>(
            () => service.EnqueueCleanAsync(
                new[] { BrowserItem(JunkBrowser.Chrome) },
                new CleanOptions(KillRunningBrowsers: false),
                null,
                CancellationToken.None));
        Assert.Contains("Chrome", ex.RunningBrowsers);
    }

    [Fact]
    public async Task Enqueue_BrowserRunningWithKill_AddsKillAction()
    {
        var processes = new FakeProcessQuery();
        processes.Processes.Add(new ProcessInfo(1, "chrome", null));

        var ctx = new FakeModuleContext();
        var service = new CleanupService(new FakeRecycleBin(), processes);
        await service.InitializeAsync(ctx, CancellationToken.None);

        await service.EnqueueCleanAsync(
            new[] { BrowserItem(JunkBrowser.Chrome) },
            new CleanOptions(KillRunningBrowsers: true),
            null,
            CancellationToken.None);

        var plan = Assert.Single(ctx.Queue.Plans);
        Assert.Equal(ActionKind.KillProcess, plan.Actions[0].Kind);
    }

    [Fact]
    public async Task Enqueue_RecycleBinItem_ActionTargetIsVirtualConstant()
    {
        var ctx = new FakeModuleContext();
        var service = new CleanupService(new FakeRecycleBin(), new FakeProcessQuery());
        await service.InitializeAsync(ctx, CancellationToken.None);

        var item = new JunkItem(
            Guid.NewGuid(),
            JunkKind.RecycleBin,
            Browser: null,
            WindowsOperationExecutor.RecycleBinTarget,
            new SafetyVerdict(SafetyLevel.High, "r", Array.Empty<string>()),
            512,
            DefaultSelected: true,
            "回收站");

        await service.EnqueueCleanAsync(
            new[] { item },
            new CleanOptions(false),
            null,
            CancellationToken.None);

        var action = Assert.Single(ctx.Queue.Plans).Actions[^1];
        Assert.Equal(WindowsOperationExecutor.RecycleBinTarget, action.Target);
        Assert.Equal(ActionKind.Purge, action.Kind);
    }

    [Fact]
    public async Task Enqueue_EmptyItems_Throws()
    {
        var service = new CleanupService(new FakeRecycleBin(), new FakeProcessQuery());
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        await Assert.ThrowsAsync<JunkItemsEmptyException>(
            () => service.EnqueueCleanAsync(
                Array.Empty<JunkItem>(),
                new CleanOptions(false),
                null,
                CancellationToken.None));
    }

    [Fact]
    public async Task Enqueue_PreviewCancelled_FalseAndSkipsQueue()
    {
        var ctx = new FakeModuleContext();
        ctx.TrustStub.Confirmed = false;
        var service = new CleanupService(new FakeRecycleBin(), new FakeProcessQuery());
        await service.InitializeAsync(ctx, CancellationToken.None);

        var result = await service.EnqueueCleanAsync(
            new[] { BrowserItem(JunkBrowser.Edge) },
            new CleanOptions(false),
            null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(ctx.Queue.Plans);
    }
}
