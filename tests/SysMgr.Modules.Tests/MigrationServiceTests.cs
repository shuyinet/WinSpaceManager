using SysMgr.Modules.Tests.Fakes;
using SysMgr.Core.Operations;
using SysMgr.Modules;
using SysMgr.Modules.Migration;
using SysMgr.Modules.Migration.Errors;
using Xunit;

namespace SysMgr.Modules.Tests;

public class MigrationServiceTests
{
    private static MigrationRequest FileRequest(
        IReadOnlyList<string> sources,
        string destination,
        bool deleteSource = false)
        => new(
            Guid.NewGuid(),
            MigrationKind.File,
            sources,
            destination,
            CreateJunction: false,
            DeleteSourceAfterVerify: deleteSource);

    [Fact]
    public async Task Validate_File_ReturnsCanMigrateAndSizes()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateFile("a.txt", "hello");
        var destination = ws.CreateDirectory("dest");
        var service = new MigrationService();
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        var validation = await service.ValidateAsync(
            FileRequest(new[] { source }, destination), null, CancellationToken.None);

        Assert.True(validation.CanMigrate);
        Assert.Equal(new FileInfo(source).Length, validation.RequiredBytes);
    }

    [Fact]
    public async Task Validate_EmptySources_AddsIssue()
    {
        using var ws = new TempWorkspace();
        var service = new MigrationService();
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        var request = new MigrationRequest(
            Guid.NewGuid(),
            MigrationKind.File,
            Array.Empty<string>(),
            ws.Root,
            false,
            false);

        var validation = await service.ValidateAsync(request, null, CancellationToken.None);

        Assert.False(validation.CanMigrate);
        Assert.Contains(validation.Issues, i => i.Contains("迁移源为空"));
    }

    [Fact]
    public async Task Validate_SystemCriticalSource_Blocked()
    {
        using var ws = new TempWorkspace();
        var notepad = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        var service = new MigrationService();
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        var validation = await service.ValidateAsync(
            FileRequest(new string[] { notepad }, ws.CreateDirectory("dest")),
            null,
            CancellationToken.None);

        Assert.False(validation.CanMigrate);
        Assert.Contains(validation.Issues, i => i.Contains("系统关键路径"));
    }

    [Fact]
    public async Task Validate_NestedFolder_Blocked()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("root");
        ws.CreateFile("root/f1.txt");
        var nested = ws.CreateDirectory("root/inside");
        var service = new MigrationService();
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        var request = new MigrationRequest(
            Guid.NewGuid(),
            MigrationKind.Folder,
            new[] { source },
            nested,
            CreateJunction: false,
            DeleteSourceAfterVerify: true);

        var validation = await service.ValidateAsync(request, null, CancellationToken.None);

        Assert.False(validation.CanMigrate);
        Assert.Contains(validation.Issues, i => i.Contains("嵌套关系"));
    }

    [Fact]
    public async Task Validate_ExistingDestinationFile_ReportsRenameWarning()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateFile("src/a.txt", "1");
        var destination = ws.CreateDirectory("dest");
        // 目标已存在同名文件
        File.WriteAllText(Path.Combine(destination, "a.txt"), "old");
        var service = new MigrationService();
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        var validation = await service.ValidateAsync(
            FileRequest(new[] { source }, destination), null, CancellationToken.None);

        Assert.True(validation.CanMigrate);
        Assert.Contains(validation.Warnings, w => w.Contains("改名"));
    }

    [Fact]
    public async Task Enqueue_UserCancelsPreview_ReturnsFalseAndSkipsQueue()
    {
        using var ws = new TempWorkspace();
        var ctx = new FakeModuleContext();
        ctx.TrustStub.Confirmed = false;
        var source = ws.CreateFile("a.txt");
        var destination = ws.CreateDirectory("dest");
        var service = new MigrationService();
        await service.InitializeAsync(ctx, CancellationToken.None);

        var result = await service.EnqueueMigrationAsync(
            FileRequest(new[] { source }, destination), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(ctx.Queue.Plans);
    }

    [Fact]
    public async Task Enqueue_CopyMode_PlanContainsCopyFile()
    {
        using var ws = new TempWorkspace();
        var ctx = new FakeModuleContext();
        var source = ws.CreateFile("a.txt");
        var destination = ws.CreateDirectory("dest");
        var service = new MigrationService();
        await service.InitializeAsync(ctx, CancellationToken.None);

        await service.EnqueueMigrationAsync(
            FileRequest(new[] { source }, destination, deleteSource: false),
            null,
            CancellationToken.None);

        var plan = Assert.Single(ctx.Queue.Plans);
        var action = Assert.Single(plan.Actions);
        Assert.Equal(ActionKind.CopyFile, action.Kind);
        Assert.Equal(source, action.Target);
        Assert.True(plan.UserConfirmed);
    }

    [Fact]
    public async Task Enqueue_FolderMoveWithJunction_ActionOrderIsMoveDeleteJunction()
    {
        using var ws = new TempWorkspace();
        var ctx = new FakeModuleContext();
        var source = ws.CreateDirectory("root");
        ws.CreateFile("root/f1.txt");
        ws.CreateFile("root/sub/f2.txt");
        var destination = ws.CreateDirectory("dest");
        var service = new MigrationService();
        await service.InitializeAsync(ctx, CancellationToken.None);

        var request = new MigrationRequest(
            Guid.NewGuid(),
            MigrationKind.Folder,
            new[] { source },
            destination,
            CreateJunction: true,
            DeleteSourceAfterVerify: true);

        await service.EnqueueMigrationAsync(request, null, CancellationToken.None);

        var plan = Assert.Single(ctx.Queue.Plans);
        Assert.Equal(
            new[]
            {
                ActionKind.MoveFile,
                ActionKind.MoveFile,
                ActionKind.DeleteFile,
                ActionKind.CreateJunction
            },
            plan.Actions.Select(a => a.Kind));

        var junction = plan.Actions[^1];
        Assert.Equal(source, junction.Target);
        Assert.Equal(destination, junction.NewPath);
    }

    [Fact]
    public async Task Enqueue_PrecheckFailure_Throws()
    {
        using var ws = new TempWorkspace();
        var service = new MigrationService();
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        var request = new MigrationRequest(
            Guid.NewGuid(),
            MigrationKind.File,
            Array.Empty<string>(),
            ws.Root,
            false,
            false);

        await Assert.ThrowsAsync<MigrationPrecheckFailedException>(
            () => service.EnqueueMigrationAsync(request, null, CancellationToken.None));
    }
}
