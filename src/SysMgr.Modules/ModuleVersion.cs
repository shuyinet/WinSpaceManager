namespace SysMgr.Modules;

/// <summary>
/// 模块版本（TDD §3.1）。Tag 对齐 requirements §6 版本规划：V1 / V1.5 / V2 / V3。
/// </summary>
public sealed record ModuleVersion(int Major, int Minor, string Tag);
