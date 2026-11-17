namespace SysMgr.Modules.Residual;

/// <summary>
/// 残留扫描目标（描述"哪个软件的残留"）。
/// 由 UninstallItem 构造（卸载后联动 FR-RESIDUAL-03，或用户在软件清单页手动触发）。
/// 三字段共同构造扫描关键词：DisplayName 全名优先，Publisher 辅助，InstallLocation 末段兜底。
/// </summary>
public sealed record ResidualScanTarget(
    Guid SoftwareId,
    string DisplayName,
    string? Publisher,
    string? InstallLocation);
