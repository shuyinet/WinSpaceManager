using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace SysMgr.App.Updater;

/// <summary>更新流程阶段。</summary>
public enum UpdatePhase
{
    /// <summary>空闲 / 已是最新版本。</summary>
    Idle,

    /// <summary>正在请求版本清单。</summary>
    Checking,

    /// <summary>正在后台下载安装包。</summary>
    Downloading,

    /// <summary>安装包已就绪，可点击更新。</summary>
    Ready,

    /// <summary>检查或下载失败（静默忽略，不打扰用户）。</summary>
    Failed
}

/// <summary>一次更新状态快照。</summary>
public sealed record UpdateStatus(
    UpdatePhase Phase,
    string Text,
    double Progress = 0,
    string? Version = null);

/// <summary>
/// 自动更新服务：读取远端 latest.json 清单，比较当前程序集版本，
/// 发现新版本后后台下载安装包（可选 SHA-256 校验），下载完成提示用户点击更新。
/// 清单格式：{ "version": "1.0.1", "url": "SysMgr-Setup-1.0.1.exe", "sha256": "..." }
/// </summary>
public sealed class UpdateService
{
    /// <summary>版本清单地址（与安装包同目录）。</summary>
    public const string DefaultManifestUrl = "https://gameres.shuyinet.com/sysmgr/latest.json";

    private static readonly HttpClient Http = new()
    {
        // 安装包体积较大，整体超时放宽到 10 分钟
        Timeout = TimeSpan.FromMinutes(10)
    };

    private readonly string _manifestUrl;
    private readonly string _downloadDir;
    private string? _installerPath;

    public UpdateService(string? manifestUrl = null)
    {
        _manifestUrl = manifestUrl ?? DefaultManifestUrl;
        _downloadDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "sysmgr", "updates");
        CurrentVersion = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0, 0);
    }

    /// <summary>当前程序集版本。</summary>
    public Version CurrentVersion { get; }

    /// <summary>是否已有下载完成的安装包。</summary>
    public bool IsReady => _installerPath is not null;

    /// <summary>状态变化通知（可能在后台线程触发，订阅方需自行调度到 UI 线程）。</summary>
    public event Action<UpdateStatus>? StatusChanged;

    /// <summary>检查新版本并在后台下载安装包。</summary>
    public async Task CheckAndDownloadAsync(CancellationToken ct = default)
    {
        if (_installerPath is not null)
        {
            return;
        }

        Report(new UpdateStatus(UpdatePhase.Checking, "正在检查新版本…"));
        try
        {
            Version remote;
            string url;
            string? sha256;

            using (var stream = await Http.GetStreamAsync(_manifestUrl, ct))
            using (var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct))
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("version", out var versionElement) ||
                    !Version.TryParse(versionElement.GetString(), out remote))
                {
                    Report(new UpdateStatus(UpdatePhase.Failed, "版本清单格式无效"));
                    return;
                }

                if (!root.TryGetProperty("url", out var urlElement) ||
                    string.IsNullOrWhiteSpace(urlElement.GetString()))
                {
                    Report(new UpdateStatus(UpdatePhase.Failed, "版本清单缺少下载地址"));
                    return;
                }

                // 支持相对路径，相对清单目录解析
                var urlText = urlElement.GetString()!;
                url = new Uri(new Uri(_manifestUrl), urlText).ToString();
                sha256 = root.TryGetProperty("sha256", out var shaElement)
                    ? shaElement.GetString()?.Trim().ToLowerInvariant()
                    : null;
            }

            if (remote <= CurrentVersion)
            {
                Report(new UpdateStatus(UpdatePhase.Idle, "已是最新版本"));
                return;
            }

            Directory.CreateDirectory(_downloadDir);
            var fileName = Path.GetFileName(new Uri(url).LocalPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = $"SysMgr-Setup-{remote}.exe";
            }

            var target = Path.Combine(_downloadDir, fileName);

            // 上次已下载且校验通过：直接复用，避免重复下载
            if (File.Exists(target) && (sha256 is null || VerifySha256(target, sha256)))
            {
                _installerPath = target;
                Report(new UpdateStatus(
                    UpdatePhase.Ready, $"新版本 v{remote} 已就绪 · 点击更新", 1, remote.ToString()));
                return;
            }

            var temp = target + ".part";
            await DownloadAsync(url, temp, remote, ct);

            if (sha256 is not null && !VerifySha256(temp, sha256))
            {
                TryDelete(temp);
                Report(new UpdateStatus(UpdatePhase.Failed, "安装包校验失败"));
                return;
            }

            File.Move(temp, target, overwrite: true);
            _installerPath = target;
            Report(new UpdateStatus(
                UpdatePhase.Ready, $"新版本 v{remote} 已就绪 · 点击更新", 1, remote.ToString()));
        }
        catch (OperationCanceledException)
        {
            Report(new UpdateStatus(UpdatePhase.Idle, string.Empty));
        }
        catch (Exception ex)
        {
            // 更新失败不应影响主流程：静默降级，仅记录到状态
            Report(new UpdateStatus(UpdatePhase.Failed, "更新检查失败：" + ex.Message));
        }
    }

    /// <summary>
    /// 运行安装包并退出当前进程。
    /// 用 cmd 延迟 2 秒再启动，确保本进程已释放 exe 文件句柄；静默安装后由安装脚本自动重启。
    /// </summary>
    public void InstallAndRestart()
    {
        var path = _installerPath;
        if (path is null || !File.Exists(path))
        {
            return;
        }

        var psi = new ProcessStartInfo(
            "cmd.exe",
            $"/c timeout /t 2 /nobreak >nul & start \"\" \"{path}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        Process.Start(psi);
        Application.Current.Shutdown();
    }

    private async Task DownloadAsync(string url, string path, Version version, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long received = 0;
        var lastReported = -1d;
        int read;

        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;

            var progress = total > 0 ? (double)received / total : 0;
            if (progress - lastReported >= 0.01 || progress >= 1)
            {
                lastReported = progress;
                Report(new UpdateStatus(
                    UpdatePhase.Downloading,
                    $"正在后台下载 v{version} · {progress:P0}",
                    progress,
                    version.ToString()));
            }
        }
    }

    private static bool VerifySha256(string path, string expected)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return hash.Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 忽略：临时文件残留不影响下次下载
        }
    }

    private void Report(UpdateStatus status) => StatusChanged?.Invoke(status);
}
