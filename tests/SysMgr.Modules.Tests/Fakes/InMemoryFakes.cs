using System.Net.Http;
using SysMgr.Core.Config;
using SysMgr.Core.Logging;
using SysMgr.Core.Network;
using SysMgr.Core.Operations;
using SysMgr.Core.Queue;
using SysMgr.Core.Rollback;
using SysMgr.Core.Rules;
using SysMgr.Core.Safety;
using SysMgr.Core.SystemAccess;
using SysMgr.Core.Trust;
using SysMgr.Modules.Migration;
using SysMgr.Modules.Residual;

namespace SysMgr.Modules.Tests.Fakes;

/// <summary>
/// 内存模块上下文：各横切句柄均为可观察的假实现，测试可直接取属性断言。
/// </summary>
public sealed class FakeModuleContext : IModuleContext
{
    public IRollbackEngine Rollback { get; set; } = new StubRollback();
    public IDestructiveOpQueue OpQueue { get; set; } = new StubOpQueue();
    public ISafetyClassifier Safety { get; set; } = new StubSafetyClassifier();
    public INetworkWhitelist Network { get; set; } = new StubNetwork();
    public IRuleSetManager Rules { get; set; } = new StubRules();
    public IOperationLogger Logger { get; set; } = new StubLogger();
    public ITrustBroadcaster Trust { get; set; } = new StubTrust();
    public IConfigStore Config { get; set; } = new StubConfig();
    public IModuleCoordinator Coordinator { get; set; } = new RecordingCoordinator();

    public StubOpQueue Queue => (StubOpQueue)OpQueue;
    public StubTrust TrustStub => (StubTrust)Trust;
    public StubSafetyClassifier SafetyStub => (StubSafetyClassifier)Safety;
    public RecordingCoordinator Recorder => (RecordingCoordinator)Coordinator;
}

/// <summary>固定等级安全分级器（记录全部入参）。</summary>
public sealed class StubSafetyClassifier : ISafetyClassifier
{
    public SafetyLevel Level { get; set; } = SafetyLevel.High;
    public List<ScannedItem> Seen { get; } = new();

    public SafetyVerdict Classify(ScannedItem item)
    {
        Seen.Add(item);
        return new SafetyVerdict(Level, "测试打级", new[] { item.Path });
    }
}

/// <summary>记录入队计划的假队列。</summary>
public sealed class StubOpQueue : IDestructiveOpQueue
{
    public List<OperationPlan> Plans { get; } = new();
    public bool Success { get; set; } = true;

    public Task<OperationResult> EnqueueAsync(
        OperationPlan plan,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        Plans.Add(plan);
        return Task.FromResult(new OperationResult(
            plan.PlanId,
            Success,
            TimeSpan.FromMilliseconds(1),
            Success ? null : "测试失败",
            null));
    }
}

/// <summary>记录广播、预演结果可配的假 TRUST 入口。</summary>
public sealed class StubTrust : ITrustBroadcaster
{
    public List<OperationLogEntry> Broadcasts { get; } = new();
    public bool Confirmed { get; set; } = true;

    public void Broadcast(OperationLogEntry entry) => Broadcasts.Add(entry);

    public Task<PreviewResult> PreviewAsync(OperationPlan plan, CancellationToken ct)
        => Task.FromResult(new PreviewResult(Confirmed, Array.Empty<string>(), null));

    public Task<bool> SelfTestDisconnectAsync(CancellationToken ct) => Task.FromResult(true);
}

/// <summary>假回滚引擎。</summary>
public sealed class StubRollback : IRollbackEngine
{
    public Task<SnapshotHandle> CreateSnapshotAsync(OperationPlan plan, CancellationToken ct)
        => Task.FromResult(new SnapshotHandle(plan.PlanId, "", RollbackStrategy.Copy));

    public Task CommitAsync(SnapshotHandle handle, CancellationToken ct) => Task.CompletedTask;

    public Task<RollbackResult> RollbackAsync(Guid opId, CancellationToken ct)
        => Task.FromResult(new RollbackResult(true, null, Array.Empty<string>()));

    public async IAsyncEnumerable<RollbackEntry> EnumerateHistoryAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>假联网白名单。</summary>
public sealed class StubNetwork : INetworkWhitelist
{
    public Task<HttpResponseMessage> RequestAsync(
        WhitelistScope scope, Uri endpoint, HttpRequestMessage req, CancellationToken ct)
        => throw new UnauthorizedNetworkAccessException(scope, endpoint);

    public bool IsAllowed(WhitelistScope scope) => true;
}

/// <summary>假规则集管理器。</summary>
public sealed class StubRules : IRuleSetManager
{
    public Task<TRuleSet> LoadAsync<TRuleSet>(CancellationToken ct)
        where TRuleSet : IRuleSet
        => throw new NotSupportedException("测试不加载规则集");

    public Task<RuleSetVersion> CheckUpdateAsync(RuleSetKind kind, CancellationToken ct)
        => Task.FromResult(new RuleSetVersion(0, 0, 0, DateTimeOffset.Now));

    public event EventHandler<RuleSetChangedEventArgs>? RuleSetChanged
    {
        add
        {
        }
        remove
        {
        }
    }
}

/// <summary>假操作日志。</summary>
public sealed class StubLogger : IOperationLogger
{
    public List<OperationLogEntry> Entries { get; } = new();

    public void LogOperation(OperationLogEntry entry) => Entries.Add(entry);

    public IObservable<OperationLogEntry> LiveStream { get; } =
        new NeverObservable<OperationLogEntry>();
}

internal sealed class NeverObservable<T> : IObservable<T>
{
    public IDisposable Subscribe(IObserver<T> observer) => new NullDisposable();

    private sealed class NullDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

/// <summary>假配置中心。</summary>
public sealed class StubConfig : IConfigStore
{
    public Dictionary<string, object> Values { get; } = new();

    public Task<T?> GetAsync<T>(string sectionKey, CancellationToken ct)
        => Task.FromResult(Values.TryGetValue(sectionKey, out var v) ? (T)v : default);

    public Task UpdateAsync<T>(string sectionKey, T value, CancellationToken ct)
    {
        Values[sectionKey] = value!;
        return Task.CompletedTask;
    }

    public Task ResetAsync(string sectionKey, CancellationToken ct)
    {
        Values.Remove(sectionKey);
        return Task.CompletedTask;
    }
}

/// <summary>
/// 记录全部跨模块调用的协调器（供 Uninstall 等服务测试使用）。
/// </summary>
public sealed class RecordingCoordinator : IModuleCoordinator
{
    public Dictionary<Guid, ResidualScanTarget> Targets { get; } = new();
    public MigrationRequest? LastRequest { get; private set; }
    public bool ResidualTriggered { get; private set; }
    public Guid TriggeredOpId { get; private set; }
    public Func<Guid, Task>? ResidualHook { get; set; }

    public void RegisterUninstallTarget(Guid uninstallOpId, ResidualScanTarget target)
        => Targets[uninstallOpId] = target;

    public Task<OperationResult> RequestMigrationAsync(
        MigrationRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        LastRequest = request;
        return Task.FromResult(new OperationResult(request.RequestId, true, TimeSpan.Zero, null, null));
    }

    public async Task TriggerResidualScanAsync(Guid uninstallOpId)
    {
        ResidualTriggered = true;
        TriggeredOpId = uninstallOpId;
        if (ResidualHook is not null)
        {
            await ResidualHook(uninstallOpId);
        }
    }

    public Task TriggerTimelineSnapshotAsync() => Task.CompletedTask;

    public Task TriggerAiDiagnosisAsync(string query) => Task.CompletedTask;
}

/// <summary>内存进程查询。</summary>
public sealed class FakeProcessQuery : IProcessQuery
{
    public List<ProcessInfo> Processes { get; } = new();
    public List<(int Id, bool EntireTree)> Killed { get; } = new();

    public IReadOnlyCollection<ProcessInfo> Enumerate() => Processes;

    public void Kill(int processId, bool entireProcessTree)
    {
        Killed.Add((processId, entireProcessTree));
        Processes.RemoveAll(p => p.Id == processId);
    }
}

/// <summary>内存服务查询。</summary>
public sealed class FakeServiceQuery : IServiceQuery
{
    public List<ServiceInfo> Services { get; } = new();
    public List<string> Stopped { get; } = new();

    public IReadOnlyCollection<ServiceInfo> EnumerateServices() => Services;

    public void Stop(string serviceName) => Stopped.Add(serviceName);
}

/// <summary>内存计划任务查询。</summary>
public sealed class FakeTaskQuery : ITaskQuery
{
    public List<ScheduledTaskInfo> Tasks { get; } = new();
    public List<string> Deleted { get; } = new();
    public List<(string Path, bool Enabled)> EnabledChanges { get; } = new();

    public IReadOnlyCollection<ScheduledTaskInfo> Enumerate() => Tasks;

    public void DeleteTask(string taskPath) => Deleted.Add(taskPath);

    public void SetTaskEnabled(string taskPath, bool enabled)
    {
        EnabledChanges.Add((taskPath, enabled));
        for (var i = 0; i < Tasks.Count; i++)
        {
            if (Tasks[i].Path.Equals(taskPath, StringComparison.OrdinalIgnoreCase))
            {
                Tasks[i] = Tasks[i] with { Enabled = enabled };
            }
        }
    }
}

/// <summary>内存回收站。</summary>
public sealed class FakeRecycleBin : IRecycleBin
{
    public long Size { get; set; }
    public bool Emptied { get; private set; }

    public long GetTotalSizeBytes() => Size;

    public void Empty()
    {
        Emptied = true;
        Size = 0;
    }
}

/// <summary>内存 Known Folder。</summary>
public sealed class FakeKnownFolder : IKnownFolder
{
    public Dictionary<Guid, string> Paths { get; } = new();
    public List<(Guid FolderId, string Path)> Redirected { get; } = new();

    public string? GetPath(Guid folderId)
        => Paths.TryGetValue(folderId, out var p) ? p : null;

    public void SetPath(Guid folderId, string path)
    {
        Redirected.Add((folderId, path));
        Paths[folderId] = path;
    }
}

/// <summary>测试临时工作区。</summary>
public sealed class TempWorkspace : IDisposable
{
    public string Root { get; }

    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "sysmgr-mod-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string CreateFile(string relativePath, string content = "x")
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string CreateDirectory(string relativePath)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// 内存版注册表（测试用 IRegistry 假实现，与 Core.Tests 版本同源）。
/// </summary>
public sealed class FakeRegistry : IRegistry
{
    private sealed class Node
    {
        public Dictionary<string, string?> Values { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, byte[]> BinaryValues { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, Node> _nodes =
        new(StringComparer.OrdinalIgnoreCase);

    public bool KeyExists(string keyPath) => _nodes.ContainsKey(Normalize(keyPath));

    public string? GetValue(string keyPath, string? valueName)
    {
        if (!_nodes.TryGetValue(Normalize(keyPath), out var node))
        {
            return null;
        }
        return node.Values.GetValueOrDefault(valueName ?? string.Empty);
    }

    public IReadOnlyCollection<string> EnumerateSubKeyNames(string keyPath)
    {
        var prefix = Normalize(keyPath);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _nodes.Keys)
        {
            if (!path.StartsWith(prefix + @"\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var rest = path[(prefix.Length + 1)..];
            var slash = rest.IndexOf('\\');
            result.Add(slash >= 0 ? rest[..slash] : rest);
        }
        return result;
    }

    public IReadOnlyCollection<RegistryValueInfo> EnumerateValues(string keyPath)
    {
        if (!_nodes.TryGetValue(Normalize(keyPath), out var node))
        {
            return Array.Empty<RegistryValueInfo>();
        }
        // 与 RegistryFacade 口径一致：返回全部值名（含 REG_BINARY，数据另经 GetBinaryValue 读）
        return node.Values
            .Select(kv => new RegistryValueInfo(kv.Key, kv.Value))
            .Concat(node.BinaryValues.Keys
                .Where(k => !node.Values.ContainsKey(k))
                .Select(k => new RegistryValueInfo(k, "System.Byte[]")))
            .ToList();
    }

    public void CreateKey(string keyPath)
    {
        var path = Normalize(keyPath);
        var parts = path.Split('\\');
        var current = parts[0];
        EnsureNode(current);
        for (var i = 1; i < parts.Length; i++)
        {
            current = current + @"\" + parts[i];
            EnsureNode(current);
        }
    }

    public void SetValue(string keyPath, string valueName, string value)
    {
        CreateKey(keyPath);
        var node = _nodes[Normalize(keyPath)];
        node.Values[valueName] = value;
    }

    public byte[]? GetBinaryValue(string keyPath, string valueName)
    {
        if (!_nodes.TryGetValue(Normalize(keyPath), out var node))
        {
            return null;
        }
        return node.BinaryValues.GetValueOrDefault(valueName);
    }

    public void SetBinaryValue(string keyPath, string valueName, byte[] value)
    {
        CreateKey(keyPath);
        _nodes[Normalize(keyPath)].BinaryValues[valueName] = value;
    }

    public void DeleteValue(string keyPath, string valueName)
    {
        if (_nodes.TryGetValue(Normalize(keyPath), out var node))
        {
            node.Values.Remove(valueName);
        }
    }

    public void DeleteKeyTree(string keyPath)
    {
        var prefix = Normalize(keyPath);
        var doomed = _nodes.Keys
            .Where(p => p.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                        || p.StartsWith(prefix + @"\", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var path in doomed)
        {
            _nodes.Remove(path);
        }
    }

    private void EnsureNode(string path) => _nodes.TryAdd(path, new Node());

    private static string Normalize(string keyPath) =>
        keyPath.Trim().Replace('/', '\\').TrimEnd('\\');
}
