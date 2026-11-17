namespace SysMgr.Core.Config;

/// <summary>
/// 配置中心（横切层，TDD §3.1 IModuleContext.Config / §4.1 config.json）。
/// 原子读写（File.WriteAllText(tmp); File.Move(tmp, final)）。
/// 便携版配置随程序目录，不写 %LocalAppData%（保持零残留）。
/// 完整 schema 与实现策略见批次 2 docs/design/07-settings.md。
/// </summary>
public interface IConfigStore
{
    /// <summary>读取配置节，反序列化为 T；不存在返回 default。</summary>
    Task<T?> GetAsync<T>(string sectionKey, CancellationToken ct);

    /// <summary>写入配置节（原子写入，合并到 config.json）。</summary>
    Task UpdateAsync<T>(string sectionKey, T value, CancellationToken ct);

    /// <summary>重置配置节为默认值。</summary>
    Task ResetAsync(string sectionKey, CancellationToken ct);
}
