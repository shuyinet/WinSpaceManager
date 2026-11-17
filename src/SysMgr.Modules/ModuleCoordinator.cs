using System.Collections.Concurrent;
using SysMgr.Core.Logging;
using SysMgr.Core.Queue;
using SysMgr.Core.Trust;
using SysMgr.Modules.Migration;
using SysMgr.Modules.Residual;

namespace SysMgr.Modules;

/// <summary>
/// 模块协调器实现（IModuleCoordinator，TDD §3.3）。
/// 模块间不直接 new：迁移路由到 Migration，卸载联动路由到 Residual。
/// Migration/Residual 采用创建后附加（Attach）打破"服务 ↔ 上下文"构造环。
/// </summary>
public sealed class ModuleCoordinator : IModuleCoordinator
{
    private readonly ITrustBroadcaster _trust;
    private readonly ConcurrentDictionary<Guid, ResidualScanTarget> _targets = new();

    private IMigrationService? _migration;
    private IResidualService? _residual;

    public ModuleCoordinator(ITrustBroadcaster trust)
    {
        _trust = trust;
    }

    /// <summary>附加模块引用（组合根在服务构造完成后调用）。</summary>
    public void Attach(IMigrationService migration, IResidualService residual)
    {
        _migration = migration;
        _residual = residual;
    }

    /// <inheritdoc />
    public void RegisterUninstallTarget(Guid uninstallOpId, ResidualScanTarget target)
        => _targets[uninstallOpId] = target;

    /// <inheritdoc />
    public Task<OperationResult> RequestMigrationAsync(
        MigrationRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var migration = _migration
            ?? throw new InvalidOperationException("Migration 模块尚未附加");
        return migration.EnqueueMigrationAsync(request, progress, ct);
    }

    /// <inheritdoc />
    public async Task TriggerResidualScanAsync(Guid uninstallOpId)
    {
        var residual = _residual
            ?? throw new InvalidOperationException("Residual 模块尚未附加");

        if (!_targets.TryRemove(uninstallOpId, out var target))
        {
            return;
        }

        var items = new List<ResidualItem>();
        await foreach (var item in residual.ScanResidualAsync(
                           target,
                           new ResidualScanOptions(Categories: null, AdditionalKeywords: null),
                           progress: null,
                           CancellationToken.None))
        {
            items.Add(item);
        }

        var sizeBytes = items.Sum(i => i.EstimatedSizeBytes ?? 0);
        _trust.Broadcast(new OperationLogEntry(
            uninstallOpId,
            DateTimeOffset.Now,
            "residual",
            "FR-RESIDUAL-03",
            "ResidualScanCompleted",
            Target: null,
            OriginalValue: null,
            NewValue: $"残留 {items.Count} 项，预计释放 {sizeBytes} 字节",
            Status: "Success",
            DurationMs: 0,
            Error: null));
    }

    /// <inheritdoc />
    public Task TriggerTimelineSnapshotAsync()
        // FR-TM-01 V2 落地
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task TriggerAiDiagnosisAsync(string query)
        // FR-AI-01 V3 落地
        => Task.CompletedTask;
}
