using System.Text.Json;
using SysMgr.Core.Rules;

namespace SysMgr.Infra;

/// <summary>
/// 规则集管理器实现（IRuleSetManager，TDD §2.5 V1 子集）。
/// 从 rules 目录按约定文件名加载 JSON 规则；<see cref="FileSystemWatcher"/> 监听文件变更，
/// 文件头版本变化时触发 <see cref="RuleSetChanged"/>。
/// V1 不接入远端规则通道，<see cref="CheckUpdateAsync"/> 返回本地当前版本。
/// </summary>
public sealed class JsonRuleSetManager : IRuleSetManager, IDisposable
{
    private static readonly Dictionary<RuleSetKind, string> FileNames =
        new()
        {
            [RuleSetKind.SafetyCriticalPaths] = "safety_critical_paths.json",
            [RuleSetKind.CleanupTargets] = "cleanup_targets.json",
            [RuleSetKind.UninstallVendors] = "uninstall_vendors.json",
            [RuleSetKind.PopupRules] = "popup_rules.json"
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directory;
    private readonly FileSystemWatcher _watcher;
    private readonly object _gate = new();

    /// <summary>初始化规则集管理器：扫描现有文件版本并开始监听热更新。</summary>
    public JsonRuleSetManager(string rulesDirectory)
    {
        _directory = rulesDirectory ?? throw new ArgumentNullException(nameof(rulesDirectory));
        Directory.CreateDirectory(_directory);

        foreach (var kind in FileNames.Keys)
        {
            var version = ReadVersion(kind);
            if (version is not null)
            {
                Remember(kind, version);
            }
        }

        _watcher = new FileSystemWatcher(_directory, "*.json")
        {
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileChanged;
    }

    /// <inheritdoc />
    public async Task<TRuleSet> LoadAsync<TRuleSet>(CancellationToken ct)
        where TRuleSet : IRuleSet
    {
        var kind = ResolveKind<TRuleSet>();
        if (!FileNames.TryGetValue(kind, out var fileName))
        {
            throw new ArgumentException(
                $"规则集类别 {kind} 在 V1 无对应文件", nameof(TRuleSet));
        }

        var path = Path.Combine(_directory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"规则文件不存在：{path}", path);
        }

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var result = await JsonSerializer.DeserializeAsync<TRuleSet>(
            stream, JsonOptions, ct);
        if (result is null)
        {
            throw new InvalidDataException($"规则文件内容无法反序列化：{path}");
        }
        return result;
    }

    /// <inheritdoc />
    public Task<RuleSetVersion> CheckUpdateAsync(RuleSetKind kind, CancellationToken ct)
    {
        var current = ReadVersion(kind) ?? new RuleSetVersion(0, 0, 0, DateTimeOffset.Now);
        return Task.FromResult(current);
    }

    /// <inheritdoc />
    public event EventHandler<RuleSetChangedEventArgs>? RuleSetChanged;

    /// <summary>按类型名约定解析规则类别：枚举名是类型名前缀即命中（如 SafetyCriticalPathsRuleSet）。</summary>
    private static RuleSetKind ResolveKind<TRuleSet>()
    {
        var typeName = typeof(TRuleSet).Name;
        foreach (RuleSetKind kind in Enum.GetValues<RuleSetKind>())
        {
            if (typeName.StartsWith(kind.ToString(), StringComparison.Ordinal))
            {
                return kind;
            }
        }
        throw new ArgumentException(
            $"类型 {typeName} 不匹配任何已知规则集类别命名约定", nameof(TRuleSet));
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        RuleSetChangedEventArgs? args = null;
        lock (_gate)
        {
            var match = FileNames.FirstOrDefault(
                kv => kv.Value.Equals(Path.GetFileName(e.Name), StringComparison.OrdinalIgnoreCase));
            if (e.Name is null || match.Value is null)
            {
                return;
            }

            var kind = match.Key;
            var newVersion = ReadVersion(kind);
            if (newVersion is null)
            {
                return;
            }

            var oldVersion = KnownVersion(kind) ?? new RuleSetVersion(0, 0, 0, DateTimeOffset.MinValue);
            if (newVersion == oldVersion)
            {
                return;
            }

            Remember(kind, newVersion);
            args = new RuleSetChangedEventArgs(kind, oldVersion, newVersion);
        }

        RuleSetChanged?.Invoke(this, args);
    }

    private RuleSetVersion? ReadVersion(RuleSetKind kind)
    {
        if (!FileNames.TryGetValue(kind, out var fileName))
        {
            return null;
        }

        var path = Path.Combine(_directory, fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            int major = 0;
            if (root.TryGetProperty("schemaVersion", out var schema)
                && schema.ValueKind == JsonValueKind.Number)
            {
                major = schema.GetInt32();
            }
            else if (root.TryGetProperty("version", out var version))
            {
                switch (version.ValueKind)
                {
                    case JsonValueKind.Number:
                        major = version.GetInt32();
                        break;
                    case JsonValueKind.Object:
                        major = version.TryGetProperty("major", out var m) ? m.GetInt32() : 0;
                        break;
                }
            }

            var publishedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
            return new RuleSetVersion(major, 0, 0, publishedAt);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private RuleSetVersion? KnownVersion(RuleSetKind kind)
    {
        lock (_gate)
        {
            return Versions.TryGetValue(kind, out var version) ? version : null;
        }
    }

    private void Remember(RuleSetKind kind, RuleSetVersion version)
    {
        lock (_gate)
        {
            Versions[kind] = version;
        }
    }

    private Dictionary<RuleSetKind, RuleSetVersion> Versions { get; } =
        new();

    /// <inheritdoc />
    public void Dispose() => _watcher.Dispose();
}
