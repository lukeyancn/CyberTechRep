# ClassIsland 顶部课表偶发闪烁调研

> 调研日期：2026-09-06（2026-09-07 增补宿主侧修复与 GrantUiAccess 结论）
> 调研人：ClassIng 任务 7（课表闪烁排查）
> 分析对象：
> - ClassIsland 宿主源码本地副本（仓库内 `ClassIsland\` 目录，2.x Avalonia 开发线，与 2.1.0.1 稳定版行为基本一致）；
> - CyberTechRep（ClassIng）插件源码（`src\CyberTechRep.Plugin\`）。
>
> **结论先行：插件侧存在一个可修的贡献因素（钉底器每秒无条件 `SetWindowPos(HWND_BOTTOM)` 与宿主
> Bottommost 重申逻辑形成 Z 序拉锯），已在 ClassIng 0.3.0.0 中修复（真 no-op 化）；宿主侧的剩余
> 触发源（`SetBottom` 无条件重申）已在本地 vendored 宿主副本中修复（`MainWindow.SetBottom`
> 越位探测，见 §3.2）；`WindowTopmostRecheckMode` 用户默认值保持 0 不变。GrantUiAccess 路线
> 经调研确认对本问题**无用**（见 §3.3）。附加设置 JsonElement 重绑定确认是真实的重绑定抖动源，
> 但与本问题（跨窗口 Z 序闪烁）无关，未改行为（见 §3.4）。**

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

`src\CyberTechRep.Plugin\Services\Overlays\DesktopLevelPinner.cs`（`PushToBottom`，压底调用处
约 :176-200）：压底前先 `GetWindow(hwnd, GW_HWNDNEXT)` 探测——**已在 Z 序最底（下方无任何窗口）时
跳过 `SetWindowPos`**，把 1 秒兜底从「无条件重申」变成「仅在真实越位时纠正」的真 no-op，
消除插件贡献的拉锯成分。置顶模式、Win+D 还原补压底、拖拽让路等既有语义不变。

插件侧其余排查项（2026-09-07 复核，均无残留闪烁源）：
- `SuspensionWindowController.ApplyToWindow`：Topmost/CanResize/Opacity/FontSize/Width/Height/
  Position 全部逐字段值变化守卫，未变化的设置广播不触碰平台窗口；
- WndProc 钩子只记录 WM_ENTER/EXIT_SIZE_MOVE 让路标记，不吞消息、不触碰 Z 序；
- 关闭穿透移除 `WS_EX_LAYERED` 走 `SetWindowLong(GWL_EXSTYLE)`，样式变更不产生 Z 序消息；
- Show/Hide 周期只在 `Show` 前经 `Apply` 触发一次压底（已被越位探测守卫），Hide 无层级重申；
- 置顶模式下钉底器直接 return，不与 Avalonia Topmost（一次性 `HWND_TOPMOST`）互相干预；
- `Views\OverlayBehaviors.cs` 快捷菜单三开关走 `ApplySettingsAsync` → `ApplyToWindow`
  （同上守卫）链路，无独立 Z 序操作。

### 3.2 宿主侧修复（本地 vendored 宿主副本，2026-09-07）

`ClassIsland\ClassIsland\MainWindow.axaml.cs` 的 `SetBottom()`（约 :814-842）：压底前增加
「已在 Z 序最底」探测（新增 `GetWindow` P/Invoke，`GW_HWNDNEXT == 0` 即下方无任何窗口 →
直接 return，跳过 `SetWindowFeature(Bottommost)`）。与插件侧 `DesktopLevelPinner` 同一手法，
一次守卫同时覆盖三条重申路径：

1. `ProcWnd` 模式 0（WM_WINDOWPOSCHANGED 响应，默认模式）；
2. `HighFreqTopmostRecheckTimerOnTick` 模式 3（高频计时器重申）;
3. `LessonsServiceOnPostMainTimerTicked` 模式 2（每 50ms 主计时器 tick 重申）。

语义不变量：仅在窗口已被证明位于 Z 序最底时跳过——此时 `SetWindowPos(HWND_BOTTOM)` 本就是
无操作，其唯一实际效果是发送一轮 `WM_WINDOWPOSCHANGED` 并触发重叠区域重合成（即闪烁本体），
因此跳过它是纯粹的去抖，不改变任何层级行为。`WindowTopmostRecheckMode` 默认值仍为 0
（`Models\Settings.cs:1860`），设置语义未动。`ReCheckTopmostState`（Topmost 重申）未加探测：
Topmost 带内重申不与钉底窗口争夺 Z 序最底，且正确探测 Topmost 带位置需检查
`WS_EX_TOPMOST` + 前驱窗口样式，收益与复杂度不成比例，保持原样。

### 3.3 GrantUiAccess 调研结论：无用（不适用于本问题）

[GrantUiAccess](https://github.com/HelloWRC/GrantUiAccess) 的作用机制：通过令牌操作（需管理员
权限，无需数字签名）把目标进程提升为 UIAccess 进程，使其窗口在 Z 序争夺中能压过**其它 UIAccess
进程**的窗口（典型场景：全屏 UWP / 开始菜单这类 UIAccess 窗口会盖住普通置顶窗口）。

对本问题**无用**，原因：

1. **UIAccess 是进程级属性，不提供进程内仲裁**。本问题的闪烁发生在同一个非 UIAccess 进程
   （ClassIsland 宿主）的多个窗口之间（主窗口 vs 插件悬浮窗）；同一进程内的窗口 Z 序由
   `SetWindowPos` 直接决定，与 UIAccess 完全无关；
2. 提升为 UIAccess 反而引入新问题：需要管理员权限运行/注入令牌，且 UIAccess 进程会绕过部分
   UIPI 保护，属于高危权限变更，与「零回归」约束冲突；
3. GrantUiAccess 面向 Avalonia ClassIsland 3.x 构建产物的提权流程，与本仓库 vendored 的
   2.x 开发线场景也不匹配。

结论：Z 序拉锯的正确解法是 §3.1/§3.2 的「越位才纠正」探测，而非进程提权。

### 3.4 LessonControlExpanded `SettingsSource` 重绑定排查结论（确认存在，但与窗口闪烁无关，未改行为）

复核确认 §2.1 所述机制属实：`LessonControlExpanded.LessonsServiceOnPostMainTimerTicked`
（`ClassIsland.Core\Controls\LessonsControls\LessonControlExpanded.axaml.cs:194-203`）每 50ms tick
重新赋值 `SettingsSource`；当命中的附加设置仍是 `JsonElement`（档案刚从 JSON 加载未物化）时，
`AttachableSettingsObject.GetAttachedObject<T>(id)`（`ClassIsland.Shared\AttachableSettingsObject.cs`
`Deserialize` 分支）每次生成**新实例**且不写回缓存 → `SetField` 以引用比较判定变更 →
`PropertyChanged("SettingsSource")` 每 tick 触发 → 该条目全部 `SettingsSource.*` 绑定每秒
重评估 20 次。

**判定：这是真实的重绑定抖动源，但不是窗口闪烁（跨窗口 Z 序拉锯）的来源**——该路径只发生在
控件绑定层，从不触碰窗口 Z 序/位置，也无法解释「所有 ClassIsland 窗口在悬浮窗打开时闪烁」的
现象（该现象只与 §2.2/§3.2 的 Bottommost 重申相关）。且其触发条件苛刻：用户配置过科目/时间点
附加设置、且档案中该项以 JsonElement 形态驻留。

**未改行为的原因（零回归约束）**：
- 唯一安全的缓存点是在 `GetAttachedObject` 物化后写回 `AttachedObjects[id]`，但这会改变
  `AttachedObjects` 字典的内容形态（JsonElement → 类型化对象），档案再次序列化落盘时该字段
  的 JSON 形态可能与原始档案不同（命名策略/结构由宿主序列化选项决定），属档案往返行为变更；
- 在 `LessonControlExpanded` 控件层缓存则无法感知附加设置的运行时更新（`WriteAttachedObject`
  写入新值后控件会读到过期缓存），同样是行为回归。

建议向宿主上游提 issue 时一并提出（见 §5），由宿主在「物化后写回 + 序列化形态验证」的前提下自行修复。

## 4. 结论

1. **插件侧可修部分（已修）**：钉底器每秒无条件压底 → 已改为越位才压底（§3.1）。
   修复后插件不再主动发起周期性 Z 序变更。
2. **宿主侧剩余部分（已在本地 vendored 宿主副本修复，§3.2）**：`SetBottom()` 三条重申路径
   （ProcWnd 模式 0 / 模式 2 计时器 / 模式 3 高频计时器）均加越位探测，已在最底时跳过
   `SetWindowPos`；`WindowTopmostRecheckMode` 默认值 0 与设置语义不变。上游若采纳可参照
   §5 提 issue。
3. **附加设置 JsonElement 重绑定（§3.4）**：确认是真实的条目级重绑定抖动源，但与跨窗口
   Z 序闪烁无关，在零回归约束下未改行为，仅在此记录。
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

## 6. 验证方法（ClassIng 修复版 + 本地宿主副本）

1. 更新插件至 0.3.0.0（含 `DesktopLevelPinner` 越位探测修复，§3.1），并使用含 §3.2
   `SetBottom` 越位探测修复的本地宿主副本构建产物；
2. 非置顶悬浮窗与主窗口同屏重叠（`WindowTopmostRecheckMode` 保持默认 0；2/3 模式下修复同样生效）；
3. 观察顶部课表：修复前若闪烁与悬浮窗共存出现，修复后应消失；
4. 若仍闪烁：宿主与插件两侧重申均已真 no-op 化，可基本排除 Z 序拉锯因素，
   按 §5 反馈宿主上游进一步排查。
