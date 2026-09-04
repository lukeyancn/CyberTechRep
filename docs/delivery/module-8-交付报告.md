# 模块 8 交付报告：更新与排错机制

日期：2026-09-04 ｜ 状态：✅ 完成（含单元测试与全量回归）

## 一、实现清单（`src/ClassIng.Plugin/Services/Maintenance/`）

### 1.1 RetryQueueService（`IRetryQueueService`）

- **JSON 持久化**：`retry-queue.json`，原子写入（`.tmp` + `File.Move(overwrite)`），跨插件重启保留（有测试）；
- **指数退避**：`NextAttemptAt = Now + min(InitialDelaySec × BackoffFactor^AttemptCount, MaxDelaySec)`，纯函数 `ComputeNextDelaySec`（internal，测试覆盖倍增与封顶）；
- **上限 GivenUp**：`AttemptCount ≥ MaintenanceSettings.MaxRetryAttempts` → `RetryItemStatus.GivenUp`，等待人工重放；
- **ReplayAsync**：重置 `AttemptCount=0`、状态 Waiting、`NextAttemptAt=UtcNow` 并立即尝试一次（测试覆盖重置计数后成功）；
- **后台调度**：`Timer` 周期扫描（`RetryQueueEnabled=false` 时整体停摆）；测试经 internal `TickAsync(forceDue)` 确定性驱动；
- **执行器注册**：`RegisterExecutor(RetryOperationType, RetryExecutorAsync)` 按类型注册；内置注册点：
  - `StoreWrite`：透传回调 `StoreWriteCallback`（模块 5 存储层完成后注入真实写回；未注入按失败退避、条目不丢失）；
  - `Custom`：透传回调 `CustomCallback`；
  - `FileDownload` / `SubjectClassify`：**真实执行器依赖模块 4 文件管道与模块 3 AI 链的重试入口，当前未实现，仅保留注册点**；未注册类型的条目按失败退避并记录 `executor_not_registered:<Type>`（有测试证明不丢失）。

### 1.2 UpdateNotifyService（`IUpdateNotifyService`）

- GitHub Releases API（`api.github.com/repos/{repo}/releases/latest`）；仓库地址经 `UpdateNotifyOptions.Repository` 可配，默认占位 `ClassIng/ClassIng`（正式发布前替换）；
- 版本比较纯函数 `IsNewerVersion`（逐段数字比较、容忍 `v` 前缀，测试 11 例）；
- 检测到新版本 → 触发 `UpdateDetected` 事件 + 结构化日志；**悬浮窗/通知条目接入属模块 5/6，本模块只发事件 + 记日志**；
- 启动检查：`UpdateCheckStartupService`（IHostedService）宿主启动后 fire-and-forget 执行一次 `CheckAsync`，超时（默认 10s）与失败只记日志不影响主流程。

### 1.3 EnvironmentMonitorService（`IEnvironmentMonitorService`）

- **磁盘剩余空间**：取监测目录所在盘 `DriveInfo.AvailableFreeSpace`，阈值默认 512 MB；纯函数 `IsDiskSpaceLowCore(freeBytes, thresholdBytes)`（测试 4 组边界：低于/等于/高于阈值）；provider 可注入（测试不打真盘）；
- **协议端离线时长**：订阅 `IMessageIngestService.StatusChanged`，`Connected` 清零、其余状态开始计时，`OfflineDuration` 暴露；
- **WarningRaised 事件**：磁盘低于阈值 / 离线超过阈值（默认 10 分钟）时触发，**去抖**（同一问题解除前只告警一次，测试覆盖告警→恢复→再告警计数）。

### 1.4 排错数据出口 `IDiagnosticsService`（定义在 Plugin 内部，设置页消费）

- `DiagnosticsSnapshot`：连接状态、离线时长、磁盘剩余（MB）、最近 50 条消息快照（`MessageTraceItem`，预览截断 80 字符脱敏）、重试队列全量；
- 订阅 `MessageReceived` 维护环形快照；
- 操作入口：`ReplayAsync(id)`（转发 `IRetryQueueService.ReplayAsync`）、`ReconnectAsync`（转发 `IMessageIngestService.ReconnectAsync`）；
- 设置页「维护」组消费：排错面板（连接状态摘要 / 最近消息列表 / 重试队列列表 + 刷新、重放选中、手动重连按钮）。

结构化日志本身由 `ILogger` 提供（各服务均输出关键字段），本服务只做聚合视图。

## 二、DI 注册（Plugin.cs 追加于标记区之后）

`RetryQueueOptions/RetryQueueService/IRetryQueueService`、`UpdateNotifyOptions/UpdateNotifyService/IUpdateNotifyService`、`EnvironmentMonitorService/IEnvironmentMonitorService`、`DiagnosticsService/IDiagnosticsService`、`AddHostedService<SettingsChangeApplier>`、`AddHostedService<UpdateCheckStartupService>`。

## 三、改动文件

新增：

- `src/ClassIng.Plugin/Services/Maintenance/RetryQueueService.cs`
- `src/ClassIng.Plugin/Services/Maintenance/UpdateNotifyService.cs`（含 `UpdateCheckStartupService`）
- `src/ClassIng.Plugin/Services/Maintenance/EnvironmentMonitorService.cs`
- `src/ClassIng.Plugin/Services/Maintenance/DiagnosticsService.cs`（`IDiagnosticsService` + 快照模型 + 实现）
- `tests/ClassIng.Tests/RetryQueueServiceTests.cs`（9 用例）
- `tests/ClassIng.Tests/EnvironmentMonitorServiceTests.cs`（9 用例，含 Theory 边界组）
- `tests/ClassIng.Tests/UpdateNotifyServiceTests.cs`（11 用例）

修改：

- `src/ClassIng.Plugin/Plugin.cs`（仅标记区后追加注册块）
- `tests/ClassIng.Tests/ClassIng.Tests.csproj`（追加 Avalonia 运行期引用，模块 6 测试需要）

## 四、测试结果

- `RetryQueueServiceTests`：退避倍增（5→10→20→40→80）、封顶 600、跨实例持久化、3 次失败后 GivenUp、失败后调度下次时间、Replay 重置计数后成功、成功标记、Purge 仅清成功项、队列停摆、未注册执行器按失败保留；
- `EnvironmentMonitorServiceTests`：阈值判断 4 组边界、注入 provider 判定、provider 抛异常不误报、离线计时/清零、磁盘告警去抖与再触发、离线超阈值告警；
- `UpdateNotifyServiceTests`：版本比较 8 组 + 空输入 2 组 + 构造兜底。

回归：全解决方案 `dotnet test` **122/122 通过（0 失败）**，含模块 1/2/3 的 36 个既有测试全部保持绿色。注：模块 6 的 `SuspensionWindowControllerTests` 在并行测试下偶发出现过非稳定失败（与本模块无关），复跑全绿。

## 五、遗留注册点（后续模块接入）

1. `RetryQueueService.StoreWriteCallback`：模块 5 存储层真实写回；
2. `FileDownload` / `SubjectClassify` 执行器：模块 4 文件管道与模块 3 AI 链提供重试入口后经 `RegisterExecutor` 注册（当前失败条目安全保留，可手动重放）；
3. `UpdateDetected` → 悬浮窗通知展示：模块 5/6 订阅事件即可，本模块已发事件。
