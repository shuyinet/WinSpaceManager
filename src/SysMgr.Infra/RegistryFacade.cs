using SysMgr.Core.SystemAccess;
using Win32Registry = Microsoft.Win32.Registry;
using Win32RegistryKey = Microsoft.Win32.RegistryKey;

namespace SysMgr.Infra;

/// <summary>
/// 注册表访问实现（IRegistry 端口，Microsoft.Win32.Registry）。
/// </summary>
public sealed class RegistryFacade : IRegistry
{
    /// <inheritdoc />
    public bool KeyExists(string keyPath)
    {
        try
        {
            using var key = OpenKey(keyPath, writable: false);
            return key is not null;
        }
        catch (UnauthorizedAccessException)
        {
            // 无权限读不等于不存在（如其他用户键）；按可见性保守返回 false
            return false;
        }
    }

    /// <inheritdoc />
    public string? GetValue(string keyPath, string? valueName)
    {
        // IRegistry 契约：键/值不存在返回 null（扫描期间键可能被并发删除，不应中断调用方）
        using var key = OpenKey(keyPath, writable: false);
        return key?.GetValue(valueName ?? string.Empty)?.ToString();
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> EnumerateSubKeyNames(string keyPath)
    {
        using var key = OpenKey(keyPath, writable: false);
        return key?.GetSubKeyNames() ?? [];
    }

    /// <inheritdoc />
    public IReadOnlyCollection<RegistryValueInfo> EnumerateValues(string keyPath)
    {
        using var key = OpenKey(keyPath, writable: false);
        if (key is null)
        {
            return [];
        }

        var result = new List<RegistryValueInfo>();
        foreach (var name in key.GetValueNames())
        {
            result.Add(new RegistryValueInfo(name, key.GetValue(name)?.ToString()));
        }
        return result;
    }

    /// <inheritdoc />
    public void CreateKey(string keyPath)
    {
        var (root, subPath) = Split(keyPath);
        using var baseKey = OpenRoot(root, writable: true);
        using var created = string.IsNullOrEmpty(subPath)
            ? null
            : baseKey.CreateSubKey(subPath);
    }

    /// <inheritdoc />
    public void SetValue(string keyPath, string valueName, string value)
    {
        var (root, subPath) = Split(keyPath);
        using var baseKey = OpenRoot(root, writable: true);
        using var key = string.IsNullOrEmpty(subPath)
            ? baseKey
            : baseKey.CreateSubKey(subPath);
        key.SetValue(valueName, value);
    }

    /// <inheritdoc />
    public byte[]? GetBinaryValue(string keyPath, string valueName)
    {
        using var key = OpenKey(keyPath, writable: false);
        return key?.GetValue(valueName) as byte[];
    }

    /// <inheritdoc />
    public void SetBinaryValue(string keyPath, string valueName, byte[] value)
    {
        var (root, subPath) = Split(keyPath);
        using var baseKey = OpenRoot(root, writable: true);
        using var key = string.IsNullOrEmpty(subPath)
            ? baseKey
            : baseKey.CreateSubKey(subPath);
        key.SetValue(valueName, value, Microsoft.Win32.RegistryValueKind.Binary);
    }

    /// <inheritdoc />
    public void DeleteValue(string keyPath, string valueName)
    {
        using var key = OpenKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    /// <inheritdoc />
    public void DeleteKeyTree(string keyPath)
    {
        var (rootToken, subPath) = Split(keyPath);
        using var baseKey = OpenRoot(rootToken, writable: true);
        if (string.IsNullOrEmpty(subPath))
        {
            return; // 不允许删除整根
        }
        baseKey.DeleteSubKeyTree(subPath, throwOnMissingSubKey: false);
    }

    private static Win32RegistryKey? OpenKey(string keyPath, bool writable)
    {
        var (rootToken, subPath) = Split(keyPath);
        var baseKey = OpenRoot(rootToken, writable);
        return string.IsNullOrEmpty(subPath)
            ? baseKey
            : baseKey.OpenSubKey(subPath, writable);
    }

    private static Win32RegistryKey OpenRoot(string rootToken, bool writable) => rootToken switch
    {
        // V1 仅 x64：LocalMachine 默认即 64 位视图；WOW6432Node 通过路径显式访问
        "HKEY_LOCAL_MACHINE" => Win32Registry.LocalMachine,
        "HKEY_CURRENT_USER" => Win32Registry.CurrentUser,
        "HKEY_CLASSES_ROOT" => Win32Registry.ClassesRoot,
        "HKEY_USERS" => Win32Registry.Users,
        "HKEY_CURRENT_CONFIG" => Win32Registry.CurrentConfig,
        _ => throw new ArgumentException($"不支持的注册表根：{rootToken}")
    };

    private static (string RootToken, string SubPath) Split(string keyPath)
    {
        var normalized = keyPath.Trim().Replace('/', '\\').TrimEnd('\\');
        var slash = normalized.IndexOf('\\');
        return slash < 0
            ? (normalized, string.Empty)
            : (normalized[..slash], normalized[(slash + 1)..]);
    }
}
