using System.Management;
using System.ServiceProcess;
using SysMgr.Core.SystemAccess;

namespace SysMgr.Infra;

/// <summary>
/// 服务/驱动查询实现（IServiceQuery 端口）。
/// 枚举走 WMI（Win32_Service，含 PathName）；停止走 ServiceController（30s 超时）。
/// </summary>
public sealed class ServiceQueryFacade : IServiceQuery
{
    /// <inheritdoc />
    public IReadOnlyCollection<ServiceInfo> EnumerateServices()
    {
        var result = new List<ServiceInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, DisplayName, PathName, State FROM Win32_Service");
        using var collection = searcher.Get();
        foreach (var obj in collection)
        {
            result.Add(new ServiceInfo(
                Convert.ToString(obj["Name"]) ?? string.Empty,
                Convert.ToString(obj["DisplayName"]) ?? string.Empty,
                Convert.ToString(obj["PathName"]),
                Convert.ToString(obj["State"]) switch
                {
                    "Running" => ServiceState.Running,
                    "Stopped" => ServiceState.Stopped,
                    _ => ServiceState.Other
                }));
        }
        return result;
    }

    /// <inheritdoc />
    public void Stop(string serviceName)
    {
        using var controller = new ServiceController(serviceName);
        if (controller.Status == ServiceControllerStatus.Stopped)
        {
            return;
        }
        controller.Stop();
        controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
    }
}
