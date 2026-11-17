using System.Diagnostics;
using SysMgr.Core.SystemAccess;

namespace SysMgr.Infra;

/// <summary>
/// 防火墙实现（IFirewall 端口，FR-TRUST-03）。
/// 通过 netsh advfirewall 管理按程序路径匹配的出站阻断规则。
/// </summary>
public sealed class FirewallFacade : IFirewall
{
    private const string RuleName = "sysmgr-self-disconnect";

    /// <inheritdoc />
    public Task BlockApplicationAsync(string applicationPath, CancellationToken ct)
    {
        // 先确保旧规则不存在（重复添加 netsh 会报错"对象已存在"）
        RunNetsh($"advfirewall firewall delete rule name=\"{RuleName}\"");
        RunNetsh(
            $"advfirewall firewall add rule name=\"{RuleName}\" " +
            $"dir=out action=block program=\"{applicationPath}\" enable=yes");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnblockApplicationAsync(string applicationPath, CancellationToken ct)
    {
        RunNetsh($"advfirewall firewall delete rule name=\"{RuleName}\" program=\"{applicationPath}\"");
        return Task.CompletedTask;
    }

    private static void RunNetsh(string arguments)
    {
        var psi = new ProcessStartInfo("netsh.exe", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"netsh 执行失败：{error.Trim()}");
        }
    }
}
