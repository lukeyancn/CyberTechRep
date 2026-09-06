namespace CyberTechRep.Plugin.Services.Overlays;

/// <summary>
/// 上课联动判定纯逻辑（可脱离 UI/宿主测试）：
/// <para>
/// 「正在上课」必须同时满足：宿主状态为 OnClass（由调用方比对 <c>CurrentState == TimeState.OnClass</c>，
/// 避免宿主枚举泄漏进可测试签名）+ IsLessonConfirmed + 科目名非哨兵
/// （宿主查不到课表/科目时 CurrentSubject 几乎不会是 null，而是 Fallback("???") 等哨兵实例）。
/// </para>
/// </summary>
public static class ClassAutoOpenDecider
{
    /// <summary>
    /// 判定是否为「正在上一节真实科目」：
    /// <paramref name="isOnClassState"/>（= 宿主 CurrentState == TimeState.OnClass）
    /// + <paramref name="lessonConfirmed"/> + 科目名非空白/哨兵（"???"/"课间休息"）。
    /// </summary>
    public static bool IsInClassState(bool isOnClassState, bool lessonConfirmed, string? subjectName)
        => isOnClassState
           && lessonConfirmed
           && SubjectAutoOpenMatcher.IsRealLessonSubject(subjectName);

    /// <summary>学科名匹配（转发 <see cref="SubjectAutoOpenMatcher.MatchArchivedSubject"/>，统一入口便于测试）。</summary>
    public static string? MatchArchivedSubject(string? currentSubjectName, IEnumerable<string?>? archivedSubjects)
        => SubjectAutoOpenMatcher.MatchArchivedSubject(currentSubjectName, archivedSubjects);
}
