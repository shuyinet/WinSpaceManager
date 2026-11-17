namespace SysMgr.Modules.CSlim;

/// <summary>
/// 新内容保存位置类别（FR-CSLIM-08，对应 Windows 设置 → 系统 → 存储 → 高级存储设置 → 新内容保存位置）。
/// </summary>
public enum SaveLocationCategory
{
    NewApps,       // 新的应用
    NewDocuments,  // 新的文档
    NewMusic,      // 新的音乐
    NewPhotos,     // 新的照片
    NewVideos,     // 新的视频
    NewMovies,     // 新的电影和电视节目
    OfflineMaps    // 脱机地图
}

/// <summary>
/// 默认保存位置建议（FR-CSLIM-08）。
/// CurrentDrive=当前保存盘（从系统 Known Folder 关联卷读取），SuggestedDrive=建议的非系统盘
/// （按剩余空间最大的固定盘给出）。CanApply=false 表示无可用第二固定盘或该项当前已不在 C 盘。
/// 仅生成建议不自动修改；"一键应用建议"需用户确认后执行。
/// </summary>
public sealed record SaveLocationSuggestion(
    SaveLocationCategory Category,
    string CurrentDrive,
    string SuggestedDrive,
    bool CanApply,
    string Description);
