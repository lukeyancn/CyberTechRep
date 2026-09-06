# ClassIsland 宿主"当前正在上课科目"判定机制调研

> 调研日期：2026-09-05
> 分析对象：
> - ClassIsland/ClassIsland **master 分支快照**（2.2 "Misha" 开发线，zip 归档，本地副本 `%TEMP%\classisland-src\ClassIsland-master`，下文源码路径均相对该目录）；
> - **稳定版 2.1.0.1**（tag 源码，关键文件已逐行对照）；
> - NuGet `ClassIsland.PluginSdk 2.1.0.1`（本插件编译所用 SDK，`ClassIsland.Core.dll 2.1.0.1` 中已验证存在 `ILessonsService`、`IPublicLessonsService`、`IAppHost`、`CurrentSubject`、`OnClass` 等符号）。
> 结论先行：**通过 DI 注入或 `IAppHost.GetService<ILessonsService>()` 拿到课程服务，读 `CurrentSubject` / `CurrentState` 属性、订阅 `OnClass` / `OnBreakingTime` / `OnAfterSchool` / `CurrentTimeStateChanged` 事件即可；"查不到当前课"时绝不能把 `CurrentSubject` 非空当作"正在上课"，必须以 `CurrentState == TimeState.OnClass`（或 `IsLessonConfirmed`）为准。**

---

## 1. 核心 API：课程服务 `ILessonsService`

### 1.1 类型与获取方式

| 项 | 内容 |
| --- | --- |
| 接口 | `ClassIsland.Core.Abstractions.Services.ILessonsService`（`ClassIsland.Core/Abstractions/Services/ILessonsService.cs`） |
| 实现 | `ClassIsland.Services.LessonsService`（`ClassIsland/Services/LessonsService.cs`，宿主内注册为 DI 单例） |
| DI 注册 | 宿主 `ClassIsland/App.Services.xaml.cs` 中 `services.AddSingleton<ILessonsService, LessonsService>()` |
| 插件获取方式 ① | 构造函数注入：插件自己的服务（含 `IHostedService`）由宿主同一容器解析，直接在构造函数声明 `ILessonsService` 参数 |
| 插件获取方式 ② | 静态服务定位器：`ClassIsland.Shared.IAppHost.GetService<ILessonsService>()` / `TryGetService<ILessonsService>()`（`ClassIsland.Shared/IAppHost.cs`，`IAppHost.Host` 即 Generic Host） |
| 跨进程 | `ClassIsland.Shared.IPC.Abstractions.Services.IPublicLessonsService`（dotnetCampus.Ipc 生成的 IPC 公开代理，`LessonsService` 构造函数中 `CreateIpcJoint<IPublicLessonsService>(this)` 注册） |

> **[ResolveService] 特性不存在**：全仓库（含 `ClassIsland.Core`）无此特性。插件获取宿主服务的正规方式就是上面两条：构造函数注入（推荐）与 `IAppHost.GetService<T>()` 静态定位器。宿主官方代码自身两种都在用（如 `ScheduleDayControl.axaml.cs:70` 用 `IAppHost.GetService<ILessonsService>()`，`TimeAdjustmentWindow.axaml.cs:34` 用构造函数注入）。
>
> **重要时序**：`IAppHost.Host` 在所有插件的 `Initialize` 执行完、宿主 Build 完容器之后才有值。因此**不能在 `PluginBase.Initialize` 里解析 `ILessonsService`**，应在插件自己的 `IHostedService.StartAsync` 或首次使用时懒解析（ClassIng 现有的 `MessageDispatchService` 等 HostedService 模式正好适用）。

`IAppHost` 关键源码（`ClassIsland.Shared/IAppHost.cs`）：

```csharp
public interface IAppHost
{
    public static readonly Version CoreVersion = new Version(2, 0, 0, 0);
    public static IHost? Host;

    public static T GetService<T>()
    {
        var s = Host?.Services.GetService(typeof(T));
        if (s != null) return (T)s;
        throw new ArgumentException($"Service {typeof(T)} is null!");
    }

    public static T? TryGetService<T>() => (T?)Host?.Services.GetService(typeof(T));
}
```

### 1.2 关键属性（`ILessonsService` / `IPublicLessonsService`）

均实现 `INotifyPropertyChanged`，可直接绑定或监听属性变更。

| 属性 | 类型 | 无课/空档时的值 | 说明 |
| --- | --- | --- | --- |
| `CurrentSubject` | `Subject?` | 见 §4 兜底分析 | **当前时间点的科目**（上课时段才有真实科目；课间是 `Subject.Breaking` 的变体） |
| `CurrentState` | `TimeState` | `None` / `AfterSchool` | 当前时间状态：`None`/`OnClass`/`PrepareOnClass`/`Breaking`/`AfterSchool`（`ClassIsland.Shared/Enums/TimeState.cs`） |
| `CurrentClassPlan` | `ClassPlan?` | `null` | 当前生效的课表（经 §3 筛选后的胜者） |
| `CurrentTimeLayoutItem` | `TimeLayoutItem` | `TimeLayoutItem.Empty` | 当前所处时间点（含起止时间、TimeType） |
| `CurrentSelectedIndex` | `int` | `-1` | 当前时间点在 `ClassPlan.TimeLayout.Layouts` 中的索引 |
| `NextClassSubject` | `Subject` | `Subject.Fallback` | 下一节上课型时间点的科目 |
| `NextClassTimeLayoutItem` / `NextBreakingTimeLayoutItem` | `TimeLayoutItem` | `TimeLayoutItem.Empty` | 下一个上课/课间时间点 |
| `OnClassLeftTime` / `OnBreakingTimeLeftTime` | `TimeSpan` | `TimeSpan.Zero` | 距上课/下课剩余时间（不适用时为 0，负值钳为 0） |
| `IsClassPlanEnabled` | `bool` | — | 用户是否启用了课表功能（false 时 `CurrentClassPlan` 恒为 null） |
| `IsClassPlanLoaded` | `bool` | `false` | 今天是否成功加载到课表 |
| `IsLessonConfirmed` | `bool` | `false` | 当前时间是否落在某个明确的时间点内（上课或课间） |

`Subject` 模型（`ClassIsland.Shared/Models/Profile/Subject.cs`）核心成员：`Name`（科目名）、`Initial`（简称）、`TeacherName`（任课教师）、`IsOutDoor`；三个静态哨兵值：

```csharp
public static readonly Subject Fallback = new() { Initial = "?", Name = "???" };   // 后备科目（查不到）
public static readonly Subject Empty    = new() { Initial = "",  Name = "" };      // 空白科目
public static readonly Subject Breaking = new() { Initial = "休", Name = "课间休息" }; // 课间
```

### 1.3 关键事件（换课、上下课通知）

定义于 `ClassIsland.Core/Abstractions/Services/ILessonsService.cs`：

| 事件 | 触发时机 |
| --- | --- |
| `event EventHandler? OnClass` | 时间状态由其它值**变为** `TimeState.OnClass` 时（进入上课），非每次 tick 重复触发 |
| `event EventHandler? OnBreakingTime` | 变为 `TimeState.Breaking` 时（下课/进入课间） |
| `event EventHandler? OnAfterSchool` | 变为 `TimeState.AfterSchool` 时（当天全部时间点结束） |
| `event EventHandler? CurrentTimeStateChanged` | 任何时间状态变化（含变回 `None`）时，先于上述三事件触发 |
| `event EventHandler? PreMainTimerTicked` / `PostMainTimerTicked` | 主计时器每 50ms 处理课表逻辑前/后（高频，勿做重活） |
| `ClassPlan.ClassesChanged`（模型事件） | 当前课表内容变化时（换课、编辑课程等），`LessonsService` 内部用它使 `ValidTimeLayoutItems` 缓存失效 |

实现要点（`ClassIsland/Services/LessonsService.cs`，master 行号）：

```csharp
// 50ms 主计时器（Avalonia DispatcherTimer，Render 优先级，UI 线程触发）
44:        Interval = TimeSpan.FromMilliseconds(50)
373:    private void MainTimerOnTick(object? sender, EventArgs e)
390:    private void ProcessLessons()
...
503:        if (CurrentState != CurrentOverlayEventStatus)   // 状态有变才发事件
519:        {
520:            CurrentTimeStateChanged?.Invoke(this, EventArgs.Empty);
521:            switch (CurrentState)
522:            {
523:                case TimeState.OnClass:    OnClass?.Invoke(this, EventArgs.Empty);        break;
526:                case TimeState.Breaking:   OnBreakingTime?.Invoke(this, EventArgs.Empty); break;
529:                case TimeState.AfterSchool: OnAfterSchool?.Invoke(this, EventArgs.Empty); break;
```

同时宿主把四个状态事件经 `IIpcService.BroadcastNotificationAsync` 广播给外部进程（`IpcRoutedNotifyIds.OnClassNotifyId` 等），独立进程的联动可走 IPC 而不必做插件。

---

## 2. 多课表筛选逻辑：哪套课表"胜出"

判定入口是 `LessonsService.ProcessLessons()`，每个主计时器 tick（50ms）执行：

```
MainTimerOnTick (50ms, UI 线程)
 └─ ProcessLessons()
     ├─ LoadCurrentClassPlan()          // 决定"今天的课表是哪套"（§2.1）
     │    ├─ Profile.RefreshTimeLayouts()
     │    ├─ 清理过期临时课表 / 临时课表群 / 临时层
     │    └─ CurrentClassPlan = GetClassPlanByDate(now)
     ├─ 在 CurrentClassPlan.ValidTimeLayoutItems 里按 now 定位当前时间点（§2.2）
     └─ 由时间点 + ClassPlan.Classes[i].SubjectId → Profile.Subjects[id] 得到 CurrentSubject
```

### 2.1 日期 → 课表：`GetClassPlanByDate`（`LessonsService.cs:143`，master）

**优先级从高到低**（先命中先返回）：

1. **预定的课表 `Profile.OrderedSchedules[date]`**（调课日历把某天精确预定为某课表）：命中即返回该 `ClassPlan`；若该课表 `IsOverlay`（临时层）且 `Profile.IsOverlayClassPlanEnabled` 才生效。这是"某天整层换课"的正规通道。
2. **临时课表 `Profile.TempClassPlanId`**：仅当 `TempClassPlanSetupTime.Date >= 当天`（过期自动清除，`LoadCurrentClassPlan` 中 `Profile.TempClassPlanId = null`）。
3. **普通课表**：遍历 `Profile.ClassPlans`，过滤条件（`CheckClassPlan`，`LessonsService.cs:582`）：
   - `IsOverlay == false` 且 `IsEnabled == true`；
   - `TimeRule.WeekDay == 当天星期`；
   - 所属课表群 ∈ {当前选中群 `SelectedClassPlanGroupId`，全局群 `GlobalGroupGuid`，临时课表群 `TempClassPlanGroupId`}；启用临时课表群且未过期时，按 `TempClassPlanGroupType` 决定临时群是**叠加**（`Inherit`：临时群课表与默认群课表都参与）还是**覆盖**（`Override`：只看临时群+全局群）；
   - 多周轮换：`TimeRule.WeekCountDiv == GetCyclePositionsByDate(date)[WeekCountDivTotal]`（2 周~N 周循环，基于 `Settings.SingleWeekStartTime` 与 `MultiWeekRotationOffset`，见 `GetCyclePositionsByDate`，`LessonsService.cs:617`）。
   - 同一天命中多套时按群优先级排序取第一：**临时课表群(3) > 选中群(2) > 全局群(1)**。

```csharp
// ClassIsland/Services/LessonsService.cs (master, 节选)
private bool CheckClassPlan(ClassPlan plan, DateTime time)
{
    if (plan.IsOverlay || !plan.IsEnabled) return false;
    if (plan.TimeRule.WeekDay != (int)time.DayOfWeek) return false;
    if (plan.AssociatedGroup != ClassPlanGroup.GlobalGroupGuid &&
        plan.AssociatedGroup != Profile.SelectedClassPlanGroupId &&
        plan.AssociatedGroup != Profile.TempClassPlanGroupId) return false;
    if (plan.TimeRule.WeekCountDivTotal > SettingsService.Settings.MultiWeekRotationMaxCycle) return false;
    if (plan.TimeRule.WeekCountDiv == 0) return true;
    var rotation = GetCyclePositionsByDate(time);
    return plan.TimeRule.WeekCountDiv == rotation[plan.TimeRule.WeekCountDivTotal];
}
```

### 2.2 课表内：时间点定位与"单节课换课"

- `ClassPlan.TimeLayout` = `TimeLayouts[TimeLayoutId]`（课表引用一套时间表）。
- `ClassPlan.ValidTimeLayoutItems`（`ClassIsland.Shared/Models/Profile/ClassPlan.cs:36-107`）：对 `TimeLayout.Layouts` 中 `TimeType is 0 or 1 or 2` 的时间点做**前向+后向裁剪**——每一节课（`ClassInfo`）通过 `CurrentTimeLayoutItem` 与时间点一一对应，若某节课 `IsEnabled == false`（用户在课表编辑里停用了这节课），则该节课覆盖的区间及其前后被裁掉，`ProcessLessons` 就查不到该时段的时间点（表现为空档）。
- **换课标记**：临时层课表（`IsOverlay == true`，`OverlaySourceId` 指向源课表）生成后，`ClassPlan.RefreshIsChangedClass()`（`ClassPlan.cs:496`）逐节比对 `Classes[i].SubjectId != 源课表.Classes[i].SubjectId`，把被换掉的课标为 `ClassInfo.IsChangedClass = true`——这只是**展示标记**，筛选逻辑本身已经因为临时层课表在 §2.1 中胜出而天然使用了新科目，无需插件自行比对。
- `ProcessLessons` 中把时间点索引映射为课程序号（`GetClassIndex`，`LessonsService.cs:531`）：过滤出 `TimeType == 0`（上课型）的时间点列表，再取其序号，从 `CurrentClassPlan.Classes[i0].SubjectId` 查 `Profile.Subjects` 字典得到 `Subject`；**查不到 SubjectId 对应科目时 `currentSubject` 保持 null**。

### 2.3 关于"科目替代 / 折合"

全仓库检索 `替代|折合` **无任何命中**——宿主**没有**"科目 A 替代/折合为科目 B"的概念，当前科目永远是 `Profile.Subjects[SubjectId]` 的原样对象。若 ClassIng 需要"数学→数学(联考折合)"之类的替代展示/判定，需在插件侧自行实现（现有 `Services/Stores/UserSubjectRuleStore.cs` 的用户规则层正是承担这个职责的位置）。

---

## 3. 更新时机与"查不到当前课"的兜底

### 3.1 什么时候变、什么时候发事件

- `CurrentState` / `CurrentSubject` 等所有属性在**每个 50ms tick**重算（`ProcessLessons`），通过 `ObservableRecipient.SetProperty` 仅在值变化时发 `PropertyChanged`；
- `OnClass` / `OnBreakingTime` / `OnAfterSchool` / `CurrentTimeStateChanged` **只在状态发生迁移的那个 tick 触发一次**（`CurrentState != CurrentOverlayEventStatus` 守卫），随后宿主还会向 IPC 广播；
- 属性更新统一发生在 tick 末尾（"预计算 → 一次性赋值"模式），事件在赋值之后发出，因此事件处理器里读到的 `CurrentSubject` / `CurrentState` 已是新值；
- 课表内容变更（换课、编辑）通过 `ClassPlan.ClassesChanged` → `MakeValidTimeLayoutItemsDirty()` 使时间点缓存失效，下一个 tick 即生效；`CurrentSubject` 的 `PropertyChanged` 还会驱动宿主规则引擎（`RulesetService.NotifyStatusChanged()`）。
- 所有事件都在 **Avalonia UI 线程**（DispatcherTimer）触发；插件处理器内不要做阻塞 IO，需要后台处理时自行 `Task.Run` / 调度。

### 3.2 空档/课间/无课时的返回值（兜底结论）

对 `ProcessLessons`（master 与 2.1.0.1 行为一致，2.1.0.1 `LessonsService.cs:491-499` 同款）逐情形核对：

| 情形 | `CurrentState` | `CurrentSubject` | 其它 |
| --- | --- | --- | --- |
| 今天没有可用课表 / 用户停用课表功能 | `None` | `Subject.Fallback`（Name=`"???"`，Initial=`"?"`） | `CurrentClassPlan=null`，`IsClassPlanLoaded=false`，`CurrentTimeLayoutItem=TimeLayoutItem.Empty`，`CurrentSelectedIndex=-1` |
| 有课表但此刻不在任何时间点内（大空档/未到第一节课/放学后） | `AfterSchool`（当后续无任何时间点）或 `None` | `Subject.Fallback` | `OnClassLeftTime` 若有下一节课则为剩余时间，否则 0 |
| 课间休息 | `Breaking` | `Subject.Breaking`，且其 `Name` 被改写为该课间的名称（如"眼保健操"） | `OnBreakingTimeLeftTime` = 距下课剩余 |
| 上课，但 `Classes[i].SubjectId` 在 `Profile.Subjects` 中不存在（含 Guid.Empty） | `OnClass` | `Subject.Fallback` | 时间点/索引仍有效，`IsLessonConfirmed=true` |
| 上课，科目正常 | `OnClass` | `Profile.Subjects[SubjectId]` 实例 | `IsLessonConfirmed=true` |

> **注意两处易错点**：
> 1. 接口 XML 注释与官方文档（docs.classisland.tech/dev/lessons-service.html）写"`CurrentSubject` 如果没有加载课表则为 null"，但 master 与 2.1.0.1 的实际实现都是 `CurrentSubject = currentSubject ?? Subject.Fallback`（master `LessonsService.cs:492`），**运行时几乎不会是 null**。插件兜底必须同时识别 `null`、`Subject.Fallback`、以及"非 OnClass 状态"三种"查不到"。
> 2. 课间时 `ProcessLessons` 直接改写静态 `Subject.Breaking.Name`（共享实例），读方不应缓存该对象，读 `Name` 后即用。

**ClassIng 联动的安全兜底推荐写法**：

```csharp
var state = lessons.CurrentState;
var subject = lessons.CurrentSubject;
var inClass = state == TimeState.OnClass
              && lessons.IsLessonConfirmed
              && subject is not null
              && !ReferenceEquals(subject, Subject.Fallback);
if (!inClass)
{
    // 非上课时段（空档/课间/放学/无课表/科目缺失）：联动逻辑进入静默或等待态，
    // 不要把 Fallback("???")、Breaking("课间休息") 当成真实科目上报或展示。
}
```

---

## 4. 宿主版本兼容性

| 版本 | 状态 | 说明 |
| --- | --- | --- |
| 1.x（≤1.7） | 已过时 | `ILessonsService` 已存在（WPF 时代）。1.6.0.0 新增"放学事件和时间状态"、`IPublicLessonService.GetClassPlanByDate`（`doc/ChangeLogs/1.6/1.6.0.0/App.md`）；1.7.0.x 修复空课程时间状态、索引溢出等 bug。1.x 命名空间/加载机制与 2.x 不兼容 |
| **2.1.x（当前稳定线，含 2.1.0.1）** | **推荐目标** | API 迁至 `ClassIsland.Core.Abstractions.Services` / `ClassIsland.Shared`（Avalonia）；本插件 `ClassIsland.PluginSdk 2.1.0.1` 的 `ClassIsland.Core.dll` 中已验证包含 `ILessonsService`/`IPublicLessonsService`/`IAppHost` 及 `CurrentSubject`、`OnClass`、`GetClassPlanByDate`、`StartMainTimer` 等成员，**插件按现有 SDK 版本即可直接编译使用** |
| 2.2 预览（master，"Misha"） | 不建议面向生产 | 官方明示"仅开发者预览"；对照本文阅读的 master 快照，`ILessonsService` 表面与 2.1.0.1 一致（`ProcessLessons`/`GetClassPlanByDate`/`CheckClassPlan` 逻辑相同，新增了 `GetCyclePositionsByDate` 公开方法、临时课表群 Inherit/Override 等），但预览线曾有档案模型破坏性变更，迁移需在 2.2 正式发布后重新核对 |

已知实现细节差异提醒：2.1.0.1 与 master 的 `ProcessLessons` 事件守卫逻辑一致（先 `CurrentTimeStateChanged` 再按状态发对应事件），master 引入的多周轮换/课表群 API（`GetCyclePositionsByDate`、`TempClassPlanGroupType`）在 2.1.0.1 中签名可能不完全相同——若插件需要用到这两个方法，编译时以 SDK 2.1.0.1 程序集为准。

官方插件开发文档（可引用）：
- 课程服务：<https://docs.classisland.tech/dev/lessons-service.html>
- API 参考：`ILessonsService` <https://api.docs.classisland.tech/api/ClassIsland.Core.Abstractions.Services.ILessonsService.html>；`IPublicLessonsService` <https://api.docs.classisland.tech/api/ClassIsland.Shared.IPC.Abstractions.Services.IPublicLessonsService.html>
- 插件开发：<https://docs.classisland.tech/dev/plugins/>

---

## 5. 插件接入最小示例（ClassIng 落地路径）

结合 ClassIng 现有结构（`src/ClassIng.Plugin/Plugin.cs` 的 `ClassIngPlugin.Initialize` 已注册多个 HostedService），接入"当前科目"只需新增一个服务 + 一个 HostedService，完整代码路径如下。

### 5.1 服务与生命周期接线（`Plugin.cs` 的 `Initialize` 内追加）

```csharp
using ClassIsland.Core.Abstractions.Services;   // ILessonsService
using ClassIsland.Shared.Enums;                // TimeState
using ClassIsland.Shared.Models.Profile;       // Subject

// ---- 当前科目联动服务 ----
services.AddSingleton<CurrentSubjectLinkService>();
services.AddHostedService<CurrentSubjectLinkService>();   // StartAsync 中才解析宿主服务
```

### 5.2 服务实现（新文件，如 `Services/Link/CurrentSubjectLinkService.cs`）

```csharp
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Enums;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassIng.Plugin.Services.Link;

/// <summary>
/// 订阅宿主课程服务，向 ClassIng 消息管道提供"当前正在上课科目"。
/// 注意：宿主服务必须在 StartAsync（容器构建完成后）解析，不能在插件 Initialize 阶段解析。
/// </summary>
public class CurrentSubjectLinkService(
    ILogger<CurrentSubjectLinkService> logger)
    : IHostedService
{
    private ILessonsService? _lessons;
    private IExactTimeService? _time;   // 可选：对表用

    public Subject? CurrentSubject { get; private set; }
    public bool IsInClass { get; private set; }

    /// <summary>订阅方可挂的事件（UI 线程触发，处理器内勿做阻塞操作）。</summary>
    public event EventHandler? OnClass;
    public event EventHandler? OnBreakingTime;
    public event EventHandler? OnAfterSchool;
    public event EventHandler? OnStateChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 方式①：从宿主根容器解析（IHostedService 由宿主容器启动，构造注入亦可：
        // 直接给构造函数加 ILessonsService lessonsService 参数，效果相同且更简洁）
        _lessons = ClassIsland.Shared.IAppHost.GetService<ILessonsService>()
            ?? throw new InvalidOperationException("宿主课程服务不可用");

        _lessons.OnClass                += (_, _) => { Refresh(); OnClass?.Invoke(this, EventArgs.Empty); };
        _lessons.OnBreakingTime         += (_, _) => { Refresh(); OnBreakingTime?.Invoke(this, EventArgs.Empty); };
        _lessons.OnAfterSchool          += (_, _) => { Refresh(); OnAfterSchool?.Invoke(this, EventArgs.Empty); };
        _lessons.CurrentTimeStateChanged += (_, _) => { Refresh(); OnStateChanged?.Invoke(this, EventArgs.Empty); };

        Refresh();   // 启动即对一次表，避免依赖下一次状态迁移
        logger.LogInformation("已接入 ClassIsland 课程服务，当前状态 {State}", _lessons.CurrentState);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Refresh()
    {
        if (_lessons is null) return;
        var state   = _lessons.CurrentState;
        var subject = _lessons.CurrentSubject;

        // 兜底：只有"确认处于上课时段 + 科目存在 + 非 Fallback"才算正在上课
        IsInClass = state == TimeState.OnClass
                    && _lessons.IsLessonConfirmed
                    && subject is not null
                    && !ReferenceEquals(subject, Subject.Fallback);
        CurrentSubject = IsInClass ? subject : null;

        if (!IsInClass && subject is not null)
            logger.LogDebug("非上课时段（State={State}, Subject={Name}），联动保持静默",
                state, subject.Name);   // Fallback="???" / Breaking="课间休息" 等
    }
}
```

> 简化替代：如果只是偶尔查一次，不需要事件，也可以在任意由宿主容器创建的对象里直接构造注入：
> `public XxxService(ILessonsService lessonsService) { ... }`（宿主内 `ClockComponent`、`TimeAdjustmentWindow` 等均为此写法）；或在 UI 线程外用 `IAppHost.TryGetService<ILessonsService>()` 懒取。

### 5.3 下游消费（对接现有管道示例）

```csharp
// 例：MessageDispatchService 或悬浮窗控制器中
var link = sp.GetRequiredService<CurrentSubjectLinkService>();
var subject = link.CurrentSubject;          // null = 非上课/查不到；否则为宿主 Profile 中的 Subject 实例
string subjectName = subject?.Name ?? "(非上课时段)";
// 如需宿主侧更细的信息：lessons.CurrentClassPlan（当前课表）、lessons.CurrentTimeLayoutItem（时间点）、
// lessons.OnBreakingTimeLeftTime（距下课）、lessons.GetClassPlanByDate(date)（任意日期课表）
```

### 5.4 编译面核对清单

- `ClassIng.Plugin.csproj` 已引用 `ClassIsland.PluginSdk 2.1.0.1`（`ExcludeAssets=runtime`，运行时由宿主提供），其中 `ClassIsland.Core.dll` 包含 `ILessonsService` 与 `IAppHost`，**无需新增包引用**；
- `ClassIsland.Shared`（`Subject`、`TimeState`、`IAppHost` 所在程序集）同样由 SDK 传递引用提供；
- 插件不得把 `ClassIsland.Core.dll` / `ClassIsland.Shared.dll` 打进 `.cipx`（与宿主同名不同源会类型分裂，现有 `StripHostProvidedAssemblies` 目标已处理 Microsoft.Extensions.*，新增引用时注意维持 `ExcludeAssets=runtime`）。

---

## 6. 关键源码文件索引（master 快照）

| 文件 | 内容 |
| --- | --- |
| `ClassIsland.Core/Abstractions/Services/ILessonsService.cs` | 课程服务接口：事件、`GetClassPlanByDate`、`GetCyclePositionsByDate` |
| `ClassIsland/Services/LessonsService.cs` | 全部判定逻辑：主计时器(50ms)、`ProcessLessons()`(:390)、`GetClassPlanByDate`(:143)、`CheckClassPlan`(:582)、`LoadCurrentClassPlan`(:549)、`GetClassIndex`、事件派发(:503) |
| `ClassIsland.Shared/IAppHost.cs` | 静态服务定位器 `GetService<T>` / `TryGetService<T>` |
| `ClassIsland.Shared/Enums/TimeState.cs` | `None/OnClass/PrepareOnClass/Breaking/AfterSchool` |
| `ClassIsland.Shared/Models/Profile/Subject.cs` | 科目模型与 `Fallback/Empty/Breaking` 哨兵 |
| `ClassIsland.Shared/Models/Profile/ClassPlan.cs` | `ValidTimeLayoutItems`（:36）、`TimeLayout` 解析（:386）、换课标记 `RefreshIsChangedClass`（:496）、`OverlaySourceId` |
| `ClassIsland.Shared/Models/Profile/ClassInfo.cs` | 单节课：`SubjectId`、`Index`、`CurrentTimeLayoutItem`、`IsEnabled`、`IsChangedClass` |
| `ClassIsland.Shared/Models/Profile/Profile.cs` | `OrderedSchedules`、`TempClassPlanId`、`TempClassPlanGroupId/Type/ExpireTime`、`IsOverlayClassPlanEnabled` 等多课表数据源 |
| `ClassIsland.Shared.IPC/Abstractions/Services/IPublicLessonsService.cs` | 跨进程公开的只读属性集（各属性空档语义的权威注释） |
| `ClassIsland/Services/Automation/Triggers/OnClassTrigger.cs` 等 | 宿主自身消费 `ILessonsService` 事件的范例 |
| `doc/ChangeLogs/1.6/1.6.0.0/App.md`、`doc/ChangeLogs/1.7/1.7.0.1/App.md` | 课程服务相关版本变更记录 |
