namespace SysMgr.Core.SystemAccess;

/// <summary>
/// 注册表访问端口（L4 抽象，TDD §6.3 RegistryFs）。实现位于 SysMgr.Infra（Microsoft.Win32.Registry）。
/// 键路径采用规范全名，根标记见 <see cref="RegistryRoots"/>，例如
/// "HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"。
/// 所有路径比较不区分大小写（注册表原生语义）。
/// </summary>
public interface IRegistry
{
    /// <summary>键是否存在。</summary>
    bool KeyExists(string keyPath);

    /// <summary>读取值；valueName=null 或空串表示键默认值；不存在返回 null。</summary>
    string? GetValue(string keyPath, string? valueName);

    /// <summary>枚举直接子键的键名（不含完整路径）。</summary>
    IReadOnlyCollection<string> EnumerateSubKeyNames(string keyPath);

    /// <summary>枚举键下所有值（名称 + 字符串数据；非字符串数据由实现尽力转为可读字符串）。</summary>
    IReadOnlyCollection<RegistryValueInfo> EnumerateValues(string keyPath);

    /// <summary>创建键（已存在则无操作）及全部父键。</summary>
    void CreateKey(string keyPath);

    /// <summary>写入字符串值（值不存在则创建）。</summary>
    void SetValue(string keyPath, string valueName, string value);

    /// <summary>读取 REG_BINARY 值；键/值不存在或类型不符返回 null（FR-STARTUP-01 StartupApproved 状态）。</summary>
    byte[]? GetBinaryValue(string keyPath, string valueName);

    /// <summary>写入 REG_BINARY 值（值不存在则创建，键不存在则创建键）。</summary>
    void SetBinaryValue(string keyPath, string valueName, byte[] value);

    /// <summary>删除单个值；值不存在视为成功。</summary>
    void DeleteValue(string keyPath, string valueName);

    /// <summary>递归删除整个键树；键不存在视为成功。</summary>
    void DeleteKeyTree(string keyPath);
}

/// <summary>注册表值信息（枚举结果项）。</summary>
public sealed record RegistryValueInfo(string Name, string? Data);

/// <summary>注册表根路径标记常量（构造键路径用）。</summary>
public static class RegistryRoots
{
    public const string Hklm = @"HKEY_LOCAL_MACHINE";
    public const string Hkcu = @"HKEY_CURRENT_USER";
    public const string Hkcr = @"HKEY_CLASSES_ROOT";

    /// <summary>64 位软件卸载键（HKLM）。</summary>
    public const string UninstallHklm =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>32 位软件卸载键（WOW6432Node，HKLM）。</summary>
    public const string UninstallHklmWow =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>当前用户卸载键（HKCU）。</summary>
    public const string UninstallHkcu =
        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>App Paths 键（HKLM）。</summary>
    public const string AppPaths =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

    /// <summary>当前用户 Run 启动项键。</summary>
    public const string RunHkcu =
        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>本机 Run 启动项键。</summary>
    public const string RunHklm =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>32 位软件 Run 启动项键（WOW6432Node，HKLM）。</summary>
    public const string RunHklmWow =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
}
