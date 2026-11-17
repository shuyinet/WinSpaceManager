namespace SysMgr.Modules;

/// <summary>迁移类型（FR-MIGRATE-01 文件夹迁移 / CSlim 大文件迁移）。</summary>
public enum MigrationKind
{
    File,    // 一个或多个文件迁移到目标目录（FR-CSLIM-02）
    Folder   // 单个目录整体迁移（FR-MIGRATE-01）
}

/// <summary>
/// 迁移请求（跨模块统一契约，TDD §1.3 耦合点）。
/// CSlim 等调用方构造后经 IModuleCoordinator.RequestMigrationAsync 路由到 Migration 模块，
/// 模块间不直接 new 对方 Service。
/// </summary>
public sealed record MigrationRequest(
    Guid RequestId,
    MigrationKind Kind,
    IReadOnlyList<string> Sources,    // File：源文件列表；Folder：仅含一个源目录
    string Destination,               // File：目标目录（保留文件名）；Folder：目标完整路径
    bool CreateJunction,              // Folder 校验通过后在源位置创建指向目标的 junction，兼容旧路径引用
    bool DeleteSourceAfterVerify);    // 校验通过后删除源（默认 true；false=仅复制出一份副本）
