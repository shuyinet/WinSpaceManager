using SysMgr.Core.Operations;
using SysMgr.Core.Safety;
using SysMgr.Modules.Shred;
using SysMgr.Modules.Shred.Errors;
using SysMgr.Modules.Tests.Fakes;
using Xunit;

namespace SysMgr.Modules.Tests;

/// <summary>
/// 文件粉碎模块测试（FR-SHRED-01/02）。
/// 真实临时目录造文件；FakeModuleContext 断言预演与入队计划字段。
/// </summary>
public class ShredServiceTests : IDisposable
{
    private readonly TempWorkspace _ws = new();

    public void Dispose() => _ws.Dispose();

    private static ShredService CreateService(FakeModuleContext ctx)
    {
        var service = new ShredService();
        service.InitializeAsync(ctx, CancellationToken.None).GetAwaiter().GetResult();
        return service;
    }

    // ---------------- AnalyzeAsync ----------------

    [Fact]
    public async Task Analyze_SingleFile_ReturnsCountAndSize()
    {
        var ctx = new FakeModuleContext();
        var service = CreateService(ctx);
        var file = _ws.CreateFile("a.bin", "0123456789");

        var targets = await service.AnalyzeAsync(new[] { file }, CancellationToken.None);

        var target = Assert.Single(targets);
        Assert.False(target.IsDirectory);
        Assert.Equal(1, target.FileCount);
        Assert.Equal(10, target.TotalBytes);
        Assert.Equal(SafetyLevel.High, target.Level); // Stub 默认 High
    }

    [Fact]
    public async Task Analyze_Directory_RecursivelyCountsIncludingHiddenReadonly()
    {
        var ctx = new FakeModuleContext();
        var service = CreateService(ctx);
        var dir = _ws.CreateDirectory("tree");
        _ws.CreateFile("tree/a.txt", "12345");
        var hidden = _ws.CreateFile("tree/sub/h.txt", "12345");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.ReadOnly);
        _ws.CreateFile("tree/sub/b.txt", "12345");

        var targets = await service.AnalyzeAsync(new[] { dir }, CancellationToken.None);

        var target = Assert.Single(targets);
        Assert.True(target.IsDirectory);
        Assert.Equal(3, target.FileCount);
        Assert.Equal(15, target.TotalBytes);
    }

    [Fact]
    public async Task Analyze_CriticalPath_Throws()
    {
        var ctx = new FakeModuleContext();
        ctx.SafetyStub.Level = SafetyLevel.Low; // 模拟命中系统关键路径
        var service = CreateService(ctx);
        var file = _ws.CreateFile("sys.dll");

        await Assert.ThrowsAsync<ShredCriticalPathException>(
            () => service.AnalyzeAsync(new[] { file }, CancellationToken.None));
    }

    [Fact]
    public async Task Analyze_DriveRoot_Throws()
    {
        var ctx = new FakeModuleContext();
        var service = CreateService(ctx);
        var root = Path.GetPathRoot(_ws.Root)!;

        await Assert.ThrowsAsync<ShredCriticalPathException>(
            () => service.AnalyzeAsync(new[] { root }, CancellationToken.None));
    }

    [Fact]
    public async Task Analyze_MissingTarget_Throws()
    {
        var ctx = new FakeModuleContext();
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<ShredTargetNotFoundException>(
            () => service.AnalyzeAsync(new[] { Path.Combine(_ws.Root, "nope.bin") },
                CancellationToken.None));
    }

    [Fact]
    public async Task Analyze_EmptyPath_Throws()
    {
        var ctx = new FakeModuleContext();
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<ShredEmptySelectionException>(
            () => service.AnalyzeAsync(new[] { "  " }, CancellationToken.None));
    }

    // ---------------- ShredAsync ----------------

    [Fact]
    public async Task Shred_SingleFile_EnqueuesShredActionWithPasses()
    {
        var ctx = new FakeModuleContext();
        var service = CreateService(ctx);
        var file = _ws.CreateFile("victim.bin", "0123456789");

        var result = await service.ShredAsync(
            new[] { file }, ShredMode.Secure7, null, CancellationToken.None);

        Assert.True(result.Success);
        var plan = Assert.Single(ctx.Queue.Plans);
        Assert.Equal("shred", plan.ModuleId);
        Assert.Equal("FR-SHRED-01", plan.FrId);
        Assert.True(plan.PreviewRequired);
        Assert.True(plan.UserConfirmed);
        Assert.Contains("不可恢复", plan.Description);
        Assert.Contains("7 遍覆写", plan.Description);
        var action = Assert.Single(plan.Actions);
        Assert.Equal(ActionKind.Shred, action.Kind);
        Assert.Equal(file, action.Target);
        Assert.Equal(10, action.SizeBytes);
        Assert.Equal(7, action.ShredPasses);
    }

    [Fact]
    public async Task Shred_Directory_ExpandsFilesAndAppendsDirCleanup()
    {
        var ctx = new FakeModuleContext();
        var service = CreateService(ctx);
        var dir = _ws.CreateDirectory("tree");
        var f1 = _ws.CreateFile("tree/a.txt");
        var f2 = _ws.CreateFile("tree/sub/b.txt");

        var result = await service.ShredAsync(
            new[] { dir }, ShredMode.Quick, null, CancellationToken.None);

        Assert.True(result.Success);
        var plan = Assert.Single(ctx.Queue.Plans);
        Assert.Equal(3, plan.Actions.Count); // 2 文件 + 1 目录收尾
        var shredActions = plan.Actions.Where(a => a.Kind == ActionKind.Shred).ToList();
        Assert.Equal(2, shredActions.Count);
        Assert.All(shredActions, a => Assert.Equal(1, a.ShredPasses));
        Assert.Contains(shredActions, a => a.Target == Path.GetFullPath(f1));
        Assert.Contains(shredActions, a => a.Target == Path.GetFullPath(f2));
        var last = plan.Actions[^1];
        Assert.Equal(ActionKind.DeleteFile, last.Kind);
        Assert.Equal(dir, last.Target);
    }

    [Fact]
    public async Task Shred_PreviewCancelled_DoesNotEnqueue()
    {
        var ctx = new FakeModuleContext { TrustStub = { Confirmed = false } };
        var service = CreateService(ctx);
        var file = _ws.CreateFile("cancel.bin");

        var result = await service.ShredAsync(
            new[] { file }, ShredMode.Quick, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("用户取消预演", result.Error);
        Assert.Empty(ctx.Queue.Plans);
    }

    [Fact]
    public async Task Shred_EmptyDirectory_Throws()
    {
        var ctx = new FakeModuleContext();
        var service = CreateService(ctx);
        var dir = _ws.CreateDirectory("empty");

        await Assert.ThrowsAsync<ShredEmptySelectionException>(
            () => service.ShredAsync(new[] { dir }, ShredMode.Quick, null, CancellationToken.None));
    }

    [Fact]
    public async Task Shred_CriticalPath_ThrowsBeforePreview()
    {
        var ctx = new FakeModuleContext();
        ctx.SafetyStub.Level = SafetyLevel.Low;
        var service = CreateService(ctx);
        var file = _ws.CreateFile("sys.dll");

        await Assert.ThrowsAsync<ShredCriticalPathException>(
            () => service.ShredAsync(new[] { file }, ShredMode.Quick, null, CancellationToken.None));
        Assert.Empty(ctx.Queue.Plans);
    }

    [Fact]
    public async Task Shred_MixedLevels_AggregatesWorstLevel()
    {
        var ctx = new FakeModuleContext();
        var service = CreateService(ctx);
        ctx.SafetyStub.Level = SafetyLevel.Medium; // 用户文件 → Medium
        var file = _ws.CreateFile("doc.zip");

        await service.ShredAsync(new[] { file }, ShredMode.Secure3, null, CancellationToken.None);

        var plan = Assert.Single(ctx.Queue.Plans);
        Assert.Equal(SafetyLevel.Medium, plan.SafetyLevel);
        var action = Assert.Single(plan.Actions);
        Assert.Equal(3, action.ShredPasses);
    }
}
