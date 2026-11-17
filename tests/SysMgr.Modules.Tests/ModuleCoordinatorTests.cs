using System.Runtime.CompilerServices;
using SysMgr.Core.Queue;
using SysMgr.Core.Safety;
using SysMgr.Modules.Tests.Fakes;
using SysMgr.Modules;
using SysMgr.Modules.Migration;
using SysMgr.Modules.Residual;
using Xunit;

namespace SysMgr.Modules.Tests;

public class ModuleCoordinatorTests
{
    private static ResidualScanTarget SomeTarget()
        => new(Guid.NewGuid(), "Sut App", null, null);

    private static ResidualItem SomeItem(long? size)
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ResidualCategory.File,
            "C:\\sut",
            null,
            new SafetyVerdict(SafetyLevel.High, "r", Array.Empty<string>()),
            size,
            "kw",
            "d");

    [Fact]
    public async Task Trigger_TargetRegistered_ScansAndBroadcastsSummary()
    {
        var trust = new StubTrust();
        var coordinator = new ModuleCoordinator(trust);
        var migration = new FakeMigrationModule();
        var residual = new FakeResidualModule
        {
            ItemsToReturn = { SomeItem(100), SomeItem(250) }
        };
        coordinator.Attach(migration, residual);

        var opId = Guid.NewGuid();
        var target = SomeTarget();
        coordinator.RegisterUninstallTarget(opId, target);

        await coordinator.TriggerResidualScanAsync(opId);

        Assert.Equal(target, residual.LastTarget);
        var broadcast = Assert.Single(trust.Broadcasts);
        Assert.Equal("ResidualScanCompleted", broadcast.Kind);
        Assert.Contains("2 项", broadcast.NewValue);
        Assert.Contains("350", broadcast.NewValue);
    }

    [Fact]
    public async Task Trigger_UnknownOpId_NoOpAndNoBroadcast()
    {
        var trust = new StubTrust();
        var residual = new FakeResidualModule();
        var coordinator = new ModuleCoordinator(trust);
        coordinator.Attach(new FakeMigrationModule(), residual);

        await coordinator.TriggerResidualScanAsync(Guid.NewGuid());

        Assert.Null(residual.LastTarget);
        Assert.Empty(trust.Broadcasts);
    }

    [Fact]
    public async Task RequestMigration_RoutedToMigrationService()
    {
        var coordinator = new ModuleCoordinator(new StubTrust());
        var migration = new FakeMigrationModule();
        coordinator.Attach(migration, new FakeResidualModule());

        var request = new MigrationRequest(
            Guid.NewGuid(),
            MigrationKind.File,
            new[] { "C:\\a.txt" },
            "D:\\",
            false,
            true);

        await coordinator.RequestMigrationAsync(request, null, CancellationToken.None);

        Assert.Equal(request, migration.LastRequest);
    }

    [Fact]
    public async Task Trigger_BeforeAttach_Throws()
    {
        var coordinator = new ModuleCoordinator(new StubTrust());
        coordinator.RegisterUninstallTarget(Guid.NewGuid(), SomeTarget());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.TriggerResidualScanAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Request_BeforeAttach_Throws()
    {
        var coordinator = new ModuleCoordinator(new StubTrust());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.RequestMigrationAsync(
                new MigrationRequest(
                    Guid.NewGuid(), MigrationKind.File,
                    new[] { "C:\\a.txt" }, "D:\\", false, true),
                null,
                CancellationToken.None));
    }

    [Fact]
    public async Task TimelineAndAiHooks_CompleteWithoutThrowing()
    {
        var coordinator = new ModuleCoordinator(new StubTrust());
        await coordinator.TriggerTimelineSnapshotAsync();
        await coordinator.TriggerAiDiagnosisAsync("hello");
    }

    private sealed class FakeResidualModule : IResidualService
    {
        public ResidualScanTarget? LastTarget { get; private set; }
        public List<ResidualItem> ItemsToReturn { get; } = new();

        public string ModuleId => "residual";
        public string DisplayName => "残留清除";
        public ModuleVersion Version => new(1, 0, "V1");

        public Task InitializeAsync(IModuleContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task<ModuleHealthReport> HealthCheckAsync(CancellationToken ct)
            => Task.FromResult(new ModuleHealthReport(
                ModuleId,
                ModuleHealthStatus.Ok,
                null,
                new Dictionary<string, string>()));

        public async IAsyncEnumerable<ResidualItem> ScanResidualAsync(
            ResidualScanTarget target,
            ResidualScanOptions options,
            IProgress<ScanProgress>? progress,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            LastTarget = target;
            foreach (var item in ItemsToReturn)
            {
                yield return item;
            }
        }

        public Task<OperationResult> EnqueueCleanupAsync(
            IReadOnlyList<ResidualItem> items,
            ResidualCleanupOptions options,
            IProgress<OperationProgress>? progress,
            CancellationToken ct)
            => Task.FromResult(new OperationResult(
                Guid.Empty, true, TimeSpan.Zero, null, null));
    }

    private sealed class FakeMigrationModule : IMigrationService
    {
        public MigrationRequest? LastRequest { get; private set; }

        public string ModuleId => "migration";
        public string DisplayName => "文件迁移";
        public ModuleVersion Version => new(1, 0, "V1");

        public Task InitializeAsync(IModuleContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task<ModuleHealthReport> HealthCheckAsync(CancellationToken ct)
            => Task.FromResult(new ModuleHealthReport(
                ModuleId,
                ModuleHealthStatus.Ok,
                null,
                new Dictionary<string, string>()));

        public Task<MigrationValidation> ValidateAsync(
            MigrationRequest request,
            IProgress<ScanProgress>? progress,
            CancellationToken ct)
            => Task.FromResult(new MigrationValidation(
                true, Array.Empty<string>(), Array.Empty<string>(), 0, 0));

        public Task<OperationResult> EnqueueMigrationAsync(
            MigrationRequest request,
            IProgress<OperationProgress>? progress,
            CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new OperationResult(
                request.RequestId, true, TimeSpan.Zero, null, null));
        }
    }
}
