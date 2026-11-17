using System.IO;
using System.Text.Json;
using SysMgr.Core.Config;
using SysMgr.Core.Logging;
using SysMgr.Core.Network;
using SysMgr.Core.Operations;
using SysMgr.Core.Queue;
using SysMgr.Core.Rollback;
using SysMgr.Core.Rules;
using SysMgr.Core.Safety;
using SysMgr.Core.Trust;
using SysMgr.Infra;
using SysMgr.Modules;
using SysMgr.Modules.CSlim;
using SysMgr.Modules.Cleanup;
using SysMgr.Modules.Migration;
using SysMgr.Modules.Residual;
using SysMgr.Modules.Shred;
using SysMgr.Modules.Startup;
using SysMgr.Modules.Uninstall;
using SysMgr.Modules.Visualization;

namespace SysMgr.App;

/// <summary>组合根产物：V1 五个模块服务、V1.5 可视化模块与共享横切句柄。</summary>
public sealed record SysMgrServices(
    IUninstallService Uninstall,
    IResidualService Residual,
    ICSlimService CSlim,
    ICleanupService Cleanup,
    IMigrationService Migration,
    IModuleCoordinator Coordinator,
    IOperationLogger Logger,
    IRollbackEngine Rollback,
    ITrustBroadcaster Trust,
    IConfigStore Config,
    IVisualizationService Visualization,
    IStartupService Startup,
    IShredService Shred);

/// <summary>
/// 组合根（TDD §3.1 L1 启动初始化）。
/// 实例化 Infra 适配器、Core 横切实现与五个模块服务，InitializeAsync 后
/// 经 <see cref="Modules.ModuleCoordinator.Attach"/> 打破构造环。
/// </summary>
public static class CompositionRoot
{
    /// <summary>构造并装配全部 V1 服务。</summary>
    public static SysMgrServices Build()
    {
        var baseDir = AppContext.BaseDirectory;
        var rulesDir = Path.Combine(baseDir, "rules");
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "sysmgr");
        Directory.CreateDirectory(dataDir);

        // ---- Infra 端口适配器 ----
        var registry = new RegistryFacade();
        var processes = new ProcessQueryFacade();
        var serviceQuery = new ServiceQueryFacade();
        var taskQuery = new TaskQueryFacade();
        var recycleBin = new RecycleBinFacade();
        var links = new LinkManager();
        var knownFolders = new KnownFolderFacade();

        // ---- Core 横切实现 ----
        var logger = new OperationLogger(Path.Combine(dataDir, "history.jsonl"));
        var config = new JsonConfigStore(Path.Combine(dataDir, "config.json"));
        var safety = new SafetyClassifier(LoadCriticalPrefixes(rulesDir));
        var rollback = new FileRollbackEngine(
            Path.Combine(dataDir, "rollback"), registry, knownFolders, taskQuery);
        var executor = new WindowsOperationExecutor(
            registry, serviceQuery, taskQuery, recycleBin, links, processes, knownFolders);
        var queue = new DestructiveOpQueue(rollback, executor, logger);
        var rules = new JsonRuleSetManager(rulesDir);
        var network = new ManagedNetworkWhitelist();
        var trust = new TrustBroadcaster(
            logger,
            new PreviewPanelPrompt(),
            new FirewallFacade(),
            new HttpConnectivityProbe(),
            // Windows NCSI 标准连通性探测端点，用于 FR-TRUST-03 自我断网验证
            new Uri("http://www.msftconnecttest.com/connecttest.txt"));

        var coordinator = new ModuleCoordinator(trust);
        IModuleContext context = new ModuleContext(
            rollback, queue, safety, network, rules, logger, trust, config, coordinator);

        // ---- 五个模块服务 ----
        var uninstall = new UninstallService(
            registry, processes, serviceQuery, taskQuery);
        var residual = new ResidualService(
            registry, serviceQuery, taskQuery);
        var cslim = new CSlimService(registry, knownFolders);
        var cleanup = new CleanupService(recycleBin, processes);
        var migration = new MigrationService();
        var visualization = new VisualizationService();
        var startup = new StartupService(registry, taskQuery);
        var shred = new ShredService();

        uninstall.InitializeAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        residual.InitializeAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        cslim.InitializeAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        cleanup.InitializeAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        migration.InitializeAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        visualization.InitializeAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        startup.InitializeAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        shred.InitializeAsync(context, CancellationToken.None).GetAwaiter().GetResult();

        coordinator.Attach(migration, residual);

        return new SysMgrServices(
            uninstall, residual, cslim, cleanup, migration, coordinator,
            logger, rollback, trust, config, visualization, startup, shred);
    }

    /// <summary>
    /// 从 rules\safety_critical_paths.json 读取关键路径前缀，
    /// 并把规则基准盘/系统目录改写为本机实际位置。
    /// </summary>
    private static IReadOnlyList<string> LoadCriticalPrefixes(string rulesDir)
    {
        var path = Path.Combine(rulesDir, "safety_critical_paths.json");
        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("criticalPaths", out var array))
            {
                return Array.Empty<string>();
            }

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var systemDriveRoot = Path.GetPathRoot(windows);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            var result = new List<string>();
            foreach (var element in array.EnumerateArray())
            {
                var value = element.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                value = value.Replace(@"C:\Windows", windows, StringComparison.OrdinalIgnoreCase);
                value = value.Replace(@"C:\Program Files (x86)", programFilesX86, StringComparison.OrdinalIgnoreCase);
                value = value.Replace(@"C:\Program Files", programFiles, StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(systemDriveRoot) && !systemDriveRoot.Equals(@"C:\", StringComparison.OrdinalIgnoreCase))
                {
                    value = value.Replace(@"C:\", systemDriveRoot, StringComparison.OrdinalIgnoreCase);
                }

                result.Add(value);
            }
            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>模块上下文生产实现（组合根内部装配用）。</summary>
    private sealed record ModuleContext(
        IRollbackEngine Rollback,
        IDestructiveOpQueue OpQueue,
        ISafetyClassifier Safety,
        INetworkWhitelist Network,
        IRuleSetManager Rules,
        IOperationLogger Logger,
        ITrustBroadcaster Trust,
        IConfigStore Config,
        IModuleCoordinator Coordinator) : IModuleContext;
}
