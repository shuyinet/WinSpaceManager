using SysMgr.Core.Operations;
using SysMgr.Core.Safety;
using SysMgr.Core.SystemAccess;
using SysMgr.Modules.Tests.Fakes;
using SysMgr.Modules;
using SysMgr.Modules.CSlim;
using SysMgr.Modules.CSlim.Errors;
using Xunit;

namespace SysMgr.Modules.Tests;

public class CSlimServiceTests : IDisposable
{
    private static readonly Guid DocumentsFolderId =
        new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");

    private readonly string _localAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private string? _bigFile;

    public void Dispose()
    {
        try
        {
            if (_bigFile is not null && File.Exists(_bigFile))
            {
                File.Delete(_bigFile);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task AnalyzeCDrive_ReturnsFiveCategories_AllNonnegative()
    {
        var service = new CSlimService(new FakeRegistry(), new FakeKnownFolder());

        var analysis = await service.AnalyzeCDriveAsync(null, CancellationToken.None);

        Assert.Equal(5, analysis.Categories.Count);
        Assert.All(analysis.Categories, c => Assert.True(c.SizeBytes >= 0));
        Assert.True(analysis.TotalBytes > 0);
        Assert.True(analysis.FreeBytes >= 0);
        Assert.False(string.IsNullOrEmpty(analysis.DriveRoot));

        // Other 兜底后，分类之和 + 剩余空间不应超过总量（允许口径误差为 0 或正）
        Assert.True(
            analysis.Categories.Sum(c => c.SizeBytes) + analysis.FreeBytes <= analysis.TotalBytes + 1024);
    }

    [Fact]
    public async Task FindLargeFiles_FindsCreatedMp4_ClassifiedVideo()
    {
        var name = "zqxsut_big_" + Guid.NewGuid().ToString("N") + ".mp4";
        _bigFile = Path.Combine(_localAppData, name);
        var size = 1024 * 1024;
        await File.WriteAllBytesAsync(_bigFile, new byte[size]);

        var service = new CSlimService(new FakeRegistry(), new FakeKnownFolder());

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        LargeFileItem? found = null;
        await foreach (var item in service.FindLargeFilesAsync(size, null, cts.Token))
        {
            if (item.Path.Equals(_bigFile, StringComparison.OrdinalIgnoreCase))
            {
                found = item;
                break;
            }
        }

        Assert.NotNull(found);
        Assert.Equal(LargeFileKind.Video, found.Kind);
        Assert.Equal(size, found.SizeBytes);
    }

    [Fact]
    public async Task EnqueueLargeFileMigration_EmptyItems_Throws()
    {
        var service = new CSlimService(new FakeRegistry(), new FakeKnownFolder());
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        await Assert.ThrowsAsync<CslimItemsEmptyException>(
            () => service.EnqueueLargeFileMigrationAsync(
                Array.Empty<LargeFileItem>(), "C:\\", null, CancellationToken.None));
    }

    [Fact]
    public async Task EnqueueLargeFileMigration_InvalidTarget_Throws()
    {
        using var ws = new TempWorkspace();
        var item = new LargeFileItem("C:\\x.mp4", 10, DateTimeOffset.Now, LargeFileKind.Video);
        var service = new CSlimService(new FakeRegistry(), new FakeKnownFolder());
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidMigrationTargetException>(
            () => service.EnqueueLargeFileMigrationAsync(
                new[] { item }, "Z:\\", null, CancellationToken.None));
    }

    [Fact]
    public async Task EnqueueLargeFileMigration_RoutesViaCoordinatorWithFileRequest()
    {
        using var ws = new TempWorkspace();
        var destination = ws.CreateDirectory("d-drive");
        var ctx = new FakeModuleContext();
        var service = new CSlimService(new FakeRegistry(), new FakeKnownFolder());
        await service.InitializeAsync(ctx, CancellationToken.None);

        var item = new LargeFileItem(
            Path.Combine(_localAppData, "movie.mp4"),
            1024,
            DateTimeOffset.Now,
            LargeFileKind.Video);

        await service.EnqueueLargeFileMigrationAsync(
            new[] { item }, destination, null, CancellationToken.None);

        var request = ctx.Recorder.LastRequest;
        Assert.NotNull(request);
        Assert.Equal(MigrationKind.File, request.Kind);
        Assert.Equal(item.Path, Assert.Single(request.Sources));
        Assert.Equal(destination, request.Destination);
        Assert.False(request.CreateJunction);
        Assert.True(request.DeleteSourceAfterVerify);
    }

    [Fact]
    public void DetectWindowsResiduals_NoDuplicateKindsAndWarningsPresent()
    {
        var service = new CSlimService(new FakeRegistry(), new FakeKnownFolder());

        var items = service.DetectWindowsResiduals();

        Assert.Equal(items.Count, items.Select(i => i.Kind).Distinct().Count());
        Assert.All(items, i => Assert.False(string.IsNullOrEmpty(i.Warning)));
    }

    [Fact]
    public async Task EnqueueWindowsResidual_Empty_Throws()
    {
        var service = new CSlimService(new FakeRegistry(), new FakeKnownFolder());
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        await Assert.ThrowsAsync<CslimItemsEmptyException>(
            () => service.EnqueueWindowsResidualCleanAsync(
                Array.Empty<WindowsResidualItem>(), null, CancellationToken.None));
    }

    [Fact]
    public async Task EnqueueWindowsResidual_ConfirmedPlanHasDeleteActions()
    {
        var ctx = new FakeModuleContext();
        var service = new CSlimService(new FakeRegistry(), new FakeKnownFolder());
        await service.InitializeAsync(ctx, CancellationToken.None);

        var item = new WindowsResidualItem(
            WindowsResidualKind.UpdateDownload,
            @"C:\Windows\SoftwareDistribution\Download",
            4096,
            "已下载的更新安装缓存，可安全删除");

        await service.EnqueueWindowsResidualCleanAsync(
            new[] { item }, null, CancellationToken.None);

        var plan = Assert.Single(ctx.Queue.Plans);
        Assert.Equal("FR-CSLIM-05", plan.FrId);
        var action = Assert.Single(plan.Actions);
        Assert.Equal(ActionKind.DeleteFile, action.Kind);
        Assert.Equal(item.Target, action.Target);
        Assert.True(plan.UserConfirmed);
    }

    [Fact]
    public async Task GetSaveLocationSuggestions_SevenItems_UnsupportedMarked()
    {
        var service = new CSlimService(new FakeRegistry(), new FakeKnownFolder());

        var suggestions = await service.GetSaveLocationSuggestionsAsync(CancellationToken.None);

        Assert.Equal(7, suggestions.Count);
        foreach (var category in new[]
                 {
                     SaveLocationCategory.NewApps,
                     SaveLocationCategory.NewMovies,
                     SaveLocationCategory.OfflineMaps
                 })
        {
            var suggestion = suggestions.Single(s => s.Category == category);
            Assert.False(suggestion.CanApply);
        }
    }

    [Fact]
    public async Task ApplySaveLocation_EmptyCategories_Throws()
    {
        var service = new CSlimService(new FakeRegistry(), new FakeKnownFolder());
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        await Assert.ThrowsAsync<CslimItemsEmptyException>(
            () => service.ApplySaveLocationSuggestionsAsync(
                Array.Empty<SaveLocationCategory>(), "D:\\", null, CancellationToken.None));
    }

    [Fact]
    public async Task ApplySaveLocation_RedirectActionCarriesCurrentAndNewPath()
    {
        using var ws = new TempWorkspace();
        var targetDrive = ws.CreateDirectory("d-drive");
        var knownFolders = new FakeKnownFolder();
        var currentPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        knownFolders.Paths[DocumentsFolderId] = currentPath;

        var ctx = new FakeModuleContext();
        var service = new CSlimService(new FakeRegistry(), knownFolders);
        await service.InitializeAsync(ctx, CancellationToken.None);

        await service.ApplySaveLocationSuggestionsAsync(
            new[] { SaveLocationCategory.NewDocuments },
            targetDrive,
            null,
            CancellationToken.None);

        var plan = Assert.Single(ctx.Queue.Plans);
        Assert.Equal("FR-CSLIM-08", plan.FrId);
        var action = Assert.Single(plan.Actions);
        Assert.Equal(ActionKind.RedirectKnownFolder, action.Kind);
        Assert.Equal(DocumentsFolderId.ToString(), action.Key);
        Assert.Equal(currentPath, action.OriginalValue);
        Assert.Equal(Path.Combine(targetDrive, "Documents"), action.NewValue);
    }

    [Fact]
    public async Task ApplySaveLocation_OnlyUnsupportedCategories_Throws()
    {
        var service = new CSlimService(new FakeRegistry(), new FakeKnownFolder());
        await service.InitializeAsync(new FakeModuleContext(), CancellationToken.None);

        await Assert.ThrowsAsync<CslimItemsEmptyException>(
            () => service.ApplySaveLocationSuggestionsAsync(
                new[] { SaveLocationCategory.NewApps },
                "D:\\",
                null,
                CancellationToken.None));
    }
}
