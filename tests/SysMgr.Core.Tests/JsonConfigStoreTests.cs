using SysMgr.Core.Config;
using SysMgr.Core.Tests.Fakes;
using Xunit;

namespace SysMgr.Core.Tests;

public sealed class JsonConfigStoreTests
{
    private sealed record CleanupSettings(bool KeepLogin, int Threshold);

    [Fact]
    public async Task 不存在的配置节返回默认值()
    {
        using var ws = new TempWorkspace();
        var store = new JsonConfigStore(Path.Combine(ws.Root, "config.json"));

        var value = await store.GetAsync<CleanupSettings>("cleanup", CancellationToken.None);

        Assert.Null(value);
    }

    [Fact]
    public async Task 写入后可按节读回()
    {
        using var ws = new TempWorkspace();
        var configPath = Path.Combine(ws.Root, "config.json");
        var store = new JsonConfigStore(configPath);

        var settings = new CleanupSettings(true, 100);
        await store.UpdateAsync("cleanup", settings, CancellationToken.None);

        var read = await store.GetAsync<CleanupSettings>("cleanup", CancellationToken.None);
        Assert.Equal(settings, read);
        Assert.True(File.Exists(configPath));
    }

    [Fact]
    public async Task 多配置节共存于同一文件()
    {
        using var ws = new TempWorkspace();
        var store = new JsonConfigStore(Path.Combine(ws.Root, "config.json"));

        await store.UpdateAsync("a", new CleanupSettings(true, 1), CancellationToken.None);
        await store.UpdateAsync("b", new CleanupSettings(false, 2), CancellationToken.None);

        Assert.Equal(1, (await store.GetAsync<CleanupSettings>("a", CancellationToken.None))!.Threshold);
        Assert.Equal(2, (await store.GetAsync<CleanupSettings>("b", CancellationToken.None))!.Threshold);
    }

    [Fact]
    public async Task Reset删除配置节()
    {
        using var ws = new TempWorkspace();
        var store = new JsonConfigStore(Path.Combine(ws.Root, "config.json"));
        await store.UpdateAsync("cleanup", new CleanupSettings(true, 1), CancellationToken.None);

        await store.ResetAsync("cleanup", CancellationToken.None);

        Assert.Null(await store.GetAsync<CleanupSettings>("cleanup", CancellationToken.None));
    }

    [Fact]
    public async Task 实例重启后配置仍可加载()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Root, "config.json");
        var first = new JsonConfigStore(path);
        await first.UpdateAsync("cleanup", new CleanupSettings(true, 42), CancellationToken.None);

        var second = new JsonConfigStore(path);
        var read = await second.GetAsync<CleanupSettings>("cleanup", CancellationToken.None);

        Assert.Equal(42, read!.Threshold);
    }
}
