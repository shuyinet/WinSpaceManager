using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml;
using SysMgr.Core.SystemAccess;

namespace SysMgr.Infra;

/// <summary>
/// 计划任务查询实现（ITaskQuery 端口）。
/// 通过 schtasks /query /xml 获取语言中立 XML（避免本地化 CSV 列名问题），按命名空间解析；
/// 删除走 schtasks /delete。
/// </summary>
public sealed class TaskQueryFacade : ITaskQuery
{
    private const string TaskSchema = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <inheritdoc />
    public IReadOnlyCollection<ScheduledTaskInfo> Enumerate()
    {
        var xml = RunSchtasks("/query /xml");
        var doc = new XmlDocument();
        doc.LoadXml(NormalizeTaskListXml(xml));

        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("t", TaskSchema);

        var result = new List<ScheduledTaskInfo>();
        foreach (XmlNode taskNode in doc.SelectNodes("//t:Task", ns)!)
        {
            var uri = taskNode.SelectSingleNode("t:RegistrationInfo/t:URI", ns)?.InnerText;
            var command = taskNode.SelectSingleNode(".//t:Actions/t:Exec/t:Command", ns)?.InnerText;
            if (string.IsNullOrEmpty(uri))
            {
                continue;
            }
            var name = uri.Contains('\\') ? uri[(uri.LastIndexOf('\\') + 1)..] : uri;
            // Settings/Enabled 缺省为 true；Triggers 下存在 LogonTrigger 即登录触发项
            var enabledText = taskNode.SelectSingleNode("t:Settings/t:Enabled", ns)?.InnerText;
            var enabled = !"false".Equals(enabledText, StringComparison.OrdinalIgnoreCase);
            var hasLogonTrigger = taskNode.SelectSingleNode("t:Triggers/t:LogonTrigger", ns) is not null;
            result.Add(new ScheduledTaskInfo(uri, name, command, enabled, hasLogonTrigger));
        }
        return result;
    }

    /// <summary>
    /// schtasks /query /xml 输出规范化：实格式为 &lt;Tasks&gt; 包装 + 每任务一段带
    /// &lt;?xml?&gt; 声明的完整文档 + 任务名注释。剥掉全部声明与外层包装后重新包根，
    /// 得到可一次 LoadXml 的合法 XML。
    /// </summary>
    internal static string NormalizeTaskListXml(string output)
    {
        var body = Regex.Replace(output, @"<\?xml[^?]*\?>", string.Empty);
        body = Regex.Replace(body, @"</?Tasks\s*>", string.Empty);
        return "<Tasks>" + body + "</Tasks>";
    }

    /// <inheritdoc />
    public void DeleteTask(string taskPath)
    {
        RunSchtasks($"/delete /tn \"{taskPath}\" /f");
    }

    /// <inheritdoc />
    public void SetTaskEnabled(string taskPath, bool enabled)
    {
        RunSchtasks($"/change /tn \"{taskPath}\" /{(enabled ? "enable" : "disable")}");
    }

    private static string RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"schtasks 执行失败：{error.Trim()}");
        }
        return output;
    }
}
