namespace CyberTechRep.Plugin.Services.Overlays;

/// <summary>上课联动调度动作（三选一，见 <see cref="ClassAutoOpenScheduler.Decide"/>）。</summary>
public enum ClassAutoOpenAction
{
    /// <summary>不评估：本次事件不触发文件匹配与弹窗（收窗等既有语义由调用方另行处理）。</summary>
    Skip = 0,

    /// <summary>立即评估：用 <see cref="ClassAutoOpenSchedule.SubjectName"/> 指定的科目匹配归档文件并弹窗。</summary>
    EvaluateNow = 1,

    /// <summary>延时评估：等待 <see cref="ClassAutoOpenSchedule.DelaySeconds"/> 秒（&gt; 0）后再评估同一科目。</summary>
    EvaluateAfterDelay = 2
}

/// <summary>
/// 调度理由（可枚举，供日志与测试断言；中文文本见
/// <see cref="ClassAutoOpenScheduler.DescribeReason"/>）。
/// </summary>
public enum ClassAutoOpenReason
{
    /// <summary>正在上课（准点或延时已到期）：立即评估当前科目。</summary>
    OnClassImmediate,

    /// <summary>正在上课且延时设置 &gt; 0：推迟 N 秒后评估。</summary>
    OnClassDelayed,

    /// <summary>本课延时窗口已到期：后续事件按立即评估处理（课中文件归档补弹规则不变）。</summary>
    OnClassDelayElapsed,

    /// <summary>课前提前窗口命中：立即按下一节课科目评估。</summary>
    PreClassWindow,

    /// <summary>不在上课状态（延时设置 ≥ 0 时无课前提前语义）：不评估。</summary>
    NotOnClass,

    /// <summary>当前时间点未确认（IsLessonConfirmed=false）：不评估。</summary>
    LessonNotConfirmed,

    /// <summary>科目名是哨兵/空白（非真实科目）：不评估。</summary>
    SubjectNotReal,

    /// <summary>提前模式：距上课剩余时间为 0（正在上课或没有下一节课）：不评估。</summary>
    PreClassLeftTimeZero,

    /// <summary>提前模式：下一节课科目非真实（哨兵/空白）：不评估。</summary>
    PreClassNextSubjectNotReal,

    /// <summary>提前模式：尚未进入提前窗口（距上课剩余时间 &gt; |延时|）：不评估。</summary>
    PreClassNotInWindow
}

/// <summary>
/// 上课联动调度结果（纯数据）：动作 + 理由 + 目标科目（立即/延时评估时非空）+ 延时秒数（仅延时动作）。
/// </summary>
public readonly record struct ClassAutoOpenSchedule(
    ClassAutoOpenAction Action,
    ClassAutoOpenReason Reason,
    string? SubjectName,
    int DelaySeconds)
{
    /// <summary>是否需要评估（立即或延时）。</summary>
    public bool ShouldEvaluate => Action != ClassAutoOpenAction.Skip;

    /// <summary>理由文本（日志用）。</summary>
    public string ReasonText => ClassAutoOpenScheduler.DescribeReason(Reason);
}

/// <summary>
/// 上课联动延时调度纯逻辑（需求 10，不依赖 UI 与宿主枚举，输入全部为 bool/字符串/TimeSpan 可测类型）：
/// 给定「是否处于上课状态 / 时间点是否确认 / 当前科目名 / 下一节课科目名 / 距上课剩余时间 /
/// 延时设置（秒）」判定本次事件的处理方式——立即评估 / 延时 N 秒后评估 / 不评估，并给出可枚举的理由
/// （<see cref="ClassAutoOpenSchedule.Reason"/>）与中文文本（<see cref="ClassAutoOpenSchedule.ReasonText"/>）。
/// <para>
/// 判定口径（与悬浮窗设置页「上课联动延时」说明一致）：
/// ① <b>延时 = 0</b>：与历史行为逐条一致——仅「正在上课 + 已确认 + 真实科目」→ 立即评估，其余不评估；
/// ② <b>延时 &gt; 0（推迟）</b>：正在上课 + 已确认 + 真实科目 → 延时 N 秒后评估；
///    同一节课延时到期后 <paramref name="delayAlreadyConsumed"/> 传 true，后续事件按立即评估处理
///    （课中文件归档补弹规则不变）；未上课 → 不评估；
/// ③ <b>延时 &lt; 0（提前）</b>：尚未上课且「下一节课科目是真实科目」且「0 &lt; 距上课剩余时间 ≤ |延时|」
///    → 立即评估（用下一节课科目名）；正在上课时按 ①/② 的「正在上课」口径评估当前科目；
///    其余不评估（含距上课剩余时间为 0 = 正在上课或没有下一节课、尚未进入课前窗口）；
/// ④ 非真实科目（哨兵 "???"/"课间休息"/空白）一律不评估。
/// </para>
/// </summary>
public static class ClassAutoOpenScheduler
{
    /// <summary>
    /// 计算本次事件的调度动作。
    /// </summary>
    /// <param name="isOnClassState">宿主 CurrentState == TimeState.OnClass（调用方比对后传入，避免宿主枚举泄漏）。</param>
    /// <param name="lessonConfirmed">宿主 IsLessonConfirmed。</param>
    /// <param name="currentSubjectName">宿主 CurrentSubject.Name（哨兵/空白视为非真实）。</param>
    /// <param name="nextSubjectName">宿主 NextClassSubject.Name（仅提前模式使用，可为 null）。</param>
    /// <param name="onClassLeftTime">宿主 OnClassLeftTime（正在上课或无下一节课时为 TimeSpan.Zero）。</param>
    /// <param name="delaySeconds">设置项 AutoOpenDelaySeconds：0=准点，正数=推迟，负数=提前。</param>
    /// <param name="delayAlreadyConsumed">本课延时是否已消耗（延时到期评估过；true 时按立即评估处理）。</param>
    public static ClassAutoOpenSchedule Decide(
        bool isOnClassState,
        bool lessonConfirmed,
        string? currentSubjectName,
        string? nextSubjectName,
        TimeSpan onClassLeftTime,
        int delaySeconds,
        bool delayAlreadyConsumed = false)
    {
        if (delaySeconds < 0)
        {
            // 提前模式：课前窗口判定（下一节课科目 + 距上课剩余时间）
            if (isOnClassState)
            {
                // 正在上课：提前窗口已结束，按「准点」口径评估当前科目（提前已弹过则由调用方去重）
                return DecideOnClass(lessonConfirmed, currentSubjectName, delaySeconds, delayAlreadyConsumed);
            }

            if (onClassLeftTime <= TimeSpan.Zero)
            {
                // 正在上课（宿主约定此场景为 Zero）或没有下一节课：没有可提前的课
                return Skip(ClassAutoOpenReason.PreClassLeftTimeZero);
            }

            if (!SubjectAutoOpenMatcher.IsRealLessonSubject(nextSubjectName))
            {
                return Skip(ClassAutoOpenReason.PreClassNextSubjectNotReal);
            }

            if (onClassLeftTime > TimeSpan.FromSeconds(-(double)delaySeconds))
            {
                return Skip(ClassAutoOpenReason.PreClassNotInWindow);
            }

            return new ClassAutoOpenSchedule(
                ClassAutoOpenAction.EvaluateNow,
                ClassAutoOpenReason.PreClassWindow,
                nextSubjectName!.Trim(),
                DelaySeconds: 0);
        }

        // 准点 / 推迟模式：只认「正在上课 + 已确认 + 真实科目」
        if (!isOnClassState)
        {
            return Skip(ClassAutoOpenReason.NotOnClass);
        }

        return DecideOnClass(lessonConfirmed, currentSubjectName, delaySeconds, delayAlreadyConsumed);
    }

    /// <summary>「正在上课」口径的公共分支（准点 / 推迟 / 提前模式下正在上课时的处理）。</summary>
    private static ClassAutoOpenSchedule DecideOnClass(
        bool lessonConfirmed, string? currentSubjectName, int delaySeconds, bool delayAlreadyConsumed)
    {
        if (!lessonConfirmed)
        {
            return Skip(ClassAutoOpenReason.LessonNotConfirmed);
        }

        if (!SubjectAutoOpenMatcher.IsRealLessonSubject(currentSubjectName))
        {
            return Skip(ClassAutoOpenReason.SubjectNotReal);
        }

        var subject = currentSubjectName!.Trim();
        if (delaySeconds > 0 && !delayAlreadyConsumed)
        {
            return new ClassAutoOpenSchedule(
                ClassAutoOpenAction.EvaluateAfterDelay,
                ClassAutoOpenReason.OnClassDelayed,
                subject,
                delaySeconds);
        }

        return new ClassAutoOpenSchedule(
            ClassAutoOpenAction.EvaluateNow,
            delayAlreadyConsumed && delaySeconds > 0
                ? ClassAutoOpenReason.OnClassDelayElapsed
                : ClassAutoOpenReason.OnClassImmediate,
            subject,
            DelaySeconds: 0);
    }

    private static ClassAutoOpenSchedule Skip(ClassAutoOpenReason reason) =>
        new(ClassAutoOpenAction.Skip, reason, SubjectName: null, DelaySeconds: 0);

    /// <summary>理由的中文文本（Information 级日志用，便于真机取证）。</summary>
    public static string DescribeReason(ClassAutoOpenReason reason) => reason switch
    {
        ClassAutoOpenReason.OnClassImmediate => "正在上课（准点）：立即评估当前科目",
        ClassAutoOpenReason.OnClassDelayed => "正在上课：按延时设置推迟后评估当前科目",
        ClassAutoOpenReason.OnClassDelayElapsed => "本课延时窗口已到期：后续事件按立即评估",
        ClassAutoOpenReason.PreClassWindow => "课前提前窗口命中：立即按下一节课科目评估",
        ClassAutoOpenReason.NotOnClass => "不在上课状态：不评估",
        ClassAutoOpenReason.LessonNotConfirmed => "当前时间点未确认：不评估",
        ClassAutoOpenReason.SubjectNotReal => "科目非真实课（哨兵/空白）：不评估",
        ClassAutoOpenReason.PreClassLeftTimeZero => "距上课剩余时间为 0（正在上课或没有下一节课）：不评估",
        ClassAutoOpenReason.PreClassNextSubjectNotReal => "下一节课科目非真实课（哨兵/空白）：不评估",
        ClassAutoOpenReason.PreClassNotInWindow => "尚未进入课前提前窗口：不评估",
        _ => reason.ToString()
    };
}
