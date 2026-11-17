# 03 C 盘瘦身模块详设（CSlim）

| 项目 | 内容 |
| --- | --- |
| 文档版本 | v0.1（V1 详设批次 1 模块 3） |
| 编写日期 | 2026-09-28 |
| 模块命名空间 | `SysMgr.Modules.CSlim` |
| 分层位置 | L2 功能模块层（TDD §1.2） |
| FR 追溯 | FR-CSLIM-01 C 盘占用分析 / FR-CSLIM-02 大文件定位与迁移 / FR-CSLIM-05 Windows.old 与更新残留 / FR-CSLIM-08 默认保存路径建议 |
| 配套文档 | [requirements.md](../requirements.md) §3.4 / [technical-design.md](../technical-design.md) §2-§5 |
| 代码骨架 | [ICSlimService.cs](../../src/SysMgr.Modules/CSlim/ICSlimService.cs) |

> 本文是 V1 MVP 详设批次 1 第 3 模块，与 [04-cleanup](04-cleanup.md) 同期产出（编号按 FR 章节号保留）。本模块接口与数据类已同步落到 [src/SysMgr.Modules/CSlim/](../../src/SysMgr.Modules/CSlim/)，sysmgr.sln 持续可编译。

---

## 1. 模块概览

### 1.1 职责

聚焦"释放 C 盘空间"最高频痛点：分析 C 盘空间分布、定位大文件并经迁移引擎转移到其他盘、清理 Windows.old 与更新残留、检测并建议修改新内容默认保存位置。

### 1.2 V1 范围与非目标

V1 范围以 TDD §9 追溯表为准（FR-CSLIM-01/02/05/08）：

| 范围内（V1） | 范围外 |
| --- | --- |
| FR-CSLIM-01 C 盘占用分析（五分类 + 大小标注） | FR-CSLIM-03 休眠文件管理（V1.5，powercfg） |
| FR-CSLIM-02 大文件定位与迁移（≥100MB，关键目录排除，走 Migration 引擎） | FR-CSLIM-04 虚拟内存转移（V1.5，PagingFiles 改写 + 重启） |
| FR-CSLIM-05 Windows.old 与更新残留清理（强制风险提示） | FR-CSLIM-06 系统还原点管理（V1.5，VSS） |
| FR-CSLIM-08 默认保存路径建议（仅建议，一键应用需确认） | FR-CSLIM-07 用户文件夹位置迁移（V1.5，Known Folder 改写 + Migration） |
| — | 与 §3.5 Treemap 下钻联动（V1.5 随可视化模块） |

### 1.3 依赖

- **横切层（L3，已落骨架）**：[ITrustBroadcaster](../../src/SysMgr.Core/Trust/ITrustBroadcaster.cs)（TRUST-02 预演 + TRUST-01 直播）/ [IDestructiveOpQueue](../../src/SysMgr.Core/Queue/IDestructiveOpQueue.cs) / [IRollbackEngine](../../src/SysMgr.Core/Rollback/IRollbackEngine.cs)（影子备份）/ [IOperationLogger](../../src/SysMgr.Core/Logging/IOperationLogger.cs) / [IConfigStore](../../src/SysMgr.Core/Config/IConfigStore.cs)
- **跨模块契约**：[IModuleCoordinator.RequestMigrationAsync](../../src/SysMgr.Modules/IModuleCoordinator.cs) + [MigrationRequest](../../src/SysMgr.Modules/MigrationRequest.cs)（TDD §1.3 耦合点，由模块 5 Migration 实现）
- **L4 系统访问层**：FileSystemSafe（枚举/空间统计，长路径封装，TDD §6.3）、DriveInfoFacade（固定盘/剩余空间）、KnownFolderFacade（新内容保存位置读取/改写，新增）
- **复用规则**：`rules\cleanup_targets.json`（临时文件分类与 FR-CLEAN 同源）、`rules\safety_critical_paths.json`
- **无新增规则文件**：大文件排除清单为产品硬约束（见 §4.2），不做用户可配外置

---

## 2. 接口契约

完整签名见 [ICSlimService.cs](../../src/SysMgr.Modules/CSlim/ICSlimService.cs)。继承 [IModuleService](../../src/SysMgr.Modules/IModuleService.cs)。

```csharp
public interface ICSlimService : IModuleService
{
    Task<CDriveAnalysis> AnalyzeCDriveAsync(IProgress<ScanProgress>? progress, CancellationToken ct);
    IAsyncEnumerable<LargeFileItem> FindLargeFilesAsync(long minSizeBytes, IProgress<ScanProgress>? progress, CancellationToken ct);
    Task<OperationResult> EnqueueLargeFileMigrationAsync(IReadOnlyList<LargeFileItem> items, string destinationRoot, IProgress<OperationProgress>? progress, CancellationToken ct);
    Task<OperationResult> EnqueueWindowsResidualCleanAsync(IReadOnlyList<WindowsResidualItem> items, IProgress<OperationProgress>? progress, CancellationToken ct);
    Task<IReadOnlyList<SaveLocationSuggestion>> GetSaveLocationSuggestionsAsync(CancellationToken ct);
    Task<OperationResult> ApplySaveLocationSuggestionsAsync(IReadOnlyList<SaveLocationCategory> categories, string targetDrive, IProgress<OperationProgress>? progress, CancellationToken ct);
}
```

**方法链语义**（TDD §3.2）：`AnalyzeCDriveAsync`（总览）→ `FindLargeFilesAsync(threshold)`（定位）→ `EnqueueLargeFileMigrationAsync`（迁移）。其余两组方法分别服务 FR-05（扫描检测 → 清除确认）与 FR-08（建议 → 应用确认）。

---

## 3. 数据结构

### 3.1 C 盘分析结果

详见 [CDriveAnalysis.cs](../../src/SysMgr.Modules/CSlim/CDriveAnalysis.cs) 与 [CDriveCategory.cs](../../src/SysMgr.Modules/CSlim/CDriveCategory.cs)。

| 类型 | 字段 | 说明 | FR |
| --- | --- | --- | --- |
| CDriveAnalysis | DriveRoot / TotalBytes / FreeBytes | C 盘根与容量（DriveInfo 读取） | 01 |
| CDriveAnalysis | Categories | 五分类占用列表（CDriveCategorySize） | 01 |
| CDriveAnalysis | AnalyzedAt | 分析时间（报告与缓存判定用） | 01 |
| CDriveCategorySize | Category | SystemReserved/InstalledApps/UserFiles/TempFiles/Other | 01 |
| CDriveCategorySize | SizeBytes / Description | 占用大小 / UI 展示说明 | 01 |

### 3.2 大文件条目

详见 [LargeFileItem.cs](../../src/SysMgr.Modules/CSlim/LargeFileItem.cs)。

| 字段 | 类型 | 说明 | FR |
| --- | --- | --- | --- |
| Path | string | 完整路径（长路径走 \\?\ 封装） | 02 |
| SizeBytes | long | 文件大小（筛选阈值） | 02 |
| LastWriteTime | DateTimeOffset | 最后写入时间（不取 Last Access，默认禁用不可靠） | 02 |
| Kind | LargeFileKind | Video/Image/Document/Installer/Database/Other（按扩展名） | 02 |

### 3.3 Windows 残留项

详见 [WindowsResidualItem.cs](../../src/SysMgr.Modules/CSlim/WindowsResidualItem.cs)。

| 字段 | 类型 | 说明 | FR |
| --- | --- | --- | --- |
| Kind | WindowsResidualKind | WindowsOld/UpdateDownload/WinSxSBackup/WinREAgent/DeliveryOptimization | 05 |
| Target | string | 目标路径 | 05 |
| SizeBytes | long | 占用大小 | 05 |
| Warning | string | 强制提示文案（TRUST-02 原样展示） | 05 |

### 3.4 保存位置建议

详见 [SaveLocationSuggestion.cs](../../src/SysMgr.Modules/CSlim/SaveLocationSuggestion.cs)。

| 字段 | 类型 | 说明 | FR |
| --- | --- | --- | --- |
| Category | SaveLocationCategory | NewApps/NewDocuments/NewMusic/NewPhotos/NewVideos/NewMovies/OfflineMaps | 08 |
| CurrentDrive | string | 当前保存盘（系统关联卷） | 08 |
| SuggestedDrive | string | 建议盘（剩余空间最大的第二固定盘） | 08 |
| CanApply | bool | 是否可应用（无第二固定盘/已不在 C 盘时 false） | 08 |
| Description | string | UI 展示说明 | 08 |

---

## 4. 关键流程伪代码

### 4.1 C 盘占用分析（FR-CSLIM-01）

```
AnalyzeCDriveAsync(progress, ct):
    drive = DriveInfoFacade.GetSystemDrive()              # C:\，Total/Free
    sizes = Dictionary<CDriveCategory, long>()

    # 1. 系统/保留（SystemReserved）
    sizes[SystemReserved] =
        EstimateDirSize(C:\Windows, timeout 30s)          # 含 WinSxS/Installer，大目录限时降级
        + FileSize(C:\pagefile.sys, ifExists)
        + FileSize(C:\hiberfil.sys, ifExists)
        + FileSize(C:\swapfile.sys, ifExists)

    # 2. 已安装应用（InstalledApps）：以 Uninstall 清单 EstimatedSize 汇总为主，
    #    Program Files 两目录做兜底（清单缺失项），去重：注册表已有 InstallLocation 的不重复计
    sizes[InstalledApps] = Sum(uninstallItems.EstimatedSize)
                         + UncoveredProgramFilesBytes()

    # 3. 临时文件（TempFiles）：复用 cleanup_targets 规则解析的系统/浏览器缓存目标
    sizes[TempFiles] = SumFileSizes(CleanupRules.ResolveTargets())

    # 4. 用户文件（UserFiles）：用户 profile 总量 - AppData - 已计入 Temp 的部分
    sizes[UserFiles] = EstimateDirSize(UserProfile, 30s)
                     - EstimateDirSize(%LocalAppData%) - EstimateDirSize(%AppData%)
                     + sizes[TempFiles 中位于用户目录的部分]   # AppData 内缓存已在 Temp 计，避免漏算

    # 5. 其他（Other）：剩余占用兜底
    accounted = sizes 四项之和
    sizes[Other] = max(0, (drive.Total - drive.Free) - accounted)

    progress.Report(Done)
    return CDriveAnalysis(C:\, Total, Free, BuildList(sizes), now)
```

**降级策略**：任一路径估算超时/拒绝访问 → 该项实际值跳过（按 0 处理并在 Description 标注"部分未完成"），差额自动落入 Other，分析永不失败。

### 4.2 大文件定位（FR-CSLIM-02）

```
FindLargeFilesAsync(minSizeBytes, progress, ct):
    for file in FileSystemSafe.EnumFilesRecursive(C:\):
        if ct.IsCancellationRequested: return
        if Excluded(file): continue                      # 见排除清单
        if file.Size < minSizeBytes: continue
        yield LargeFileItem(file.Path, file.Size, file.LastWriteTime, ClassifyByExtension(file))
    progress.Report(Done)
```

**默认排除清单（FR-CSLIM-02 硬约束：设置中可见，默认不可取消）**：

- `C:\Windows\*`（含 System32、SysWOW64、WinSxS、Installer、SoftwareDistribution、Recovery、Boot、WinSxS\Backup）
- `C:\Program Files` / `C:\Program Files (x86)` 内系统组件目录
- `C:\$Recycle.Bin`、`C:\System Volume Information`、`C:\Recovery`
- `pagefile.sys`、`hiberfil.sys`、`swapfile.sys`

排除匹配使用**路径前缀规范化比较**（含 junction 展开后的真实路径，防止通过联接绕过）。

### 4.3 大文件迁移（FR-CSLIM-02）

```
EnqueueLargeFileMigrationAsync(items, destinationRoot, progress, ct):
    if items.Empty(): throw CslimItemsEmptyException()
    if not DriveInfoFacade.IsFixedWritable(destinationRoot):
        throw InvalidMigrationTargetException(destinationRoot)
    if Sum(items.Size) > DriveInfo.FreeBytes(destinationRoot):
        throw InsufficientDestinationSpaceException()     # 预检，避免迁到一半失败

    request = MigrationRequest(
        RequestId=NewGuid(), Kind=File,
        Sources=items.Select(i => i.Path),
        Destination=destinationRoot,                      # 保留原文件名；同名冲突由迁移模块处理
        CreateJunction=false,                             # 单文件迁移不建联接
        DeleteSourceAfterVerify=true)

    # 经协调器路由到 Migration 模块（TRUST-02 预演 + 复制 + SHA-256 校验 + 删源 + 回滚均在该模块）
    return await ctx.Coordinator.RequestMigrationAsync(request, progress, ct)
```

### 4.4 Windows 残留清除（FR-CSLIM-05）

```
DetectWindowsResiduals():                                # UI 进入页面时调用
    items = []
    if Exists(C:\Windows.old):
        items.Add(WindowsResidualItem(WindowsOld, C:\Windows.old, Size,
                    "清理后将无法回退到上一个 Windows 版本"))
    if Exists(SoftwareDistribution\Download): items.Add(..., "已下载的更新安装缓存，可安全删除")
    if Exists(WinSxS\Backup): items.Add(..., "组件备份，删除后无法卸载部分更新")
    if Exists(C:\$WinREAgent): items.Add(..., "更新临时文件，可安全删除")
    if Exists(DeliveryOptimization\Cache): items.Add(..., "传递优化缓存，可安全删除")
    return items

EnqueueWindowsResidualCleanAsync(items, progress, ct):
    if items.Any(i => i.Kind == WindowsOld):             # Windows.old 二次强提示
        # TRUST-02 面板对该项固定展示 Warning + 需单独勾选确认
    actions = items.Select(i => OperationAction(Kind=DeleteFile, Target=i.Target,
                                                SizeBytes=i.SizeBytes, Sha256=null))
    plan = OperationPlan(NewGuid(), "cslim", "FR-CSLIM-05",
                         $"清理 {items.Count} 项 Windows 更新残留", Medium, actions, true, false)
    preview = await ctx.Trust.PreviewAsync(plan, ct)
    if not preview.Confirmed: return Cancelled
    return await ctx.OpQueue.EnqueueAsync(plan, progress, ct)   # 删除前影子备份 Copy
```

### 4.5 默认保存位置建议（FR-CSLIM-08）

```
GetSaveLocationSuggestionsAsync(ct):
    fixedDrives = DriveInfoFacade.GetFixedDrives().Where(d => d != C:\)   # 第二固定盘候选
    target = fixedDrives.OrderByDescending(FreeBytes).FirstOrDefault()
    for category in Enum<SaveLocationCategory>():
        current = KnownFolderFacade.GetAssociatedDrive(category)          # Known Folder 当前路径所在卷
        yield SaveLocationSuggestion(category, current, target?.Root,
                                     CanApply=(target != null and current == C:\),
                                     Describe(category))

ApplySaveLocationSuggestionsAsync(categories, targetDrive, progress, ct):
    # 不直接改注册表：统一走 SHSetKnownFolderPath（系统会处理新位置创建与后续重定向）
    actions = categories.Select(c => OperationAction(
        Kind=SetRegistry, Key=KnownFolderKeyOf(c),
        OriginalValue=KnownFolderFacade.GetPath(c),
        NewValue=KnownFolderFacade.BuildRedirectedPath(c, targetDrive)))
    plan = OperationPlan(..., SafetyLevel=Medium, actions, PreviewRequired=true, ...)
    preview = await ctx.Trust.PreviewAsync(plan, ct)      # 展示每个类别的新旧路径
    if not preview.Confirmed: return Cancelled
    result = await ctx.OpQueue.EnqueueAsync(plan, progress, ct)
    # 注意：SetRegistry 仅兜底记录，真正执行由 KnownFolderFacade.SetPath 完成（OpQueue 特化 action）
    return result
```

**仅建议不强制**：V1 不主动搬运已存在的用户文件（新位置只对"今后产生的新内容"生效），因此无需 Migration 介入，秒级生效。

---

## 5. 边界条件

| 场景 | 处理 |
| --- | --- |
| 只有一个固定盘（无第二盘） | 迁移入口禁用；保存建议 CanApply=false，文案"请先接入第二块硬盘" |
| 目标盘剩余空间不足 | 迁移预检抛 InsufficientDestinationSpaceException，不开始复制 |
| C:\Windows 目录巨大估算超时（30s） | 降级按已统计部分计，差额落 Other，不阻塞分析 |
| 大文件扫描遇到 reparse point（junction 循环） | 不跟随重解析点，按真实路径去重 |
| 通过 junction 访问 C:\Windows 试图绕过排除 | 排除比较展开真实路径后进行 |
| Windows.old 不存在 | FR-05 列表无该项（系统已自动清理或未升级过） |
| 同名文件已存在于目标目录 | Migration 模块冲突处理（覆盖需在 TRUST-02 列出；默认改名跳过） |
| 保存位置类别当前已在非 C 盘 | CanApply=false，不列入应用清单 |
| Known Folder 改写失败（策略/权限） | 该类别失败不回滚已成功类别，报告逐项列出 |
| 扫描/分析中取消（ct） | 大文件流式扫描返回已收集项；分析为聚合操作，取消即无结果（成本低可重跑） |
| 便携版运行 | 分析/建议只读部分正常；迁移与 Known Folder 改写提权后执行，不写程序自身注册表 |

---

## 6. 错误码

所有异常继承 [SysMgrException](../../src/SysMgr.Core/Common/SysMgrException.cs)（TDD §6.2）。子类落 `SysMgr.Modules.CSlim.Errors` 命名空间，批次 1 编码阶段补。

| 异常 | 触发 | 用户提示 |
| --- | --- | --- |
| CSlimException | 模块基类 | "C 盘瘦身操作失败：<原因>" |
| CslimItemsEmptyException | 未选择任何项即提交 | "请先选择要处理的项目" |
| InvalidMigrationTargetException | 目标盘不可写/非固定盘 | "目标位置不可用，请选择本机其他硬盘上的文件夹" |
| InsufficientDestinationSpaceException | 目标盘剩余空间不足 | "目标盘空间不足，请清理后重试或选择其他盘" |
| WindowsResidualDeleteFailedException | 残留删除失败（权限/占用） | "更新残留清理失败，请重启后以管理员身份重试" |
| KnownFolderRedirectFailedException | Known Folder 改写失败 | "<类别>的保存位置修改失败，已保留原设置" |

---

## 7. 与横切层交互

| 横切层组件 | 交互点 | 说明 |
| --- | --- | --- |
| IModuleCoordinator.RequestMigrationAsync | 大文件迁移 | 路由到 Migration 模块，跨模块不直接 new |
| ITrustBroadcaster.PreviewAsync | FR-05/08 入队前 | TRUST-02 预演；Windows.old 强制 Warning 单独勾选 |
| ITrustBroadcaster.Broadcast | 分析/扫描进度 | TRUST-01 直播 |
| IDestructiveOpQueue | FR-05 删除 + FR-08 改写 | 串行执行，单项失败不阻塞 |
| IRollbackEngine | FR-05 DeleteFile 删除前快照 | Copy 影子备份；FR-08 Known Folder 改写原值可回滚 |
| IOperationLogger | 全程落 history.jsonl | 分析结果/迁移路由/清理记录可回放 |
| IConfigStore | 阈值配置 | 大文件默认 100MB、目录估算超时 30s |

---

## 8. 实现优先级（编码阶段）

1. AnalyzeCDriveAsync（容量读取 + 五分类 + 降级兜底）
2. FindLargeFilesAsync（递归枚举 + 排除清单 + 扩展名分类）
3. EnqueueWindowsResidualCleanAsync（检测 + 强制提示 + OpQueue）
4. MigrationRequest 与协调器对接（依赖模块 5 落地，接口已先行）
5. GetSaveLocationSuggestionsAsync + ApplySaveLocationSuggestionsAsync（KnownFolderFacade）

每步完成后 `dotnet test`（SysMgr.Modules.Tests）验证，保持 0 警告 0 错误。

---

## 9. 待确认事项

- [ ] Known Folder ID 与 SaveLocationCategory 精确映射（NewApps 对应 FOLDERID_UserProgramFiles 等，编码阶段 PoC 验证设置页联动）
- [ ] Windows 11 当前版本"新内容保存位置"的底层存储（Known Folder 重定向 vs 注册表私有值，实测确认 SHSetKnownFolderPath 是否被设置页正确回读）
- [ ] C:\Windows 30s 估算超时在普通机械盘上的覆盖率（不足则对已知大子目录并行估算）
- [ ] 大文件扫描全 C 盘耗时基线（目标 <30s，需 Enum 流式 + 不打开文件内容）
- [ ] DeliveryOptimization 缓存目录在不同 Windows 版本的实际路径与占用统计方式
