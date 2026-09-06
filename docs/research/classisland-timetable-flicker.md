# ClassIsland 顶部课表偶发闪烁调研

> 调研日期：2026-09-06
> 调研人：ClassIng 任务 7（课表闪烁排查）
> 分析对象：
> - ClassIsland 宿主源码本地副本（仓库内 `ClassIsland\` 目录，2.x Avalonia 开发线，与 2.1.0.1 稳定版行为基本一致）；
> - ClassIng 插件源码（`src\ClassIng.Plugin\`）。
>
> **结论先行：插件侧存在一个可修的贡献因素（钉底器每秒无条件 `SetWindowPos(HWND_BOTTOM)` 与宿主
> Bottommost 重申逻辑形成 Z 序拉锯），已在 ClassIng 0.3.0.0 中修复（真 no-op 化）；但更根本的触发源
> 是宿主主窗口自身的层级重申策略（`WindowTopmostRecheckMode` 2/3 模式下每 tick/高频重申 Bottommost），
> 该部分属宿主自身行为，插件侧不可根治，建议向宿主提 issue。**

---

## 1. 现象

ClassIsland 主窗口顶部的课表组件（`ScheduleComponent` → `LessonsListBox`）偶发闪烁，
非持续复现，与特定窗口布局（悬浮窗/组件与主窗口屏幕区域重叠）及宿主设置相关。

## 2. 宿主侧刷新机制盘点（源码依据）

### 2.1 课表控件的绑定/刷新结构

- 课表组件 `ClassIsland\ClassIsland\Controls\Components\ScheduleComponent.axaml`：
  内部 `ci:LessonsListBox#MainLessonsListBox`，`IsLiveUpdatingEnabled=True`；
  `ItemsSource` 绑定 `ClassPlan.TimeLayout.Layouts`（`LessonsListBox.axaml:43`），
  选中态样式绑定 `LessonsService.CurrentSelectedIndex`（TwoWay）。
- 每节课条目 `LessonControlExpanded`（`ClassIsland.Core\Controls\LessonsControls\`）：
  订阅宿主 `ILessonsService.PostMainTimerTicked`（主计时器 **50ms**，`LessonsService.cs` `Interval=TimeSpan.FromMilliseconds(50)`），
  每 tick 重算 `Seconds/LeftSeconds/ProgressPercent/MasterTabIndex/DetailIndex`。
  - `Seconds/LeftSeconds` 为整秒值，每 tick 赋值但值仅在整秒变化时真实改变（`SetField` 去重），无害；
  - **`SettingsSource` 每 tick 重新赋值**（`LessonControlExpanded.axaml.cs:196-203`）：
    `GetAttachedSettingsByPriority(...)` 若命中的附加设置为 `JsonElement`（档案刚从 JSON 加载、
    尚未物化为类型对象时，`AttachableSettingsObject.GetAttachedObject` 直接
    `o1.Deserialize<T>()` 且**不写回缓存**，`AttachableSettingsObject.cs:23-32`），
    则每 50ms 生成一个**新实例** → `SetField` 判定变更 → `PropertyChanged("SettingsSource")` →
    该条目所有 `SettingsSource.*` 绑定（ScheduleSpacing 缩放、ShowExtraInfoOnTimePoint、
    CountdownSeconds、ExtraInfoType 等）每秒重评估 20 次 → 渲染抖动。
    **触发条件**：用户对某科目/时间点/课表配置过附加设置，且档案以 JsonElement 形态驻留。
  - 主题切换修复代码 `StyledElement_OnActualThemeVariantChanged` 对计时文本逐控件
    `InvalidateVisual()`（issue #1468 的 workaround），主题热切换瞬间整体重绘。

### 2.2 主窗口层级重申策略（与闪烁直接相关的部分）

`ClassIsland\ClassIsland\MainWindow.axaml.cs`：

```csharp
// :684-704  ProcWnd（WndProc）
if (msg == 0x0047) // WM_WINDOWPOSCHANGED
{
    if ((pos.flags & SWP_NOZORDER) == 0 && ViewModel.Settings.WindowTopmostRecheckMode == 0)
    {
        if (pos.hwndInsertAfter != HWND_TOPMOST) ReCheckTopmostState();
        if (pos.hwndInsertAfter != HWND_BOTTOM)  SetBottom();     // 再度自我压底
    }
}
// :419-427  HighFreqTopmostRecheckTimerOnTick —— WindowTopmostRecheckMode == 3 时按计时器频率
// :456-462  LessonsServiceOnPostMainTimerTicked —— WindowTopmostRecheckMode == 2 时每 50ms 主计时器 tick
{
    ReCheckTopmostState();
    SetBottom();    // SetWindowFeature(Bottommost) → SetWindowPos(HWND_BOTTOM)
}
```

即宿主在「桌面层」模式下会**主动争夺 Z 序最底**，且重申频率取决于
`Settings.WindowTopmostRecheckMode`：0=仅响应自身 Z 序变化（默认），1=前台窗口变化时，
**2=每 50ms 主计时器 tick，3=高频计时器**。任何其它也在争夺最底的窗口都会与之形成拉锯，
每次 `SetWindowPos(HWND_BOTTOM)` 在窗口重叠区域产生重合成/重绘，表观为顶部课表闪烁。

## 3. 插件侧排查结论

| 插件行为 | 是否可能引发宿主课表闪烁 | 依据 |
| --- | --- | --- |
| 高频设置广播 | **否**。插件 `SettingsService.SettingsChanged` 是插件内部事件，宿主未订阅；几何回写已 300ms 防抖（`GeometrySaveDebounceMs=300`），防抖后为单次落盘+单次广播，且只作用于插件自己的悬浮窗 | `SuspensionWindowController.cs:49`、`ApplyToWindow` 逐字段值变化守卫 |
| 设置页动画影响宿主渲染 | **否**。插件未向宿主 `Application.Styles`/`MergedDictionaries` 合并任何样式或动画资源（全插件源码无 `Application.Current`/`StyleInclude`/`MergedDictionaries` 调用） | 源码检索 |
| **悬浮窗钉底器 Z 序操作** | **是（已修复）**。`DesktopLevelPinner` 每窗一个 1 秒兜底计时器，非置顶模式无条件 `SetWindowPos(hwnd, HWND_BOTTOM)`；与宿主 Bottommost 重申（尤其 `WindowTopmostRecheckMode=2/3`）形成**周期性 Z 序拉锯**：宿主压底→插件 1s 内再压底→宿主再重申……每次真实 Z 序变更都在重叠区域触发重绘 | `DesktopLevelPinner.PushToBottom`；宿主 `MainWindow.axaml.cs:684-704/419-462` |

### 3.1 插件侧修复（0.3.0.0 已落地）

`src\ClassIng.Plugin\Services\Overlays\DesktopLevelPinner.cs`：压底前先
`GetWindow(hwnd, GW_HWNDNEXT)` 探测——**已在 Z 序最底（下方无任何窗口）时跳过
`SetWindowPos`**，把 1 秒兜底从「无条件重申」变成「仅在真实越位时纠正」的真 no-op，
消除插件贡献的拉锯成分。置顶模式、Win+D 还原补压底、拖拽让路等既有语义不变。

## 4. 结论

1. **插件侧可修部分（已修）**：钉底器每秒无条件压底 → 已改为越位才压底。
   修复后插件不再主动发起周期性 Z 序变更；若闪烁消失即可确认此因素。
2. **宿主自身行为（插件侧不可根治）**：
   - `WindowTopmostRecheckMode=2/3` 时宿主以 20Hz/高频自我压底，任何其它底部窗口
     （含其它桌面组件类软件）都会与其拉锯，这是宿主设计选择；
   - 附加设置 JsonElement 每 tick 反序列化产生新实例的 `SettingsSource` 重绑定
     （见 2.1），会让配置过附加设置的用户的课表条目每秒抖动。
   两者的彻底修复只能在宿主侧做（重申逻辑去抖/仅在越位时纠正；
   `GetAttachedObject` 物化后写回缓存）。
3. **用户侧规避建议**（修复版验证期间可用）：
   - 宿主设置中把「窗口层级重申模式」（`WindowTopmostRecheckMode`）从 2/3 改为 0；
   - 让 ClassIng 悬浮窗与宿主主窗口不重叠，或将悬浮窗设为「置顶」而非「钉底」。

## 5. 建议的宿主 issue 文案

> **标题**：桌面层模式下主窗口频繁自我压底（Bottommost 重申）与其它桌面组件窗口形成 Z 序拉锯，顶部课表闪烁
>
> **版本**：2.1.0.1（master 开发线源码同样存在）
> **现象**：主窗口停靠桌面层（`WindowLayer=0`）时，与其它也钉在桌面层的窗口
> （如第三方桌面组件/插件悬浮窗）重叠时，顶部课表组件偶发闪烁。
> **复现**：另起任一进程以 1 秒周期调用 `SetWindowPos(its_hwnd, HWND_BOTTOM)`；
> 主窗口 `WindowTopmostRecheckMode=2`（每主计时器 tick 重申）时闪烁显著。
> **原因分析**（源码）：
> 1. `MainWindow.ProcWnd`（WM_WINDOWPOSCHANGED）+ `HighFreqTopmostRecheckTimerOnTick` +
>    `LessonsServiceOnPostMainTimerTicked` 三处都会 `SetBottom()` 无条件重申 Bottommost，
>    未先探测自身是否已在最底，也未与其它底部窗口去抖/协商；
> 2. `LessonControlExpanded.LessonsServiceOnPostMainTimerTicked` 每 50ms 对
>    `SettingsSource` 重新赋值；`AttachableSettingsObject.GetAttachedObject<T>(id)` 命中
>    `JsonElement` 时每次 `Deserialize<T>()` 生成新实例且不写回缓存，导致该条目绑定
>    每秒重评估 20 次。
> **建议**：`SetBottom`/`ReCheckTopmostState` 前先 `GetWindow(hwnd, GW_HWNDNEXT)` 探测
> 越位再纠正；`GetAttachedObject` 物化后写回 `AttachedObjects[id]`；重申模式 2 建议合并去抖。

## 6. 验证方法（ClassIng 修复版）

1. 更新插件至 0.3.0.0（含 `DesktopLevelPinner` 越位探测修复）；
2. 保持宿主 `WindowTopmostRecheckMode=0`，非置顶悬浮窗与主窗口同屏重叠；
3. 观察顶部课表：修复前若闪烁与悬浮窗共存出现，修复后应消失；
4. 若仍闪烁：检查宿主 `WindowTopmostRecheckMode` 是否为 2/3（改为 0 复测）；
   仍复现则基本可归因宿主自身行为，按 §5 提 issue。
