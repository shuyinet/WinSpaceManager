namespace SysMgr.Modules.Migration;

/// <summary>
/// 迁移预检结果（FR-MIGRATE-01 扫描阶段产出）。
/// Issues 为阻断性问题清单（CanMigrate=false 时至少一项）；
/// Warnings 为非阻断提示（如目标存在同名文件将改名、根目录跨盘等）。
/// </summary>
public sealed record MigrationValidation(
    bool CanMigrate,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> Warnings,
    long RequiredBytes,
    long AvailableBytes);
