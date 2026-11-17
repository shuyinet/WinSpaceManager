using SysMgr.Core.Queue;

namespace SysMgr.Modules.Migration;

/// <summary>
/// 文件迁移引擎模块服务（FR-MIGRATE-01，TDD §3.2 / §1.2 命名空间 SysMgr.Modules.Migration）。
/// 通用迁移能力，被 CSlim（大文件迁移）等模块经 IModuleCoordinator.RequestMigrationAsync 调用，
/// 也可由 UI 直接发起文件夹迁移（TDD：MigrateFolderAsync(src, dst, options)）。
///
/// 迁移步骤（FR-MIGRATE-01 硬约束）：扫描 → 复制 → 校验（SHA-256）→ 创建联接（按需）→ 删除原文件，
/// 任一步失败可回滚。实现方式：按 MigratedFileEntry 列表构造 OperationPlan
/// （actions=逐文件 MoveFile + 末尾 CreateJunction?），走 TRUST-02 预演 → OpQueue 串行执行；
/// 源文件在校验通过后才删除，回滚 = 删除半成品目标 + 已删源从备份恢复。
///
/// V1 不落地：FR-MIGRATE-02 应用迁移（V2：安装目录 + 注册表路径修正 + 强制备份）。
/// </summary>
public interface IMigrationService : IModuleService
{
    /// <summary>
    /// 迁移预检（FR-MIGRATE-01 扫描阶段）。
    /// 枚举源内容生成 MigratedFileEntry（供进度预览），校验：目标可写、剩余空间、同名冲突、
    /// 源/目标非同卷子目录（防止递归复制）、源是否含系统关键路径。只读不改动。
    /// </summary>
    Task<MigrationValidation> ValidateAsync(
        MigrationRequest request,
        IProgress<ScanProgress>? progress,
        CancellationToken ct);

    /// <summary>
    /// 提交迁移任务（协调器与 UI 的统一入口）。
    /// 内部先执行预检，通过后构造 OperationPlan 走 TRUST-02 预演 → OpQueue 执行；
    /// 返回结果含已迁移/跳过/回滚条目统计。
    /// </summary>
    Task<OperationResult> EnqueueMigrationAsync(
        MigrationRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken ct);
}
