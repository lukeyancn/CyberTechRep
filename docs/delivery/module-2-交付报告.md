# 模块 2 交付报告：消息分类管道

> 状态：**完成**（编码 → 单元测试 → 构建回归 → 自检 → 交付）
> 日期：2026-09-04

## 0. 范围说明

本模块仅实现 `IMessageClassifier`（关键词规则引擎：通知 / 作业分流），**未**实现模块 3 及以后（学科识别链、文件管道、悬浮窗、设置页等）。

## 1. 改动文件清单

| 文件 | 说明 |
| --- | --- |
| `src/ClassIng.Plugin/Services/Classification/ClassifierOptionsProvider.cs` | 分类器依赖提供者（设置委托 + 数据目录） |
| `src/ClassIng.Plugin/Services/Classification/KeywordRulesFile.cs` | 外置 JSON 词表读写 / 首次种子 / 规范化 |
| `src/ClassIng.Plugin/Services/Classification/KeywordMessageClassifier.cs` | `IMessageClassifier` 实现：文本提取、匹配、作业优先、防错 |
| `src/ClassIng.Plugin/Assets/classification-keywords.json` | 默认关键词表模板（与 `ClassificationSettings` 默认一致） |
| `src/ClassIng.Plugin/Plugin.cs` | DI 注册 `ClassifierOptionsProvider` + `KeywordMessageClassifier` / `IMessageClassifier`（不破坏模块 1） |
| `src/ClassIng.Plugin/ClassIng.Plugin.csproj` | 复制默认关键词 JSON 到输出目录 |
| `tests/ClassIng.Tests/MessageClassifierTests.cs` | 模块 2 单元测试（10 个用例） |
| `docs/delivery/module-2-交付报告.md` | 本交付报告 |

## 2. 关键词优先级策略

**作业优先**：通知与作业两侧关键词均命中时，判定为 `MessageKind.Homework`（作业漏检成本更高）；仅一侧命中取该侧；均未命中或无可分类文本 → `Unknown`。

匹配方式：对拼接后的纯文本做大小写不敏感子串包含（`OrdinalIgnoreCase`）；`MatchReason` 记录命中词与优先级标记（如 `both_hit_homework_priority`）。

## 3. 文本提取与空文本降级

- 输入：仅拼接 `MessageSegment.Type == text` 的 `Text` 字段（忽略 image/file/at 等段的展示文本）。
- **纯媒体无文本**（有图片/文件等附件但无 text 段，或 text 为空）：返回 `Unknown`，`MatchReason=empty_text_with_media`（不根据附件文件名猜「作业」——文件名可能误导，交后续人工/排错面板）。
- **空 Segments / 空白文本**：`Unknown`，`MatchReason=empty_text`。

## 4. 外置词表与热生效

- 路径：`{DataDirectory}/classification-keywords.json`（默认文件名可配）。
- 首次 `ReloadRules()`：若文件不存在，用 `ClassificationSettings.NoticeKeywords` / `HomeworkKeywords` 种子写入 JSON。
- 之后以 JSON 为准；设置页改词表后写回 JSON 并调用 `ReloadRules()` 即可热生效。
- JSON 损坏或读写异常：吞掉异常、保留上一份内存规则并写日志（不抛到外层）。

## 5. 测试与验收结果

```
dotnet build -c Debug          → 0 警告 / 0 错误；ClassIng.Plugin.cipx 已生成
dotnet test tests\ClassIng.Tests
已通过! - 失败: 0，通过: 20，已跳过: 0，总计: 20
```

| 用例 | 覆盖点 |
| --- | --- |
| 通知关键词命中_分流为Notice | Notice 命中 |
| 作业关键词命中_分流为Homework | Homework 命中 |
| 两边都不命中_返回Unknown | 无命中 |
| 两边都命中_作业优先 | 优先级策略 |
| ReloadRules_热生效_修改JSON后立即生效 | 热重载 |
| ReloadRules_设置默认值变更_无JSON时种子后可读 | 默认种子 |
| 纯媒体无文本_返回Unknown | 空文本降级 |
| 多文本段拼接_可命中跨段关键词 | Segments 拼接 |
| 异常输入_空Segments与损坏规则_不崩溃 | 防错 |
| ExtractPlainText_只拼接text段 | 文本提取 |

模块 1 回归（10 个用例）全部通过。

> 运行提示：本机若设置了 `HTTP_PROXY`/`HTTPS_PROXY`，本地假网关 WebSocket 会被代理劫持；测试时需附加 `NO_PROXY=localhost,127.0.0.1`（或等价环境变量）。

## 6. 约束自查（逐条）

| 约束 | 自查结果 |
| --- | --- |
| 实现 `IMessageClassifier`（通知/作业/Unknown） | ✅ `KeywordMessageClassifier` |
| 关键词表外置 JSON，默认来自 `ClassificationSettings` | ✅ `classification-keywords.json` + 首次种子 |
| `ReloadRules()` 热生效 | ✅ 内存快照替换；单测覆盖 |
| 分类异常不外抛，返回 Unknown + 日志 | ✅ try/catch；损坏 JSON / 空输入覆盖 |
| 从 `Segments` 提取 text 作为分类输入 | ✅ `ExtractPlainText` |
| 含文件/图片但文本为空时合理降级 | ✅ `Unknown` + `empty_text_with_media`（见 §3） |
| Plugin DI 注册且不破坏模块 1 | ✅ 追加注册；模块 1 测试全绿 |
| 单元测试覆盖分流/Unknown/优先级/热生效/异常 | ✅ 10 个新用例 |
| 构建 + 模块 1 回归 + 模块 2 测试通过 | ✅ 20/20 |
| 未实现模块 3+ | ✅ 无学科链/文件管道/悬浮窗/设置页 |

## 7. 与后续模块衔接点

- 模块 5 `ISettingsService` 接入后：将 `ClassifierOptionsProvider.GetSettings` 改为读真实配置；设置页保存词表后调用 `IMessageClassifier.ReloadRules()`。
- 模块 3 消费 `ClassifiedMessage`：仅对 `Kind=Homework` 走学科识别；`Notice` 写通知存储；`Unknown` 进降级/排错路径。
- 数据目录仍为 `%LocalAppData%\ClassIsland\Plugins\classisland.classing\data`（与模块 1 一致），模块 7 再切宿主 `PluginConfigFolder`。
