# 模块 6 交付报告：作业悬浮窗 + 共享悬浮窗控制器

> 状态：**完成**（编码 → 单元测试 → 构建回归 → 自检 → 交付）
> 日期：2026-09-04
> 说明：模块 5/6 共享悬浮窗控制器（`SuspensionWindowController`），同源实现；存储层与通知窗见 `docs/delivery/module-5-交付报告.md`。

## 0. 范围说明

本模块实现 `IHomeworkStore`（`HomeworkStore`）、`HomeworkSuspensionWindow` 与两窗共用的 `ISuspensionWindowController`（Avalonia 11 实现）。
**未**实现模块 7 及以后（设置页 UI、重试队列、打包）。

## 1. 改动文件清单

| 文件 | 说明 |
| --- | --- |
| `src/ClassIng.Plugin/Services/Stores/HomeworkStore.cs` | `IHomeworkStore` 实现：homework.json 原子写入、同 MessageId/同 Id 幂等 Upsert、`SetSubjectAsync` 人工修正写回（SubjectSource=Manual）、`.bak` 损坏恢复、`Changed` 事件 |
| `src/ClassIng.Plugin/Views/HomeworkSuspensionWindow.axaml` | 作业悬浮窗 UI：按学科 Expander 分组、条目正文 + 附件状态行、「修正学科」ComboBox、空状态文案、拖拽/缩放 |
| `src/ClassIng.Plugin/Views/HomeworkSuspensionWindow.axaml.cs` | 分组视图模型（`HomeworkGroup`/`HomeworkRow`）、`Changed` → 200ms debounce 刷新、`SetSubjectAsync` 接线 |
| `src/ClassIng.Plugin/Services/Overlays/OverlayGeometry.cs` | 多屏几何纯函数（无 UI 依赖）：`IsOnScreen` 跨全部屏幕边界判定、`ClampToScreen`、像素→DIP 换算 |
| `src/ClassIng.Plugin/Services/Overlays/SuspensionWindowController.cs` | `ISuspensionWindowController` 实现：创建/显示/隐藏、位置大小透明度字号即时应用、`ResetPositionAsync`、`IsOnScreen` 多屏检测、overlays.json 持久化 |
| `src/ClassIng.Plugin/Plugin.cs` | 追加模块 6 注册（控制器窗口工厂注入两窗，键 notice/homework） |
| `tests/ClassIng.Tests/HomeworkStoreTests.cs` | 作业存储单元测试 8 个用例 |
| `tests/ClassIng.Tests/OverlayWindowTests.cs` | 几何纯函数 8 个用例 + 控制器逻辑层 6 个用例 |
| `docs/delivery/module-6-交付报告.md` | 本交付报告 |

## 2. 实现要点

- **作业悬浮窗**：按学科分组（`Expander` 组头，组内条目含正文、附件数量状态行、时间）；「修正学科」为每条右侧 `ComboBox`（候选 = 全部作业已出现学科，当前学科置顶），选择后调 `SetSubjectAsync`（`SubjectSource=Manual, Confidence=1.0`），分组归属经 `Changed` 刷新自动迁移；空状态「暂无作业」。
- **共享控制器**（模块 5 通知窗同源使用）：
  - `ShowAsync/HideAsync`：经 `Dispatcher.UIThread` 调度；窗口由工厂委托创建（DI 注册时注入），控制器不持有 XAML 依赖，逻辑可脱离 UI 测试；
  - `ApplySettingsAsync`：透明度（0.1~1.0 钳制）/字号/位置/大小/置顶即时应用；位置用**逻辑坐标 DIP** 持久化，落地时按窗口 `RenderScaling` 换算像素（DPI 适配）；
  - `ResetPositionAsync`：一键复位到默认逻辑坐标 (40, 40) 并持久化；
  - `IsOnScreen`：窗口实际像素位置 + **全部 Screen**（`Screens.All`，逐屏按各自 Scaling 换算 DIP）做边界相交判定，可见面积占比 ≥ 15% 视为在屏（阈值常量 `OverlayGeometry.MinVisibleRatio`）；窗口未创建/无屏幕信息时按持久化设置兜底判定为在屏（不误触发复位）；
  - 拖拽/缩放回写：订阅窗口 `PositionChanged`/`Bounds` 变化，把实际位置大小（换算回 DIP）持久化到 `dataDir/overlays.json`（原子写 + `.bak`），并广播 `SettingsPersisted`（模块 7 设置页可订阅）；
  - 未知 overlayKey 全部安全 no-op（记 Warning，不抛异常）。
- **持久化文件**：`notices.json` / `homework.json` / `overlays.json`，均在构造注入的 dataDir 下（与现有模块风格一致；模块 7 将把 dataDir 与 overlay 设置迁移到宿主目录/`ISettingsService`，代码内已注释注明）。

## 3. 已确认约束逐条自查

| 约束 | 自查结果 |
| --- | --- |
| `SystemDecorations="None"` + Topmost + ShowInTaskbar=false | ✅ 两窗 XAML 固定 |
| 拖拽 `BeginMoveDrag` + 角部 Thumb 缩放 | ✅ 标题栏拖拽、右下角 Thumb（`DragDelta` 调整 Width/Height，`CanResize=False` 下手动控制） |
| 位置/大小/透明度/字号持久化到 `OverlayWindowSettings`（DIP） | ✅ overlays.json（结构即 `OverlaySettings`/`OverlayWindowSettings` 契约模型）；透明度钳制 0.1~1.0 |
| `ShowAsync/HideAsync/ApplySettingsAsync/ResetPositionAsync/IsOnScreen` 全契约 | ✅ |
| `IsOnScreen` 跨全部 Screen 边界判断 | ✅ `Screens.All` + 几何纯函数（8 个边界用例：全在屏/全出屏/拖出 87.5%/跨双屏/空屏幕兜底/零尺寸/钳制/DIP 换算） |
| 按学科分组 + 附件状态 + 修正学科 → `SetSubjectAsync` | ✅ |
| 数据目录风格与现有模块一致 | ✅ 构造注入 dataDir |
| 窗口创建逻辑尽量薄、逻辑层可测 | ✅ 窗口经工厂委托注入；几何抽纯函数；控制器持久化/复位/参数校验均可在无 Avalonia 平台下测试 |
| 模块间只经 `ClassIng.Shared` 接口通信 | ✅ 窗口 ↔ 控制器仅经 `ISuspensionWindowController`/`OverlayWindowSettings` |
| 不做模块 7 及以后 | ✅ |

## 4. 构建回归与全量测试（模块 5/6 共用闭环）

> 测试结果以最终一次互斥锁下的运行为准；下方数字为交付时记录。

| 项 | 结果 |
| --- | --- |
| `dotnet build -c Debug`（整个解决方案） | ✅ 通过（0 错误，最终回归 0 警告） |
| 模块 1/2/3 回归 | ✅ 36/36 通过（含于全量） |
| 模块 5 新增（NoticeStoreTests） | ✅ 9/9 通过 |
| 模块 6 新增（HomeworkStoreTests 8 + OverlayWindowTests 14） | ✅ 22/22 通过 |
| 全量合计 | ✅ **122/122 通过，0 失败**（含模块 1/2/3 的 36 个回归；另含并行 worker 的模块 4/7 测试） |

- Headless UI 测试说明：环境未预装 `Avalonia.Headless`，按任务约定以**逻辑层单测**为准——窗口创建/Show 路径保持薄（工厂注入），多屏几何、debounce 常量、控制器设置持久化/复位/兜底判定全部以纯逻辑覆盖；真实窗口渲染留待宿主环境人工验收（`ShowAsync("notice")` / `ShowAsync("homework")`）。

## 5. 验收自检清单（关键场景）

| 场景 | 结果 |
| --- | --- |
| 通知：写入 → 标已读 → 重启（新 Store 实例）→ 未读不含已读项 | ✅ 测试覆盖 |
| 通知：同 MessageId 重复推送 → 单条、内容取最新、无多余事件 | ✅ 测试覆盖 |
| 作业：同 MessageId 重复 Upsert → 单条幂等 | ✅ 测试覆盖 |
| 作业：修正学科 → 写回 Manual + 跨实例持久化 + 分组迁移 | ✅ 测试覆盖 |
| 主文件损坏 → `.bak` 自愈恢复；无备份 → `.corrupt` 保留 + 空数据继续 | ✅ 两 Store 均有测试 |
| 窗口拖出屏幕（仅 12.5% 可见）→ `IsOnScreen=false` → 复位回 (40,40) | ✅ 几何测试 + 控制器复位测试 |
| 设置（透明度/字号/位置）跨实例持久化 | ✅ 控制器测试覆盖 |

## 6. 已知缺口

1. **overlays.json 迁移**：模块 7 接入 `ISettingsService` 后，悬浮窗设置应迁入 `AppSettings.Overlays` 统一导入导出；控制器已暴露 `Settings`/`SettingsPersisted` 供接线，迁移点已注释。
2. **Ingest 管道 → 作业存储接线**：分类后的作业条目尚未自动 `UpsertAsync`（后续模块集成范围）。
3. **多屏 DPI 换算简化**：窗口位置换算用窗口自身 `RenderScaling`；跨不同缩放率屏幕拖拽时的逐屏精确换算未做（位置经 `PositionChanged` 回写自校正，偏差自愈）。
4. 角部 Thumb 缩放未联动 `MinWidth/MinHeight` 之外的屏幕边界钳制（拖出屏幕可经一键复位恢复）。
5. 作业条目「已完成/已处理」标记（`IsResolved`）UI 入口未做（契约字段已支持，属设置页/交互细化范围）。
