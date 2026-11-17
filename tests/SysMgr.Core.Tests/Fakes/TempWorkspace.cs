namespace SysMgr.Core.Tests.Fakes;

/// <summary>
/// 测试临时工作区（实现 IDisposable，xUnit 用完即删）。
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    public string Root { get; }

    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "sysmgr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string CreateFile(string relativePath, string content = "x")
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string CreateDirectory(string relativePath)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 杀软扫描等瞬时占用，测试结束不纠结
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }
}
