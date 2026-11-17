# 01 软件卸载模块详设（Uninstall）

| 项目 | 内容 |
| --- | --- |
| 文档版本 | v0.1（V1 详设批次 1 模块 1） |
| 编写日期 | 2026-09-28 |
| 模块命名空间 | `SysMgr.Modules.Uninstall` |
| 分层位置 | L2 功能模块层（TDD §1.2） |
| FR 追溯 | FR-UNINSTALL-01 软件清单展示 / FR-UNINSTALL-02 标准卸载 / FR-UNINSTALL-04 强制卸载 |
| 配套文档 | [requirements.md](../requirements.md) §3.2 / [technical-design.md](../technical-design.md) §2-§5 |
| 代码骨架 | [IUninstallService.cs](../../src/SysMgr.Modules/Uninstall/IUninstallService.cs) |

> 本文是 V1 MVP 详设批次 1 第 1 模块。前置横切层接口骨架（SysMgr.Core）与模块基类（SysMgr.Modules）已落盘并 build 通过，本模块接口与数据类已同步落到 [src/SysMgr.Modules/Uninstall/](../../src/SysMgr.Modules/Uninstall/)，sysmgr.sln 持续可编译。

---

## 1. 模块概览

### 1.1 职责

软件卸载模块负责扫描系统中已安装软件、识别卸载命令、执行标准/强制卸载，并在卸载后联动残留扫描模块。

### 1.2 V1 范围与非目标

| 范围内（V1） | 范围外 |
| --- | --- |
| FR-UNINSTALL-01 软件清单展示（含 UWP/Feature 扫描展示） | FR-UNINSTALL-03 静默卸载（Mode=Silent 抛 NotSupportedException） |
| FR-UNINSTALL-02 标准卸载（含强制结束进程开关） | FR-UNINSTALL-05 批量卸载（V1 单次单软件） |
| FR-UNINSTALL-04 强制卸载（清理目录+注册表+服务+计划任务） | UWP 卸载（走 UwpDeploymentFacade，V1.5） |
| 长期未用复合判定（Prefetch + InstallDate + 启动项） | Windows 可选功能卸载（走 DISM，V1.5） |

### 1.3 依赖

- **横切层（L3，已落骨架）**：[IRollbackEngine](../../src/SysMgr.Core/Rollback/IRollbackEngine.cs) / [IDestructiveOpQueue](../../src/SysMgr.Core/Queue/IDestructiveOpQueue.cs) / [ITrustBroadcaster](../../src/SysMgr.Core/Trust/ITrustBroadcaster.cs)（TRUST-02 预演）/ [IOperationLogger](../../src/SysMgr.Core/Logging/IOperationLogger.cs)（TRUST-01 直播）
- **L4 系统访问层**：RegistryFs（注册表枚举）、ProcessFacade（进程枚举/结束）、TaskSchedulerFacade（计划任务枚举，V1.5；V1 用 WMI 占位）、ServiceControllerFacade（服务枚举，V1.5；V1 用 WMI 占位）
- **规则集**：`rules\uninstall_vendors.json`（软件厂商特征映射，辅助长期未用判定与关联项收集）
- **模块间协作**：[IModuleCoordinator.TriggerResidualScanAsync](../../src/SysMgr.Modules/IModuleCoordinator.cs)（卸载完成后触发残留扫描，FR-RESIDUAL-03）

---

## 2. 接口契约

完整签名见 [IUninstallService.cs](../../src/SysMgr.Modules/Uninstall/IUninstallService.cs)。继承 [IModuleService](../../src/SysMgr.Modules/IModuleService.cs)（模块统一基类，DI 容器统一初始化）。

```csharp
public interface IUninstallService : IModuleService
{
    IAsyncEnumerable<UninstallItem> ScanInstalledAsync(IProgress<ScanProgress>? progress, CancellationToken ct);
    Task<UninstallCommand> GetUninstallCommandAsync(UninstallItem item, CancellationToken ct);
    Task<OperationResult> EnqueueUninstallAsync(UninstallItem item, UninstallOptions options, IProgress<OperationProgress>? progress, CancellationToken ct);
}
```

**方法链语义**（TDD §3.2）：`ScanInstalledAsync`（扫描清单）→ `GetUninstallCommandAsync`（获取卸载命令）→ `EnqueueUninstallAsync`（提交卸载任务）。UI 先扫描得到清单，用户选定某项后调 GetUninstallCommandAsync 决定可用模式，用户确认后调 EnqueueUninstallAsync 入队。

---

## 3. 数据结构

### 3.1 软件条目 UninstallItem

详见 [UninstallItem.cs](../../src/SysMgr.Modules/Uninstall/UninstallItem.cs)。

| 字段 | 类型 | 说明 | FR |
| --- | --- | --- | --- |
| SoftwareId | Guid | 内部 ID（注册表键路径哈希或包名） | 01 |
| DisplayName | string | 显示名 | 01 |
| Publisher | string? | 发布者 | 01 |
| DisplayVersion | string? | 版本 | 01 |
| InstallDate | DateTimeOffset? | 注册表 InstallDate（格式 yyyymmdd 解析） | 01 |
| EstimatedSizeBytes | long? | 占用大小（注册表 EstimatedSize KB → 字节，或扫 InstallLocation 算） | 01 |
| InstallLocation | string? | 安装路径 | 01/04 |
| UninstallString | string? | 注册表 UninstallString | 02 |
| Source | SoftwareSource | 来源（RegistryX64/X86/User/Uwp/WindowsFeature） | 01 |
| IsSystemComponent | bool | 系统组件标记（注册表 SystemComponent=1），默认折叠隐藏 | 01 |
| IsLongUnused | bool | 长期未用标记（复合判定，见 §4.4） | 01 |
| LastUsedTime | DateTimeOffset? | Prefetch *.pf 最后写入时间 | 01 |
| HasStartupEntry | bool | 是否仍存在启动项 | 01 |

### 3.2 卸载命令 UninstallCommand

详见 [UninstallCommand.cs](../../src/SysMgr.Modules/Uninstall/UninstallCommand.cs)。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| SoftwareId | Guid | 关联软件 |
| UninstallString | string? | 注册表原值 |
| UninstallerType | UninstallerType | Msi/InnoSetup/Nsis/BasicExe/None/Unknown |
| IsSilentSupported | bool | 是否支持静默参数（FR-03，V1 占位） |
| SilentArgs | string? | 静默参数（/qn、/VERYSILENT、/S） |
| CanForceUninstall | bool | None 或损坏时 true，强制卸载入口启用 |
| InstallLocation | string? | 强制卸载要清理的目录 |
| RelatedRegistryKeys | IReadOnlyList&lt;string&gt; | 强制卸载要清理的注册表键 |
| RelatedServices | IReadOnlyList&lt;string&gt; | 强制卸载要停止的服务 |
| RelatedScheduledTasks | IReadOnlyList&lt;string&gt; | 强制卸载要删除的计划任务 |

### 3.3 枚举与选项

- [SoftwareSource](../../src/SysMgr.Modules/Uninstall/SoftwareSource.cs)：RegistryX64 / RegistryX86 / RegistryUser / Uwp / WindowsFeature
- [UninstallerType](../../src/SysMgr.Modules/Uninstall/UninstallerType.cs)：Msi / InnoSetup / Nsis / BasicExe / None / Unknown
- [UninstallMode](../../src/SysMgr.Modules/Uninstall/UninstallMode.cs)：Standard / Silent / Force
- [UninstallOptions](../../src/SysMgr.Modules/Uninstall/UninstallOptions.cs)：(Mode, KillRunningProcesses, CustomUninstallArgs)

---

## 4. 关键流程伪代码

### 4.1 扫描已装软件（FR-UNINSTALL-01）

```
ScanInstalledAsync(progress, ct):
    # 1. 注册表来源（三个 hive 的 Uninstall 键）
    for hive in [HKLM\SOFTWARE\..., HKLM\SOFTWARE\WOW6432Node\..., HKCU\...]:
        for key in Registry.EnumSubKeys(hive):
            if ct.IsCancellationRequested: return
            props = Registry.ReadValues(key, [
                "DisplayName", "Publisher", "DisplayVersion", "InstallDate",
                "EstimatedSize", "InstallLocation", "UninstallString",
                "SystemComponent", "WindowsInstaller"
            ])
            if props["DisplayName"] is null: continue   # 无名称项跳过
            item = BuildItemFromRegistry(key, props, hive→Source)
            item.IsLongUnused = ClassifyLongUnused(item)   # 见 §4.4
            progress.Report(Enumerating, ScannedCount++)
            yield return item

    # 2. UWP / 商店应用（V1 仅扫描展示，不可卸载）
    for pkg in PackageManager.FindPackages():   # Windows.Management.Deployment
        yield return BuildItemFromUwp(pkg)      # Source=Uwp, UninstallString=null

    # 3. Windows 可选功能（V1 仅展示，标记 IsSystemComponent）
    for feature in Dism.GetFeatures():          # V1 用 WMI Win32_OptionalFeature 占位
        if feature.InstallState == Installed:
            yield return BuildItemFromFeature(feature)  # Source=WindowsFeature

    progress.Report(Done, ScannedCount)
```

**EstimatedSizeBytes 取值优先级**：① 注册表 `EstimatedSize`（KB→字节）② 若为空且 InstallLocation 存在，递归算目录大小（流式，超时 5s 降级返回 null）③ 都为空返回 null。

### 4.2 标准卸载（FR-UNINSTALL-02）

```
EnqueueUninstallAsync(item, options{Mode=Standard}, progress, ct):
    cmd = await GetUninstallCommandAsync(item, ct)

    # 1. 进程检测
    runningProcs = ProcessFacade.FindProcessesInPath(item.InstallLocation)
    if runningProcs.Any():
        if not options.KillRunningProcesses:
            throw SoftwareRunningException(item.InstallLocation, runningProcs)
        # 用户选了强制结束，加入 actions

    # 2. 构造 OperationPlan（actions 含 KillProcess? + RunExternalUninstaller）
    actions = []
    if options.KillRunningProcesses and runningProcs.Any():
        actions.append(OperationAction(
            Kind=KillProcess,
            Target=item.InstallLocation,   # 按目录匹配，OpQueue 执行时枚举并结束
            OriginalValue=string.Join(",", runningProcs.Select(p => p.Id))
        ))
    actions.append(OperationAction(
        Kind=RunExternalUninstaller,
        Target=cmd.UninstallString,
        NewValue=options.CustomUninstallArgs ?? cmd.SilentArgs   # V1 Standard 走交互模式，CustomUninstallArgs 通常 null
    ))

    plan = OperationPlan(
        PlanId=NewGuid(),
        ModuleId="uninstall",
        FrId="FR-UNINSTALL-02",
        Description=$"标准卸载 {item.DisplayName}",
        SafetyLevel=Medium,                # 调用外部程序，中风险
        Actions=actions,
        PreviewRequired=true,
        UserConfirmed=false
    )

    # 3. TRUST-02 预演
    preview = await ctx.Trust.PreviewAsync(plan, ct)
    if not preview.Confirmed: return Cancelled
    plan.UserConfirmed = true

    # 4. 入 OpQueue（回滚策略 PathRecordOnly：外部程序执行不可逆，仅记录）
    result = await ctx.OpQueue.EnqueueAsync(plan, progress, ct)

    # 5. 卸载完成后触发残留扫描（FR-RESIDUAL-03）
    if result.Success:
        await ctx.Coordinator.TriggerResidualScanAsync(plan.PlanId)

    return result
```

**外部卸载程序执行细节**（OpQueue 消费 RunExternalUninstaller action 时）：
- 解析 UninstallString：Msi 用 `msiexec /x {ProductCode}`，其余直接 `Process.Start(UninstallString)`
- 等待完成：`process.WaitForExitAsync(ct)`，超时 300s 抛 ExternalUninstallerTimeoutException
- 静默模式（V1 不落地）：若 CustomUninstallArgs 非空，拼接为命令行参数

### 4.3 强制卸载（FR-UNINSTALL-04）

```
EnqueueUninstallAsync(item, options{Mode=Force}, progress, ct):
    cmd = await GetUninstallCommandAsync(item, ct)
    if not cmd.CanForceUninstall:
        throw UninstallerAvailableException(item.DisplayName)   # 自带卸载正常时不建议强制

    # 1. 收集关联项（GetUninstallCommandAsync 已收集，这里展开）
    actions = []

    # 强制结束进程
    if options.KillRunningProcesses:
        runningProcs = ProcessFacade.FindProcessesInPath(cmd.InstallLocation)
        if runningProcs.Any():
            actions.append(OperationAction(Kind=KillProcess, Target=cmd.InstallLocation, ...))

    # 删除安装目录（递归）
    if cmd.InstallLocation not null:
        actions.append(OperationAction(
            Kind=DeleteFile,                 # OpQueue 执行时递归删除目录
            Target=cmd.InstallLocation,
            SizeBytes=EstimateDirSize(cmd.InstallLocation),
            Sha256=null                      # 目录不校验单文件 SHA
        ))

    # 删除注册表项
    for key in cmd.RelatedRegistryKeys:
        actions.append(OperationAction(Kind=DeleteRegistry, Key=key, ValueName=null))

    # 停止并禁用服务
    for svc in cmd.RelatedServices:
        actions.append(OperationAction(Kind=StopService, ServiceName=svc))

    # 删除计划任务
    for task in cmd.RelatedScheduledTasks:
        actions.append(OperationAction(Kind=DeleteScheduledTask, TaskPath=task))

    plan = OperationPlan(
        PlanId=NewGuid(),
        ModuleId="uninstall",
        FrId="FR-UNINSTALL-04",
        Description=$"强制卸载 {item.DisplayName}",
        SafetyLevel=Low,                    # 直接清理，低安全等级需强确认
        Actions=actions,
        PreviewRequired=true,
        UserConfirmed=false
    )

    # TRUST-02 预演（含低等级项护栏：默认不勾选，需展开高级）
    preview = await ctx.Trust.PreviewAsync(plan, ct)
    if not preview.Confirmed: return Cancelled
    plan.UserConfirmed = true

    # 入 OpQueue + 回滚快照（Copy 策略：目录文件复制到 Rollback\files\，注册表导出 .reg）
    result = await ctx.OpQueue.EnqueueAsync(plan, progress, ct)

    if result.Success:
        await ctx.Coordinator.TriggerResidualScanAsync(plan.PlanId)

    return result
```

**关联项收集规则**（GetUninstallCommandAsync 内部）：
- RelatedRegistryKeys：`HKLM\SOFTWARE\<Publisher>\<DisplayName>`、`HKCU\SOFTWARE\<Publisher>\<DisplayName>`、Uninstall 键本身、App Paths 下的 `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\<exe>.exe`
- RelatedServices：WMI `SELECT * FROM Win32_Service WHERE PathName LIKE '%<InstallLocation>%'`（V1 用 WMI，V1.5 切 ServiceControllerFacade）
- RelatedScheduledTasks：`TaskService.Instance.AllTasks` 过滤 Path 含 InstallLocation 或任务名含 DisplayName（V1.5；V1 返回空列表占位）

### 4.4 长期未用复合判定（FR-UNINSTALL-01）

```
ClassifyLongUnused(item, thresholdDays=90):
    # 三项复合，任一有近 90 天活动即不算"长期未用"
    lastActivity = max(
        GetPrefetchLastWrite(item.DisplayName),      # %SystemRoot%\Prefetch\<DisplayName>.pf 最后写入
        item.InstallDate ?? DateTimeOffset.MinValue,  # 注册表 InstallDate
        HasStartupEntry(item) ? DateTimeOffset.Now : DateTimeOffset.MinValue  # 仍存在启动项算"近期活动"
    )
    return (DateTimeOffset.Now - lastActivity) > TimeSpan.FromDays(thresholdDays)
```

**降级策略**：
- Prefetch 文件不存在（如系统禁用 Prefetch 或软件从未前台运行）→ 只看 InstallDate + 启动项
- InstallDate 解析失败（格式非 yyyymmdd）→ 返回 null，降级
- 三项全无数据 → IsLongUnused=false（保守不标记）

### 4.5 卸载后残留联动（FR-RESIDUAL-03）

`IModuleCoordinator.TriggerResidualScanAsync(planId)` 路由到 Residual 模块（详设见 [02-residual.md](02-residual.md) §4.5）。传递 PlanId 用于关联：Residual 扫描时根据原软件 InstallLocation + DisplayName 构造扫描关键词。UI 展示"残留项数 + 预计释放空间"。

---

## 5. 边界条件

| 场景 | 处理 |
| --- | --- |
| UWP / WindowsFeature 选定卸载 | UI 禁用卸载按钮，提示"V1.5 支持"。EnqueueUninstallAsync 收到 Source=Uwp/WindowsFeature 抛 NotSupportedException |
| 系统组件（IsSystemComponent=true） | UI 默认折叠隐藏，展开后选定卸载抛 NotSupportedException |
| Mode=Silent（V1 不落地） | EnqueueUninstallAsync 抛 NotSupportedException，提示"静默卸载 V1.5 支持" |
| 软件在运行且未选 KillRunningProcesses | 抛 SoftwareRunningException，UI 提示"请先关闭软件或开启强制结束进程" |
| UninstallString 缺失（UninstallerType=None） | CanForceUninstall=true，UI 仅显示"强制卸载"入口；Standard 模式抛 UninstallerNotFoundException |
| 外部卸载程序超时（300s） | 抛 ExternalUninstallerTimeoutException，提示用户手动处理或转强制卸载 |
| EstimatedSizeBytes 算目录超时（5s） | 降级返回 null，UI 显示"--"，不阻塞扫描 |
| InstallLocation 含长路径（>260） | 走 FileSystemSafe 的 \\?\ 前缀封装（TDD §6.3） |
| 强制卸载目录被占用（系统服务锁定） | StopService action 先执行；仍占用则 DeleteFile 失败走回滚 + 报告 |
| 便携版运行 | 不写注册表右键菜单（FR-SHRED-03 同款约束）；卸载功能正常 |

---

## 6. 错误码

所有异常继承 [SysMgrException](../../src/SysMgr.Core/Common/SysMgrException.cs)（TDD §6.2 统一错误模型）。子类落 `SysMgr.Modules.Uninstall.Errors` 命名空间，批次 1 编码阶段补。

| 异常 | 触发 | 用户提示 |
| --- | --- | --- |
| UninstallException | 模块基类 | "卸载失败：<原因>" |
| SoftwareRunningException | 进程在运行且未选强制结束 | "软件正在运行，请先关闭或开启强制结束进程" |
| UninstallerNotFoundException | Standard 模式但 UninstallString 缺失 | "未找到卸载程序，建议使用强制卸载" |
| UninstallerAvailableException | Force 模式但自带卸载程序正常 | "该软件自带卸载程序，建议使用标准卸载" |
| ExternalUninstallerTimeoutException | 外部卸载程序 300s 未退出 | "卸载程序无响应，可手动处理或转强制卸载" |
| ForceUninstallSnapshotException | 强制卸载快照生成失败 | "回滚快照创建失败，已中止卸载以保护可恢复性" |
| UwpNotSupportedV1Exception | 选定 UWP 卸载 | "UWP 应用卸载将在 V1.5 支持" |

---

## 7. 与横切层交互

| 横切层组件 | 交互点 | 说明 |
| --- | --- | --- |
| IDestructiveOpQueue | EnqueueUninstallAsync → EnqueueAsync | 标准/强制卸载均入队串行执行 |
| ITrustBroadcaster | EnqueueUninstallAsync → PreviewAsync | TRUST-02 预演，强制卸载含 Low 等级项触发高级模式护栏 |
| ITrustBroadcaster.Broadcast | OpQueue 执行每步 action 时广播 | TRUST-01 直播面板实时展示 |
| IRollbackEngine | 强制卸载的 DeleteFile/DeleteRegistry 触发快照 | Copy 策略；标准卸载 RunExternalUninstaller 走 PathRecordOnly |
| IOperationLogger | 全程落 history.jsonl | 卸载前/中/后每步记录，可回放 |
| ISafetyClassifier | 强制卸载关联项分级 | 服务/计划任务项默认 Medium/Low，需用户逐项确认 |
| IRuleSetManager | uninstall_vendors.json 加载 | 厂商特征辅助识别关联注册表键 |
| IModuleCoordinator | TriggerResidualScanAsync | 卸载完成后联动残留扫描 |

---

## 8. 实现优先级（编码阶段）

1. ScanInstalledAsync（注册表三 hive 枚举 + EstimatedSize 降级 + 长期未用判定）
2. GetUninstallCommandAsync（UninstallerType 识别 + 关联项收集）
3. EnqueueUninstallAsync Standard 模式（KillProcess + RunExternalUninstaller）
4. EnqueueUninstallAsync Force 模式（关联项 DeleteFile/DeleteRegistry/StopService/DeleteScheduledTask）

每步完成后 `dotnet test`（SysMgr.Modules.Tests）验证，保持 0 警告 0 错误。

---

## 9. 待确认事项

- [ ] UWP 扫描是否 V1 必须展示（PackageManager API 在 net8.0-windows 是否可用，需编码阶段 PoC）
- [ ] Prefetch 文件访问权限（管理员可读，确认 UAC 提权后无障碍）
- [ ] 外部卸载程序超时阈值 300s 是否合理（编码阶段调研常见卸载程序耗时分布）
- [ ] 强制卸载关联项收集的误报率（WMI `PathName LIKE` 可能匹配过宽，需编码阶段验证）
