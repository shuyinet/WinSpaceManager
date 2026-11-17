namespace SysMgr.Modules.Internal;

/// <summary>
/// 模块层共享文件系统工具。各模块的目录大小估算/超时降级口径集中在此，避免重复实现。
/// </summary>
internal static class FileUtilities
{
    /// <summary>展开环境变量并取完整路径；路径非法时返回原值。</summary>
    public static string Normalize(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        try
        {
            return Path.GetFullPath(expanded);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return expanded;
        }
    }

    /// <summary>文件或目录大小；不存在返回 0。</summary>
    public static long TryGetSize(string path)
        => Measure(path, timeout: null, out _);

    /// <summary>
    /// 测量文件/目录大小。
    /// timeout=null 不限时；超时返回已累加部分且 completed=false（调用方据此降级）。
    /// 不跟随 reparse point；拒绝访问项跳过。
    /// </summary>
    public static long Measure(string path, TimeSpan? timeout, out bool completed)
    {
        completed = true;
        if (File.Exists(path))
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }
        if (!Directory.Exists(path))
        {
            return 0;
        }

        var deadline = timeout is null
            ? DateTime.MaxValue
            : DateTime.UtcNow.Add(timeout.Value);
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(path);

        while (pending.Count > 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                completed = false;
                return total;
            }

            var dir = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 占用/无权限文件跳过
                    }
                }
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var attrs = File.GetAttributes(sub);
                    if (!attrs.HasFlag(FileAttributes.ReparsePoint))
                    {
                        pending.Push(sub);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 不可访问目录跳过
            }
        }
        return total;
    }

    /// <summary>路径前缀判断（OrdinalIgnoreCase，按规范化完整路径）。</summary>
    public static bool IsUnder(string candidate, string ancestor)
    {
        var fullCandidate = Normalize(candidate).TrimEnd(Path.DirectorySeparatorChar);
        var fullAncestor = Normalize(ancestor).TrimEnd(Path.DirectorySeparatorChar);
        return fullCandidate.Equals(fullAncestor, StringComparison.OrdinalIgnoreCase)
               || fullCandidate.StartsWith(
                   fullAncestor + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>获取路径所在卷根，如 "C:\"；失败返回 null。</summary>
    public static string? GetDriveRoot(string path)
    {
        try
        {
            return new DriveInfo(Normalize(path)).RootDirectory.Name;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return null;
        }
    }
}
