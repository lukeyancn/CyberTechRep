using ClassIng.Plugin.Services.Stores;
using ClassIng.Shared.Models;

namespace ClassIng.Plugin.Services.SubjectChain;

/// <summary>
/// 「学科识别完全重置」结果摘要：只含清除条数与重置前的模式快照，
/// 不含任何成员/群 OpenID 明文（OpenID 属用户数据，不进日志与 UI 反馈）。
/// </summary>
public sealed record SubjectRecognitionResetSummary(
    int RemovedBindings,
    int RemovedRules,
    SubjectRecognitionMode ModeBefore,
    bool SelectionWindowEnabledBefore);

/// <summary>
/// 两段式轻确认状态机（与作业窗删除同口径：首次点击进入待确认态，
/// 3 秒内再次点击才执行，超时自动复位）。时间源可注入以便单测「超时不执行」。
/// </summary>
public sealed class TwoStageConfirmGate
{
    /// <summary>待确认窗口时长（与作业窗删除一致）。</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(3);

    private readonly Func<DateTimeOffset> _utcNow;
    private DateTimeOffset? _deadline;

    public TwoStageConfirmGate(Func<DateTimeOffset>? utcNow = null) =>
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    /// <summary>是否处于待确认态（窗口内）。</summary>
    public bool IsPending => _deadline is not null && _utcNow() <= _deadline;

    /// <summary>进入待确认态（重复调用会刷新截止时间）。</summary>
    public void Begin(TimeSpan? window = null) => _deadline = _utcNow() + (window ?? DefaultWindow);

    /// <summary>
    /// 第二次点击：窗口内返回 true（应执行）并退出待确认态；
    /// 超时或未进入待确认态返回 false 且复位（fail-safe，不会跨窗口残留放行）。
    /// </summary>
    public bool TryConfirm()
    {
        if (_deadline is null || _utcNow() > _deadline)
        {
            _deadline = null;
            return false;
        }

        _deadline = null;
        return true;
    }

    /// <summary>手动复位（执行完成或 UI 刷新时调用）。</summary>
    public void Reset() => _deadline = null;
}

/// <summary>
/// 「学科识别完全重置」纯逻辑（可脱离 UI 测试）。
/// <para>
/// 重置范围（明确边界，只动学科识别相关）：
/// ① 清空成员学科显式绑定（member-subject-bindings.json，<see cref="MemberSubjectBindingStore"/>）；
/// ② 清空按发送者学习映射（user-subjects.json，<see cref="UserSubjectRuleStore"/>）；
/// ③ <see cref="SubjectRecognitionSettings"/> 恢复默认（Mode=MemberSelection、SelectionWindowEnabled=true）。
/// 不动：作业/通知数据、files.json 归档库、AI 凭据、其他页面设置。
/// </para>
/// <para>
/// 调用方（词表编辑页）在两段式确认通过后执行 <see cref="Execute"/>，
/// 随后自行调用 <c>ISettingsService.SaveAsync</c> 广播 SettingsChanged 热生效；
/// 绑定/学习映射在 <see cref="MemberSubjectBindingStore.Set"/> /
/// <see cref="UserSubjectRuleStore.Set"/> 写入路径上即时持久化，清空同理。
/// </para>
/// </summary>
public static class SubjectRecognitionResetLogic
{
    /// <summary>收集当前要重置的项（计划阶段，只读；条数不含 OpenID 明文）。</summary>
    public static (int Bindings, int Rules) Collect(MemberSubjectBindingStore? bindings, UserSubjectRuleStore? rules) =>
        (bindings?.GetAll().Count ?? 0, rules?.Count ?? 0);

    /// <summary>
    /// 执行重置：清空两个存储（各自即时持久化并记日志，仅条数）并把识别设置恢复默认。
    /// 设置本身的持久化与广播（SettingsChanged）由调用方经 ISettingsService.SaveAsync 完成。
    /// </summary>
    public static SubjectRecognitionResetSummary Execute(
        MemberSubjectBindingStore? bindings,
        UserSubjectRuleStore? rules,
        SubjectRecognitionSettings subjectRecognition)
    {
        ArgumentNullException.ThrowIfNull(subjectRecognition);

        var summary = new SubjectRecognitionResetSummary(
            RemovedBindings: bindings?.ClearAll() ?? 0,
            RemovedRules: rules?.ClearAll() ?? 0,
            ModeBefore: subjectRecognition.Mode,
            SelectionWindowEnabledBefore: subjectRecognition.SelectionWindowEnabled);

        subjectRecognition.Mode = SubjectRecognitionMode.MemberSelection;
        subjectRecognition.SelectionWindowEnabled = true;
        return summary;
    }
}
