# 05 文件迁移引擎模块详设（Migration）

| 项目 | 内容 |
| --- | --- |
| 文档版本 | v0.1（V1 详设批次 1 模块 5） |
| 编写日期 | 2026-09-28 |
| 模块命名空间 | `SysMgr.Modules.Migration` |
| 分层位置 | L2 功能模块层（TDD §1.2） |
| FR 追溯 | FR-MIGRATE-01 文件夹迁移 |
| 配套文档 | [requirements.md](../requirements.md) §3.7 / [technical-design.md](../technical-design.md) §2-§5 |
| 代码骨架 | [IMigrationService.cs](../../src/SysMgr.Modules/Migration/IMigrationService.cs) |

> 本文是 V1 MVP 详设批次 1 第 5 模块（末模块），收口批次 1。本模块接口与数据类已同步落到 [src/SysMgr.Modules/Migration/](../../src/SysMgr.Modules/Migration/)，sysmgr.sln 持续可编译。

---

## 1. 模块概览

### 1.1 职责

提供通用文件/文件夹迁移能力：枚举源内容、预检可行性、按"复制 → SHA-256 校验 → 创建目录联接（可选）→ 删除源文件"顺序执行，任一步失败可回滚。作为引擎被 CSlim 大文件迁移（FR-CSLIM-02）经协调器调用，并可由 UI 直接发起任意文件夹迁移。

### 1.2 V1 范围与非目标

| 范围内（V1） | 范围外 |
| --- | --- |
| FR-MIGRATE-01 文件夹迁移（含 junction 兼容旧路径） | FR-MIGRATE-02 应用迁移（V2：安装目录 + 注册表路径修正 + 不兼容软件警告清单） |
| CSlim 大文件（多文件批量）迁移 | 跨卷 EFS 加密文件迁移（V1 预检阻断并提示） |
| 预检、dry-run 明细、回滚 | NTFS ACL 之外的扩展属性/备用数据流迁移（V1 随文件复制保留，不做逐项校验） |

### 1.3 依赖

- **横切层（L3，已落骨架）**：[IDestructiveOpQueue](../../src/SysMgr.Core/Queue/IDestructiveOpQueue.cs) / [ITrustBroadcaster](../../src/SysMgr.Core/Trust/ITrustBroadcaster.cs)（TRUST-02 预演 + TRUST-01 直播）/ [IRollbackEngine](../../src/SysMgr.Core/Rollback/IRollbackEngine.cs)（策略 Copy/VssShadow/PathRecordOnly）/ [IOperationLogger](../../src/SysMgr.Core/Logging/IOperationLogger.cs) / [IConfigStore](../../src/SysMgr.Core/Config/IConfigStore.cs)
- **调用入口**：[IModuleCoordinator.RequestMigrationAsync](../../src/SysMgr.Modules/IModuleCoordinator.cs) 路由（[MigrationRequest](../../src/SysMgr.Modules/MigrationRequest.cs) 为统一请求契约）
- **L4 系统访问层**：FileSystemSafe（枚举/复制/删除，长路径与占用封装，TDD §6.3）、JunctionFacade（创建/删除 reparse point，新增）、DriveInfoFacade（空间预检）
- **无规则文件**：迁移引擎不内置业务路径表，路径完全由请求方给出

---

## 2. 接口契约

完整签名见 [IMigrationService.cs](../../src/SysMgr.Modules/Migration/IMigrationService.cs)。继承 [IModuleService](../../src/SysMgr.Modules/IModuleService.cs)。

```csharp
public interface IMigrationService : IModuleService
{
    Task<MigrationValidation> ValidateAsync(MigrationRequest request, IProgress<ScanProgress>? progress, CancellationToken ct);
    Task<OperationResult> EnqueueMigrationAsync(MigrationRequest request, IProgress<OperationProgress>? progress, CancellationToken ct);
}
```

**调用语义**：

- 协调器 `RequestMigrationAsync`（CSlim 等跨模块场景）→ 直接委托 `EnqueueMigrationAsync`。
- UI 文件夹迁移：可先调 ValidateAsync 展示"将迁移 N 个文件共 X MB + 警告"，用户确认后调 EnqueueMigrationAsync；EnqueueMigrationAsync 内部仍会再做一次预检（防止确认后状态变化）。
- TDD §3.2 早期签名 `MigrateFolderAsync(src, dst, MigrationOptions)` 语义由 `EnqueueMigrationAsync(MigrationRequest(Folder, [src], dst, ...))` 承载。

---

## 3. 数据结构

### 3.1 迁移请求 MigrationRequest

定义在 [MigrationRequest.cs](../../src/SysMgr.Modules/MigrationRequest.cs)（模块根，跨模块共享）。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| RequestId | Guid | 请求 ID（贯穿预检/预演/执行/报告） |
| Kind | MigrationKind | File（多文件到目标目录）/ Folder（单目录整体迁移） |
| Sources | IReadOnlyList&lt;string&gt; | File：源文件列表；Folder：仅一个源目录 |
| Destination | string | File：目标目录（保留文件名）；Folder：目标完整路径 |
| CreateJunction | bool | Folder 迁移后在源位置建 junction（FR-MIGRATE-01 可选项） |
| DeleteSourceAfterVerify | bool | 校验通过后删除源（false=仅复制副本，V1 用于安全场景） |

### 3.2 预检与文件条目

详见 [MigrationValidation.cs](../../src/SysMgr.Modules/Migration/MigrationValidation.cs) / [MigratedFileEntry.cs](../../src/SysMgr.Modules/Migration/MigratedFileEntry.cs)。

| 类型 | 字段 | 说明 |
| --- | --- | --- |
| MigrationValidation | CanMigrate / Issues / Warnings | 阻断问题 / 非阻断提示（同名改名等） |
| MigrationValidation | RequiredBytes / AvailableBytes | 目标卷所需/可用空间（预检对账） |
| MigratedFileEntry | Source / Destination | 文件级源→目标映射（保留相对目录结构） |
| MigratedFileEntry | SizeBytes / Sha256 | 大小与源哈希（复制前算；目标复制后复算） |

---

## 4. 关键流程伪代码

### 4.1 扫描与预检（FR-MIGRATE-01 扫描阶段）

```
ValidateAsync(request, progress, ct):
    issues = []; warnings = []

    # 1. 源合法性
    entries = request.Kind == Folder
        ? EnumFilesRecursive(request.Sources[0])     # 不跟随 junction；含相对路径映射
        : ResolveFiles(request.Sources)
    if entries.Empty(): issues.Add("源位置不存在或为空")
    if AnyUnder(safety_critical_paths, entries):
        issues.Add("源包含系统关键路径，禁止迁移")    # 引擎层兜底，CSlim 排除清单之外的二次防护

    # 2. 目标合法性
    dstRoot = request.Kind == Folder ? request.Destination : request.Destination
    EnsureParentExists(dstRoot)
    if IsUnder(dstRoot, source) or IsUnder(source, dstRoot):
        issues.Add("源与目标存在嵌套关系")            # 防递归复制
    if DriveOf(dstRoot) == DriveOf(source):
        warnings.Add("源与目标在同一卷，迁移不释放该盘空间")

    # 3. 空间与冲突
    required = Sum(entries.Size)
    available = DriveInfoFacade.FreeBytes(DriveOf(dstRoot))
    if required > available: issues.Add("目标卷空间不足")
    for e in entries:
        if Exists(e.Destination):
            warnings.Add($"{e.Destination} 已存在，将改名为 ...(1).后缀")   # 默认改名不覆盖

    # 4. 哈希（源，供复制后校验；大文件流式计算，可与复制合并为单趟读取优化）
    for e in entries: e.Sha256 = Sha256Of(e.Source)
    progress.Report(Done, entries.Count)
    return MigrationValidation(issues.Empty(), issues, warnings, required, available)
```

### 4.2 构造计划与执行（FR-MIGRATE-01 复制/校验/联接/删除）

```
EnqueueMigrationAsync(request, progress, ct):
    validation = await ValidateAsync(request, null, ct)      # 二次预检
    if not validation.CanMigrate: throw MigrationPrecheckFailedException(validation.Issues)

    # 1. 逐文件 actions（MoveFile 由 OpQueue 特化执行：复制 → 哈希校验）
    actions = entries.Select(e => OperationAction(
        Kind=MoveFile, Target=e.Source, NewPath=ResolveConflict(e.Destination),
        SizeBytes=e.SizeBytes, Sha256=e.Sha256))

    # 2. 目录联接（Folder + CreateJunction，必须在全部 MoveFile 成功后执行）
    if request.Kind == Folder and request.CreateJunction:
        actions.Add(OperationAction(Kind=CreateJunction,
                                    Target=request.Sources[0], NewPath=request.Destination))

    # 3. 删除源（DeleteSourceAfterVerify=false 时不删，plan 不含删除）
    if request.DeleteSourceAfterVerify:
        # 源文件删除隐含在 MoveFile 语义中（校验通过后删源）；
        # CreateJunction 时文件先于 junction 删除；空目录最后移除
        plan = OperationPlan(NewGuid(), "migration", "FR-MIGRATE-01",
                             $"迁移 {entries.Count} 个文件到 {dst}", SafetyLevel=Medium,
                             actions, PreviewRequired=true, UserConfirmed=false)

    # 4. TRUST-02 预演（展示文件清单/总大小/warnings；同卷迁移警告置顶）
    preview = await ctx.Trust.PreviewAsync(plan, ct)
    if not preview.Confirmed: return Cancelled

    # 5. OpQueue 串行执行（每个 MoveFile 的阶段见 §4.3）；整体失败触发回滚（见 §4.4）
    return await ctx.OpQueue.EnqueueAsync(plan, progress, ct)
```

### 4.3 单个 MoveFile 的执行阶段（OpQueue 消费侧）

```
ExecuteMoveFile(action):
    # 1. 复制（流式；目标先落 .partial 临时名，完成后改名，防半成品被误用）
    CopyFile(action.Target → action.NewPath + ".partial")
    # 2. 校验：目标 SHA-256 == action.Sha256；大小一致
    if Sha256Of(partial) != action.Sha256:
        Delete(partial); throw MigrationVerifyFailedException(action.Target)
    Rename(partial → ResolveConflict(action.NewPath))
    # 3. 删除源（MoveFile 语义；删除前由 IRollbackEngine 按文件大小选策略留底：
    #    <500MB Copy；≥500MB VssShadow 可用则卷影，否则 PathRecordOnly 仅记录）
    Rollback.Snapshot(action)
    DeleteFile(action.Target)
```

### 4.4 失败回滚（FR-MIGRATE-01 硬约束）

```
OnFailure(failedAction):
    # 源尚未删除（复制/校验阶段失败）→ 删除已产生的目标半成品即可，源原样保留
    # 源已删除（删除之后的步骤失败，如 junction 创建失败）：
    #   从回滚快照恢复源文件 → 再清理目标
    Rollback.Restore(planId)
    PurgePartialDestinations()
    return RollbackResult(RolledBack=true, RestoredEntries=n)

# 幂等性：重跑同一 RequestId 时按条目状态跳过已完成文件（报告标注"断点续迁"）
```

---

## 5. 边界条件

| 场景 | 处理 |
| --- | --- |
| 源/目标同卷 | 允许执行（用户可能想整理目录），TRUST-02 置顶警告"不释放空间" |
| 目标存在同名文件 | 默认改名（name (1).ext）并在 warnings 列出；永不静默覆盖 |
| 目标盘空间不足 | 预检阻断不开始复制；执行中磁盘被占满导致失败 → 已完成文件保留，失败项回滚 |
| 源文件被占用 | MoveFile 复制可读即成功；删源被锁时该文件保留在源处，报告"待手动删除"，不算失败 |
| 源含 junction/symlink | 默认复制其内容副本（不把联接迁成联接）；源为 reparse point 根时按引用复制并警告 |
| 源含 EFS 加密文件 | 跨用户/目标卷不支持解密时预检阻断，提示先解密 |
| 长路径（>260） | 全程 \\?\ 前缀封装（TDD §6.3 FileSystemSafe） |
| junction 创建失败（权限/源父目录不存在） | 触发整 plan 回滚（已删源恢复）；目标副本保留供重试 |
| 目标为可移动/网络盘 | 允许但 warning 提示稳定性；可移动盘剩余空间预检 |
| 用户取消（ct） | 已完成文件保留，未完成项不复制不删源；报告支持断点续迁 |
| 便携版运行 | 功能正常；迁移操作与程序自身位置无关 |

---

## 6. 错误码

所有异常继承 [SysMgrException](../../src/SysMgr.Core/Common/SysMgrException.cs)（TDD §6.2）。子类落 `SysMgr.Modules.Migration.Errors` 命名空间，批次 1 编码阶段补。

| 异常 | 触发 | 用户提示 |
| --- | --- | --- |
| MigrationException | 模块基类 | "文件迁移失败：<原因>" |
| MigrationPrecheckFailedException | 预检存在阻断项 | "迁移条件不满足：<逐条 Issues>" |
| MigrationVerifyFailedException | 复制后 SHA-256 不一致 | "文件校验失败，已中止迁移以保证数据一致" |
| JunctionCreateFailedException | 目录联接创建失败 | "路径联接创建失败，已回滚迁移；文件副本保留在目标位置" |
| MigrationSourceInUseException | 源文件删除被锁定（非阻断） | "<n> 个文件被占用未能删除，关闭程序后可手动清理源目录" |

---

## 7. 与横切层交互

| 横切层组件 | 交互点 | 说明 |
| --- | --- | --- |
| IDestructiveOpQueue | EnqueueMigrationAsync → EnqueueAsync | MoveFile 串行；CreateJunction 排末尾 |
| ITrustBroadcaster.PreviewAsync | 入队前 | TRUST-02 展示清单/大小/warnings，二次确认 |
| ITrustBroadcaster.Broadcast | 每文件复制/校验/删除进度 | TRUST-01 直播（大文件阶段百分比） |
| IRollbackEngine | MoveFile 删源前 Snapshot | 按大小选 Copy/VssShadow/PathRecordOnly；失败 Restore |
| IOperationLogger | 全程落 history.jsonl | 预检/迁移/回滚/断点续迁可回放 |
| IConfigStore | 阈值配置 | Copy 上限 500MB、.partial 命名规则、同名改名策略 |

---

## 8. 实现优先级（编码阶段）

1. ValidateAsync（枚举 + 路径/空间/冲突预检 + 源哈希）
2. OpQueue 侧 MoveFile 执行（.partial 复制 + SHA-256 校验 + 删源）
3. IRollbackEngine 三策略对接与失败回滚
4. CreateJunction（JunctionFacade + 回滚联动）
5. 协调器 RequestMigrationAsync 对接 CSlim
6. 断点续迁（按 RequestId 状态恢复）

每步完成后 `dotnet test`（SysMgr.Modules.Tests）验证，保持 0 警告 0 错误。

---

## 9. 待确认事项

- [ ] 大文件哈希性能（复制前算 SHA 占整趟 IO；编码阶段验证"边复制边算"单趟方案的正确性与速度）
- [ ] VSS 在普通家用环境的可用率（VssShadow 降级 PathRecordOnly 的用户提示文案，需确认误回滚风险）
- [ ] junction 与杀软/安全软件兼容性（部分安全软件拦截 reparse point 创建，编码阶段实测主流环境）
- [ ] 文件夹时间戳/空目录/ACL 的保留策略（复制后是否同步目录属性，V1 做到什么程度）
- [ ] 断点续迁状态存储位置（随 Rollback 目录还是 Config，编码阶段定）
- [ ] CSlim 批量文件迁移的原子性边界：V1 以"单文件原子、整批尽力"为准，确认与产品口径一致
