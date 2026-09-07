# CyberTechRep 设置持久化审计（入口 → 保存点 → 加载点）

> 审计范围：任何入口（设置页/快捷菜单/拖拽回写/可见性同步/导入恢复/首次引导/连接热更新）修改的设置，在应用重启后必须还原。
> 持久化机制：插件自管 JSON —— `%LOCALAPPDATA%\ClassIsland\Plugins\classisland.classing\data\settings.json`，由 `Services\Maintenance\SettingsService.cs` 管理（原子写入：同目录临时文件 + `File.Move` 覆盖 + 有界重试）。

## 一、总览：读写的单一来源

- **内存单一来源**：`SettingsService.Current`（`AppSettings`）。各设置页以 `Settings.*` 绑定路径直接读写该活实例的分组对象。
- **保存**：所有入口最终汇入 `SettingsService.SaveAsync()`（`SettingsService.cs:54`）→ `WriteAtomicallyAsync`（`SettingsService.cs:211`，临时文件 + `File.Move(overwrite:true)`，遇并发读取句柄有界重试 3 次）→ 广播 `SettingsChanged` 热生效。
- **加载**：启动时 `SettingsService` 构造函数 → `LoadOrDefault()`（`SettingsService.cs:142`）。文件缺失 → 默认值；文件损坏/为空/SchemaVersion≠1 → **先备份为 `settings.json.corrupt-<时间戳>`（`BackupUnreadableSettingsFile`，`SettingsService.cs:186`）再回退默认**；未知 JSON 字段被反序列化器忽略（schema 容错，不触发重置）；AI 字段旧位置→新位置一次性迁移（`MigrateLegacyAiFields`）。

## 二、入口 → 保存点 → 加载点 映射表

| 入口（修改设置的 UI/事件） | 代码位置 | 保存点（落盘调用） | 热生效 | 启动加载点 |
|---|---|---|---|---|
| **连接设置页**（AppId/ApiBase/TokenApiUrl/白名单/发送目标群） | `Controls\SettingsPages\ConnectionSettingsPage.axaml.cs` | ①「保存并应用」按钮 `OnSaveClicked:42` → `SaveNow(sender)`；②页面从可视树分离自动保存（基类 `CyberTechRepSettingsPageBase.cs:73` `OnDetachedAutoSave` → `SaveNow`）；③宿主退出兜底（见下） | `SettingsChanged` → `SettingsChangeApplier.Apply` ④ → `MessageIngestService.ApplySettings`（`MessageIngestService.cs:195`）：白名单经 `MessageIngestPipeline.UpdateSettings` 即时生效；AppId/ApiBase/TokenApiUrl 变更触发重连 | ① `SettingsService.LoadOrDefault`（`SettingsService.cs:142`）；② `MessageIngestService.StartAsync` 经 `IngestOptionsProvider.GetSettings`（`Plugin.cs:42` 委托热读取 `Current.Connection`）建立连接 |
| **分类设置页**（关键词表/通知前缀等） | `Controls\SettingsPages\ClassificationSettingsPage.axaml.cs:26` | 同上（按钮 + 分离自动保存） | `SettingsChanged` → `KeywordMessageClassifier.ReloadRules` / `KeywordSubjectClassifier.ReloadRules`（`SettingsChangeApplier.Apply` ①②） | `SettingsService.LoadOrDefault`；分类器启动装载词表时经 `ClassifierOptionsProvider.GetSettings`（`Plugin.cs:61/74`）热读取 |
| **CyberTechRep AI 设置页**（识别模式/云端参数/密钥） | `Controls\SettingsPages\AiSettingsPage.axaml.cs:68` | 同上 | `SettingsChanged` → 识别链经 `GetAiSettings` 委托热读取（`Plugin.cs:78`） | `SettingsService.LoadOrDefault`（含 AI 字段旧位置迁移）；链路经 `SubjectChainOptionsProvider` 委托热读取 |
| **悬浮窗设置页**（外观/位置/分组顺序/复位） | `Controls\SettingsPages\OverlaySettingsPage.axaml.cs:54` | 同上 + 单窗「复位」`ResetWindow:84` → `SaveNow()` | `SettingsChanged` → `SettingsChangeApplier.ApplyOverlaysAsync` → 控制器 `ApplySettingsAsync` + Show/Hide | `SettingsService.LoadOrDefault`；控制器构造时 `LoadFromSettingsService`（`SuspensionWindowController.cs:499`）以 `Current.Overlays` 为活实例（并一次性迁移旧 overlays.json → `overlays.json.migrated`） |
| **文件设置页**（下载根目录/磁盘上限等） | `Controls\SettingsPages\FileSettingsPage.axaml.cs:21` | 同上 | 文件管道经 `FilePipelineOptionsProvider.GetSettings`（`Plugin.cs:151`）热读取 | `SettingsService.LoadOrDefault` |
| **维护设置页**（日志级别/保留期）＋**导入/恢复默认** | `Controls\SettingsPages\MaintenanceSettingsPage.axaml.cs:135`；导入 `:323`；恢复默认 `:340` | `SaveNow`；导入 → `SettingsService.ImportAsync`（`SettingsService.cs:68`）；恢复默认 → `ResetToDefaultsAsync`（`:125`）——二者**整体替换 `Current`**，替换后保存并广播 | 广播 → 各模块热生效（含控制器重捕获，见下） | `SettingsService.LoadOrDefault` |
| **词表编辑页**（subjects.json + 识别模式回写） | `Controls\SettingsPages\SubjectRulesEditorPage.axaml.cs` | `SaveCoreAsync` 重写：先原子写 subjects.json，再 `SettingsService.SaveAsync`（`:299`）；分离自动保存同基类 | 广播 → 分类器 ReloadRules | 词表：`SubjectRuleFile.LoadOrSeed`；识别模式：`LoadOrDefault` |
| **悬浮窗快捷菜单**（置顶/固定/穿透，ⓘ ⋯ 按钮） | `Views\OverlayBehaviors.cs` `OverlayQuickMenu.OnToggleClick` → `ApplyAsync:148` | `ApplyAsync`：先 `controller.ApplySettingsAsync`（写活实例 + 内存生效），再 `settingsService.SaveAsync()`（`:159`） | 经 `ApplySettingsAsync` 即时应用到窗口 | 同悬浮窗组（`Current.Overlays`） |
| **悬浮窗拖拽/缩放回写**（防抖 300ms） | `SuspensionWindowController.CaptureBounds:393` → `ScheduleGeometrySave:432` → `OnGeometrySaveDue:443` → `SaveSettings:569` | 防抖到期 → `SaveSettings` → `PersistViaSettingsServiceAsync` → `SaveAsync`（内存即时更新，落盘合并） | 广播回放有 `cameFromSettingsService` 引用判定防回环（`ApplySettingsCoreAsync`） | 同悬浮窗组 |
| **悬浮窗可见性同步**（Show/Hide/× 关闭） | `SuspensionWindowController.SyncVisible:457` | 真实变化时 → `SaveSettings` → `SaveAsync`；宿主退出期由 `_suppressVisiblePersist` 抑制（`NotifyHostStopping`），**不会把退出关窗写成 Visible=false** | 广播 | 同悬浮窗组 |
| **圆圈栏分组顺序**（上移/下移） | `Services\Overlays\SubjectFilesController.cs:151` | 重排写回 `SubjectCircle.Order` 后 `SaveAsync` | 广播 | 同悬浮窗组 |
| **首次启动引导** | `Services\FirstRun\FirstRunService.cs:89` | 完成引导后 `SaveAsync`（`FirstRunCompleted` 标志 + 向导产生的设置） | 广播 | `SettingsService.LoadOrDefault` |
| **宿主退出兜底保存**（本轮新增） | `Services\Maintenance\SettingsChangeApplier.StopAsync:70` → `FlushSettingsOnShutdownAsync:97` | 宿主停止流程（先 `NotifyHostStopping` 抑制可见性回写、先退订自身广播，再 `SaveAsync`）——覆盖「宿主直接退出、页面未触发 DetachedFromVisualTree」场景；保存的是用户最后一次可见性意图，不重现 Visible=false 旧缺陷 | —（退出路径） | —（重启后由 LoadOrDefault 读回） |
| **连接设置热更新**（设置广播自动触发，本轮新增） | `SettingsChangeApplier.Apply` ④（`SettingsChangeApplier.cs:143`）→ `MessageIngestService.ApplySettings:195` | 不落盘（落盘由广播源头的 SaveAsync 完成）；只负责把已保存的连接设置应用到运行中的管道/连接 | 白名单即时生效；AppId/ApiBase/TokenApiUrl 变更重连；无变化不重连（差异判定先于快照更新，快照为独立副本 `CloneSettings:292`） | — |
| **NapCat 连接设置**（Mode/WS 地址/反向端口/AccessToken/ExePath —— 并行开发新增字段） | `ConnectionSettings`（`SettingsModels.cs`，本审计未改动）与连接设置页新增控件（并行开发中） | 持久化路径与连接页完全一致（基类 `SaveNow`/分离自动保存/退出兜底） | NapCat 模式参数经排错面板手动重连生效（`MessageIngestService.RestartNapCatGatewayAsync`），与模型注释约定一致 | `SettingsService.LoadOrDefault`（schema 容错自动兼容新字段） |

## 三、本轮修复明细

### 缺陷 1：`SuspensionWindowController` 持有失联的 `_settings` 实例（stale reference）
- **现象**：控制器构造时一次性捕获 `ISettingsService.Current.Overlays`；`ImportAsync`/`ResetToDefaultsAsync` 整体替换 `Current` 后，控制器继续读写已脱离设置服务的旧对象——拖拽回写、置顶/穿透等改动落不进 settings.json，重启即回退。
- **修复**（`SuspensionWindowController.cs`）：构造时订阅 `SettingsChanged`（`OnSettingsServiceChanged`，`:91`）。广播到达时若 `e.Overlays` 与 `_settings` 引用不同（即 Current 被导入/恢复默认替换），则重捕获新活实例并取消仍挂起的拖拽回写防抖（其待写目标已失效）；引用相同（普通保存，含控制器自身回写）则 no-op，不产生广播回环。替换后的新设置由 `SettingsChangeApplier` 的广播回放按 `cameFromSettingsService` 判定应用到窗口（该判定每调用读取最新 `Current`，无需改动）。
- **活跃 UI 状态的迁移策略**：以新设置为准（回放路径会按新值重排/更新窗口）；挂起的旧实例防抖落盘被丢弃，避免把旧状态写回磁盘。

### 缺陷 2：`LoadOrDefault` 静默整档重置
- **现象**：文件损坏、为空、或 SchemaVersion 不符时，只记一条日志就整体回退默认，用户数据不可恢复。
- **修复**（`SettingsService.cs`）：新增 `BackupUnreadableSettingsFile`（`:186`）——重置前把原文件改名备份为 `settings.json.corrupt-<yyyyMMdd-HHmmss>`（重名自动追加序号），并记警告日志；备份失败（如文件被占用）不影响回退默认的兜底。schema 容错：未知字段本就被 `System.Text.Json` 忽略（不重置），仅真正不可读/版本不符才重置。同时修复：schema-不匹配分支原来在文件读取流未关闭时执行 `File.Move` 必然失败——读取流改为块级作用域先释放（`:142` 起的 `using` 块）。

### 缺陷 3：设置页自动保存的可靠性
- **现象**：页面仅在 `DetachedFromVisualTree` 自动保存；宿主直接退出（不经设置窗口关闭/页面分离）时最后编辑丢失。另：原子替换 `File.Move(overwrite:true)` 若恰逢并发读取方持有文件句柄会瞬时 `IOException`，整次保存丢失。
- **修复**：
  - `SettingsChangeApplier.StopAsync`（`SettingsChangeApplier.cs:70`）新增**宿主退出兜底保存**：先 `NotifyHostStopping`（抑制退出期可见性回写，保证不把 Visible=false 写回）与退订自身广播（避免兜底保存触发一轮悬浮窗回放），再 `SaveAsync`（`FlushSettingsOnShutdownAsync:97`，独立 try/catch，不阻断宿主停止）。
  - `WriteAtomicallyAsync`（`SettingsService.cs:211`）对 `File.Move` 增加 3 次有界重试（50ms 间隔），消除并发读取句柄导致的偶发保存丢失；原子写语义（临时文件 + 覆盖移动）不变。
  - 设置页原有「保存按钮 + 分离自动保存」行为不变（零回归）。

### 缺陷 4：连接设置从不热应用
- **现象**：`MessageIngestService.ApplySettings`（`MessageIngestService.cs:195`）零调用点；且 `_lastApplied = settings` 先赋值再 `RequiresReconnect(_lastApplied, settings)` 比较（恒等 → 恒 false，死代码），连接参数变更后从不重连。另 `ConnectionSettings` 是设置服务活实例，`_lastApplied` 若指向同一实例，原地修改后新旧比较同样恒等。
- **修复**（均限于 `ApplySettings`/`RequiresReconnect` 区域）：差异判定先于快照更新；`_lastApplied` 改为 JSON 往返独立副本（`CloneSettings:292`，`StartAsync:67`、`ApplySettings:205` 及 NapCat 网关重建路径一致使用快照）；重连仅由 AppId/ApiBase/TokenApiUrl 变更触发——AppSecret 密文因 DPAPI 非确定性加密每次保存都变，不参与判定（密文变更经排错面板手动重连或重启生效，与现状一致，避免任何保存都重连）；群白名单经 `MessageIngestPipeline.UpdateSettings` 即时生效（无需重连）。挂接点：`SettingsChangeApplier.Apply` ④（`SettingsChangeApplier.cs:143`），随 `SettingsChanged` 广播自动调用，no-op 不重连。

### 其他入口排查结果（无新增缺口）
全仓扫描 `SaveAsync`/`Current` 直写：设置页全部经基类保存链；`SubjectFilesController`（圆圈顺序）、`FirstRunService`（引导完成）、`MaintenanceSettingsPage`（导入/恢复默认）均各自落盘；其余对 `Current` 的访问均为委托热读取（`Plugin.cs:42/61/74/151/187/253` 等），不构成丢失路径。

## 四、回归测试

新增 `tests\CyberTechRep.Tests\SettingsPersistenceTests.cs`（12 个用例）：

| 用例 | 覆盖点 |
|---|---|
| `Load_CorruptFile_BacksUpBeforeResetting` | 损坏文件 → 备份 + 回退默认 |
| `Load_WrongSchemaVersion_BacksUpBeforeResetting` | 版本不符 → 备份 + 回退默认 |
| `Load_EmptyFile_BacksUpBeforeResetting` | 空文件 → 备份 + 回退默认 |
| `Load_UnknownJsonFields_AreIgnored_SettingsStillLoaded` | schema 容错：未知字段不触发重置 |
| `Controller_WritesReachNewCurrent_AfterResetToDefaults` | 恢复默认替换 Current 后控制器写入落进新实例并持久化 |
| `Controller_WritesReachNewCurrent_AfterImport` | 导入替换 Current 后同上 |
| `Controller_OrdinarySave_DoesNotRecaptureOrLoop` | 普通保存不重捕获、广播无回环放大 |
| `StopAsync_FlushesFinalEditsToDisk` | 宿主退出兜底保存落盘 |
| `RequiresReconnect_OnlyConnectionParamsTrigger` | 差异判定矩阵（白名单/开关变化不重连） |
| `RequiresReconnect_InPlaceMutation_DetectedAgainstSnapshot` | 活实例原地修改 × 快照比较检出 |
| `ApplySettings_NoConnectionChange_DoesNotReconnect` | 端到端：无变化保存广播不重连（本地假网关） |
| `ApplySettings_ConnectionParamsChanged_Reconnects` | 端到端：AppId 变更触发重连（本地假网关） |

测试结果：`SettingsPersistenceTests` 12/12 通过（重复运行稳定）；设置/悬浮窗/接入相关既有回归套件（`SettingsServiceTests`、`OverlayVisibilitySyncTests`、`OverlaySettingsMigrationTests`、`OverlayQuickMenuTests`、`FirstRunServiceTests`、`MessageIngestServiceTests`、`IngestPipelineTests`）47/47 通过。全量套件 444/445 通过，唯一失败为并行开发中的 NapCat 事件规范化用例（`NapCatIngestTests.群消息_数组段格式_规范化为官方事件并进管道`），与本审计改动无关。

## 五、遗留说明

- **AppSecret/NapCat AccessToken 变更**：密文受 DPAPI 非确定性加密影响无法参与差异判定，热更后需经排错面板「手动重连」或重启生效（行为与本轮修复前一致，未引入回归）。
- **设置页编辑的最后一道防线**仍依赖宿主退出兜底保存（而非页面级编辑防抖）：设置页绑定写的是 `Current` 活实例且无 INPC 通知，页面级变更检测不可靠；退出兜底 + 分离自动保存 + 显式保存按钮三层覆盖已满足「任何入口重启还原」。
- 环境备注：本机构建需 `DOTNET_ROOT` 指向 `%LOCALAPPDATA%\Microsoft\dotnet`（SDK 8.0.424），且 ClassIsland PluginSdk 的 cipx 校验目标调用 `pwsh`，本机未装 PowerShell 7 时可用 `powershell.exe` 垫片代替（脚本兼容 PS 5.1）。
