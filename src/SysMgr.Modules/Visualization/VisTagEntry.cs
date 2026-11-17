namespace SysMgr.Modules.Visualization;

/// <summary>
/// FR-VIS-03 标签存储节（config.json "vis_tags"）。路径 → 标签文本。
/// </summary>
public sealed record VisTagEntry(string Path, string Tag);
