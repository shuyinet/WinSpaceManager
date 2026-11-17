# sysmgr 技术设计文档（TDD）

| 项目 | 内容 |
| --- | --- |
| 文档版本 | v0.1（初稿，对齐 requirements.md v0.3） |
| 编写日期 | 2026-09-28 |
| 状态 | 进行中（骨架 + §1-2 已落，§3-9 占位待续） |
| 配套文档 | [requirements.md](./requirements.md) v0.3 |
| 项目代号 | sysmgr |

> 本文档是需求到代码之间的契约层：定义架构、模块边界、横切层接口、数据模型、跨模块协议、系统访问封装策略、打包发布与技术风险。逐模块伪代码级详设见后续 V1 MVP 详设批次，不在本文档展开到伪代码深度。

## 1. 系统架构总览

### 1.1 四层架构细化

延续 requirements §5.2 的四层架构，本文档进一步明确每层的职责、对外契约、依赖方向。

```
┌──────────────────────────────────────────────────────────────────────┐
│ L1 统一入口层（Presentation + Entry）                                  │
│   WPF MainWindow / WPF-UI Fluent / 电脑体检 / TRUST 操作直播面板        │
│   sysmgr-cli（命令行入口，FR-CLI-02）/ 右键菜单宿主（FR-CLI-04）       │
│   AI 自然语言入口（FR-AI-01，V3）                                       │
├──────────────────────────────────────────────────────────────────────┤
│ L2 功能模块层（Domain Modules）                                        │
│   15 个功能模块 + 8 组增强能力，每个模块对外暴露 I<Module>Service        │
│   只依赖 L3 横切层与 L4 系统访问层封装，模块间通过事件总线/显式调用解耦  │
├──────────────────────────────────────────────────────────────────────┤
│ L3 横切层（Cross-Cutting）                                             │
│   回滚引擎 / 破坏性操作全局串行队列 / 安全等级判定 / 联网白名单          │
│   规则集管理 / 日志与遥测 / 操作历史 / 进程与权限上下文 / 配置中心       │
├──────────────────────────────────────────────────────────────────────┤
│ L4 系统访问层（System Access Facades）                                 │
│   RegistryFs / WmiBridge / ServiceController / TaskScheduler           │
│   UwpDeployment / FirewallRule / WinEventHook / MiniFilter / Vss       │
│   SmartDisk / FileSystemSafe（长路径/重试/占用检测）                     │
└──────────────────────────────────────────────────────────────────────┘
```

**依赖方向硬约束**（编译期通过项目引用强制）：
- L1 → L2 → L3 → L4（仅允许向下一层依赖）
- L4 不依赖任何上层；L3 不依赖 L2；L2 之间不直接引用，跨模块协作走 L3 事件总线或显式 `IModuleCoordinator`
- 例外：L2 增强能力模块（如 §3.15.6 CLI-04 右键菜单）需在 L1 注册宿主，由 L1 反向回调 L2 接口（通过 DI 容器解析，不形成编译期循环）

### 1.2 模块到分层映射

| 模块（requirements §3） | 分层位置 | 命名空间 | V1 是否落地 |
| --- | --- | --- | --- |
| §3.1 电脑体检 | L1+L2 | `SysMgr.Modules.Checkup` | V1.5 |
| §3.2 软件卸载 | L2 | `SysMgr.Modules.Uninstall` | **V1** |
| §3.3 拦留清除 | L2 | `SysMgr.Modules.Residual` | **V1** |
| §3.4 C 盘瘦身 | L2 | `SysMgr.Modules.CSlim` | **V1（基础）** |
| §3.5 磁盘可视化 | L2 | `SysMgr.Modules.Visualization` | V1.5 |
| §3.6 垃圾清理 | L2 | `SysMgr.Modules.Cleanup` | **V1** |
| §3.7 文件迁移引擎 | L2 | `SysMgr.Modules.Migration` | **V1（文件夹迁移）** |
| §3.8 启动项与服务 | L2 | `SysMgr.Modules.Startup` | V1.5 |
| §3.9 拦留弹窗拦截 | L2 | `SysMgr.Modules.Popup` | V2 |
| §3.10 文件粉碎 | L2 | `SysMgr.Modules.Shred` | V1.5 |
| §3.11 系统优化 | L2 | `SysMgr.Modules.Optimize` | V2 |
| §3.12 系统监控 | L2 | `SysMgr.Modules.Monitor` | V1.5 |
| §3.13 回滚引擎 | L3 | `SysMgr.Core.Rollback` | **V1** |
| §3.14 设置与帮助 | L1+L2 | `SysMgr.Modules.Settings` | **V1** |
| §3.15.1 TRUST | L1+L3 | `SysMgr.Core.Trust` | **V1（TRUST-01/02/03）** |
| §3.15.2 TM 时间机器 | L2+L3 | `SysMgr.Modules.Timeline` | V1（**TM-04**）/ V1.5 / V2 |
| §3.15.3 GUARD 反捆绑 | L2+L4 | `SysMgr.Modules.Guard` | V2（V1 预研） |
| §3.15.4 PROFILE 软件画像 | L2 | `SysMgr.Modules.Profile` | V1.5 |
| §3.15.5 FAMILY 家庭多机 | L2+L4 | `SysMgr.Modules.Family` | V3 |
| §3.15.6 CLI | L1 | `SysMgr.Cli` | **V1（CLI-01 便携）** / V1.5（02/03/04） |
| §3.15.7 GAME/HW | L2+L4 | `SysMgr.Modules.Game`, `SysMgr.Modules.Hardware` | V1.5（HW-01）/ V2（GAME） |
| §3.15.8 AI 辅助 | L2+L4 | `SysMgr.Modules.Ai` | V3 |

### 1.3 模块依赖图（V1 范围）

> 仅画 V1 MVP 模块依赖，V1.5/V2/V3 模块按版本演进图后续追加。

```
                ┌─────────────────────────────────────┐
                │  L1: WPF MainWindow + sysmgr-cli   │
                └───────────┬─────────────────────────┘
                            │  DI 解析 IModuleService
       ┌────────────────────┼────────────────────────────────┐
       │                    │                                │
       ▼                    ▼                                ▼
┌─────────────┐   ┌──────────────────┐              ┌──────────────┐
│ Uninstall   │──▶│ Residual         │              │  CSlim       │
│ (扫描+卸载) │   │ (残留扫描+清除)  │◀──联动───────│  (大文件迁移) │
└─────┬───────┘   └────────┬─────────┘              └──────┬───────┘
      │                    │                               │
      │ 卸载后触发残留扫描   │                              │ 调用迁移引擎
      ▼                    ▼                               ▼
              ┌──────────────────────────┐        ┌──────────────────┐
              │  Migration (文件夹迁移)  │◀───────│  (CSlim/CSLIM-02) │
              └────────────┬─────────────┘        └──────────────────┘
                           │ 复制+校验+联接
                           │ 失败/成功均走回滚
                           ▼
              ┌──────────────────────────┐
              │  Cleanup (垃圾清理)       │  独立模块，复用回滚引擎
              └────────────┬─────────────┘
                           │
                           ▼
       ┌───────────────────────────────────────────────────┐
       │  L3: Rollback Engine + Safety Classifier          │
       │       + DestructiveOpQueue + NetworkWhitelist     │
       │       + RuleSetManager + OperationHistory         │
       │       + TrustBroadcaster (TRUST-01 直播)         │
       └────────────────────┬──────────────────────────────┘
                            │
                            ▼
       ┌───────────────────────────────────────────────────┐
       │  L4: Registry / FileSystemSafe / WmiBridge /       │
       │      ServiceController / TaskScheduler /           │
       │      FirewallRule / VssBridge                      │
       └───────────────────────────────────────────────────┘
```

**关键耦合点**：
- `Uninstall` 卸载完成后通过 `IModuleCoordinator.TriggerResidualScan(softwareId)` 调用 `Residual`，不直接 new 对方
- `CSlim` 的大文件迁移（FR-CSLIM-02）通过 `IMigrationService.MigrateFolderAsync` 调用 `Migration`，迁移走回滚引擎
- 所有破坏性操作通过 `IDestructiveOpQueue.Enqueue(plan)` 入队，由队列调度 `IRollbackEngine.Snapshot(plan) → Execute → OnFailure → Rollback`

### 1.4 进程与权限模型

- **单进程架构**：sysmgr 是单进程 WPF 应用（V1-V2 不引入多进程）；V3 家庭多机远程管理可能引入后台 Agent 子进程，V1 不预留路径。
- **权限**：启动即请求管理员权限（app.manifest `requestedExecutionLevel level="requireAdministrator"`）。便携版通过 UAC 提权；安装版通过服务或 ComElevation 显式提权（V1 用 UAC 提权，不安装服务以保持绿色）。
- **线程模型**：
  - UI 线程：仅 WPF 调度，禁止阻塞调用（扫描/哈希/回滚等一律 await）
  - 扫描线程：`ThreadPool` + `IAsyncEnumerable<T>` 流式返回，避免大集合 OOM
  - 破坏性操作串行队列：单线程 `Channel<T>` 消费者，全局唯一
  - AI 推理线程（V3）：独立 `Task` + `Progress<T>` 回调 UI
- **取消**：所有长任务实现 `IAsyncEnumerable` + `CancellationToken`；用户点"停止"立即停止扫描，已入队破坏性操作不可中断（避免半完成的破坏态）。
- **崩溃恢复**：进程启动时扫描 `<DataDir>\Rollback\` 下未关闭的 OpId（无 `committed.json` 标记的），标记为"待回滚 - 上次异常退出"，提示用户在历史与回滚界面处理。

---

## 2. 横切层契约（L3）

> 横切层是 sysmgr 的"宪法"层：所有功能模块必须通过这些契约与系统交互，不得绕过。本节定义每个横切组件的接口骨架与不变量。

### 2.1 回滚引擎（FR-ROLLBACK-01/02/03/04）

**职责**：破坏性操作执行前生成快照、执行后落盘元数据、失败时回滚、空间 LRU 管理、失败兜底报告。

**核心接口**：

```csharp
namespace SysMgr.Core.Rollback;

public interface IRollbackEngine
{
    /// <summary>为待执行的操作计划生成快照（文件副本/.reg/JSON），返回快照句柄。调用者必须先 Snapshot 再 Execute。</summary>
    Task<SnapshotHandle> CreateSnapshotAsync(OperationPlan plan, CancellationToken ct);

    /// <summary>操作执行成功后调用，落盘 committed.json 标记快照可被 LRU 清理。</summary>
    Task CommitAsync(SnapshotHandle handle, CancellationToken ct);

    /// <summary>操作执行失败或用户主动回滚时调用。先校验目标环境未冲突，再恢复。</summary>
    Task<RollbackResult> RollbackAsync(Guid opId, CancellationToken ct);

    /// <summary>枚举所有可回滚操作（用于历史与回滚界面）。</summary>
    IAsyncEnumerable<RollbackEntry> EnumerateHistoryAsync(CancellationToken ct);
}

public sealed record SnapshotHandle(Guid OpId, string SnapshotDir, RollbackStrategy Strategy);
public enum RollbackStrategy { Copy, VssShadow, PathRecordOnly }

public sealed record RollbackResult(bool Success, string? FailureReportPath, IReadOnlyList<string> Conflicts);
```

**大文件策略决策表（FR-ROLLBACK-01 大文件回滚）**：

| 单文件大小 | VSS 可用 | 策略 | 说明 |
| --- | --- | --- | --- |
| < 500MB（阈值可配） | — | `Copy` | 复制到 Rollback 目录 |
| ≥ 500MB | 是 | `VssShadow` | 创建卷影副本，记录 ShadowCopyId，回滚时从卷影恢复 |
| ≥ 500MB | 否（如非 NTFS、VSS 服务禁用） | `PathRecordOnly` | 仅记录原路径 + SHA-256，回滚时若文件仍在原位且校验通过则恢复，否则标记"原文件不存在，仅占位" |

**LRU 清理策略（FR-ROLLBACK-03）**：
- 触发条件：单次 OpId 快照超过 1GB 或保留超过 14 天，**任一先到即清理**
- 清理顺序：按 `CreatedAt` 升序（最旧优先），逐个删除直到空间低于阈值
- **不可清理项**：标记为 `RollbackFailed_NeedsManual` 的快照（FR-ROLLBACK-04 兜底）跳过 LRU，等用户手动确认

**失败兜底流程（FR-ROLLBACK-04）**：
1. 回滚执行失败 → 生成 `rollback_failed_<timestamp>.txt` 到快照目录，含 OpId / 失败项清单 / 原快照路径 / SHA-256 / 目标路径 / 失败原因
2. 报告复制一份到用户桌面，文件名标记 `_待人工恢复`
3. 历史与回滚界面该项标记 `RollbackFailed_NeedsManual`，提供报告打开入口
4. 跳过 LRU 清理，等用户手动确认

### 2.2 破坏性操作全局串行队列（§4.3）

**职责**：所有破坏性操作（删除/迁移/注册表修改/服务变更/粉碎）全局串行执行；非破坏性扫描可并行。

**核心接口**：

```csharp
namespace SysMgr.Core.Queue;

public interface IDestructiveOpQueue
{
    /// <summary>入队破坏性操作计划，返回可 await 的任务（完成即操作结束）。</summary>
    Task<OperationResult> EnqueueAsync(OperationPlan plan, IProgress<OperationProgress>? progress, CancellationToken ct);
}
```

**实现约束**：
- 内部 `System.Threading.Channels.Channel<QueueItem>`，单消费者任务，容量无上限（背压由调用方 await 处理）
- 每个队列项生命周期：`Snapshot → ExecutePlan → OnSuccess(Commit) / OnFailure(Rollback) → Broadcast(TrustPanel+History)`
- **不允许并发执行破坏性操作**的原因：① 快照冲突（两个操作同时快照同一文件）② 文件占用（A 在删除 B 想复制的文件）③ 回滚错乱（无法判定哪个操作失败该回滚什么）
- 队列项可携带"取消令牌"，但**一旦进入 Snapshot 阶段即不可取消**（避免半完成态）；Execute 阶段可取消但会触发 Rollback

**与扫描并行的边界**：扫描（`IScanService.ScanAsync`）不进队列，可并发；扫描结果转成 `OperationPlan` 后才入队。体检（FR-CHECK-01）并行触发五个扫描器，扫描结果转成计划后依次入队。

### 2.3 安全等级判定（§3.0）

**职责**：对扫描结果项（文件/注册表项/服务/计划任务）打安全等级（高/中/低），全局唯一口径，模块不得自定义冲突规则。

**核心接口**：

```csharp
namespace SysMgr.Core.Safety;

public interface ISafetyClassifier
{
    SafetyVerdict Classify(ScannedItem item);
}

public enum SafetyLevel { High, Medium, Low }
public sealed record SafetyVerdict(SafetyLevel Level, string Reason, IReadOnlyList<string> Evidence);

public abstract record ScannedItem(string Path, ItemType Type);
public record FileItem(string Path, long Size) : ScannedItem(Path, ItemType.File);
public record RegistryItem(string KeyPath, string? ValueName) : ScannedItem(KeyPath, ItemType.Registry);
public record ServiceItem(string ServiceName) : ScannedItem(ServiceName, ItemType.Service);
public record ScheduledTaskItem(string TaskPath) : ScannedItem(TaskPath, ItemType.ScheduledTask);
```

**判定流程（§3.0）**：
1. 命中系统关键白名单路径（如 `C:\Windows\System32`、`HKLM\SYSTEM\CurrentControlSet`）→ 直接 `Low`
2. 被其他已装软件的注册表/快捷方式引用 → `Medium`（除非明确为软件私有残留）
3. 可由重装或系统机制重建 + 不被系统引用 → `High`
4. 综合给出等级与 `Reason` + `Evidence`（用于 TRUST-02 预演面板展示）

**用户调整约束**：UI 允许用户调整等级，**仅允许上调不允许下调**（避免误把高危项降为中/高后默认勾选删除）。下调需用户切换"高级模式"并二次确认。

**规则可外置**：系统关键白名单路径表外置为 `rules\safety_critical_paths.json`，便于无重新编译更新（FR 可维护性 4.5）。

### 2.4 联网白名单（§2.4）

**职责**：运行期禁止任何非白名单域名的出站请求；命中白名单的请求需经统一网关；非白名单出站尝试本地拦截并落盘告警。

**核心接口**：

```csharp
namespace SysMgr.Core.Network;

public interface INetworkWhitelist
{
    /// <summary>注册一次联网请求，必须命中白名单类别，否则抛 UnauthorizedNetworkAccessException。</summary>
    Task<HttpResponseMessage> RequestAsync(WhitelistScope scope, Uri endpoint, HttpRequestMessage req, CancellationToken ct);

    /// <summary>是否允许某 scope（用户可能在设置中关闭某项）。</summary>
    bool IsAllowed(WhitelistScope scope);
}

public enum WhitelistScope
{
    UpdateCheck,        // §2.4.1 检查更新（便携版手动触发，不自动）
    PopupRuleUpdate,    // §2.4.2 弹窗规则库更新
    TrustSelfTest,      // §2.4.3 自我断网验证端点
    CloudAiAndWallet,   // §2.4.4 云端 AI + 钱包校验 + 模型文件下载
}
```

**实现策略（V1 阶段）**：
- 不写 LSP / 不挂驱动；通过 `HttpClientFactory` + 单一 `ManagedHttpClient` 强制所有出站走 `INetworkWhitelist.RequestAsync`，绕过即视为 bug
- 进程内安装一个 `SelfDisconnectFilter`（FR-TRUST-03）：开启时调 `netsh advfirewall firewall add rule name="sysmgr_block_out" dir=out program="<self path>" action=block`，关闭时移除该规则
- **L1-2 层不允许直接 new HttpClient**，编译期通过 BannedApiAnalyzer（Roslyn analyzer）警告，运行期 `ManagedHttpClient` 单例托管全部出站
- 非白名单出站尝试：理论上不存在（无其他出站点），但保留兜底——通过 `ETW System.Network` 监听出站连接，命中非白名单远端 IP 即落盘 `WARN` 日志并提示用户

**白名单域名清单（V1）**：
| Scope | 域名 | 用途 |
| --- | --- | --- |
| UpdateCheck | `update.sysmgr.local`（待定） | 版本检查 |
| PopupRuleUpdate | V2 落地 | 弹窗规则 JSON 下载 |
| TrustSelfTest | `selftest.sysmgr.local`（固定） | 断网验证端点 |
| CloudAiAndWallet | `ai.sysmgr.local` + `wallet.sysmgr.local` | V3 落地 |

> 实际域名在 V1 发布前由运维确定，本文档仅占位。

### 2.5 规则集管理

**职责**：所有外置规则（安全等级路径表、弹窗规则、软件缓存路径表、安全等级规则、CLI-03 YAML 规则集）统一加载/热更新/版本管理。

**核心接口**：

```csharp
namespace SysMgr.Core.Rules;

public interface IRuleSetManager
{
    Task<TRuleSet> LoadAsync<TRuleSet>(CancellationToken ct) where TRuleSet : IRuleSet;
    Task<RuleSetVersion> CheckUpdateAsync(RuleSetKind kind, CancellationToken ct);  // 仅 PopupRuleUpdate 允许联网
    event EventHandler<RuleSetChangedEventArgs> RuleSetChanged;  // 热更新通知
}
```

**规则集清单（V1）**：
| 文件 | 格式 | 内容 | V1 落地 |
| --- | --- | --- | --- |
| `rules\safety_critical_paths.json` | JSON | 系统关键路径白名单（§3.0） | 是 |
| `rules\cleanup_targets.json` | JSON | 垃圾清理目标路径（§3.6） | 是 |
| `rules\uninstall_vendors.json` | JSON | 软件厂商特征映射（§3.2 长期未用判定辅助） | 是 |
| `rules\popup_rules.json` | JSON | 弹窗拦截规则（§3.9） | V2 |
| `rules\*.yaml` | YAML | CLI-03 配置即代码规则集 | V1.5 |

**热更新**：规则集文件变更后 `IRuleSetManager` 触发 `RuleSetChanged` 事件，模块订阅刷新内存索引；运行中扫描使用旧版本完成（避免半路换规则导致结果不一致），下一次扫描使用新版本。

### 2.6 日志、遥测与操作历史

**职责**：分级日志落盘、操作历史落盘 JSONL（可回放）、TRUST-01 操作直播面板广播、AI 行为审计。

**核心接口**：

```csharp
namespace SysMgr.Core.Logging;

public interface IOperationLogger
{
    void LogOperation(OperationLogEntry entry);  // 落盘 history.jsonl
    IObservable<OperationLogEntry> LiveStream { get; }  // TRUST-01 直播面板订阅
}

public interface IAuditLogger  // FR-TRUST-04 + FR-AI-07
{
    void AppendAiTrace(AiTraceEntry entry);  // 落盘 ai_trace.jsonl
    void AppendBilling(BillingEntry entry);  // 落盘 ai_billing.jsonl（V3）
}
```

**日志分级**：
- `DEBUG` / `INFO` / `WARN` / `ERROR` 写 `%LocalAppData%\sysmgr\logs\sysmgr.<date>.log`，保留 7 天
- 操作历史 `history.jsonl`（FR-MONITOR-02 + TRUST-01 落盘），**永不自动清理**（用户主动才行）
- AI trace `ai_trace.jsonl` 保留 90 天，AI billing `ai_billing.jsonl` 保留 2 年（§5.3）

**TRUST-01 直播协议**：
- `IOperationLogger.LiveStream` 是 `IObservable<OperationLogEntry>`，UI 订阅后 push 到面板
- 面板支持"暂停滚动 / 清屏 / 导出"，但**不停止落盘**（落盘是 TRUST-04 行为级可验证的硬约束）
- `OperationLogEntry` 格式公开（§5.1 详），第三方可写解析工具核验（FR-TRUST-04 第 1 层证据）

**遥测**：默认关闭。即使开启，**仅本地匿名统计，不联网**（FR-SETTINGS-01）；统计结果只在本地展示，不导出不联网。

---

## 3. 功能模块层契约骨架（L2）

> 本节定义每个功能模块对外暴露的 Service 接口签名与核心数据类型，是模块间解耦的契约边界。逐模块伪代码级详设见 V1 MVP 详设批次文档，本节只到接口与数据结构层。

### 3.1 模块统一基类与生命周期

所有 L2 模块实现 `IModuleService`，由 DI 容器在启动时统一初始化：

```csharp
namespace SysMgr.Modules;

public interface IModuleService
{
    string ModuleId { get; }              // e.g. "uninstall"
    string DisplayName { get; }           // e.g. "软件卸载"
    ModuleVersion Version { get; }       // 对齐 requirements §6 版本规划

    Task InitializeAsync(IModuleContext ctx, CancellationToken ct);  // 加载规则、注册菜单
    Task<ModuleHealthReport> HealthCheckAsync(CancellationToken ct);  // 体检模块调度
}
```

`IModuleContext` 横切层句柄注入：

```csharp
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
}
```

### 3.2 V1 模块契约签名清单

> 仅列 V1 落地模块；V1.5/V2/V3 模块的契约随版本演进追加。

| 模块 | 接口 | 关键方法 |
| --- | --- | --- |
| Uninstall | `IUninstallService` | `ScanInstalledAsync` → `GetUninstallCommandAsync` → `EnqueueUninstallAsync` |
| Residual | `IResidualService` | `ScanResidualAsync(softwareId)` → `EnqueueCleanupAsync(items)` |
| CSlim | `ICSlimService` | `AnalyzeCDriveAsync` → `FindLargeFilesAsync(threshold)` → `EnqueueMigrationAsync` |
| Cleanup | `ICleanupService` | `ScanJunkAsync(scope)` → `EnqueueCleanAsync(items)` |
| Migration | `IMigrationService` | `MigrateFolderAsync(src, dst, MigrationOptions)` → 返回走 OpQueue |
| Settings | `ISettingsService` | `GetAsync` / `UpdateAsync` / `ResetAsync` |
| Trust | `ITrustService` | `EnableSelfDisconnectAsync` / `DisableSelfDisconnectAsync` / `PreviewOperationAsync(plan)` |
| Timeline | `ITimelineService` | `RunBenchmarkAsync` → `GetTrendAsync(days)` |
| Cli | （L1，无 Service 接口） | 入口直接调用各模块 Service |

### 3.3 模块间协作协议（IModuleCoordinator）

模块间不允许直接 new 对方 Service，统一走协调器：

```csharp
namespace SysMgr.Modules;

public interface IModuleCoordinator
{
    Task TriggerResidualScanAsync(Guid uninstallOpId);  // 卸载完成后调
    Task TriggerTimelineSnapshotAsync();                 // 关键变更后调（TM-01，V2）
    Task TriggerAiDiagnosisAsync(string query);          // V3
}
```

实现位于 L1 启动初始化阶段，DI 注入所有模块引用；模块通过 `IModuleContext` 拿到协调器引用。

### 3.4 扫描结果统一数据契约

所有扫描模块返回 `IAsyncEnumerable<ScannedItem>` 流式输出，避免大集合 OOM：

```csharp
public interface IScanService
{
    IAsyncEnumerable<ScannedItem> ScanAsync(ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken ct);
}

public sealed record ScanOptions(
    string? SoftwareId,        // Residual 扫描时绑定软件
    long? MinFileSize,         // CSlim 大文件阈值
    string[]? IncludePaths,
    string[]? ExcludePaths,   // 默认走 §3.4 FR-CSLIM-02 排除清单
    SafetyLevel? MinLevel);    // 默认 High
```

UI 通过 `IAsyncEnumerable` + `CancellationToken` 实现"边扫描边显示 + 随时停止"。

---

## 4. 数据模型与存储映射

> 本节将 requirements §5.3 的存储路径表扩展为 schema 与读写规则，是 L3/L4 持久化层的契约。

### 4.1 全局配置 config.json

路径：`%LocalAppData%\sysmgr\config.json`

```jsonc
{
  "schemaVersion": 1,
  "language": "zh-CN",
  "theme": "Auto",
  "rollback": {
    "directory": "%ProgramData%\\sysmgr\\Rollback",
    "maxSizeMb": 1024,
    "maxAgeDays": 14,
    "largeFileThresholdMb": 500
  },
  "network": {
    "scopes": { "UpdateCheck": true, "PopupRuleUpdate": true, "CloudAiAndWallet": false }
  },
  "telemetry": { "enabled": false }
}
```

读写约束：原子写入（`File.WriteAllText(tmp); File.Move(tmp, final)`）；便携版配置随程序目录，不写 `%LocalAppData%`（保持零残留）。

### 4.2 OperationPlan JSON Schema（跨模块协议核心）

所有破坏性操作入队前必须构造 `OperationPlan`，是 TRUST-02 预演、回滚快照、TRUST-01 直播、AI 操作建议的统一数据载体：

```jsonc
{
  "planId": "guid",
  "moduleId": "uninstall",
  "frId": "FR-UNINSTALL-04",
  "description": "强制卸载 FooBar 1.2",
  "safetyLevel": "Medium",
  "actions": [
    {
      "kind": "DeleteFile",
      "target": "C:\\Program Files\\FooBar\\foo.exe",
      "sizeBytes": 5242880,
      "sha256": "..."
    },
    {
      "kind": "DeleteRegistry",
      "key": "HKLM\\SOFTWARE\\FooBar",
      "valueName": null
    },
    {
      "kind": "StopService",
      "serviceName": "FooBarSvc"
    }
  ],
  "previewRequired": true,
  "userConfirmed": false
}
```

**ActionKind 枚举**：`DeleteFile` / `MoveFile` / `DeleteRegistry` / `SetRegistry` / `StopService` / `DisableService` / `DisableStartup` / `DeleteScheduledTask` / `Shred`（不走回滚）。

**`previewRequired`**：除 Shred 外所有 ActionKind 均为 `true`，TRUST-02 预演面板必须展示完整 actions 列表，用户点"确认执行"才设置 `userConfirmed=true` 入 OpQueue。

### 4.3 回滚快照目录结构

```
%ProgramData%\sysmgr\Rollback\<OpId>\
  ├── meta.json              # 元数据：OpId, 时间, moduleId, plan JSON, strategy
  ├── files\                 # Copy 策略的文件副本（保持相对路径）
  │   └── Program Files\FooBar\foo.exe
  ├── registry\*.reg         # 注册表导出（按 hive 分文件）
  ├── config\*.json          # 服务/启动项/计划任务配置序列化
  ├── vss\                   # VssShadow 策略的 ShadowCopyId 记录
  │   └── shadow.json        # { "shadowId": "...", "created": "..." }
  ├── committed.json         # 操作成功提交后才创建，标记可被 LRU 清理
  └── rollback_failed_<ts>.txt   # 仅回滚失败时存在（FR-ROLLBACK-04）
```

**meta.json schema**：

```jsonc
{
  "opId": "guid",
  "createdAt": "2026-09-28T10:00:00Z",
  "moduleId": "uninstall",
  "planRef": "<planId>",
  "strategy": "Copy|VssShadow|PathRecordOnly",
  "totalSizeBytes": 10485760,
  "status": "Active|Committed|RolledBack|FailedNeedsManual"
}
```

### 4.4 操作历史 history.jsonl（JSON Lines）

每行一条 `OperationLogEntry`，公开格式（FR-TRUST-04 第 1 层证据）：

```jsonc
{
  "opId": "guid",
  "ts": "2026-09-28T10:00:00.123Z",
  "moduleId": "uninstall",
  "frId": "FR-UNINSTALL-04",
  "kind": "DeleteFile",
  "target": "C:\\Program Files\\FooBar\\foo.exe",
  "originalValue": null,
  "newValue": null,
  "status": "Success|Failed|RolledBack",
  "durationMs": 1234,
  "error": null
}
```

**永不自动清理**（用户主动才行）；追加写入（append-only）；第三方可写解析工具核验。

### 4.5 Timeline 与性能基准

- `Timeline\<YYYYMMDD>\snapshot.json`：定期配置快照（TM-01，V2）；V1 只落 `benchmark.json`（TM-04 性能基准曲线）
- `benchmark.json` schema：

```jsonc
{
  "samples": [
    { "ts": "2026-09-28T10:00:00Z", "bootMs": 23400, "cpuIdle": 0.92, "diskRespMs": 12, "cDriveFreeMb": 51200 }
  ]
}
```

采样频率：每周一次（V1）；可配置为每天。

### 4.6 AI 数据文件（V3，占位）

- `ai_memory\`：本地 KV + 向量索引，永不联网
- `ai_trace.jsonl`：每次 AI 解析与建议落盘，90 天保留
- `ai_billing.jsonl`：每次云端调用 token 明细，2 年保留
- `models\`：本地 AI 模型文件（用户自供或官方下载）

具体 schema 在 V3 落地前再细化，V1 不预留实现路径（仅命名空间占位）。

---

## 5. 关键接口与跨模块协议

### 5.1 TRUST-01 操作直播面板协议

`ITrustBroadcaster` 是横切层入口，所有模块执行操作时通过它广播：

```csharp
public interface ITrustBroadcaster
{
    void Broadcast(OperationLogEntry entry);     // 落盘 history.jsonl + 推送 LiveStream
    Task<PreviewResult> PreviewAsync(OperationPlan plan, CancellationToken ct);  // TRUST-02
    Task<bool> SelfTestDisconnectAsync(CancellationToken ct);  // TRUST-03
}
```

UI 主面板订阅 `IOperationLogger.LiveStream`，订阅后所有 `Broadcast` 调用实时推送；UI"暂停滚动"仅暂停渲染，不停止 `Broadcast`/落盘。

### 5.2 TRUST-02 操作前预演协议

任何破坏性操作入队前必须经 `PreviewAsync`：

1. 模块构造 `OperationPlan`（含所有 actions）
2. 调用 `ITrustBroadcaster.PreviewAsync(plan)` → 弹出预演面板逐项展示 actions + 安全等级 + 影响
3. 用户审核点"确认执行" → `plan.userConfirmed = true`
4. 入 `IDestructiveOpQueue.EnqueueAsync(plan)`
5. 队列消费时 `IRollbackEngine.CreateSnapshotAsync(plan) → 执行 actions → Commit/Rollback → Broadcast`

**安全护栏**：若 plan 中存在 `SafetyLevel.Low` 项，预演面板默认显示但不勾选，需用户展开"高级模式"逐项勾选（FR-AI-01 安全护栏 + §3.0 用户调整约束的统一实现）。

### 5.3 回滚执行协议

`RollbackAsync(opId)` 流程：

1. 读 `<OpId>\meta.json`，校验 status ∈ {Active, Committed, FailedNeedsManual}
2. 对当前环境做冲突检测：
   - 文件类：目标路径是否已被新文件占用 → 返回 `Conflicts`
   - 注册表类：目标键是否已被修改 → 返回 `Conflicts`
3. 按 strategy 恢复：
   - `Copy`：从 `files\` 复制回原路径
   - `VssShadow`：从卷影副本恢复
   - `PathRecordOnly`：校验原路径文件仍在 + SHA-256 匹配则跳过，否则标记"原文件已不存在"
4. 全部成功 → status = `RolledBack`；任一失败 → 生成 `rollback_failed_<ts>.txt` + 复制到桌面 + status = `FailedNeedsManual` + 跳过 LRU（FR-ROLLBACK-04）

### 5.4 AI 操作计划协议（V3，占位）

AI 生成的"建议清单"必须落地为 §4.2 的 `OperationPlan` JSON，强制走 TRUST-02 预演，AI 永不直接调用 `OpQueue.EnqueueAsync`。`previewRequired` 字段对 AI 生成的 plan 强制为 `true`，违例即视为安全漏洞（§8 AI 误判风险）。

---

## 6. 系统访问层封装策略（L4）

> L4 是 Win32 API / WMI / COM / NuGet 库的统一封装边界。原则：① 错误码统一转 SysMgrException；② 长路径 / 占用文件 / 权限不足有统一兜底；③ 所有 P/Invoke 集中在 SysMgr.Infra.Native，便于审计。

### 6.1 L4 组件清单与封装策略

| 组件 | 用途 | 封装方式 | 关键 API | V1 落地 |
| --- | --- | --- | --- | --- |
| `RegistryFs` | 注册表读写/导出/枚举 | `Microsoft.Win32.Registry` + `reg export` 子进程 | `Registry.LocalMachine.OpenSubKey` | 是 |
| `FileSystemSafe` | 长路径/占用检测/重试/校验 | `System.IO` + P/Invoke `CreateFileW` (long path) | `Kernel32.CreateFileW` with `\\?\` prefix | 是 |
| `WmiBridge` | 软件清单/磁盘 SMART 部分 | `System.Management` NuGet | `SELECT * FROM Win32_Product` (慎用) / `Win32_DiskDrive` | 是 |
| `ServiceControllerFacade` | 服务管理 | `System.ServiceProcess.ServiceController` NuGet | `ServiceController` | V1.5（V1 仅扫描软件时识别服务） |
| `TaskSchedulerFacade` | 计划任务管理 | `Microsoft.Win32.TaskScheduler` NuGet | `TaskService.Instance` | V1.5 |
| `UwpDeploymentFacade` | UWP/商店应用卸载 | WinRT `Windows.Management.Deployment.PackageManager` | `PackageManager.RemovePackageAsync` | V1.5 |
| `FirewallRuleFacade` | 自我断网规则 | `netsh advfirewall firewall` 子进程 | `netsh ... add rule dir=out program=... action=block` | 是（TRUST-03） |
| `VssBridge` | 大文件回滚卷影副本 | `Microsoft.VisualStudio.OLE.Interop` 或调用 `vssadmin` 子进程 | `IVssBackupComponents` (COM) | V1（FR-ROLLBACK-01 大文件策略） |
| `WinEventHookFacade` | 弹窗事件驱动监听 | P/Invoke `user32!SetWinEventHook` | `SetWinEventHook(EVENT_OBJECT_CREATE)` | V2 |
| `MiniFilterFacade` | 反捆绑文件系统监控 | 自研 minifilter driver（C/C++）+ ETW | `FltRegisterFilter` | V2（V1 预研） |
| `SmartDiskFacade` | S.M.A.R.T. 读取 | WMI `MSStorageDriver_PredictFailure` + P/Invoke `DeviceIoControl` `IOCTL_STORAGE_QUERY_PROPERTY` | `DeviceIoControl` | V1.5 |
| `ProcessFacade` | 进程枚举/结束 | `System.Diagnostics.Process` + `taskkill /F /T` | `Process.GetProcesses` | 是 |

### 6.2 统一错误模型

所有 L4 失败转 `SysMgrException`，子类按场景：

```csharp
namespace SysMgr.Infra;

public class SysMgrException : Exception
{
    public ErrorCode Code { get; }
    public bool IsRetryable { get; }
    public string? SystemPath { get; }
}

public enum ErrorCode
{
    FileLocked,           // 文件被占用 → 提示结束进程或重试
    PathTooLong,          // 超过 260 字符 → 自动转 \\?\ 前缀重试
    AccessDenied,         // 权限不足 → 提示以管理员运行
    RegistryKeyMissing,
    ServiceNotFound,
    VssUnavailable,       // VSS 服务不可用 → 降级 PathRecordOnly
    NetshCommandFailed,   // Firewall 规则失败 → 降级提示而非静默
    UninstallStringBroken // 卸载字符串损坏 → 触发强制卸载
}
```

`IsRetryable=true` 的错误在 OpQueue 内置 3 次退避重试（指数退避，1s/2s/4s）。

### 6.3 长路径与占用文件统一处理

`FileSystemSafe` 强制：
- 所有路径调用前 `NormalizeLongPath(p)` → 加 `\\?\` 前缀（支持 32K 字符路径），兼容 NTFS 长路径
- 删除前 `IsFileLocked(p)` 通过 `CreateFileW(GENERIC_WRITE, FILE_SHARE_NONE)` 试开，失败返回占用进程 PID
- 用户可选"先结束占用进程"（FR-SHRED-01 / FR-UNINSTALL-02 / FR-CLEAN-02），统一走 `ProcessFacade.KillProcessTree(pid)`

### 6.4 P/Invoke 集中原则

- 所有 `[DllImport]` 必须集中在 `SysMgr.Infra.Native` 命名空间，文件命名 `<Win32Module>Native.cs`（如 `Kernel32Native.cs`、`User32Native.cs`、`Advapi32Native.cs`）
- 禁止 L2/L3 直接写 P/Invoke，编译期通过 Roslyn analyzer 警告
- 每个签名必须有 XML 注释说明对应 Win32 API、是否 Unicode、错误处理方式

### 6.5 子进程调用规范

`netsh` / `reg` / `vssadmin` / `taskkill` / `powercfg` 等子进程统一封装：

```csharp
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(ProcessRunSpec spec, CancellationToken ct);
    Task<IAsyncEnumerable<string>> StreamOutputAsync(ProcessRunSpec spec, CancellationToken ct);
}
```

约束：① 必须传 `CancellationToken`；② 标准 stdout/stderr 重定向，不弹窗；③ 退出码 != 0 转 `SysMgrException(Code=<对应>)`；④ 超时默认 30s 可配。

---

## 7. 打包与发布

### 7.1 项目结构与发布 Profile

```
sysmgr.sln
├── src/
│   ├── SysMgr.App/                 # L1 WPF 主应用（exe）+ sysmgr-cli 共用入口
│   │   ├── SysMgr.App.csproj       # OutputType=WinExe, self-contained, single-file
│   │   └── PublishProfiles/
│   │       ├── Portable.pubxml     # 便携版（≤ 8MB 目标）
│   │       ├── Installed.pubxml    # 安装版
│   │       └── Store.pubxml       # Microsoft Store（MSIX 打包，走 Store 流程）
│   ├── SysMgr.Core/               # L3 横切层（类库）
│   ├── SysMgr.Modules/            # L2 功能模块（类库，按子命名空间分模块）
│   ├── SysMgr.Infra/              # L4 系统访问层封装（类库）
│   └── SysMgr.Infra.Native/       # P/Invoke 集中（类库，内部）
├── tests/
│   ├── SysMgr.Core.Tests/
│   ├── SysMgr.Modules.Tests/
│   └── SysMgr.Infra.Tests/
├── rules/                          # 外置规则集（构建时复制到输出）
└── .github/workflows/ci.yml       # CI
```

### 7.2 单文件自包含配置

`SysMgr.App.csproj` 关键属性：

```xml
<PropertyGroup>
  <TargetFramework>net8.0-windows</TargetFramework>
  <UseWPF>true</UseWPF>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <SelfContained>true</SelfContained>
  <PublishSingleFile>true</PublishSingleFile>
  <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
  <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  <InvariantGlobalization>false</InvariantGlobalization>
  <SatelliteResourceLanguages>zh-CN</SatelliteResourceLanguages>
</PropertyGroup>
```

便携版目标体积 ≤ 8MB（FR-CLI-01）。压缩与依赖裁剪策略：
- `EnableCompressionInSingleFile=true`（默认 deflate）
- 移除未使用 BCL（`<TrimMode>full</TrimMode>` 谨慎，因 WPF 反射敏感，需测试）
- 资源仅保留 zh-CN
- WPF-UI 按需引入控件，避免全量

> 风险：WPF + 单文件 + Trim 兼容性需 V1 阶段实测，详见 §8。

### 7.3 三种分发形态差异

| 形态 | 入口 | 更新机制 | 右键菜单 | 配置位置 | 联网默认 |
| --- | --- | --- | --- | --- | --- |
| 便携版 | 单 exe | 手动检查+提示下载链接（不自动下载） | 不安装 | exe 同目录 `sysmgr_portable.json` | UpdateCheck 默认关 |
| 安装版 | exe + MSI 安装器 | 安装器检查更新 | 可选安装 | `%LocalAppData%\sysmgr\` | UpdateCheck 默认开 |
| Store 版 | MSIX | Store 自动接管 | Store 沙箱限制 | 应用容器内 | UpdateCheck 走 Store |

便携版"零残留"约束（FR-CLI-01）：
- 不写注册表
- 不安装服务
- 配置文件随 exe 同目录
- 退出后不留 `%TEMP%` 外的文件
- 不安装右键菜单（FR-SHRED-03 已声明）

### 7.4 版本号策略

`{Major}.{Minor}.{Patch}` + 预发布标识符（如 `1.0.0-rc.1`）。CI 构建时从 git tag 注入。版本对齐 requirements §6：V1 = 1.0.x，V1.5 = 1.5.x，V2 = 2.0.x，V3 = 3.0.x。

### 7.5 CI workflow（ci.yml）

阶段：`restore → build → test → pack(portable) → pack(installed) → upload artifact`

```yaml
name: ci
on: [push, pull_request]
jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '8.0.x' }
      - run: dotnet restore sysmgr.sln
      - run: dotnet build sysmgr.sln -c Release --no-restore
      - run: dotnet test sysmgr.sln -c Release --no-build
      - run: dotnet publish src/SysMgr.App/SysMgr.App.csproj -c Release -p:PublishProfile=Portable --no-build
      - uses: actions/upload-artifact@v4
        with: { name: sysmgr-portable, path: 'src/SysMgr.App/bin/Release/net8.0-windows/win-x64/publish/' }
```

V1 阶段不引入签名；正式发布前需配置代码签名证书（不在本 TDD 范围）。

---

## 8. 技术风险与 V1 预研清单

### 8.1 V1 阶段需技术验证项（按风险排序）

| # | 风险项 | 验证方式 | 失败兜底 |
| --- | --- | --- | --- |
| R1 | WPF + 单文件自包含 + Trim 体积/兼容性 | 构建最小 WPF-UI 应用单文件发布，测体积与启动 | 关闭 Trim，接受体积略大；或拆为双文件（exe+runtime.zip） |
| R2 | 便携版 ≤ 8MB 硬约束 | 同上，实测 | 放宽至 15MB，更新 FR-CLI-01 文案 |

> **PoC 实测（2026-09-28）**：R1/R2 风险确认成立。最小脚手架（5 个项目 + WPF-UI 4.3.0 + App.xaml 引用 ThemesDictionary/ControlsDictionary）按 `dotnet publish -p:PublishProfile=Portable` 实测 `sysmgr.exe` 单文件体积 = **65.21 MB**，远超 FR-CLI-01 的 ≤ 8MB 硬约束（约 8 倍超标）。
>
> **根因**：体积大头是 .NET 8 runtime + WPF runtime 自包含（BCL + WPF 框架 ~50MB+），非 sysmgr 代码或 WPF-UI 本身开销。这是 WPF + .NET 自包含的固有成本，业界同类应用（.NET WPF 单文件）普遍 50-80MB。对比 C++ 原生工具：CCleaner 便携 ~30MB、Geek Uninstaller ~6MB——.NET + WPF 技术栈决定了 8MB 不可达。
>
> **建议处理（需用户决策）**：
> 1. **放宽 FR-CLI-01 至 ≤ 70MB**（推荐）：与业界 .NET WPF 自包含工具对齐，保持"单文件 + 不依赖预装 Runtime + 便携零残留"核心承诺不变；更新 requirements §3.15.6 FR-CLI-01 与 §2.3 文案。
> 2. **改框架依赖便携版**：`SelfContained=false` 后体积 ~5-10MB，但要求用户预装 .NET 8 Runtime，**违背 §2.3 "不依赖系统预装 Runtime"约束**，不推荐。
> 3. **拆双文件**（exe ~5MB + runtime 压缩包 ~30MB）：违背"真·单文件"承诺，不推荐。
> 4. **改技术栈**：.NET + WPF 已在 requirements §5.1 定稿，不可推翻。
| R3 | VSS 卷影副本 API 在不同 Windows 版本的可用性 | 用 `vssadmin create shadow` 在 Win10 1809/21H2/Win11 各测一次 | 降级 `PathRecordOnly` 策略 |
| R4 | `netsh advfirewall firewall add rule program=<self>` 提权后是否立即生效 | 在管理员进程内调用并实测出站阻断 | 改用 Windows Firewall COM API (`INetFwPolicy2`) |
| R5 | 长路径 `\\?\` 前缀在 .NET 8 的支持范围 | 跑 test fixture 覆盖 260+ 字符路径的删除/重命名 | 退至 `cmd /c del` 子进程 |
| R6 | WMI `Win32_Product` 查询慢（reconfigure 风险） | 改用注册表 Uninstall 键 + `Win32_InstalledWin32Program` | 仅在用户启用"高精度扫描"时使用 `Win32_Product` |
| R7 | 静默卸载参数兼容性（MSI/Inno/NSIS） | 收集样本安装包测参数识别 | 不识别则提示"无法静默"退回标准卸载 |

### 8.2 跨版本风险（提前记录，V1 不解）

- **V2 minifilter 驱动签名**：需 EV 代码签名证书 + Microsoft Hardware Dashboard 提交，开发周期长。V1 阶段做技术预研（写未签名驱动本地测试模式跑通），V2 落地前申请证书。
- **V3 家庭多机远程**：P2P/中转架构需安全评审，避免被滥用为"远程控制木马"（§8 FR-FAMILY 风险）。
- **V3 AI 钱包一致性**：分布式账本与客户端缓存的一致性需对账机制压测。

### 8.3 V1 不预留路径的明确约束（requirements §2.4 / §6 已声明）

- ARM64 构建：V1 不预留 csproj 配置；V1 发布后视 ARM64 Windows 设备市占再评估
- 多语言：i18n 架构按可扩展设计（资源文件外置），但 V1 仅交付 zh-CN 资源
- 多进程：V1-V2 单进程；V3 远程 Agent 视需求再引入

---

## 9. 模块到 FR 追溯映射

> 用于编码阶段快速定位"FR 由哪个模块/接口承载"。仅列 V1 FR；V1.5+ 随详设批次追加。

| FR 编号 | 模块 | 接口/契约 | TDD 章节 |
| --- | --- | --- | --- |
| FR-UNINSTALL-01/02/04 | Uninstall | `IUninstallService` | §3.2 |
| FR-RESIDUAL-01/02/03 | Residual | `IResidualService` + `IModuleCoordinator.TriggerResidualScanAsync` | §3.2/§3.3 |
| FR-CSLIM-01/02/05/08 | CSlim | `ICSlimService` | §3.2 |
| FR-CLEAN-01/02/06 | Cleanup | `ICleanupService` | §3.2 |
| FR-MIGRATE-01 | Migration | `IMigrationService` | §3.2 |
| FR-ROLLBACK-01/02/03/04 | Rollback (L3) | `IRollbackEngine` + `SnapshotHandle` + LRU + 失败兜底 | §2.1/§4.3/§5.3 |
| FR-SETTINGS-01/02 | Settings | `ISettingsService` + `IConfigStore` | §3.2/§4.1 |
| FR-TRUST-01 | Trust | `ITrustBroadcaster.Broadcast` + `IOperationLogger.LiveStream` | §2.6/§5.1 |
| FR-TRUST-02 | Trust | `ITrustBroadcaster.PreviewAsync` | §5.2 |
| FR-TRUST-03 | Trust | `ITrustService.EnableSelfDisconnectAsync` + `FirewallRuleFacade` | §2.4/§6.1 |
| FR-TRUST-04 | Trust (横切) | history.jsonl 公开 schema + 第三方审计 + 行为级可验证 | §4.4 |
| FR-CLI-01 (便携版) | App 打包 | `PublishProfiles/Portable.pubxml` + 便携版配置 | §7.1/§7.2/§7.3 |
| FR-TM-04 | Timeline | `ITimelineService.RunBenchmarkAsync` + `benchmark.json` | §4.5 |
| §3.0 安全等级 | Safety (L3) | `ISafetyClassifier.Classify` + `rules\safety_critical_paths.json` | §2.3 |
| §4.3 串行队列 | Queue (L3) | `IDestructiveOpQueue.EnqueueAsync` | §2.2 |
| §2.4 联网白名单 | Network (L3) | `INetworkWhitelist.RequestAsync` + `ManagedHttpClient` | §2.4/§6.1 |

--- 

> TDD v0.1 骨架完成。下一步：① 工程脚手架落地（§7 描述的项目结构）；② V1 MVP 详设批次（卸载/残留/C盘瘦身/垃圾清理/迁移、回滚/设置/TRUST/CLI-01/TM-04）。
