using SysMgr.Infra;
using Xunit;

namespace SysMgr.Modules.Tests;

/// <summary>
/// schtasks /query /xml 输出规范化测试（实跑发现的解析 bug 回归）。
/// 实格式：&lt;Tasks&gt; 包装 + 每任务一段带 &lt;?xml?&gt; 声明的完整文档 + 任务名注释。
/// </summary>
public class TaskQueryFacadeNormalizeTests
{
    private const string Decl = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>";
    private const string TaskA =
        "<Task version=\"1.6\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
        "<RegistrationInfo><URI>\\A</URI></RegistrationInfo></Task>";
    private const string TaskB =
        "<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
        "<RegistrationInfo><URI>\\Folder\\B</URI></RegistrationInfo></Task>";

    [Fact]
    public void Normalize_WrappedMultiDocWithComments_ParsesAllTasks()
    {
        // 与真实输出同构：包装 + 注释 + 两段声明文档 + 空行
        var raw = "\r\n<Tasks>\r\n\r\n<!-- \\A -->\r\n" + Decl + "\r\n" + TaskA +
                  "\r\n\r\n<!-- \\Folder\\B -->\r\n" + Decl + "\r\n" + TaskB + "\r\n\r\n</Tasks>\r\n";

        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(TaskQueryFacade.NormalizeTaskListXml(raw));

        var ns = new System.Xml.XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
        var tasks = doc.SelectNodes("//t:Task", ns)!;
        Assert.Equal(2, tasks.Count);
    }

    [Fact]
    public void Normalize_SingleDocument_Parses()
    {
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(TaskQueryFacade.NormalizeTaskListXml(Decl + TaskA));

        Assert.NotNull(doc.DocumentElement);
    }

    [Fact]
    public void Normalize_NoiseOnly_YieldsEmptyRoot()
    {
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(TaskQueryFacade.NormalizeTaskListXml("ERROR: no tasks\r\n"));

        Assert.NotNull(doc.DocumentElement);
        Assert.Empty(doc.DocumentElement!.ChildNodes.Cast<System.Xml.XmlNode>()
            .OfType<System.Xml.XmlElement>());
    }
}
