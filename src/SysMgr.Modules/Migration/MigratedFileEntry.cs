namespace SysMgr.Modules.Migration;

/// <summary>
/// 迁移计划中的单个文件条目（FR-MIGRATE-01 扫描阶段产出，复制/校验/删除各阶段围绕它推进）。
/// Source/Destination 为完整文件路径；Sha256 在复制前对源文件计算（也作去重依据），
/// 复制后对目标文件再算一次比对，不一致即判失败回滚。
/// </summary>
public sealed record MigratedFileEntry(
    string Source,
    string Destination,
    long SizeBytes,
    string? Sha256);
