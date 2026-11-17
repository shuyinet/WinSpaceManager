# 04 垃圾清理模块详设（Cleanup）

| 项目 | 内容 |
| --- | --- |
| 文档版本 | v0.1（V1 详设批次 1 模块 4） |
| 编写日期 | 2026-09-28 |
| 模块命名空间 | `SysMgr.Modules.Cleanup` |
| 分层位置 | L2 功能模块层（TDD §1.2） |
| FR 追溯 | FR-CLEAN-01 系统临时文件清理 / FR-CLEAN-02 浏览器隐私清理 / FR-CLEAN-06 一键清理 |
| 配套文档 | [requirements.md](../requirements.md) §3.6 / [technical-design.md](../technical-design.md) §2-§5 |
| 代码骨架 | [ICleanupService.cs](../../src/SysMgr.Modules/Cleanup/ICleanupService.cs) |

> 本文是 V1 MVP 详设批次 1 第 4 模块。前置模块 [01-uninstall](01-uninstall.md) / [02-residual](02-residual.md) 已完成；本模块接口与数据类已同步落到 [src/SysMgr.Modules/Cleanup/](../../src/SysMgr.Modules/Cleanup/)，sysmgr.sln 持续可编译。
>
> **编号说明**：模块 3（CSlim）尚未详设，本文按 FR 章节号占用 04 编号；03-cslim.md 将随后补齐。

---

## 1. 模块概览

### 1.1 职责

垃圾清理模块负责扫描并清除系统临时文件（%TEMP%、Windows Temp、Prefetch、缩略图、错误报告、内存转储、回收站）与浏览器隐私数据（Edge/Chrome/Firefox/Brave/Opera 的缓存、Cookie、历史、下载记录、表单、密码），逐项标注预计释放空间与安全等级；支持"仅清理缓存保留登录态"预设、浏览器运行检测与强制结束开关，以及"一键清理"编排入口。

### 1.2 V1 范围与非目标

TDD §9 追溯表明确 V1 仅落 FR-CLEAN-01/02/06：

| 范围内（V1） | 范围外 |
| --- | --- |
| FR-CLEAN-01 系统临时文件清理（7 类目标） | FR-CLEAN-03 注册表清理（V1.5，缺引用/空键/无效 CLSID 扫描） |
| FR-CLEAN-02 浏览器隐私清理（5 浏览器 × 6 类数据） | FR-CLEAN-04 软件缓存清理（V1.5，微信/QQ/VSCode/IDEA 规则集） |
| FR-CLEAN-06 一键清理（High 项全选 + 前后空间对比 + 报告） | FR-CLEAN-05 重复文件查找（V1.5，依赖 §3.5 Treemap 联动） |
| "仅清理缓存保留登录态"预设 + 浏览器运行检测/强制结束 | IE 清理（IE 已退役，永不支持） |

### 1.3 依赖

- **横切层（L3，已落骨架）**：[ISafetyClassifier](../../src/SysMgr.Core/Safety/ISafetyClassifier.cs)（全局分级唯一口径）/ [ITrustBroadcaster](../../src/SysMgr.Core/Trust/ITrustBroadcaster.cs)（TRUST-02 预演 + TRUST-01 直播）/ [IDestructiveOpQueue](../../src/SysMgr.Core/Queue/IDestructiveOpQueue.cs) / [IRollbackEngine](../../src/SysMgr.Core/Rollback/IRollbackEngine.cs)（删除前影子备份）/ [IOperationLogger](../../src/SysMgr.Core/Logging/IOperationLogger.cs) / [IConfigStore](../../src/SysMgr.Core/Config/IConfigStore.cs)
- **L4 系统访问层**：FileSystemSafe（枚举/删除，长路径封装，TDD §6.3）、ProcessFacade（浏览器进程检测/结束）、RecycleBinFacade（`SHEmptyRecycleBin`，新增）、Shell32（缩略图路径定位）
- **规则集**：`rules\cleanup_targets.json`（TDD §3.1 规则集清单，V1 落地）——系统垃圾路径、浏览器数据目录与 profile 发现、回滚策略（PathRecordOnly/Copy）均按条目外置
- **复用规则**：`rules\safety_critical_paths.json`（系统关键白名单）
- **模块间协作**：无强依赖（独立模块，TDD §1.3 架构图"独立模块，复用回滚引擎"）；FR-CLEAN-05 的 Treemap 联动随 V1.5

---

## 2. 接口契约

完整签名见 [ICleanupService.cs](../../src/SysMgr.Modules/Cleanup/ICleanupService.cs)。继承 [IModuleService](../../src/SysMgr.Modules/IModuleService.cs)。

```csharp
public interface ICleanupService : IModuleService
{
    IAsyncEnumerable<JunkItem> ScanJunkAsync(
        JunkScope scope,
        IProgress<ScanProgress>? progress,
        CancellationToken ct);

    Task<OperationResult> EnqueueCleanAsync(
        IReadOnlyList<JunkItem> items,
        CleanOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken ct);
}
```

**方法链语义**（TDD §3.2）：`ScanJunkAsync(scope)`（按范围扫描 + 空间估算 + 分级）→ `EnqueueCleanAsync(items)`（勾选项二次确认后入队清除）。

**一键清理语义**（FR-CLEAN-06）：UI 调 `ScanJunkAsync(JunkScope(null, null))` 后取全部 `DefaultSelected=true` 项直接进入 EnqueueCleanAsync；TRUST-02 预演不省略（"一键"指自动勾选，不是免确认）。

---

## 3. 数据结构

### 3.1 垃圾项 JunkItem

详见 [JunkItem.cs](../../src/SysMgr.Modules/Cleanup/JunkItem.cs)。

| 字段 | 类型 | 说明 | FR |
| --- | --- | --- | --- |
| ItemId | Guid | 垃圾项内部 ID | 01 |
| Kind | JunkKind | 垃圾类型（7 系统 + 6 浏览器，见 §3.2） | 01/02 |
| Browser | JunkBrowser? | 浏览器项所属浏览器；系统项 null | 02 |
| Target | string | 待删文件/目录路径；回收站为虚拟目标 "$RecycleBin" | 01 |
| Verdict | SafetyVerdict | 分级结论（Level + Reason + Evidence） | 01 |
| EstimatedSizeBytes | long | 预计释放空间（占用文件跳过不计） | 01/06 |
| DefaultSelected | bool | UI 默认勾选提示（产品层，非安全等级，见 §4.3） | 02/06 |
| Description | string | UI 展示描述 | 01 |

### 3.2 枚举

- [JunkKind](../../src/SysMgr.Modules/Cleanup/JunkKind.cs)：
  - 系统（FR-CLEAN-01）：Temp / WindowsTemp / Prefetch / ThumbnailCache / WindowsErrorReporting / MemoryDump / RecycleBin
  - 浏览器（FR-CLEAN-02）：BrowserCache / BrowserCookies / BrowserHistory / BrowserDownloads / BrowserFormData / BrowserPasswords
- [JunkBrowser](../../src/SysMgr.Modules/Cleanup/JunkBrowser.cs)：Edge / Chrome / Firefox / Brave / Opera

### 3.3 范围与选项

- [JunkScope](../../src/SysMgr.Modules/Cleanup/JunkScope.cs)：(SystemKinds=null 全扫, BrowserTargets=null 五浏览器全扫)
- [JunkBrowserTarget](../../src/SysMgr.Modules/Cleanup/JunkBrowserTarget.cs)：(Browser, DataKinds)——"仅清理缓存"预设 = DataKinds=[BrowserCache]
- [CleanOptions](../../src/SysMgr.Modules/Cleanup/CleanOptions.cs)：(KillRunningBrowsers=false)

**与横切层 ScannedItem 的关系**：扫描内部将每个候选构造为 [FileItem](../../src/SysMgr.Core/Safety/ScannedItems.cs)（Path + 大小）送 `ISafetyClassifier.Classify`，结论包进 JunkItem.Verdict；回收站项不走文件扫描，大小经 RecycleBinFacade 统计，Verdict 直接 High（用户主动清空语义明确）。

---

## 4. 关键流程伪代码

### 4.1 扫描系统垃圾（FR-CLEAN-01）

```
ScanSystemJunk(kinds, progress, ct):
    for kind in kinds ?? 全7类:
        if ct.IsCancellationRequested: return
        targets = Rules.Get("cleanup_targets.json").Resolve(kind)   # 规则条目支持环境变量/通配符展开
        for target in targets:
            if not Path.Exists(target): continue                   # 不存在即 0，跳过不展示
            size = SumFileSizes(target, skipInUse=true)            # 占用文件跳过不计；目录流式累加
            scanned = FileItem(target, size)
            verdict = Safety.Classify(scanned)                     # 全局口径，见 §4.3
            yield JunkItem(NewGuid(), kind, Browser=null, target, verdict, size,
                           DefaultSelected=(verdict.Level==High), Describe(kind))
        progress.Report(Enumerating, ++doneKinds)
```

**各目标路径来源（外置 cleanup_targets.json，此处为语义基准）**：

| Kind | 路径语义 |
| --- | --- |
| Temp | `%TEMP%`（当前用户）、`%TMP%` |
| WindowsTemp | `%SystemRoot%\Temp` |
| Prefetch | `%SystemRoot%\Prefetch\*.pf`（仅文件，不递归） |
| ThumbnailCache | `%LocalAppData%\Microsoft\Windows\Explorer\thumbcache_*.db` |
| WindowsErrorReporting | `%ProgramData%\Microsoft\Windows\WER\ReportQueue`、`ReportArchive` |
| MemoryDump | `%SystemRoot%\Minidump\*.dmp`、`%SystemRoot%\MEMORY.DMP` |
| RecycleBin | 虚拟目标 "$RecycleBin"（经 RecycleBinFacade 统计与清空，逐盘汇总） |

### 4.2 扫描浏览器隐私数据（FR-CLEAN-02）

```
ScanBrowserJunk(browserTargets, progress, ct):
    for bt in browserTargets ?? 五浏览器默认目标:
        if ct.IsCancellationRequested: return
        install = Rules.ResolveBrowser(bt.Browser)              # 用户数据根目录（Chromium: User Data；Firefox: Profiles）
        if not Directory.Exists(install): continue              # 未安装/从未运行 → 跳过

        for profile in EnumProfiles(install):                   # Default / Profile 1 等；Firefox 遍历 *.default-release
            for kind in bt.DataKinds ?? [BrowserCache]:         # 默认扫描仅 Cache（其余类型按需勾选才扫）
                paths = Rules.ResolveBrowserData(bt.Browser, kind, profile)
                size = SumFileSizes(paths, skipInUse=true)
                if size == 0: continue                          # 无数据不展示，减少噪音
                verdict = Safety.Classify(FileItem(paths, size))
                defaultSel = (kind == BrowserCache)             # 隐私数据仅缓存默认勾选（FR-02）
                yield JunkItem(NewGuid(), kind, bt.Browser, paths, verdict, size, defaultSel,
                               DescribeBrowser(bt.Browser, kind, profile))
        progress.Report(Enumerating, ++doneBrowsers)
```

**浏览器数据位置规则（编码阶段随 cleanup_targets.json 落，需实测版本差异）**：

| Kind | Chromium 系（Edge/Chrome/Brave/Opera） | Firefox |
| --- | --- | --- |
| BrowserCache | `User Data\<profile>\Cache`、`Code Cache`、`GPUCache` | `Profiles\<p>\cache2` |
| BrowserCookies | `Network\Cookies`（SQLite） | `cookies.sqlite` |
| BrowserHistory | `History`（SQLite） | `places.sqlite`（历史部分） |
| BrowserDownloads | `History` 内 downloads 表（仅清记录） | `places.sqlite` |
| BrowserFormData | `Web Data`（autofill 表） | `formhistory.sqlite` |
| BrowserPasswords | `Login Data`（SQLite） | `logins.json` + `key4.db` |

注意 Firefox 的 History/Downloads 共库（places.sqlite），清理一类不能物理删库，需 OpQueue 执行时按表删除（规则条目标记 `mode=sqlite-table`，编码阶段在浏览器 Facade 中支持）。

### 4.3 默认勾选与安全分级

两层概念分离：

```
安全等级（Verdict.Level，全局口径，模块不自定）：
    High    → %TEMP%/WindowsTemp 非占用文件、缩略图、WER、内存转储、回收站、浏览器缓存/隐私库
              （均可重建，不在系统关键目录；FR §3.0 明确浏览器缓存为 High 代表项）
    Medium  → Prefetch（删除后系统重建期内启动性能略降，业界惯例默认不勾；
                        通过 cleanup_targets.json 条目标记提示分类器判 Medium）
    Low     → 命中 safety_critical_paths 的目标（正常配置不会出现，属兜底）

产品默认勾选（DefaultSelected）：
    系统项：Verdict.Level == High
    浏览器项：Kind == BrowserCache（Cookie/历史/密码等隐私数据 FR-02 强制默认不勾，与等级无关）

用户调整约束沿用 §3.0：等级仅上调不下调；勾选可自由调整。
```

### 4.4 清理入队执行（FR-CLEAN-01/02）

```
EnqueueCleanAsync(items, options, progress, ct):
    if items.Empty(): throw JunkItemsEmptyException()

    # 1. 浏览器运行检测（仅针对 items 涉及的浏览器）
    browsers = items.Where(i => i.Browser != null).Select(i => i.Browser).Distinct()
    running = ProcessFacade.FindByImageNames(browsers.Select(ImageNameOf))   # msedge/chrome/firefox/brave/opera.exe
    if running.Any():
        if not options.KillRunningBrowsers:
            throw BrowserRunningException(running)
        killActions = browsers.Select(b => OperationAction(
            Kind=KillProcess,
            Target=ImageNameOf(b),            # 按进程映像名匹配（含子进程），区别于卸载场景的目录匹配
            OriginalValue=null))
    else:
        killActions = []

    # 2. 垃圾项映射 actions
    cleanActions = for item in items:
        if item.Kind == RecycleBin:
            OperationAction(Kind=DeleteFile, Target="$RecycleBin", SizeBytes=item.EstimatedSizeBytes)
            # OpQueue 消费时识别虚拟目标 → RecycleBinFacade.Empty(SHEmptyRecycleBin)
        else:
            OperationAction(Kind=DeleteFile, Target=item.Target, SizeBytes=item.EstimatedSizeBytes, Sha256=null)

    plan = OperationPlan(
        PlanId=NewGuid(), ModuleId="cleanup", FrId="FR-CLEAN-01",  # 02 由 actions 中浏览器项体现，报告按 Kind 拆分
        Description=$"清理 {items.Count} 项垃圾，预计释放 {FormatBytes(Sum(size))}",
        SafetyLevel=Min(items.Select(i => i.Verdict.Level)),
        Actions=killActions + cleanActions,        # KillProcess 必须排在删除之前
        PreviewRequired=true, UserConfirmed=false)

    # 3. TRUST-02 预演（逐项展示路径/大小/等级；含 Cookie/密码项时面板额外提示隐私影响）
    preview = await ctx.Trust.PreviewAsync(plan, ct)
    if not preview.Confirmed: return Cancelled

    # 4. OpQueue 串行执行；单项失败不阻塞。影子备份策略按规则条目：
    #    系统临时垃圾 PathRecordOnly（海量小文件复制成本高且本就可丢弃）；
    #    浏览器 Cookie/密码/历史库 Copy（用户可能误勾隐私项，保留恢复可能）
    result = await ctx.OpQueue.EnqueueAsync(plan, progress, ct)

    # 5. 清理报告（实际释放 = Σ 已删项大小；含清理前后磁盘剩余空间对比，FR-CLEAN-06）
    return result with { Report = BuildCleanReport(plan, result) }
```

### 4.5 一键清理（FR-CLEAN-06）

```
OneClickCleanAsync(progress, ct):
    items = await ScanJunkAsync(JunkScope(null, null), progress, ct).ToListAsync(ct)
    picked = items.Where(i => i.DefaultSelected).ToList()     # High 系统项 + 各浏览器缓存
    if picked.Empty():
        ctx.Trust.Broadcast(Info("没有可清理的垃圾"))
        return OperationResult.Success(report=Empty)
    return await EnqueueCleanAsync(picked, CleanOptions(KillRunningBrowsers=false), progress, ct)
    # 浏览器在运行时：缓存中被占用文件自动跳过（skipInUse），不强制结束浏览器；
    # 报告列出"因浏览器运行跳过 <n> MB，关闭后可再次清理"。
```

---

## 5. 边界条件

| 场景 | 处理 |
| --- | --- |
| 目标路径不存在（从未产生该类垃圾） | 跳过不展示，不作为 0 项噪音 |
| 文件被占用（%TEMP% 活动文件/浏览器运行） | 扫描与删除均跳过占用文件，不计大小不报错；报告汇总跳过量 |
| 浏览器在运行且未选强制结束 | 仅当勾选了该浏览器隐私项时抛 BrowserRunningException；纯系统垃圾不检测浏览器 |
| KillProcess 按名匹配过宽（chrome.exe 与其他 Chromium） | 仅结束 items 中对应浏览器的进程名；进程名冲突项通过安装路径二次校验 |
| 多浏览器 profile（Default/Profile 1/工作账号） | 全部 profile 枚举，逐项展示并标注 profile 名 |
| Firefox places.sqlite 历史/下载共库 | 标记 sqlite-table 模式按表清理，不删库；Cookie 清理时浏览器必须关闭（SQLite 锁） |
| 回收站跨多盘 | RecycleBinFacade 逐盘统计汇总；清空逐盘执行，单盘失败不阻塞 |
| MEMORY.DMP 权限 | 管理员所有，提权后删除；权限不足跳过并提示 |
| 规则条目路径展开失败（环境变量缺失） | 该条目跳过并落 warning 日志，不阻塞其他条目 |
| 扫描中取消（ct） | 返回已收集项并标记"扫描未完成"，可直接对已收集项执行清理 |
| 便携版运行 | 功能正常；仅清理本机垃圾，不写注册表/右键菜单 |
| 影子备份空间不足（Copy 类隐私库） | 该项降级为不删除（保守），列入报告；PathRecordOnly 项不受影响 |

---

## 6. 错误码

所有异常继承 [SysMgrException](../../src/SysMgr.Core/Common/SysMgrException.cs)（TDD §6.2）。子类落 `SysMgr.Modules.Cleanup.Errors` 命名空间，批次 1 编码阶段补。

| 异常 | 触发 | 用户提示 |
| --- | --- | --- |
| CleanupException | 模块基类 | "垃圾清理失败：<原因>" |
| JunkItemsEmptyException | 未勾选任何项即提交 | "请先勾选要清理的项目" |
| BrowserRunningException | 勾选浏览器项且其进程运行、未选强制结束 | "浏览器正在运行，请先关闭或开启强制结束浏览器进程" |
| RecycleBinEmptyFailedException | 回收站清空失败（权限/COM） | "回收站清空失败，请手动清空后重试" |
| BrowserProfileLockedException | Firefox SQLite 库锁定（浏览器后台进程残留） | "浏览器数据被锁定，请确认浏览器（含后台进程）已完全退出" |
| CleanRuleResolveException | cleanup_targets.json 条目格式错误/展开失败 | "清理规则异常已跳过相关项，请检查规则文件版本" |

---

## 7. 与横切层交互

| 横切层组件 | 交互点 | 说明 |
| --- | --- | --- |
| ISafetyClassifier | 每候选项 Classify | 全局口径；Prefetch Medium 通过规则条目提示，不在模块硬编码 |
| ITrustBroadcaster.PreviewAsync | EnqueueCleanAsync 入队前 | TRUST-02 预演；隐私项（Cookie/密码）面板额外提示 |
| ITrustBroadcaster.Broadcast | 扫描进度 + 清理报告 | TRUST-01 直播；一键清理无可清项时 Info 提示 |
| IDestructiveOpQueue | EnqueueCleanAsync → EnqueueAsync | KillProcess 先于 DeleteFile；单项失败不阻塞 |
| IRollbackEngine | Copy 类条目删除前快照 | 隐私库 Copy；系统垃圾 PathRecordOnly（规则条目配置） |
| IOperationLogger | 全程落 history.jsonl | 扫描/跳过/删除/报告可回放 |
| IRuleSetManager | cleanup_targets.json 加载与热更新 | 路径/profile 发现/回滚策略/SQLite 表模式全外置 |
| IConfigStore | 开关与阈值 | 浏览器列表、默认数据类型、占用文件策略 |

---

## 8. 实现优先级（编码阶段）

1. cleanup_targets.json 系统垃圾 7 条目 + ScanSystemJunk（空间估算 + 占用跳过）
2. 浏览器 profile 发现 + ScanBrowserJunk（先 Chromium 系四浏览器，Firefox 单独适配）
3. EnqueueCleanAsync（TRUST-02 预演 + DeleteFile + 回收站特化）
4. CleanOptions 浏览器运行检测与 KillProcess
5. Firefox SQLite 按表清理（places/formhistory）
6. FR-CLEAN-06 一键清理 + 前后空间对比报告

每步完成后 `dotnet test`（SysMgr.Modules.Tests）验证，保持 0 警告 0 错误。

---

## 9. 待确认事项

- [ ] cleanup_targets.json schema 设计（条目字段：kind/path 展开/profile 模式/回滚策略/sqlite-table 标记），编码阶段先定 schema 再填条目
- [ ] 各浏览器 2026 年现行版本数据目录实测（Opera/Brave 分支路径、Edge 多 profile 结构、Firefox profile 名规则）
- [ ] Prefetch 判 Medium 的实现方式（分类器是否支持规则条目携带等级提示，还是由路径规则推导，编码阶段定）
- [ ] 浏览器清理回滚策略：隐私库 Copy 备份的实际价值与磁盘成本（Cookie/密码库通常 <1MB，可接受；历史库可能数十 MB）
- [ ] 一键清理时浏览器运行的产品策略（当前方案：跳过占用项不结束进程；备选：弹一次询问），编码阶段做用户测试后定
- [ ] Chromium 下载记录仅清表不删文件的实现（History SQLite downloads 表，需浏览器关闭；V1 是否随历史同库清理）
