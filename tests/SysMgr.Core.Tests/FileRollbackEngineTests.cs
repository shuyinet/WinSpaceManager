using SysMgr.Core.Operations;
using SysMgr.Core.Rollback;
using SysMgr.Core.Safety;
using SysMgr.Core.Tests.Fakes;
using Xunit;

namespace SysMgr.Core.Tests;

public sealed class FileRollbackEngineTests
{
    private static OperationPlan Plan(params OperationAction[] actions) => new(
        Guid.NewGuid(), "uninstall", "FR-UNINSTALL-04", "测试计划",
        SafetyLevel.Medium, actions, PreviewRequired: true, UserConfirmed: true);

    [Fact]
    public async Task 单文件快照后可恢复()
    {
        using var ws = new TempWorkspace();
        var file = ws.CreateFile("Foo/app.ini", "data");
        var engine = new FileRollbackEngine(ws.CreateDirectory("rb"), new FakeRegistry());

        var handle = await engine.CreateSnapshotAsync(
            Plan(new OperationAction(ActionKind.DeleteFile, file, null, 4, null,
                                    null, null, null, null, null, null)),
            CancellationToken.None);
        Assert.Equal(RollbackStrategy.Copy, handle.Strategy);

        File.Delete(file);
        var result = await engine.RollbackAsync(handle.OpId, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(file));
        Assert.Equal("data", File.ReadAllText(file));
    }

    [Fact]
    public async Task 目录树快照后可完整恢复()
    {
        using var ws = new TempWorkspace();
        var dir = ws.CreateDirectory("Foo");
        ws.CreateFile("Foo/sub/a.txt", "a");
        ws.CreateFile("Foo/sub/deep/b.txt", "b");
        var engine = new FileRollbackEngine(ws.CreateDirectory("rb"), new FakeRegistry());

        var handle = await engine.CreateSnapshotAsync(
            Plan(new OperationAction(ActionKind.DeleteFile, dir, null, 0, null,
                                    null, null, null, null, null, null)),
            CancellationToken.None);

        Directory.Delete(dir, recursive: true);
        var result = await engine.RollbackAsync(handle.OpId, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(dir, "sub", "a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(dir, "sub", "deep", "b.txt")));
    }

    [Fact]
    public async Task 注册表树删除后可重建()
    {
        using var ws = new TempWorkspace();
        var registry = new FakeRegistry();
        const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Foo";
        registry.SetValue(key, "Setting", "V1");
        registry.SetValue(key + @"\Sub", "Other", "V2");

        var engine = new FileRollbackEngine(ws.CreateDirectory("rb"), registry);
        var handle = await engine.CreateSnapshotAsync(
            Plan(new OperationAction(ActionKind.DeleteRegistry, null, null, null, null,
                                    key, null, null, null, null, null)),
            CancellationToken.None);

        registry.DeleteKeyTree(key);
        Assert.False(registry.KeyExists(key));

        var result = await engine.RollbackAsync(handle.OpId, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal("V1", registry.GetValue(key, "Setting"));
        Assert.Equal("V2", registry.GetValue(key + @"\Sub", "Other"));
    }

    [Fact]
    public async Task SetRegistry回滚恢复原值()
    {
        using var ws = new TempWorkspace();
        var registry = new FakeRegistry();
        const string key = @"HKEY_CURRENT_USER\SOFTWARE\Bar";
        registry.SetValue(key, "PagingFiles", "C:\\pagefile.sys");

        var engine = new FileRollbackEngine(ws.CreateDirectory("rb"), registry);
        var handle = await engine.CreateSnapshotAsync(
            Plan(new OperationAction(ActionKind.SetRegistry, null, null, null, null,
                                    key, "PagingFiles", "C:\\pagefile.sys", "D:\\pagefile.sys",
                                    null, null)),
            CancellationToken.None);

        var result = await engine.RollbackAsync(handle.OpId, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("C:\\pagefile.sys", registry.GetValue(key, "PagingFiles"));
    }

    [Fact]
    public async Task 不可恢复动作快照策略为仅记录()
    {
        using var ws = new TempWorkspace();
        var engine = new FileRollbackEngine(ws.CreateDirectory("rb"), new FakeRegistry());

        var handle = await engine.CreateSnapshotAsync(
            Plan(new OperationAction(ActionKind.Shred, @"C:\x", null, 1, null,
                                    null, null, null, null, null, null)),
            CancellationToken.None);

        Assert.Equal(RollbackStrategy.PathRecordOnly, handle.Strategy);
    }

    [Fact]
    public async Task EnumerateHistory返回已捕获操作()
    {
        using var ws = new TempWorkspace();
        var engine = new FileRollbackEngine(ws.CreateDirectory("rb"), new FakeRegistry());
        var handle = await engine.CreateSnapshotAsync(
            Plan(new OperationAction(ActionKind.DeleteFile,
                                    ws.CreateFile("a.txt"), null, 1, null,
                                    null, null, null, null, null, null)),
            CancellationToken.None);
        await engine.CommitAsync(handle, CancellationToken.None);

        var entries = new List<RollbackEntry>();
        await foreach (var entry in engine.EnumerateHistoryAsync(CancellationToken.None))
        {
            entries.Add(entry);
        }

        Assert.Single(entries);
        Assert.Equal(SnapshotStatus.Committed, entries[0].Status);
    }

    [Fact]
    public async Task 回滚时目标已存在记录冲突且不覆盖()
    {
        using var ws = new TempWorkspace();
        var file = ws.CreateFile("a.txt", "original");
        var engine = new FileRollbackEngine(ws.CreateDirectory("rb"), new FakeRegistry());
        var handle = await engine.CreateSnapshotAsync(
            Plan(new OperationAction(ActionKind.DeleteFile, file, null, 8, null,
                                    null, null, null, null, null, null)),
            CancellationToken.None);

        // 不删文件，直接回滚：目标仍存在
        var result = await engine.RollbackAsync(handle.OpId, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Conflicts, c => c.Contains(file));
        Assert.Equal("original", File.ReadAllText(file));
    }
}
