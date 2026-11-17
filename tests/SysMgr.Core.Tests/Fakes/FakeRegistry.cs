using SysMgr.Core.SystemAccess;

namespace SysMgr.Core.Tests.Fakes;

/// <summary>
/// 内存版注册表（测试用 IRegistry 假实现）。
/// 以规范键路径组织：键 = 完整路径，节点持有值字典（键="" 表示默认值）。
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
            return [];
        }
        return node.Values
            .Select(kv => new RegistryValueInfo(kv.Key, kv.Value))
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

    private void EnsureNode(string path)
    {
        _nodes.TryAdd(path, new Node());
    }

    private static string Normalize(string keyPath) =>
        keyPath.Trim().Replace('/', '\\').TrimEnd('\\');
}
