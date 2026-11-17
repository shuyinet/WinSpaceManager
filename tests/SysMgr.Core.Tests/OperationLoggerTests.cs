using SysMgr.Core.Logging;
using SysMgr.Core.Tests.Fakes;
using Xunit;

namespace SysMgr.Core.Tests;

public sealed class OperationLoggerTests
{
    [Fact]
    public void LogOperation_写入JSONL并推送直播()
    {
        using var ws = new TempWorkspace();
        var historyPath = Path.Combine(ws.Root, "history.jsonl");
        var logger = new OperationLogger(historyPath);

        OperationLogEntry? received = null;
        using var sub = logger.LiveStream.Subscribe(new ActionObserver<OperationLogEntry>(e => received = e));

        var entry = new OperationLogEntry(
            Guid.NewGuid(), DateTimeOffset.Now, "uninstall", "FR-UNINSTALL-02",
            "DeleteFile", @"C:\foo", null, null, "Success", 12, null);

        logger.LogOperation(entry);

        // 落盘
        var lines = File.ReadAllLines(historyPath);
        Assert.Single(lines);
        Assert.Contains("\"uninstall\"", lines[0]);
        Assert.Contains("DeleteFile", lines[0]);

        // 直播
        Assert.NotNull(received);
        Assert.Equal(entry.OpId, received!.OpId);
    }

    [Fact]
    public void LogOperation_多次调用均追加不覆盖()
    {
        using var ws = new TempWorkspace();
        var historyPath = Path.Combine(ws.Root, "nested", "history.jsonl");
        var logger = new OperationLogger(historyPath);

        logger.LogOperation(SampleEntry());
        logger.LogOperation(SampleEntry());

        Assert.Equal(2, File.ReadAllLines(historyPath).Length);
    }

    private static OperationLogEntry SampleEntry() => new(
        Guid.NewGuid(), DateTimeOffset.Now, "m", "f", "k", null, null, null, "Success", 1, null);
}
