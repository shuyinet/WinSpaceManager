using SysMgr.Core.Queue;

namespace SysMgr.Modules.Cleanup;

/// <summary>
/// 垃圾清理模块服务（FR-CLEAN-01/02/06，TDD §3.2 / §1.2 命名空间 SysMgr.Modules.Cleanup）。
/// 方法链：ScanJunkAsync（按范围扫描系统垃圾/浏览器隐私数据 + 安全分级）→ EnqueueCleanAsync（勾选项入队清除）。
///
/// 清除执行：垃圾项映射 OperationAction——普通垃圾 DeleteFile（Target 路径），
/// 回收站特化为虚拟目标由 OpQueue 调 SHEmptyRecycleBin；浏览器在运行时按 CleanOptions 决定
/// 是否先 KillProcess（按浏览器进程映像名匹配）。构造 OperationPlan 走 TRUST-02 预演（二次确认）
/// → OpQueue 串行执行，删除前由 OpQueue 触发 IRollbackEngine 影子备份（垃圾以 PathRecordOnly 为主，
/// 浏览器隐私数据 Copy 备份，编码阶段在规则条目上配置策略）。
///
/// FR-CLEAN-06 一键清理：扫描后自动取 DefaultSelected=true 的全部项（即所有 High 系统项 + 浏览器缓存），
/// 仍经 TRUST-02 预演确认后执行，返回清理前后空间对比与清理报告。
///
/// V1 不落地：FR-CLEAN-03 注册表清理 / FR-CLEAN-04 软件缓存清理 / FR-CLEAN-05 重复文件查找（均 V1.5，
/// 05 依赖 §3.5 Treemap 联动）。
/// </summary>
public interface ICleanupService : IModuleService
{
    /// <summary>
    /// 按范围扫描垃圾（FR-CLEAN-01/02）。
    /// 系统垃圾路径来自 rules\cleanup_targets.json；浏览器按数据目录是否存在判定已安装，
    /// 逐项计算预计释放空间（占用文件跳过）并经 ISafetyClassifier 打级。流式返回。
    /// </summary>
    IAsyncEnumerable<JunkItem> ScanJunkAsync(
        JunkScope scope,
        IProgress<ScanProgress>? progress,
        CancellationToken ct);

    /// <summary>
    /// 提交垃圾清理任务（FR-CLEAN-01/02/06）。
    /// 浏览器在运行且未选 KillRunningBrowsers 时抛 BrowserRunningException；
    /// 否则构造 OperationPlan 走 TRUST-02 预演 → OpQueue 串行执行，单项失败不阻塞其他项。
    /// </summary>
    Task<OperationResult> EnqueueCleanAsync(
        IReadOnlyList<JunkItem> items,
        CleanOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken ct);

    /// <summary>
    /// 重新测量单个垃圾项清理后的剩余体积（文件/目录不存在返回 0；回收站按当前回收站总量）。
    /// 供 UI 在清理完成后把「已释放」更新为「剩余」展示。
    /// </summary>
    Task<long> GetRemainingSizeAsync(JunkItem item, CancellationToken ct);
}
