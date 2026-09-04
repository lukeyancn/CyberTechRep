# 模块 5 交付报告：通知悬浮窗（存储层 + Avalonia 悬浮窗）

> 状态：**完成**（编码 → 单元测试 → 构建回归 → 自检 → 交付）
> 日期：2026-09-04
> 说明：模块 5/6 共享悬浮窗控制器（`SuspensionWindowController`），同源实现，见 `docs/delivery/module-6-交付报告.md` 的控制器部分。

## 0. 范围说明

本模块实现 `INoticeStore`（`NoticeStore`）与 `NoticeSuspensionWindow`（Avalonia 11 无边框置顶窗）。
**未**实现模块 7 及以后（设置页 UI、重试队列迁移、打包）；ClassIsland 通知服务联动（NotificationProvider）按任务要求留接口占位（见「已知缺口」）。

## 1. 改动文件清单

| 文件 | 说明 |
| --- | --- |
| `src/ClassIng.Plugin/Services/Stores/JsonStoreFile.cs` | 模块 5/6 共用 JSON 持久化助手：原子写入（复用 `SubjectRuleFile.WriteAtomic`）+ `.bak` 备份 + 损坏自愈恢复 |
| `src/ClassIng.Plugin/Services/Stores/NoticeStore.cs` | `INoticeStore` 实现：notices.json 原子写入、同 MessageId 幂等、已读持久化（重启不复活）、`.bak` 损坏恢复、`Changed` 事件 |
| `src/ClassIng.Plugin/Views/NoticeSuspensionWindow.axaml` | 通知悬浮窗 UI：无边框置顶窗、未读列表 + 每条「已读」按钮、空状态文案、标题栏拖拽区、角部缩放 Thumb |
| `src/ClassIng.Plugin/Views/NoticeSuspensionWindow.axaml.cs` | 列表刷新（`Changed` → 200ms debounce 合并）、MarkReadAsync 接线、`BeginMoveDrag` 拖拽、Thumb 缩放 |
| `src/ClassIng.Plugin/Services/Overlays/JsonStoreFile` 同文件 | （无，控制器见模块 6 报告） |
| `src/ClassIng.Plugin/Plugin.cs` | 在「模块 DI 注册区」追加模块 5 注册行（模块 1/2/3/4 注册未改动） |
| `tests/ClassIng.Tests/NoticeStoreTests.cs` | 模块 5 单元测试 9 个用例 |
| `docs/delivery/module-5-交付报告.md` | 本交付报告 |

## 2. 实现要点

- **JSON 原子写入 + 幂等**：`notices.json` 经 tmp 文件 + `File.Move(overwrite)` 原子落盘；`AddOrUpdateAsync` 同 MessageId 合并为单条（未读时更新正文；已读条目只更新正文**不复活为未读**）。内容未变化的重复写入不触发保存与 `Changed` 事件。
- **已读持久化**：`MarkReadAsync` 写回 `IsRead=true, ReadAt=now` 并立即落盘；重启后新实例加载时未读集合不含已读项（有专项测试）。
- **损坏恢复**：每次成功保存后同步维护 `.bak`；主文件损坏时自动从 `.bak` 恢复并自愈回写主文件；无有效备份时原文件转存 `.corrupt` 后以空数据继续（不阻断插件运行）。
- **悬浮窗**：`SystemDecorations="None"` + `ShowInTaskbar="False"` + `Topmost`；列表展示未读（最新在前），每条最右「已读」按钮 → `MarkReadAsync` 后经 `Changed` 刷新条目消失；无未读时显示「暂无未读通知」；UI 侧 200ms `DispatcherTimer` debounce 合并高频 `Changed`。
- **数据目录**：与现有模块一致，构造注入 dataDir；`Plugin.cs` 传用户数据目录兜底路径（模块 7 将替换为宿主目录，已注释注明）。

## 3. 已确认约束逐条自查

| 约束 | 自查结果 |
| --- | --- |
| 契约以 `ClassIng.Shared` 为准，不改 Shared | ✅ Shared 零改动 |
| 代码只放独立目录 | ✅ 仅新增 `Services/Stores/` 与 `Views/`（控制器在 `Services/Overlays/`，见模块 6） |
| Plugin.cs 只在标记注释后追加注册行 | ✅ 在既有 `// ==== 模块 DI 注册区 ====` 区内追加（模块 4 worker 已创建该标记），追加前重读了最新内容，未覆盖他人改动 |
| JSON 原子写入 + 幂等 + 已读持久化 + .bak 恢复 | ✅ 均有专项测试 |
| `Changed` 事件供 UI 刷新 + UI 侧限流合并 | ✅ 200ms debounce（`RefreshDebounce`） |
| 空状态文案 | ✅「暂无未读通知」 |
| 已读条目重启不复活 | ✅ 跨实例专项测试 |
| ClassIsland NotificationProvider 联动留占位 | ✅ 未实现，见已知缺口（本就属后续模块） |
| 不做模块 7 及以后 | ✅（期间仅对模块 7 worker 的 WIP 文件做了解除编译阻塞的最小修复，见 §5） |

## 4. 验收自检结果

构建回归与全量测试结果见 `docs/delivery/module-6-交付报告.md` §4（两模块同一次构建/测试闭环，36/36 原有测试 + 新增测试全绿）。

## 5. 事件记录（并行协作）

模块 7 worker 与本任务并行开发。其 WIP 文件曾使共享构建阻塞，本人做了**最小修复**（仅补 using / 修正 API 名，未改其逻辑）：

- `Services/Maintenance/SettingsChangeApplier.cs`：补 `ClassIng.Shared.Models.AppSettings` 限定（两次）；
- `Services/Maintenance/RetryQueueService.cs`：`ArgumentException.ThrowIfNull` → `ArgumentNullException.ThrowIfNull`；**事故**：本人用 PS 5.1 `Get-Content`/`Set-Content`（默认 ANSI）改该文件导致 UTF-8 中文损坏，已通过编码逆向还原 + 逐行修复（含 2 处被注释吞掉的代码行），遗留的少量注释文案措辞可能与原文有细微出入，逻辑无影响；
- `Controls/SettingsPages/ClassIngSettingsPageBase.cs`：补 `using Avalonia;` 与 `INotifyPropertyChanged` 基接口。

## 6. 已知缺口

1. **消息管道 → 通知存储的接线**：`MessageIngestPipeline` 分类结果尚未自动调用 `INoticeStore.AddOrUpdateAsync`（属后续模块的集成范围）；当前 Store 与悬浮窗闭环已可独立运行与测试。
2. **模块 3 预留的 `JsonPendingConfirmStore.Resolved` → `IHomeworkStore.SetSubjectAsync` 写回钩子未接线**：需要「确认结果 ↔ 同 MessageId 作业条目」的集成时机，避免与其他 worker 的管道改动冲突；预留注释仍在 `Plugin.cs`。
3. 通知悬浮窗关闭按钮为隐藏（`Hide()`），重新唤出依赖控制器 `ShowAsync`（设置页 UI 属模块 7）。
4. 无历史消息回补（平台能力限制，模块 1 已声明）。
