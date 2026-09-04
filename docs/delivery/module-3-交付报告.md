# 模块 3 交付报告：三级学科识别链

> 状态：**完成**（编码 → 单元测试 → 构建回归 → 自检 → 交付）
> 日期：2026-09-04

## 0. 范围说明

本模块仅实现 `ISubjectClassifier` / `ISubjectClassifierChain` / `IAiProvider` / `IPendingConfirmStore`（三级降级链闭环），**未**实现模块 4 及以后（文件管道、悬浮窗、设置页等）。

> 说明：上次执行被中途停止，`Services/SubjectChain/` 与测试目录均未留下半成品，本次为全新实现。

## 1. 改动文件清单

| 文件 | 说明 |
| --- | --- |
| `src/ClassIng.Plugin/Services/SubjectChain/SubjectChainOptionsProvider.cs` | 链依赖提供者（设置委托 + 数据目录 + Assets 目录 + 解密委托 + SafeGetSettings） |
| `src/ClassIng.Plugin/Services/SubjectChain/SubjectRuleFile.cs` | subjects.json 读写：Assets 模板种子 → 内置默认兜底、原子写入、规范化 |
| `src/ClassIng.Plugin/Services/SubjectChain/KeywordSubjectClassifier.cs` | 第①级：关键词规则识别（多学科命中按分值/优先级取最高，命中即 1.0） |
| `src/ClassIng.Plugin/Services/SubjectChain/LocalOnnxAiProvider.cs` | 第②级（本地）：ONNX 骨架（模型缺失 `IsAvailable=false` 自动降级） |
| `src/ClassIng.Plugin/Services/SubjectChain/CloudOpenAiProvider.cs` | 第②级（云端）：OpenAI 兼容 chat/completions，HttpClient 注入可测 + 每日限额 |
| `src/ClassIng.Plugin/Services/SubjectChain/JsonPendingConfirmStore.cs` | 第③级：人工确认队列（JSON 原子持久化，Enqueue/GetWaiting/Resolve + 写回钩子） |
| `src/ClassIng.Plugin/Services/SubjectChain/SubjectClassifierChain.cs` | 链组合器：①关键词 → ②AI（本地优先/云端可配）→ ③人工投递；永不返回 null |
| `src/ClassIng.Plugin/Assets/subjects.json` | 默认学科关键词表模板（9 科，随插件输出复制） |
| `src/ClassIng.Plugin/Plugin.cs` | DI 注册模块 3 全部服务（模块 1/2 注册未改动） |
| `src/ClassIng.Plugin/ClassIng.Plugin.csproj` | 引用 `System.Text.Json 9.0.2`；复制 subjects.json；`InternalsVisibleTo` 测试工程 |
| `tests/ClassIng.Tests/SubjectChainTests.cs` | 模块 3 单元测试（16 个用例） |
| `docs/delivery/module-3-交付报告.md` | 本交付报告 |

## 2. 降级链执行逻辑

```
文本 → ①关键词（subjects.json，命中 Confidence=1.0）
        未命中/低置信 ↓
     ②AI（PreferLocalModel 排序：本地 ONNX / 云端 OpenAI 兼容）
        置信度 ≥ ConfidenceThreshold → 返回（SubjectSource 标记来源）
        低置信（< 阈值）→ 候选保留，继续走完链取更优
        IsAvailable=false / 异常 / 返回 null → 下一级
     ③兜底：取候选中置信度最高者（无候选则 Subject=「未分类」），
        NeedsManualConfirm=true，投递 IPendingConfirmStore（幂等：同 MessageId 复用）
```

- **永不返回 null**：所有实现级异常在实现内吞掉（返回 null 进下一级），链级异常进入兜底路径，最终总有 `SubjectResult`。
- 阈值来自 `ClassificationSettings.ConfidenceThreshold`（默认 0.7）；`ManualConfirmQueueEnabled=false` 时只返回结果不投递（记 Warning，可追溯）。
- 云端保护：未配置端点/密钥 → 不可用；HTTP 错误 / 响应不可解析 → null 进下一级；每日调用计数超 `CloudDailyCallLimit` → `IsAvailable=false`（跨日自动清零）。

## 3. 已确认约束逐条自查

| 约束 | 自查结果 |
| --- | --- |
| 接口以 `ClassIng.Shared.Abstractions` 为准，未改动 Shared | ✅ Shared 零改动 |
| 三级实现统一 `ISubjectClassifier` / `IAiProvider` 契约，可替换可测 | ✅ 关键词/本地/云端均独立构造函数注入，测试全部用假实现/真实类混搭 |
| 低置信/全失败 `NeedsManualConfirm=true` 且投递 `IPendingConfirmStore` | ✅（含投递失败兜底日志） |
| 链永不返回 null | ✅ 有专项测试 |
| 本地模型文件不存在 `IsAvailable=false` 自动降级 | ✅ 真实 `LocalOnnxAiProvider` 参与测试并断言 |
| 云端 HttpClient 可测实现 | ✅ `FakeHttpHandler` 验证请求头/请求体/响应解析/限额 |
| `IPendingConfirmStore` JSON 原子写入（tmp + Move） | ✅ 复用 `SubjectRuleFile.WriteAtomic`；损坏文件备份 `.corrupt` 后以空队列继续 |
| 外置学科关键词表 JSON（Assets 模板 subjects.json） | ✅ 缺失时从 Assets 种子到数据目录，支持 `ReloadRules` 热生效 |
| 阈值来自 `ClassificationSettings.ConfidenceThreshold` | ✅ 链内每次判定实时读取 |
| 云端每日限额保护（简单计数） | ✅ 进程内计数 + 跨日清零（持久化计数列入已知缺口） |
| DI 注册到 `Plugin.cs`，不破坏模块 1/2 | ✅ 仅追加注册；模块 1/2 代码零改动 |
| 禁止实现模块 4 及以后 | ✅ 未触碰文件管道/悬浮窗/设置页 |

## 4. 人工确认写回

`JsonPendingConfirmStore.Resolved` 回调：`ResolveAsync(id, subject)` 持久化 `Status=Resolved` + `ResolvedSubject` 后，在锁外调用回调。模块 5 接入 `IHomeworkStore` 后在 `Plugin.cs` 挂接即可实现「写回 `HomeworkItem.Subject`（SubjectSource=Manual）」；当前阶段由测试验证回调收到人工选择结果（写回 HomeworkItem 属模块 5 职责，见已知缺口）。

## 5. 测试与验收结果

```
dotnet build -c Debug              → 0 错误（0 警告）
dotnet test tests\ClassIng.Tests
已通过! - 失败: 0，通过: 36，已跳过: 0，总计: 36
```

**回归**：模块 1（10）+ 模块 2（10）= 20 个既有用例全部仍通过 ✅

模块 3 新增 16 个用例：

| 用例 | 覆盖点 |
| --- | --- |
| 关键词命中_返回KeywordRule且置信度达标 | 第①级命中直达 |
| 关键词未命中_自动进入AI级 | ①→②自动降级（假 IAiProvider） |
| 本地模型缺失_跳过本地_使用云端 | 本地 `IsAvailable=false` 自动降级 |
| AI低置信_进人工队列且NeedsManualConfirm | ②→③低置信兜底 + 候选随队入列 |
| AI全部不可用_兜底未分类_永不静默丢失 | 全失败兜底 + 投递 |
| 空文本_链仍返回非null并进人工队列 | 永不返回 null |
| 人工Resolve_状态与学科写回并持久化 | Resolve 写回 + 重载持久化验证 |
| 同一消息重复投递_幂等不重复入队 | 队列幂等 |
| PreferLocalModelfalse_云端排在本地之前 | 本地/云端顺序可配 |
| 云端识别_成功解析OpenAI兼容响应 | 请求构造（Bearer/model）+ 响应解析 |
| 云端识别_HTTP错误或坏JSON_返回null不抛出 | 云端防错 |
| 云端每日限额_超限后IsAvailable关闭 | 限额保护 |
| 云端未配置密钥或端点_不可用 | 配置缺失降级 |
| 端点URL_自动补chatCompletions后缀 | 端点兼容 |
| 词表_数据目录缺失时从Assets模板种子 | 模板种子 + 热重载 |
| 词表损坏_关键词级返回null_链不中断 | 词表防错不中断链 |

**中途修复记录**（构建/测试迭代）：补 `System.Text.Json.Serialization` using；`System.Text.Json` 版本对齐 9.0.2（避免 NU1605 降级）；`ILocalAiProvider` 排序的 `Concat` 泛型推断；测试文本两处误中关键词规则（改为无关键词文本）；请求体改用 `UnsafeRelaxedJsonEscaping`（中文不转义，部分兼容端点对 `\uXXXX` 支持差）。

## 6. 已知缺口（均不影响本模块闭环）

1. **本地 ONNX 推理为占位骨架**：模型存在性检查、配置路径（`LocalModelPath`，相对 DataDirectory 解析）、降级链路完整可用；`InferenceSession` 真实推理未接入（无真实模型可验证，按任务说明未引入 OnnxRuntime 原生依赖）。接入点：`LocalOnnxAiProvider.ClassifyAsync` 内「推理占位」段。
2. **云端每日计数为进程内存计数**：重启清零。若需跨重启限额，落盘计数文件即可（实现点：`CloudOpenAiProvider.IncrementCount`）。
3. **人工确认写回 HomeworkItem**：经 `Resolved` 回调预留，模块 5 接入 `IHomeworkStore.SetSubjectAsync` 后接线。
4. **subjects.json / pending-confirm.json 暂无设置页编辑入口**：属模块 6/7；当前支持手改 JSON + `ReloadRules()`（词表）热生效。
