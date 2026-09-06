using ClassIng.Shared.Models;

namespace ClassIng.Plugin.Services.Stores;

/// <summary>学科判定结果（含是否套用了发送者映射，供测试与日志断言）。</summary>
public readonly record struct SubjectResolution(
    string Subject,
    double Confidence,
    SubjectSource Source,
    bool RuleApplied);

/// <summary>
/// 作业学科判定优先级矩阵（纯函数，可单测）：
/// <list type="number">
/// <item>显式人工修正（SubjectSource=Manual 的既有条目）永不回退——由调用方先行短路，不进入本函数；</item>
/// <item>识别链正常命中（Subject != 「未分类」）→ 采用识别链结果，<strong>不用发送者映射覆盖</strong>；</item>
/// <item>识别链返回「未分类」且该发送者有映射 → 套用映射学科（SubjectSource=Manual 语义保留）；</item>
/// <item>其余（识别链「未分类」且无映射）→ 识别链结果原样保留。</item>
/// </list>
/// 背景修复：此前 <c>ApplyUserRule</c>/<c>Merge</c> 无条件用 user-subjects.json 映射覆盖识别链结果，
/// 一旦学过某发送者的映射，该发送者的作业永远按映射归类，识别链再也无法生效（自动分类失效的根因）。
/// </summary>
public static class HomeworkSubjectResolver
{
    /// <summary>识别链的「未分类」占位值。</summary>
    public const string Unclassified = "未分类";

    /// <summary>学科是否为「未分类」（含空/空白）。</summary>
    public static bool IsUnclassified(string? subject) =>
        string.IsNullOrWhiteSpace(subject) || string.Equals(subject, Unclassified, StringComparison.Ordinal);

    /// <summary>
    /// 判定一条「非人工修正」作业的最终学科。
    /// 不含「既有条目已人工修正」分支——该情况必须由调用方（Merge/Upsert）先行短路，保证永不回退。
    /// </summary>
    /// <param name="chainSubject">识别链返回的学科。</param>
    /// <param name="chainConfidence">识别链置信度。</param>
    /// <param name="chainSource">识别链来源级别。</param>
    /// <param name="userRule">发送者映射学科（user-subjects.json；无映射为 null）。</param>
    public static SubjectResolution Resolve(
        string chainSubject, double chainConfidence, SubjectSource chainSource, string? userRule)
    {
        // ③ 识别链未分类 + 有映射 → 套用映射（映射本身为「未分类」视为无映射，避免空转）
        if (IsUnclassified(chainSubject)
            && !IsUnclassified(userRule))
        {
            return new SubjectResolution(userRule!, 1.0, SubjectSource.Manual, RuleApplied: true);
        }

        // ②/④ 识别链结果原样保留（正常命中或未分类且无映射）
        return new SubjectResolution(chainSubject, chainConfidence, chainSource, RuleApplied: false);
    }
}
