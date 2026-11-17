using System.Text.Json;
using System.Text.Json.Nodes;

namespace SysMgr.Core.Config;

/// <summary>
/// 配置中心实现（TDD §4.1 config.json）。
/// 全部配置节持久化在单个 config.json（键 = sectionKey）；原子写入（tmp + Move 覆盖）。
/// 懒加载 + 进程内信号量串行化；便携版配置文件由组合层指向程序目录即可保持零残留。
/// </summary>
public sealed class JsonConfigStore : IConfigStore
{
    private readonly string _configFilePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private JsonObject? _root;

    public JsonConfigStore(string configFilePath)
    {
        _configFilePath = configFilePath ?? throw new ArgumentNullException(nameof(configFilePath));
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string sectionKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sectionKey);
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedCoreAsync(ct);
            if (_root![sectionKey] is not JsonNode node)
            {
                return default;
            }
            return node.Deserialize<T>();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task UpdateAsync<T>(string sectionKey, T value, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sectionKey);
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedCoreAsync(ct);
            _root![sectionKey] = JsonSerializer.SerializeToNode(value);
            await SaveAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ResetAsync(string sectionKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sectionKey);
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedCoreAsync(ct);
            _root!.Remove(sectionKey);
            await SaveAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedCoreAsync(CancellationToken ct)
    {
        if (_root is not null)
        {
            return;
        }
        ct.ThrowIfCancellationRequested();
        if (File.Exists(_configFilePath))
        {
            var text = await File.ReadAllTextAsync(_configFilePath, ct);
            _root = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        }
        else
        {
            _root = new JsonObject();
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configFilePath)!);
        var tmpPath = _configFilePath + ".tmp";
        var text = _root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(tmpPath, text, ct);
        File.Move(tmpPath, _configFilePath, overwrite: true);
    }
}
