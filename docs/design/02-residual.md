# 02 残留清除模块详设（Residual）

| 项目 | 内容 |
| --- | --- |
| 文档版本 | v0.1（V1 详设批次 1 模块 2） |
| 编写日期 | 2026-09-28 |
| 模块命名空间 | `SysMgr.Modules.Residual` |
| 分层位置 | L2 功能模块层（TDD §1.2） |
| FR 追溯 | FR-RESIDUAL-01 残留扫描 / FR-RESIDUAL-02 残留清除 / FR-RESIDUAL-03 软件关联残留 |
| 配套文档 | [requirements.md](../requirements.md) §3.3 / [technical-design.md](../technical-design.md) §2-§5 |
| 代码骨架 | [IResidualService.cs](../../src/SysMgr.Modules/Residual/IResidualService.cs) |

> 本文是 V1 MVP 详设批次 1 第 2 模块。模块 1（[Uninstall](01-uninstall.md)）已完成并在卸载完成后联动本模块（FR-RESIDUAL-03）；本模块接口与数据类已同步落到 [src/SysMgr.Modules/Residual/](../../src/SysMgr.Modules/Residual/)，sysmgr.sln 持续可编译。

---

## 1. 模块概览

### 1.1 职责

残留清除模块负责对目标软件（已卸载或即将卸载）扫描五维度残留——注册表、文件系统、服务与驱动、计划任务、启动项——按安全等级分类展示；用户勾选后走 TRUST-02 预演二次确认，经 OpQueue 串行清除，删除前由回滚引擎生成影子备份。同时承接 Uninstall 模块的卸载后自动联动扫描，产出"残留项数 + 预计释放空间"摘要。

### 1.2 V1 范围与非目标

| 范围内（V1） | 范围外 |
| --- | --- |
| FR-RESIDUAL-01 残留扫描（注册表 + 文件系统 + 服务/驱动 + 计划任务 + 启动项五维度） | 无目标软件的全盘孤儿残留扫描页（V1.5，扫描必须有软件目标） |
| FR-RESIDUAL-02 残留清除（勾选 + TRUST-02 二次确认 + 影子备份 + dry-run） | 驱动 .sys 文件删除（V1 仅 StopService，文件删除 V1.5） |
| FR-RESIDUAL-03 软件关联残留（卸载后自动扫描 + 残留项数/释放空间摘要） | UWP 应用残留（V1.5） |
| dry-run 仅扫描不删除（FR-RESIDUAL-02） | AI 语义识别残留（深度启发式匹配，V3 随 FR-AI） |

### 1.3 依赖

- **横切层（L3，已落骨架）**：[ISafetyClassifier](../../src/SysMgr.Core/Safety/ISafetyClassifier.cs)（全局分级唯一口径）/ [ITrustBroadcaster](../../src/SysMgr.Core/Trust/ITrustBroadcaster.cs)（TRUST-02 预演）/ [IDestructiveOpQueue](../../src/SysMgr.Core/Queue/IDestructiveOpQueue.cs) / [IRollbackEngine](../../src/SysMgr.Core/Rollback/IRollbackEngine.cs)（影子备份由 OpQueue 在删除前触发）/ [IOperationLogger](../../src/SysMgr.Core/Logging/IOperationLogger.cs)（TRUST-01 直播）
- **L4 系统访问层**：RegistryFs（注册表枚举）、FileSystemSafe（长路径/占用文件封装，TDD §6.3）、ServiceControllerFacade（V1 用 WMI `Win32_Service` / `Win32_SystemDriver` 占位，V1.5 切换）、TaskSchedulerFacade（V1 用 WMI 占位，V1.5 切换）
- **规则集**：`rules\safety_critical_paths.json`（复用 §3.0 系统关键白名单）、`rules\residual_stopwords.json`（关键词停用词表，新增，防止 "Update"/"Setup" 类通用词误伤）
- **模块间协作**：[IModuleCoordinator.TriggerResidualScanAsync](../../src/SysMgr.Modules/IModuleCoordinator.cs)（被 Uninstall 联动，本模块提供处理逻辑，FR-RESIDUAL-03）

---

## 2. 接口契约

完整签名见 [IResidualService.cs](../../src/SysMgr.Modules/Residual/IResidualService.cs)。继承 [IModuleService](../../src/SysMgr.Modules/IModuleService.cs)（模块统一基类，DI 容器统一初始化）。

```csharp
public interface IResidualService : IModuleService
{
    IAsyncEnumerable<ResidualItem> ScanResidualAsync(
        ResidualScanTarget target,
        ResidualScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken ct);

    Task<OperationResult> EnqueueCleanupAsync(
        IReadOnlyList<ResidualItem> items,
        ResidualCleanupOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken ct);
}
```

**方法链语义**（TDD §3.2）：`ScanResidualAsync`（五维度扫描 + 安全分级，流式返回）→ `EnqueueCleanupAsync`（勾选项入队清除）。UI 先扫描得到残留列表（High 默认勾选 / Medium、Low 默认不勾选），用户调整勾选后调 EnqueueCleanupAsync；DryRun=true 时仅返回预览明细不入队。

**联动语义**（FR-RESIDUAL-03）：Uninstall 的 `EnqueueUninstallAsync` 成功后调 `IModuleCoordinator.TriggerResidualScanAsync(planId)`，协调器路由到本模块内部处理方法（见 §4.5），UI 收到摘要后跳转残留页。

---

## 3. 数据结构

### 3.1 扫描目标 ResidualScanTarget

详见 [ResidualScanTarget.cs](../../src/SysMgr.Modules/Residual/ResidualScanTarget.cs)。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| SoftwareId | Guid | 关联软件内部 ID（与 UninstallItem.SoftwareId 一致） |
| DisplayName | string | 软件显示名（关键词主来源） |
| Publisher | string? | 发布者（关键词辅助来源） |
| InstallLocation | string? | 安装路径（末段目录名作关键词兜底） |

### 3.2 残留项 ResidualItem

详见 [ResidualItem.cs](../../src/SysMgr.Modules/Residual/ResidualItem.cs)。

| 字段 | 类型 | 说明 | FR |
| --- | --- | --- | --- |
| ItemId | Guid | 残留项内部 ID | 01 |
| SoftwareId | Guid | 关联软件 | 03 |
| Category | ResidualCategory | 类别（Registry/File/Service/ScheduledTask/Startup） | 01 |
| Target | string | 目标（按 Category 语义不同，见代码注释） | 01 |
| ValueName | string? | 注册表值名（null=整键；Startup 为 Run 键值名） | 01 |
| Verdict | SafetyVerdict | 分级结论（Level + Reason + Evidence，TRUST-02 展示） | 01 |
| EstimatedSizeBytes | long? | 文件/目录大小（估算超时降级 null） | 03 |
| MatchedBy | string | 命中的关键词（供人工判断参考） | 01 |
| Description | string | UI 展示描述 | 01 |

### 3.3 枚举与选项

- [ResidualCategory](../../src/SysMgr.Modules/Residual/ResidualCategory.cs)：Registry / File / Service / ScheduledTask / Startup（Startup 按底层形态映射 DeleteRegistry 或 DeleteFile，见 §4.4）
- [ResidualScanOptions](../../src/SysMgr.Modules/Residual/ResidualScanOptions.cs)：(Categories=null 全维度, AdditionalKeywords)
- [ResidualCleanupOptions](../../src/SysMgr.Modules/Residual/ResidualCleanupOptions.cs)：(DryRun)

**与横切层 ScannedItem 的关系**：扫描候选在内部构造为 [ScannedItem](../../src/SysMgr.Core/Safety/ScannedItems.cs) 子类（FileItem/RegistryItem/ServiceItem/ScheduledTaskItem）送入 `ISafetyClassifier.Classify`（其签名接收基类）；分类结论包进 ResidualItem.Verdict。Startup 维度按底层形态落 RegistryItem（Run 键）或 FileItem（启动文件夹 .lnk）。

---

## 4. 关键流程伪代码

### 4.1 关键词构造

```
BuildKeywords(target, options):
    keywords = OrderedSet()                      # 按长度降序，长词优先
    keywords.Add(target.DisplayName.Trim())      # 全名优先（含空格精确匹配）
    if target.Publisher not null: keywords.Add(target.Publisher.Trim())
    if target.InstallLocation not null:
        keywords.Add(Path.GetFileName(target.InstallLocation.TrimEnd('\\')))  # 末段目录名
    keywords.AddRange(options.AdditionalKeywords ?? [])

    # 1. 停用词过滤（rules/residual_stopwords.json）："Update"/"Setup"/"Microsoft"/"Runtime"/"Tools" 等
    keywords = keywords.Where(k => k not in Stopwords)
    # 2. 短词过滤：长度 < 3 丢弃（避免 "Go" 误伤 Golang 目录）
    keywords = keywords.Where(k => k.Length >= Config.MinKeywordLength)   # 默认 3
    if keywords.Empty():
        throw ResidualTargetInvalidException(target)   # RES-001，三字段全空或全被过滤

    return keywords
```

**匹配规则**：目录/键名用**全词边界匹配**（目录名等于关键词，或 `关键词` + 分隔符后缀，如 `Foo` 命中 `Foo`、`Foo Tray`，不命中 `FooBar`）；服务 PathName / 任务 ExecutablePath 用子串匹配（路径天然带分隔符边界）。

### 4.2 残留扫描（FR-RESIDUAL-01）

```
ScanResidualAsync(target, options, progress, ct):
    keywords = BuildKeywords(target, options)
    categories = options.Categories ?? 全五维度

    # 1. 注册表维度
    if Registry in categories:
        # 1a. Uninstall 孤儿键：三 hive 中 DisplayName 命中但卸载程序已残留的键
        for hive in [HKLM\SOFTWARE\...\Uninstall, HKLM\SOFTWARE\WOW6432Node\...\Uninstall, HKCU\...\Uninstall]:
            for key in Registry.EnumSubKeys(hive):
                if ct.IsCancellationRequested: goto Report
                name = Registry.ReadValue(key, "DisplayName")
                if name not null and MatchKeyword(name, keywords):
                    Emit(Registry, key, ValueName=null)

        # 1b. App Paths：HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\<exe>.exe
        for key in Registry.EnumSubKeys(AppPaths):
            if MatchKeyword(key.Name.TrimEnd(".exe"), keywords): Emit(Registry, key)

        # 1c. Shell Extensions / CLSID：InprocServer32 默认值路径命中 InstallLocation 或关键词
        #     （键量大，限时 Config.ClsidScanTimeoutMs 默认 10s，超时降级跳过并在进度标注"部分未完成"）
        for clsid in Registry.EnumSubKeys(HKLM\SOFTWARE\Classes\CLSID):
            inproc = Registry.ReadValue(clsid + "\InprocServer32", null)
            if inproc not null and (inproc.StartsWith(target.InstallLocation) or MatchKeyword(inproc, keywords)):
                Emit(Registry, clsid)
        # 1d. 启动项（注册表部分）：HKCU/HKLM ...\CurrentVersion\Run 键值数据命中
        for runKey in [HKCU Run, HKLM Run]:
            for value in Registry.EnumValues(runKey):
                if MatchKeyword(value.Data, keywords):
                    Emit(Startup, runKey, ValueName=value.Name)

    # 2. 文件系统维度（六根目录顶层子目录 + 启动文件夹）
    if File in categories:
        for root in [%ProgramFiles%, %ProgramFiles(x86)%, %LocalAppData%, %AppData%, %ProgramData%, %PUBLIC%]:
            for dir in Directory.EnumTopLevelDirs(root):
                if ct.IsCancellationRequested: goto Report
                if MatchWordBoundary(dir.Name, keywords):
                    size = EstimateDirSize(dir, timeout 5s)   # 流式，超时降级 null
                    Emit(File, dir, SizeBytes=size)
        for lnk in Directory.EnumFiles([%AppData%\Microsoft\Windows\Start Menu, %ProgramData%\Microsoft\Windows\Start Menu]):
            if MatchKeyword(Path.GetFileNameWithoutExtension(lnk), keywords):
                Emit(Startup, lnk, ValueName=null)

    # 3. 服务/驱动维度（V1 用 WMI 占位）
    if Service in categories:
        for svc in Wmi.Query("Win32_Service") + Wmi.Query("Win32_SystemDriver"):
            if MatchKeyword(svc.Name or svc.DisplayName or svc.PathName, keywords):
                Emit(Service, svc.Name)

    # 4. 计划任务维度（V1 用 WMI 占位）
    if ScheduledTask in categories:
        for task in Wmi.Query("Win32_ScheduledJob") + TaskFolder.EnumRecursive():   # 编码阶段定 V1 具体枚举源
            if MatchKeyword(task.Path or task.Name or task.ExecutablePath, keywords):
                Emit(ScheduledTask, task.Path)

    Report:
    progress.Report(Done, ScannedCount)
```

每个 `Emit` 内部统一执行：

```
Emit(category, target, valueName, sizeBytes):
    scanned = ToScannedItem(category, target)      # FileItem/RegistryItem/ServiceItem/ScheduledTaskItem
    verdict = Safety.Classify(scanned)             # 全局统一口径，模块不自定规则
    yield ResidualItem(NewGuid(), target.SoftwareId, category, target, valueName,
                       verdict, sizeBytes, matchedKeyword, Describe(category, target))
```

### 4.3 安全分级与默认勾选（FR-RESIDUAL-01）

```
UI 按Verdict.Level 展示三档分类：
    High   → "建议删除"，默认勾选（残留主形态：孤儿目录/孤儿键/孤儿任务）
    Medium → "需人工判断"，默认不勾选（可能被其他软件引用，展示 Evidence 供判断）
    Low    → "可能影响其他软件"，默认不勾选（系统白名单路径命中/共享组件）

用户调整约束（TDD §3.0 全局规则）：仅允许上调不允许下调；
下调需切换"高级模式"并二次确认（与 TRUST-02 Low 项护栏联动）。
```

### 4.4 残留清除入队（FR-RESIDUAL-02）

```
EnqueueCleanupAsync(items, options, progress, ct):
    if items.Empty(): throw ResidualItemsEmptyException()      # RES-002

    # 1. dry-run：仅扫描预览不入队（FR-RESIDUAL-02 "仅扫描不删除"）
    if options.DryRun:
        return BuildPreviewReport(items)       # 每项 Target + Verdict + Size，不构造 plan 不触达 OpQueue

    # 2. 按 Category 映射 OperationAction
    actions = for item in items:
        match item.Category:
            File          → OperationAction(Kind=DeleteFile, Target=item.Target,
                                               SizeBytes=item.EstimatedSizeBytes, Sha256=null)  # 目录不校验单文件 SHA
            Registry      → OperationAction(Kind=DeleteRegistry, Key=item.Target, ValueName=item.ValueName)
            Service       → OperationAction(Kind=StopService, ServiceName=item.Target)      # V1 仅停止，禁用/删 .sys V1.5
            ScheduledTask → OperationAction(Kind=DeleteScheduledTask, TaskPath=item.Target)
            Startup       → item.ValueName not null
                              ? OperationAction(Kind=DeleteRegistry, Key=item.Target, ValueName=item.ValueName)  # Run 键值
                              : OperationAction(Kind=DeleteFile, Target=item.Target)                             # 启动文件夹 .lnk

    plan = OperationPlan(
        PlanId=NewGuid(),
        ModuleId="residual",
        FrId="FR-RESIDUAL-02",
        Description=$"清除 {items.Count} 项软件残留",
        SafetyLevel=Min(items.Select(i => i.Verdict.Level)),   # 含任一 Low 项则整体 Low → 触发 TRUST-02 高级模式护栏
        Actions=actions,
        PreviewRequired=true,
        UserConfirmed=false
    )

    # 3. TRUST-02 预演（二次确认，面板逐项展示 Reason + Evidence）
    preview = await ctx.Trust.PreviewAsync(plan, ct)
    if not preview.Confirmed: return Cancelled
    plan.UserConfirmed = true

    # 4. 入 OpQueue 串行执行；单项失败不阻塞其他项
    #    DeleteFile/DeleteRegistry 执行前由 OpQueue 调 IRollbackEngine 生成影子备份（Copy 策略：
    #    文件复制到 Rollback\files\，注册表导出 .reg）；备份失败则跳过该项不删除（保守）
    return await ctx.OpQueue.EnqueueAsync(plan, progress, ct)
```

### 4.5 卸载后联动（FR-RESIDUAL-03）

`IModuleCoordinator.TriggerResidualScanAsync(uninstallOpId)` 由协调器路由到本模块处理方法：

```
OnUninstallCompleted(uninstallOpId):
    plan = OpQueue.GetPlan(uninstallOpId)              # 从执行记录取回卸载 plan
    item = plan 元数据中携带的 UninstallItem            # Uninstall 入队时写入
    target = ResidualScanTarget(item.SoftwareId, item.DisplayName, item.Publisher, item.InstallLocation)

    residuals = []
    for r in ScanResidualAsync(target, ResidualScanOptions(null, null), null, CancellationToken.None):
        residuals.Add(r)

    # 摘要经 TRUST-01 直播推送 UI（IOperationLogger 落 history.jsonl + 推 LiveStream）
    ctx.Trust.Broadcast(OperationLogEntry(
        OpId=uninstallOpId,
        Stage=ResidualScanCompleted,
        Detail=$"残留 {residuals.Count} 项，预计释放 {FormatBytes(Sum(residuals.EstimatedSizeBytes))}"))

    # UI 收到 ResidualScanCompleted 事件后跳转残留页，预填扫描结果
```

**时序注意**：联动扫描在 OpQueue 消费完 RunExternalUninstaller（进程退出）之后触发；外部卸载程序异步清理尾巴（自删延迟）可能造成扫描瞬时误判，编码阶段验证是否需延迟 2-3s 再扫（见 §9）。

---

## 5. 边界条件

| 场景 | 处理 |
| --- | --- |
| 关键词过短/通用（"Go""Run"） | 长度 < 3 丢弃 + 停用词表过滤，避免误伤 Golang 等无关目录 |
| 系统组件同名（如 Visual C++ Redistributable） | 全名含空格精确匹配优先；命中系统白名单路径的项分类器直接判 Low，默认不勾选 |
| 关键词部分前缀（Foo vs FooBar） | 目录/键名全词边界匹配，不做子串盲配；InstallLocation 末段精确优先 |
| 目录大小估算超时（5s） | 降级返回 null，UI 显示"--"，不阻塞扫描 |
| CLSID 键量过大（数千键） | 扫描限时 10s，超时降级跳过该子维度，进度面板标注"部分维度未完成" |
| 服务被系统/其他软件依赖 | 分类器判 Medium/Low 默认不勾选；清除时 StopService 失败单项不阻塞 |
| 文件被占用（进程已结束后仍锁定） | DeleteFile 失败走回滚恢复已删部分，锁定文件列入"重试清单"报告 |
| 影子备份失败（磁盘空间不足） | 跳过该项不删除（保守），列入错误清单，不阻塞其他项 |
| HKCU 与提权上下文不一致 | V1 仅扫描当前用户 hive 与系统 hive，不枚举其他用户 profile |
| 扫描中用户取消（ct） | 返回已收集项并标记"扫描未完成"，可直接对已收集项执行清除 |
| dry-run 请求 | 不构造 plan、不触达 OpQueue，仅返回预览明细（数据同 TRUST-02 面板） |
| 便携版运行 | 扫描/清除功能正常（纯本机操作，无注册表右键菜单写入） |

---

## 6. 错误码

所有异常继承 [SysMgrException](../../src/SysMgr.Core/Common/SysMgrException.cs)（TDD §6.2 统一错误模型）。子类落 `SysMgr.Modules.Residual.Errors` 命名空间，批次 1 编码阶段补。

| 异常 | 触发 | 用户提示 |
| --- | --- | --- |
| ResidualException | 模块基类 | "残留清除失败：<原因>" |
| ResidualTargetInvalidException | 扫描目标三字段全空或全被停用词过滤 | "无法识别该软件的特征，请补充关键词后重试" |
| ResidualItemsEmptyException | 未勾选任何项即提交清除 | "请先勾选要清除的残留项" |
| ShadowBackupFailedException | 影子备份失败（跳过该项） | "<n> 项备份失败已跳过，未删除以保护可恢复性" |
| ServiceStopFailedException | 服务停止失败（被依赖/权限） | "服务停止失败（可能被其他程序使用），已跳过" |
| ScheduledTaskDeleteFailedException | 计划任务删除失败（权限/不存在） | "计划任务删除失败，请以管理员身份重试" |
| FileInUseException | 文件被占用删除失败 | "部分文件被占用已回滚，请关闭相关程序后重试" |

---

## 7. 与横切层交互

| 横切层组件 | 交互点 | 说明 |
| --- | --- | --- |
| ISafetyClassifier | 每个 Emit 前 Classify | 全局统一口径；Reason/Evidence 存入 ResidualItem.Verdict |
| ITrustBroadcaster.PreviewAsync | EnqueueCleanupAsync 入队前 | TRUST-02 预演逐项展示；含 Low 项触发高级模式护栏 |
| ITrustBroadcaster.Broadcast | 扫描进度 + 联动摘要 | TRUST-01 直播（ResidualScanCompleted 事件） |
| IDestructiveOpQueue | EnqueueCleanupAsync → EnqueueAsync | 串行执行，单项失败不阻塞 |
| IRollbackEngine | OpQueue 对 DeleteFile/DeleteRegistry 删除前快照 | 影子备份 Copy 策略（文件复制 Rollback\files\，注册表导出 .reg） |
| IOperationLogger | 全程落 history.jsonl | 扫描/预演/执行/结果每步记录，可回放 |
| IRuleSetManager | residual_stopwords.json + safety_critical_paths.json 加载 | 停用词与关键白名单外置，无重编译更新 |
| IConfigStore | 阈值配置 | 目录估算超时 5s / CLSID 扫描限时 10s / 短词长度下限 3 |
| IModuleCoordinator | TriggerResidualScanAsync 路由 | 本模块提供 OnUninstallCompleted 处理，Uninstall 联动调用 |

---

## 8. 实现优先级（编码阶段）

1. BuildKeywords（停用词 + 短词过滤 + 全词边界匹配）
2. ScanResidualAsync 文件系统维度 + 注册表 Uninstall/App Paths/Run 维度 + 安全分级
3. EnqueueCleanupAsync（Category→ActionKind 映射 + dry-run + TRUST-02 预演）
4. 服务/计划任务维度（WMI 占位）
5. FR-RESIDUAL-03 联动（OnUninstallCompleted + 摘要推送）
6. CLSID/Shell Extensions 深度维度（限时降级）

每步完成后 `dotnet test`（SysMgr.Modules.Tests）验证，保持 0 警告 0 错误。

---

## 9. 待确认事项

- [ ] CLSID/Shell Extensions 全量遍历性能实测（HKLM\SOFTWARE\Classes\CLSID 数千键，限时 10s 是否足够，编码阶段 PoC）
- [ ] 停用词表初始条目清单（"Update"/"Setup"/"Microsoft"/"Runtime" 等，需结合常见误报案例补全）
- [ ] 计划任务枚举源 V1 具体方案（WMI Win32_ScheduledJob 覆盖不足，可能需直接 COM TaskScheduler，编码阶段定）
- [ ] 联动扫描时机：OpQueue 卸载动作完成即扫 vs 延迟 2-3s 等外部卸载程序自删尾巴（编码阶段验证误报率）
- [ ] 服务维度 V1 仅 StopService 不删文件，UI 文案需明确"服务已停止，文件删除将在 V1.5 提供"
- [ ] 残留项缓存（同软件二次打开加速）vs 每次实时扫描：V1 实时，确认性能可接受
