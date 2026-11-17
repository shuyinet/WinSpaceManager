using System.IO;

namespace SysMgr.Modules.Migration;

/// <summary>大文件分类（大文件搬家页的聚合维度）。</summary>
public enum LargeFileCategory
{
    Installer,
    Archive,
    Log,
    Video,
    Image,
    Other
}

/// <summary>扫描到的大文件条目。</summary>
public sealed record LargeFileEntry(string Path, string Name, long SizeBytes, LargeFileCategory Category);

/// <summary>
/// 大文件扫描器：全盘（C:）枚举候选文件，按扩展名归类。
/// 跳过 Windows、回收站、卷影等系统目录与系统文件，避免误搬导致系统异常。
/// </summary>
public sealed class LargeFileScanner
{
    /// <summary>默认最小体积 50MB（与同类产品口径一致，低于此值搬家收益有限）。</summary>
    public const long DefaultMinSizeBytes = 50L * 1024 * 1024;

    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "$Recycle.Bin", "System Volume Information",
        "Recovery", "PerfLogs", "WinSxS",
        "Program Files", "Program Files (x86)",
        "node_modules", ".git", ".svn"
    };

    /// <summary>系统盘根目录下的系统文件，绝不可搬家。</summary>
    private static readonly HashSet<string> SkippedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "swapfile.sys", "pagefile.sys", "hiberfil.sys",
        "DumpStack.log", "DumpStack.log.tmp", "bootmgr", "BOOTNXT"
    };

    private static readonly Dictionary<string, LargeFileCategory> CategoryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".exe"] = LargeFileCategory.Installer,
        [".msi"] = LargeFileCategory.Installer,
        [".msix"] = LargeFileCategory.Installer,
        [".appx"] = LargeFileCategory.Installer,
        [".appxbundle"] = LargeFileCategory.Installer,
        [".msu"] = LargeFileCategory.Installer,

        [".zip"] = LargeFileCategory.Archive,
        [".rar"] = LargeFileCategory.Archive,
        [".7z"] = LargeFileCategory.Archive,
        [".tar"] = LargeFileCategory.Archive,
        [".gz"] = LargeFileCategory.Archive,
        [".tgz"] = LargeFileCategory.Archive,
        [".bz2"] = LargeFileCategory.Archive,
        [".xz"] = LargeFileCategory.Archive,
        [".cab"] = LargeFileCategory.Archive,

        [".log"] = LargeFileCategory.Log,
        [".etl"] = LargeFileCategory.Log,
        [".dmp"] = LargeFileCategory.Log,
        [".mdmp"] = LargeFileCategory.Log,
        [".bak"] = LargeFileCategory.Log,
        [".old"] = LargeFileCategory.Log,

        [".mp4"] = LargeFileCategory.Video,
        [".mkv"] = LargeFileCategory.Video,
        [".avi"] = LargeFileCategory.Video,
        [".mov"] = LargeFileCategory.Video,
        [".wmv"] = LargeFileCategory.Video,
        [".flv"] = LargeFileCategory.Video,

        [".iso"] = LargeFileCategory.Image,
        [".img"] = LargeFileCategory.Image,
        [".vhd"] = LargeFileCategory.Image,
        [".vhdx"] = LargeFileCategory.Image,
        [".wim"] = LargeFileCategory.Image,
        [".esd"] = LargeFileCategory.Image
    };

    /// <summary>
    /// 扫描常见大文件目录（后台线程执行）。
    /// </summary>
    public Task<List<LargeFileEntry>> ScanAsync(
        long minSizeBytes,
        IProgress<int>? progress,
        CancellationToken ct)
        => Task.Run(() => Scan(minSizeBytes, progress, ct), ct);

    private static List<LargeFileEntry> Scan(long minSizeBytes, IProgress<int>? progress, CancellationToken ct)
    {
        var results = new List<LargeFileEntry>();
        var scanned = 0;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, recursive) in EnumerateRoots().Distinct())
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                continue;
            }

            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = pending.Pop();
                if (!visited.Add(dir))
                {
                    continue;
                }

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(dir);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                foreach (var file in files)
                {
                    scanned++;
                    if (scanned % 200 == 0)
                    {
                        progress?.Report(scanned);
                    }

                    try
                    {
                        var info = new FileInfo(file);
                        if (!info.Exists
                            || info.Length < minSizeBytes
                            || SkippedFileNames.Contains(info.Name)
                            || info.Attributes.HasFlag(FileAttributes.System)
                            || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            continue;
                        }

                        var category = Classify(info.Extension);
                        if (category is null)
                        {
                            continue;
                        }

                        results.Add(new LargeFileEntry(
                            info.FullName,
                            info.Name,
                            info.Length,
                            category.Value));
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        // 单个文件不可读时跳过
                    }
                }

                if (!recursive)
                {
                    continue;
                }

                IEnumerable<string> subDirs;
                try
                {
                    subDirs = Directory.EnumerateDirectories(dir);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                foreach (var sub in subDirs)
                {
                    var name = Path.GetFileName(sub);
                    if (SkippedDirectoryNames.Contains(name))
                    {
                        continue;
                    }
                    try
                    {
                        if (File.GetAttributes(sub).HasFlag(FileAttributes.ReparsePoint))
                        {
                            continue;
                        }
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        continue;
                    }
                    pending.Push(sub);
                }
            }
        }

        progress?.Report(scanned);
        return results
            .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(r => r.SizeBytes)
            .ToList();
    }

    private static IEnumerable<(string Root, bool Recursive)> EnumerateRoots()
    {
        yield return (@"C:\", true);
    }

    private static LargeFileCategory? Classify(string extension)
        => CategoryMap.TryGetValue(extension, out var category) ? category : null;
}
