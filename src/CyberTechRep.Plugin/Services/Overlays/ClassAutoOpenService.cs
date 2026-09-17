using Avalonia.Threading;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.Overlays;

/// <summary>
/// 上课自动弹出对应学科文件悬浮窗联动（模块 10 + 需求 10 上课联动延时）：
/// <para>
/// 订阅宿主课程服务 <see cref="ILessonsService"/> 的状态迁移事件——
/// <see cref="ILessonsService.OnClass"/>（进入上课）、<see cref="ILessonsService.OnBreakingTime"/>（下课进课间）、
/// <see cref="ILessonsService.OnAfterSchool"/>（放学），另监听
/// <see cref="ILessonsService.CurrentSubject"/> 的 PropertyChanged 以覆盖「连堂无课间直接切课」
/// （事件本身仅在值变化时触发一次，不存在每 tick 重复触发）。
/// </para>
/// <para>
/// 行为：进入上课且科目为真实科目（非哨兵）时，查该学科已归档文件——有文件且
/// <see cref="SubjectCircleBarSettings.AutoOpenWithClass"/> 开启 → 弹出/切换文件悬浮窗到该学科；
/// 无文件 → 不弹，且若此前由联动打开过窗则收起（避免残留上一节课的过期内容；
/// 用户手动打开的窗不受影响，以联动来源标记区分）。下课/放学/查不到当前课 → 收起联动窗。
/// 另监听文件管道 <see cref="IFilePipelineService.FileUpdated"/>（仅 Archived 记录触发评估）：
/// 课中学生把文件发进群归档后，当前这节课立即补弹，不必等到下一次状态迁移。
/// </para>
/// <para>
/// 需求 10 延时（<see cref="SubjectCircleBarSettings.AutoOpenDelaySeconds"/>，判定口径见
/// <see cref="ClassAutoOpenScheduler"/>）：
/// <b>准点（0）</b>与历史行为逐条一致；
/// <b>推迟（正数）</b>——进入上课时不再立即评估，而是用 <see cref="DispatcherTimer"/> 一次性等待 N 秒后再评估；
/// 等待期间「离开上课状态 / 科目变化 / 设置变化」都会取消本次等待；等待到期后本课延时即视为已消耗，
/// 课中到达的文件归档事件恢复「立即评估补弹」的既有语义；
/// <b>提前（负数）</b>——仅当延时为负时处理 <see cref="ILessonsService.PostMainTimerTicked"/> 节拍，
/// 在「尚未上课 + 下一节课是真实科目 + 0 &lt; 距上课剩余时间 ≤ |延时|」时立即按下一节课科目评估；
/// 去重键 = 下一节课科目名 + 上课时间点开始时间（同一节课只提前触发一次），
/// 随后真正的上课迁移到达时沿用 <c>_lastOpenedSubject</c> 去重，不重复弹。
/// </para>
/// <para>
/// 时序约束：宿主服务必须在 <see cref="StartAsync"/>（宿主容器构建完成后）解析，
/// 不能在插件 Initialize 阶段解析（IAppHost.Host 未就绪）；解析失败只记日志并跳过，
/// 不影响宿主启动。所有评估经 <see cref="Dispatcher.UIThread"/> 调度、整体吞异常记日志，
/// 绝不向宿主事件源抛异常；<see cref="StopAsync"/> 退订全部事件（含取消挂起的延时等待）。
/// </para>
/// </summary>
public sealed class ClassAutoOpenService(
    IFilePipelineService pipeline,
    ISettingsService settingsService,
    SubjectFilesController filesController,
    ILogger<ClassAutoOpenService>? logger = null) : IHostedService
{
    private readonly ILogger _logger = logger ?? NullLogger<ClassAutoOpenService>.Instance;
    private ILessonsService? _lessons;
    private bool _subscribed;

    /// <summary>去重标记：最近一次已成功弹出联动窗的科目名（同科目重复事件不再反复查询/弹窗）。
    /// 仅在真正弹窗后设置；「无归档文件不弹」不算已处理——课中文件归档到达后须允许同科目重评估补弹。</summary>
    private string? _lastOpenedSubject;

    /// <summary>防刷屏标记：最近一次记过「无归档文件不弹」日志的科目名（同一科目课中不重复打这条日志）。</summary>
    private string? _lastNoArchiveSubject;

    /// <summary>需求 10 延时已消耗标记：本课延时窗口已到期的科目名（延时到期后同科目的
    /// 文件归档补弹等事件按立即评估处理）。离开上课状态或设置变更时复位。</summary>
    private string? _delayConsumedSubject;

    /// <summary>挂起的延时等待：等待中的科目名（null = 没有挂起等待；仅在 UI 线程读写）。</summary>
    private string? _pendingDelaySubject;

    /// <summary>延时等待计时器（需求 10 推迟路径；首次使用时在 UI 线程创建）。</summary>
    private DispatcherTimer? _delayWaitTimer;

    /// <summary>需求 10 提前路径去重键：下一节课科目名 + 上课时间点开始时间（同一节课只提前触发一次）。</summary>
    private string? _preClassTriggerKey;

    /// <summary>最近一次观察到的上课联动设置（开关 + 延时）。设置变更广播时只有这两项真的变化才
    /// 取消挂起的延时等待——否则悬浮窗拖拽/位置回写等高频保存会把等待不断推后，延时可能永不到期。</summary>
    private (bool AutoOpen, int DelaySeconds)? _lastClassAutoOpenSettings;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 宿主容器此时已构建完成；TryGetService 失败（老宿主/异常环境）只记日志跳过
        var lessons = ClassIsland.Shared.IAppHost.TryGetService<ILessonsService>();
        if (lessons is null)
        {
            _logger.LogWarning("未能从宿主解析 ILessonsService，上课联动不启用（不影响宿主启动）");
            return Task.CompletedTask;
        }

        _lessons = lessons;
        lessons.OnClass += OnLessonsEvent;
        lessons.OnBreakingTime += OnLessonsEvent;
        lessons.OnAfterSchool += OnLessonsEvent;
        // 覆盖连堂/无课间直接切课：CurrentSubject 变化时同样评估（宿主仅在实际变化时触发）
        lessons.PropertyChanged += OnLessonsPropertyChanged;
        // 需求 10 提前路径：主计时器每 tick 处理完课表后触发，用于课前窗口判定
        //（处理器内仅当延时为负时继续，其余模式立即返回，避免无谓开销）
        lessons.PostMainTimerTicked += OnPostMainTimerTicked;
        // 课中文件归档到达（群里发来附件并下载完成）立即重评估：当前这节课马上补弹，不等下一次状态迁移
        pipeline.FileUpdated += OnPipelineFileUpdated;
        // 设置变更（含上课中途打开 AutoOpenWithClass 开关、改延时）立即重新评估，
        // 否则开关在课中打开要等到下一次状态迁移才生效，用户感知为「开了也不弹」
        settingsService.SettingsChanged += OnSettingsChanged;
        _subscribed = true;
        var circle = settingsService.Current.Overlays.SubjectCircle;
        _lastClassAutoOpenSettings = (circle.AutoOpenWithClass, circle.AutoOpenDelaySeconds);
        _logger.LogInformation(
            "上课联动已接入宿主课程服务，当前状态 {State}，AutoOpenWithClass={AutoOpen}，AutoOpenDelaySeconds={Delay}",
            lessons.CurrentState, circle.AutoOpenWithClass, circle.AutoOpenDelaySeconds);

        // 启动即对一次表：插件在上课中途加载（宿主重启/热重载）时也能恢复联动
        EvaluateOnUi("启动对表");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lessons is not null && _subscribed)
        {
            _lessons.OnClass -= OnLessonsEvent;
            _lessons.OnBreakingTime -= OnLessonsEvent;
            _lessons.OnAfterSchool -= OnLessonsEvent;
            _lessons.PropertyChanged -= OnLessonsPropertyChanged;
            _lessons.PostMainTimerTicked -= OnPostMainTimerTicked;
        }

        pipeline.FileUpdated -= OnPipelineFileUpdated;
        settingsService.SettingsChanged -= OnSettingsChanged;
        RunOnUiThread(() => CancelDelayWait("服务停止"));
        _lessons = null;
        _subscribed = false;
        return Task.CompletedTask;
    }

    private void OnLessonsEvent(object? sender, EventArgs e) => EvaluateOnUi("时间状态迁移");

    /// <summary>设置变更（需求 10）：若「联动开关 / 延时值」真的变化，先取消挂起的延时等待并复位
    /// 「延时已消耗」标记（新的延时值对当前这节课重新生效），再按新值重新评估。
    /// 两项都未变化（如悬浮窗拖拽位置回写触发的保存）时只重新评估、不动挂起的等待——
    /// 否则高频保存会把延时等待不断推后，延时可能永不到期。</summary>
    private void OnSettingsChanged(object? sender, AppSettings e)
    {
        var circle = settingsService.Current.Overlays.SubjectCircle;
        var current = (circle.AutoOpenWithClass, circle.AutoOpenDelaySeconds);
        var changed = _lastClassAutoOpenSettings != current;
        _lastClassAutoOpenSettings = current;

        Dispatcher.UIThread.Post(() =>
        {
            if (changed)
            {
                _delayConsumedSubject = null;
                CancelDelayWait("上课联动设置变更");
            }

            _ = EvaluateCoreAsync("设置变更");
        });
    }

    private void OnPipelineFileUpdated(object? sender, FileRecord e)
    {
        // 只有真正归档落盘的记录才可能改变「当前学科有无文件」的判定结果；
        // 下载中/失败/重复的中间态不值得一次评估
        if (e.Status == FileStatus.Archived)
        {
            EvaluateOnUi("文件归档更新");
        }
    }

    private void OnLessonsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // ILessonsService 实现 INotifyPropertyChanged；科目属性名以字符串比较（跨程序集稳定）
        if (e.PropertyName == "CurrentSubject")
        {
            EvaluateOnUi("当前科目变更");
        }
    }

    /// <summary>需求 10 提前路径：主计时器每 tick 处理完课表后触发（每秒一次）。
    /// 仅延时为负（提前模式）且尚未上课时需要做课前窗口判定；正在上课时提前窗口已结束，
    /// 由 <see cref="ILessonsService.OnClass"/> 等事件负责评估，这里直接跳过避免每秒一次的无谓评估。</summary>
    private void OnPostMainTimerTicked(object? sender, EventArgs e)
    {
        try
        {
            if (settingsService.Current.Overlays.SubjectCircle.AutoOpenDelaySeconds >= 0)
            {
                return;
            }

            if (_lessons?.CurrentState == TimeState.OnClass)
            {
                return;
            }

            EvaluateOnUi("主计时器（课前窗口判定）", verbose: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "上课联动：主计时器节拍处理失败（已吞掉，不影响宿主）");
        }
    }

    /// <param name="verbose">是否输出每次评估的决策日志（主计时器节拍路径传 false 防刷屏）。</param>
    private void EvaluateOnUi(string reason, bool verbose = true)
    {
        // 事件源在 Avalonia UI 线程，但保持统一调度以兼容任意来源；Post 不阻塞宿主事件派发
        Dispatcher.UIThread.Post(() => _ = EvaluateCoreAsync(reason, verbose));
    }

    /// <summary>把动作调度到 UI 线程执行（DispatcherTimer 等 UI 亲和对象只允许在 UI 线程操作；
    /// 宿主事件源可能在后台线程，<see cref="StopAsync"/> 也可能不在 UI 线程）。</summary>
    private void RunOnUiThread(Action action)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.UIThread.Post(action);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "上课联动：UI 线程调度失败（已吞掉）");
        }
    }

    /// <summary>核心评估：按 <see cref="ClassAutoOpenScheduler"/> 的决策分流（提前触发 / 延时等待 / 立即评估 /
    /// 不评估并收起），吞掉全部异常记日志，绝不向宿主事件源抛。</summary>
    private async Task EvaluateCoreAsync(string reason, bool verbose = true)
    {
        try
        {
            var lessons = _lessons;
            if (lessons is null)
            {
                return;
            }

            var circle = settingsService.Current.Overlays.SubjectCircle;
            var autoOpen = circle.AutoOpenWithClass;
            var delay = circle.AutoOpenDelaySeconds;

            var state = lessons.CurrentState;
            var isOnClassState = state == TimeState.OnClass;
            var confirmed = lessons.IsLessonConfirmed;
            var currentName = lessons.CurrentSubject?.Name;
            var inClass = ClassAutoOpenDecider.IsInClassState(isOnClassState, confirmed, currentName);

            // 课前提前窗口输入：仅提前模式读取宿主属性，其余模式不触碰（避免无谓开销）
            string? nextName = null;
            var leftTime = TimeSpan.Zero;
            var nextStart = TimeSpan.Zero;
            if (delay < 0)
            {
                nextName = lessons.NextClassSubject?.Name;
                leftTime = lessons.OnClassLeftTime;
                nextStart = lessons.NextClassTimeLayoutItem?.StartTime ?? TimeSpan.Zero;
            }

            var schedule = ClassAutoOpenScheduler.Decide(
                isOnClassState, confirmed, currentName, nextName, leftTime, delay, IsDelayConsumed(currentName));

            if (verbose)
            {
                _logger.LogInformation(
                    "上课联动评估：Reason={Reason} State={State} Subject={Subject} Confirmed={Confirmed} " +
                    "Delay={Delay}s 决策={Action}（{Detail}）",
                    reason, state, currentName, confirmed, delay, schedule.Action, schedule.ReasonText);
            }

            // ① 课前提前触发（提前模式 + 开关开启 + 尚未上课 + 窗口命中）：用下一节课科目立即评估；
            //    去重键含上课时间点开始时间，同一节课的后续节拍直接返回（不重复评估/刷日志）
            if (delay < 0 && autoOpen && !inClass
                && schedule.Action == ClassAutoOpenAction.EvaluateNow
                && schedule.Reason == ClassAutoOpenReason.PreClassWindow)
            {
                var key = $"{schedule.SubjectName}|{nextStart:c}";
                if (string.Equals(_preClassTriggerKey, key, StringComparison.Ordinal))
                {
                    return;
                }

                _preClassTriggerKey = key;
                _logger.LogInformation(
                    "上课联动：课前提前触发（距上课 {Left:0.#} 秒 ≤ 提前 {Ahead} 秒），按下一节课科目 {Subject} 评估",
                    leftTime.TotalSeconds, -delay, schedule.SubjectName);
                await EvaluateSubjectAsync(reason, schedule.SubjectName!);
                return;
            }

            // ② 正在上课但联动开关关闭：现状语义 = 只记日志跳过（不弹窗、不收用户可见的联动窗）
            if (inClass && !autoOpen)
            {
                _logger.LogInformation(
                    "上课联动：检测到上课 {Subject}，但 AutoOpenWithClass 未开启（悬浮窗设置页可开启），跳过",
                    currentName?.Trim());
                return;
            }

            // ③ 不在上课（下课/课间/放学/哨兵/未确认）：收起联动窗并复位标记，允许下一节课重新评估
            if (!inClass)
            {
                _lastOpenedSubject = null;
                _lastNoArchiveSubject = null;
                _delayConsumedSubject = null;
                _preClassTriggerKey = null;
                CancelDelayWait("离开上课状态");
                await filesController.HideIfAutoOpenedAsync(reason);
                return;
            }

            // ④ 正在上课且开关开启：按延时设置推迟等待，或立即评估
            var name = currentName!.Trim();
            if (delay > 0 && !IsDelayConsumed(name))
            {
                StartDelayWait(name, delay, reason);
                return;
            }

            await EvaluateSubjectAsync(reason, name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "上课联动评估失败 Reason={Reason}（已吞掉，不影响宿主事件源）", reason);
        }
    }

    /// <summary>本课延时是否已消耗（延时到期评估过；同科目重复判断）。</summary>
    private bool IsDelayConsumed(string? subjectName) =>
        !string.IsNullOrWhiteSpace(subjectName)
        && string.Equals(_delayConsumedSubject, subjectName.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 需求 10 推迟路径：启动（或延续）一次性延时等待；等待到期后评估该科目。
    /// 同一节课的重复事件不重复启动；科目变化时重新启动（等价于「取消旧等待 + 按新科目重新等待」）。
    /// 仅可在 UI 线程调用（由 <see cref="EvaluateCoreAsync"/> 分流保证）。
    /// </summary>
    private void StartDelayWait(string subject, int delaySeconds, string reason)
    {
        if (_delayWaitTimer is null)
        {
            _delayWaitTimer = new DispatcherTimer();
            _delayWaitTimer.Tick += OnDelayWaitElapsed;
        }

        if (_pendingDelaySubject is { } pending)
        {
            if (string.Equals(pending, subject, StringComparison.OrdinalIgnoreCase))
            {
                return; // 同一节课已有挂起等待：幂等 no-op
            }

            _logger.LogInformation("上课联动：延时等待重新开始（科目变化 {Old} → {New}）", pending, subject);
        }

        _pendingDelaySubject = subject;
        _delayWaitTimer.Stop();
        _delayWaitTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, delaySeconds));
        _delayWaitTimer.Start();
        _logger.LogInformation(
            "上课联动：检测到上课 {Subject}，按延时设置推迟 {Delay} 秒后评估（等待中；Reason={Reason}，" +
            "期间离开上课/科目变化/设置变化会取消本次等待）",
            subject, delaySeconds, reason);
    }

    /// <summary>取消挂起的延时等待（无等待时为 no-op）。仅可在 UI 线程调用。</summary>
    private void CancelDelayWait(string reason)
    {
        var pending = _pendingDelaySubject;
        if (pending is null)
        {
            return;
        }

        try
        {
            _pendingDelaySubject = null;
            _delayWaitTimer?.Stop();
            _logger.LogInformation("上课联动：取消延时等待（{Reason}，等待中的学科 {Subject}）", reason, pending);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "上课联动：取消延时等待失败（已吞掉）");
        }
    }

    /// <summary>延时等待到期：标记本课延时已消耗，随后评估该科目（到期即视为「已进入上课」的最终状态）。</summary>
    private void OnDelayWaitElapsed(object? sender, EventArgs e)
    {
        try
        {
            _delayWaitTimer?.Stop();
            var subject = _pendingDelaySubject;
            _pendingDelaySubject = null;
            if (string.IsNullOrWhiteSpace(subject))
            {
                return;
            }

            _delayConsumedSubject = subject;
            _logger.LogInformation(
                "上课联动：延时等待结束，开始评估学科 {Subject}（本课后续事件按立即评估，文件归档补弹规则不变）",
                subject);
            _ = EvaluateSubjectAsync("延时到期", subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "上课联动：延时等待结束处理失败（已吞掉，不影响宿主）");
        }
    }

    /// <summary>对指定学科执行「查归档文件 → 命中则弹出 / 无文件则收起」的评估主体
    /// （与历史行为一致；异常只记日志）。</summary>
    private async Task EvaluateSubjectAsync(string reason, string subjectName)
    {
        try
        {
            // 防御：延时等待期间被外部关闭开关（设置变更本应取消等待，这里是双保险）
            if (!settingsService.Current.Overlays.SubjectCircle.AutoOpenWithClass)
            {
                return;
            }

            var name = subjectName.Trim();
            if (string.Equals(_lastOpenedSubject, name, StringComparison.OrdinalIgnoreCase))
            {
                return; // 本节课已弹过窗的重复事件（状态迁移 + 科目变更 + 文件归档 + 提前触发后上课），去重
            }

            var records = await pipeline.GetRecordsAsync();
            var archivedSubjects = records
                .Select(r => SubjectFilesQuery.ExtractSubject(r.ArchivedRelativePath))
                .ToList();
            var matched = ClassAutoOpenDecider.MatchArchivedSubject(name, archivedSubjects);
            if (matched is null)
            {
                // 该学科没有已归档文件：不弹；若联动窗开着（上一节课留下的）则收起。
                // 取舍：不保留旧学科内容展示，避免悬浮窗与当前课不一致造成误导；
                // 用户手动打开的窗仍保持不动。
                // 「无归档文件」不算已处理（不设 _lastOpenedSubject）：课中文件归档到达后
                // 同科目可重评估补弹。日志按科目去重防刷屏，并为空库/不匹配两种情况给出可行动提示。
                if (!string.Equals(_lastNoArchiveSubject, name, StringComparison.OrdinalIgnoreCase))
                {
                    _lastNoArchiveSubject = name;
                    if (records.Count == 0)
                    {
                        _logger.LogInformation(
                            "上课联动：学科 {Subject} 不弹窗——归档库为空（文件记录 0 条）。" +
                            "群里发附件归档后（见文件设置页归档根目录），本节课内或下一节课会自动弹出",
                            name);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "上课联动：学科 {Subject} 不弹窗——归档库有 {Count} 条记录但无该学科文件" +
                            "（归档学科段 [{Segments}]，检查文件设置页学科目录名是否与课表科目名一致）",
                            name, records.Count, string.Join(", ", archivedSubjects.Distinct()));
                    }
                }
                await filesController.HideIfAutoOpenedAsync("当前学科无已归档文件");
                return;
            }

            _lastOpenedSubject = name;
            _lastNoArchiveSubject = null;
            _logger.LogInformation(
                "上课联动：学科 {Subject} 归档命中 {Matched}，弹出文件悬浮窗（文件记录 {Count} 条，Reason={Reason}）",
                name, matched, records.Count, reason);
            await filesController.OpenForClassAsync(matched);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "上课联动：学科 {Subject} 评估失败（已吞掉，不影响宿主）", subjectName);
        }
    }
}
