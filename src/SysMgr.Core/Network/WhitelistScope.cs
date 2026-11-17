namespace SysMgr.Core.Network;

/// <summary>
/// 联网白名单类别（TDD §2.4）。V1 仅 UpdateCheck/TrustSelfTest 落地；
/// PopupRuleUpdate=V2、CloudAiAndWallet=V3。便携版 UpdateCheck 仅手动触发不自动。
/// </summary>
public enum WhitelistScope
{
    UpdateCheck,        // §2.4.1 检查更新（便携版手动触发，不自动）
    PopupRuleUpdate,    // §2.4.2 弹窗规则库更新
    TrustSelfTest,      // §2.4.3 自我断网验证端点
    CloudAiAndWallet    // §2.4.4 云端 AI + 钱包校验 + 模型文件下载
}
