using System.Text.Json;
using SysMgr.Core.Operations;
using SysMgr.Core.Safety;
using SysMgr.Modules.Tests.Fakes;
using SysMgr.Modules.Visualization;
using SysMgr.Modules.Visualization.Errors;
using Xunit;

namespace SysMgr.Modules.Tests;

/// <summary>
/// 磁盘可视化模块测试（FR-VIS-01/02/03）。
/// 扫描用临时目录夹具；删除/迁移/标签用 FakeModuleContext 断言契约。
/// </summary>
public class VisualizationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vis_test_" + Guid.NewGuid().ToString("N"));

    public VisualizationServiceTests() => Directory.CreateDirectory(_root);

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

    private string WriteFile(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static VisualizationService CreateService(FakeModuleContext? ctx = null)
    {
        var service = new VisualizationService();
        service.InitializeAsync(ctx ?? new FakeModuleContext(), CancellationToken.None)
            .GetAwaiter().GetResult();
        return service;
    }

    // ---------------- FR-VIS-01 扫描 ----------------

    [Fact]
    public async Task Scan_NestedTree_SizesAggregateBottomUp()
    {
        WriteFile("a.bin", 1000);
        WriteFile(Path.Combine("sub1", "b.bin"), 2000);
        WriteFile(Path.Combine("sub1", "deep", "c.bin"), 4000);

        var service = CreateService();
        var root = await service.ScanAsync(_root, null, CancellationToken.None);

        Assert.True(root.IsDirectory);
        Assert.Equal(7000, root.TotalBytes);

        var sub1 = Assert.Single(root.Children.Where(c => c.IsDirectory));
        Assert.Equal(6000, sub1.TotalBytes);

        var deep = Assert.Single(sub1.Children.Where(c => c.IsDirectory));
        Assert.Equal(4000, deep.TotalBytes);
    }

    [Fact]
    public async Task Scan_TinyFiles_MergedIntoOtherAggregate()
    {
        WriteFile("big.bin", 100 * 1024 * 1024 / 8); // 12.5MB，阈值约 100KB
        for (var i = 0; i < 5; i++)
        {
            WriteFile($"tiny{i}.bin", 1024);
        }

        var service = CreateService();
        var root = await service.ScanAsync(_root, null, CancellationToken.None);

        var aggregate = Assert.Single(root.Children.Where(c => c.IsOtherAggregate));
        Assert.Equal(5, aggregate.FileCount);
        Assert.Equal(5 * 1024, aggregate.TotalBytes);
        Assert.Contains(root.Children, c => !c.IsDirectory && !c.IsOtherAggregate && c.Name == "big.bin");
        // 目录统计口径仍覆盖全部直属文件（含被聚合的）
        Assert.Equal(6, root.FileCount);
    }

    [Fact]
    public async Task Scan_ClassifiesDominantKindByBytes()
    {
        WriteFile("movie.mp4", 9000);
        WriteFile("note.txt", 1000);

        var service = CreateService();
        var root = await service.ScanAsync(_root, null, CancellationToken.None);

        Assert.Equal(VisFileKind.Video, root.DominantKind);
        var movie = Assert.Single(root.Children.Where(c => c.Name == "movie.mp4"));
        Assert.Equal(VisFileKind.Video, movie.DominantKind);
    }

    [Fact]
    public async Task Scan_NonexistentRoot_Throws()
    {
        var service = CreateService();
        await Assert.ThrowsAsync<InvalidScanTargetException>(() =>
            service.ScanAsync(Path.Combine(_root, "missing_dir"), null, CancellationToken.None));
    }

    [Fact]
    public async Task Scan_Cancelled_Throws()
    {
        WriteFile("a.bin", 100);
        var service = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ScanAsync(_root, null, cts.Token));
    }

    [Fact]
    public async Task Scan_ChildrenSortedDescending()
    {
        WriteFile("small.bin", 1000 * 1024 * 1024 / 16); // 确保高于聚合阈值
        WriteFile("large.bin", 1000 * 1024 * 1024 / 8);

        var service = CreateService();
        var root = await service.ScanAsync(_root, null, CancellationToken.None);

        Assert.True(root.Children.Count >= 2);
        Assert.Equal("large.bin", root.Children[0].Name);
    }

    // ---------------- FR-VIS-02 筛选 ----------------

    [Fact]
    public async Task Filter_ByExtension_KeepsMatchingFilesOnly()
    {
        WriteFile("keep.mp4", 5000);
        WriteFile("drop.bin", 5000);

        var service = CreateService();
        var root = await service.ScanAsync(_root, null, CancellationToken.None);
        var filtered = service.FilterTree(root, "mp4", 0, null, false);

        var leaf = Assert.Single(filtered.Children);
        Assert.Equal("keep.mp4", leaf.Name);
        Assert.Equal(5000, filtered.TotalBytes);
    }

    [Fact]
    public async Task Filter_ByMinSize_DropsSmallFiles()
    {
        WriteFile("big.bin", 9000);
        WriteFile("small.bin", 1000);

        var service = CreateService();
        var root = await service.ScanAsync(_root, null, CancellationToken.None);
        var filtered = service.FilterTree(root, null, 5000, null, false);

        var leaf = Assert.Single(filtered.Children);
        Assert.Equal("big.bin", leaf.Name);
    }

    [Fact]
    public async Task Filter_NoCondition_ReturnsSameTree()
    {
        WriteFile("a.bin", 100);
        var service = CreateService();
        var root = await service.ScanAsync(_root, null, CancellationToken.None);

        Assert.Same(root, service.FilterTree(root, null, 0, null, false));
    }

    [Fact]
    public async Task Filter_ExcludeHidden_DropsHiddenFilesAndDirs()
    {
        var visible = WriteFile("visible.bin", 3000);
        var hidden = WriteFile("hidden.bin", 3000);
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var hiddenDir = WriteFile(Path.Combine("secret_dir", "inner.bin"), 3000);
        File.SetAttributes(Path.GetDirectoryName(hiddenDir)!, FileAttributes.Hidden);

        var service = CreateService();
        var root = await service.ScanAsync(_root, null, CancellationToken.None);

        // 不排除时全部可见
        Assert.Equal(3, root.Children.Count);

        var filtered = service.FilterTree(root, null, 0, null, true);
        var leaf = Assert.Single(filtered.Children);
        Assert.Equal("visible.bin", leaf.Name);
        Assert.Equal(3000, filtered.TotalBytes);
    }

    [Fact]
    public async Task Filter_DirectoryNameMatch_KeepsWholeSubtree()
    {
        WriteFile(Path.Combine("target_dir", "a.bin"), 3000);
        WriteFile("other.bin", 3000);

        var service = CreateService();
        var root = await service.ScanAsync(_root, null, CancellationToken.None);
        var filtered = service.FilterTree(root, "target_dir", 0, null, false);

        var dir = Assert.Single(filtered.Children);
        Assert.Equal("target_dir", dir.Name);
        Assert.True(dir.Children.Count > 0);
    }

    // ---------------- FR-VIS-02 删除 / 迁移 ----------------

    [Fact]
    public async Task EnqueueDelete_PreviewConfirmed_EnqueuesDeletePlan()
    {
        var ctx = new FakeModuleContext();
        ctx.TrustStub.Confirmed = true;
        var service = CreateService(ctx);
        var file = WriteFile("victim.bin", 100);

        var result = await service.EnqueueDeleteAsync(
            new[] { file }, null, CancellationToken.None);

        Assert.True(result.Success);
        var plan = Assert.Single(ctx.Queue.Plans);
        Assert.Equal("visualization", plan.ModuleId);
        Assert.Equal("FR-VIS-02", plan.FrId);
        Assert.True(plan.UserConfirmed);
        Assert.True(plan.PreviewRequired);
        var action = Assert.Single(plan.Actions);
        Assert.Equal(ActionKind.DeleteFile, action.Kind);
        Assert.Equal(file, action.Target);
    }

    [Fact]
    public async Task EnqueueDelete_PreviewCancelled_SkipsQueue()
    {
        var ctx = new FakeModuleContext();
        ctx.TrustStub.Confirmed = false;
        var service = CreateService(ctx);
        var file = WriteFile("victim.bin", 100);

        var result = await service.EnqueueDeleteAsync(
            new[] { file }, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(ctx.Queue.Plans);
    }

    [Fact]
    public async Task EnqueueDelete_Empty_Throws()
    {
        var service = CreateService();
        await Assert.ThrowsAsync<VisItemsEmptyException>(() =>
            service.EnqueueDeleteAsync(Array.Empty<string>(), null, CancellationToken.None));
    }

    [Fact]
    public async Task EnqueueMigration_RoutesFileRequestToCoordinator()
    {
        var ctx = new FakeModuleContext();
        var service = CreateService(ctx);
        var file = WriteFile("move_me.bin", 100);
        var dest = Path.GetPathRoot(_root)!;

        var result = await service.EnqueueMigrationAsync(
            new[] { file }, dest, null, CancellationToken.None);

        Assert.True(result.Success);
        var request = ctx.Recorder.LastRequest;
        Assert.NotNull(request);
        Assert.Equal(MigrationKind.File, request!.Kind);
        Assert.Equal(new[] { file }, request.Sources);
        Assert.False(request.CreateJunction);
        Assert.True(request.DeleteSourceAfterVerify);
    }

    // ---------------- FR-VIS-03 标签 / 导出 ----------------

    [Fact]
    public async Task Tags_SetGetRemove_RoundTrips()
    {
        var ctx = new FakeModuleContext();
        var service = CreateService(ctx);

        await service.SetTagAsync(@"D:\a\b.zip", "待处理", CancellationToken.None);
        var tags = await service.GetTagsAsync(CancellationToken.None);
        Assert.Equal("待处理", tags[@"D:\a\b.zip"]);

        await service.SetTagAsync(@"D:\a\b.zip", null, CancellationToken.None);
        tags = await service.GetTagsAsync(CancellationToken.None);
        Assert.False(tags.ContainsKey(@"D:\a\b.zip"));
    }

    [Fact]
    public async Task ExportSnapshot_WritesJsonWithTree()
    {
        WriteFile("a.bin", 1234);
        var service = CreateService();
        var root = await service.ScanAsync(_root, null, CancellationToken.None);
        var outFile = Path.Combine(_root, "snapshot.json");

        await service.ExportSnapshotAsync(root, outFile, CancellationToken.None);

        Assert.True(File.Exists(outFile));
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outFile));
        Assert.Equal("vis_snapshot", doc.RootElement.GetProperty("kind").GetString());
        var tree = doc.RootElement.GetProperty("root");
        Assert.Equal(1234, tree.GetProperty("TotalBytes").GetInt64());
    }
}
