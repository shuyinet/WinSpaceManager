using SysMgr.Core.Config;
using SysMgr.Core.Logging;
using SysMgr.Core.Network;
using SysMgr.Core.Operations;
using SysMgr.Core.Queue;
using SysMgr.Core.Rollback;
using SysMgr.Core.Rules;
using SysMgr.Core.Safety;
using SysMgr.Core.Trust;

namespace SysMgr.Modules;

/// <summary>
/// 模块上下文（TDD §3.1）。横切层句柄注入，模块通过此接口访问所有横切层能力，不直接持有实现。
/// 由 L1 启动初始化阶段构造，DI 注入到每个 IModuleService。
/// </summary>
public interface IModuleContext
{
    IRollbackEngine Rollback { get; }
    IDestructiveOpQueue OpQueue { get; }
    ISafetyClassifier Safety { get; }
    INetworkWhitelist Network { get; }
    IRuleSetManager Rules { get; }
    IOperationLogger Logger { get; }
    ITrustBroadcaster Trust { get; }    // TRUST-01 直播 + TRUST-02 预演入口
    IConfigStore Config { get; }
    IModuleCoordinator Coordinator { get; }
}
