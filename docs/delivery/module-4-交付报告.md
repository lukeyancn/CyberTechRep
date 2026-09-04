# 模块 4 交付报告：文件处理管道

- 日期：2026-09-04
- 范围：仅模块 4（文件处理管道）。未实现模块 5 及以后（Store/悬浮窗/设置页/重试队列 UI）。
- 契约依据：`ClassIng.Shared` 中 `IFilePipelineService` / `FileRecord` / `FileSettings` / `FileStatus`。

## 1. 改动文件清单

| 文件 | 类型 | 说明 |
| --- | --- | --- |
| `src/ClassIng.Plugin/Services/Files/FilePipelineOptionsProvider.cs` | 新增 | 选项提供器（`Func<FileSettings>` + 数据目录），模块 5 接入 ISettingsService 后替换 |
| `src/ClassIng.Plugin/Services/Files/FilePipelineService.cs` | 新增 | 管道实现：入队 → 路径安全校验 → 并发下载（.tmp）→ 流式 MD5 → 去重/归档；磁盘上限与清理策略；ReassignSubject 二次归档；files.json 原子持久化 |
| `tests/ClassIng.Tests/FilePipelineTests.cs` | 新增 | 模块 4 单元测试（9 个用例，含 Theory 展开 15 个断言实例） |
| `src/ClassIng.Plugin/Plugin.cs` | 修改 | 仅在 `Initialize` 方法体末尾新增标记注释 `// ==== 模块 DI 注册区 ====` 及其后的模块 4 DI 注册（3 个 AddSingleton）；新增 1 行 using |

未改动任何 csproj（无需新增包），未改动 `ClassIng.Shared`。

## 2. 完成状态

| 要求 | 状态 |
| --- | --- |
| 原子下载：临时文件(.tmp) → MD5 校验 → 归档 | ✅ 临时文件写入下载根目录（`.download-*.tmp`），流式计算 MD5 后 `File.Move` 归档到 `下载文件/<学科>/<yyyy-MM-dd>/<文件名>` |
| MD5 去重 | ✅ 命中已归档记录即删除临时/重复文件、记日志、`FileStatus=Duplicate`，磁盘仅保留一份；同名不同内容以 `name(n).ext` 顺延 |
| 失败处理 | ✅ `FileStatus=Failed` + `LastError` + `AttemptCount` 累计；失败即清理临时文件，无半成品归档 |
| 可重入 EnqueueAsync 与尽力重试 | ✅ 同 MessageId+FileName 的 Failed 记录再次入队时复用（状态复位、AttemptCount 续计）。边界：无后台自动重试、无持久化重试队列（均属模块 8）；附件 URL 有时效，过期后需上游换新链接重新入队 |
| 路径安全 | ✅ 见下文第 3 节 |
| 单文件上限 MaxFileSizeMb | ✅ 已知 Content-Length 时下载前拒绝；未知时流内累计字节强制中断 |
| 下载并发 DownloadConcurrency | ✅ `SemaphoreSlim(DownloadConcurrency)` 队列化（构造时读取一次设置；运行期热更新并发数留待模块 5 设置热更后处理） |
| 磁盘上限 MaxDiskUsageMb + CleanupPolicy | ✅ 0=拒绝新文件并告警日志；1=按 LastWriteTimeUtc 从旧到新清理（跳过 .tmp）直到腾出空间，仍不足则拒绝；被清理文件对应的记录标记 Failed 以保持一致 |
| ReassignSubjectAsync 幂等 | ✅ 从 `未分类/` 移动到 `<学科>/<原归档日期>/`；目标学科已是当前路径首段时直接返回；学科名走同一套路径安全校验 |
| HttpClient 可注入 | ✅ 构造参数 `HttpClient? httpClient`（与模块 3 CloudOpenAiProvider 同模式） |
| DI 注册不破坏模块 1/2/3 | ✅ 仅追加，未触碰既有注册 |

## 3. 路径安全与去重（自查摘要）

- 拒绝：空名；`/` `\` 路径分隔符；含 `..` 片段；`Path.GetInvalidFileNameChars()` 非法字符（含 `: * ? " < > |` 与控制字符）；长度超过 `MaxFileNameLength`；Windows 保留名 CON/PRN/AUX/NUL/COM1-9/LPT1-9（含带扩展名形式，如 `aux.txt`）；结尾空格/点已修剪。
- 防穿越终检：归档与二次归档的最终绝对路径均经 `IsInsideRoot`（`GetFullPath` 前缀匹配下载根目录）验证，越界即抛错/拒绝。
- 去重：MD5（十六进制小写）在下载时流式计算；开启 `Md5DedupEnabled` 时与已归档（Archived）记录比对，命中则删除新文件、状态置 Duplicate、记日志，磁盘仅一份。
- 记录持久化：`files.json`（数据目录，临时文件 + 原子替换，崩溃安全），重启后去重索引随记录恢复。

## 4. 测试结果（最终，2026-09-04 21:10）

`tests/ClassIng.Tests/FilePipelineTests.cs`（xunit，9 个方法 / Theory 展开后 15 例）——**15/15 全部通过**：

1. 正常下载归档到 `未分类/<yyyy-MM-dd>/`，MD5/大小正确、无 .tmp 残留
2. MD5 去重：不同名同内容 → 第二条 Duplicate，磁盘仅一份
3. 路径穿越/分隔符/`..`/保留名（CON、aux.txt、com1.log）/非法字符 → Failed 且不产生任何文件（Theory 7 例）
4. 超长文件名（>MaxFileNameLength）拒绝
5. 下载中断（假流中途抛 IO 异常）→ Failed、无半成品归档、无临时文件残留、AttemptCount=1
6. 单文件大小超限 → 下载前拒绝
7. ReassignSubject 移动 + 幂等（重复执行无变化）
8. 磁盘上限策略 0：超限拒绝新文件、保留既有文件
9. 磁盘上限策略 1：清理最旧文件后新文件成功归档

回归：模块 1/2/3 既有 **36 个测试全部通过**（IngestPipelineTests 7 + MessageIngestServiceTests 3 + MessageClassifierTests 10 + SubjectChainTests 16）。

全量仓库测试快照（122 例）：114→120 通过；仅剩 2 例失败位于 `SuspensionWindowControllerTests`（模块 6/7 悬浮窗，其他并行 worker 代码，按协作纪律未触碰）。

## 5. 已知缺口 / 边界

- 重试队列（持久化、指数退避、重放 UI）属模块 8；本模块仅保证 `EnqueueAsync` 可重入与记录 `AttemptCount/LastError` 供其消费。
- `SpeedLimitKbps` 已实现为简易分块限速（按每批字节数换算延迟），未做令牌桶级精确限速。
- `DownloadConcurrency` 在服务构造时读取一次；设置热更新语义待模块 5 接入 `ISettingsService` 后统一处理。
- 磁盘占用按下载根目录实测文件大小统计；网络盘/符号链接场景未特殊处理。
- 多下载并发时磁盘预检与清理在信号量槽位内执行，极端并发下仍可能短暂超限（预检非全局锁），已在策略上偏保守（不足即拒绝）。

## 6. 备注

本次执行期间其他并行 worker 同时在实现模块 5/6/7/8，期间解决方案多次处于中间态不可构建（错误均不位于模块 4 文件）。已在仓库外临时验证工程先行确认模块 4 可编译、15 例测试通过；待仓库恢复可构建后完成最终回归：模块 4 测试 15/15 通过，模块 1/2/3 回归 36/36 通过。清理了临时验证目录 `%TEMP%\module4-verify`。
