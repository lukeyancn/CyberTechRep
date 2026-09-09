using CyberTechRep.Shared.Models;

namespace CyberTechRep.Plugin.Services.Stores;

/// <summary>文档追加决策。</summary>
public enum DocumentMergeDecision
{
    /// <summary>新增一条文档条目（与已有条目无重叠）。</summary>
    Append,

    /// <summary>完全重复（归一化后逐字相同）：跳过，不新增条目、不改正文。</summary>
    Duplicate,

    /// <summary>部分重叠：并入已有条目（取并集增量，保留原顺序）。</summary>
    MergeIntoExisting
}

/// <summary>追加计划（纯数据，可单测）。</summary>
/// <param name="Decision">决策。</param>
/// <param name="ExistingIndex">并入目标在 Entries 中的下标（仅 MergeIntoExisting 有意义）。</param>
/// <param name="MergedText">并集文本（仅 MergeIntoExisting 有意义）。</param>
/// <param name="NewLines">本次消息中「原有内容没有」的行（供 ManualText 增量追加）。</param>
public sealed record DocumentMergePlan(
    DocumentMergeDecision Decision, int ExistingIndex, string MergedText, IReadOnlyList<string> NewLines);

/// <summary>
/// 作业文档去重合并算法（需求 1，纯函数、无 IO，可单测）。
/// <para>
/// <b>选型说明</b>：采用「归一化 + 行级集合对齐 + 并集增量」方案，而不是整段相似度（编辑距离）
/// 或语义相似度。理由：
/// <list type="number">
/// <item>老师重复发送作业的两种真实形态是「完全重发」与「重发并补充」——前者归一化后逐字相同，
/// 后者是行级超集/子集，行级集合对齐能精确覆盖且结果可解释（不会把两条不同作业误合并）；</item>
/// <item>编辑距离/语义相似度阈值难调，误合并（丢作业）的代价远高于漏合并（多一行），
/// 故只在「行集合高度重叠」时才合并；</item>
/// <item>合并后取并集并保留原有行顺序，用户手工编辑过的文本单独走 ManualText 增量追加，
/// 不会因为合并而覆盖用户的删改。</item>
/// </list>
/// <b>合并边界</b>：仅同一学科文档内、且同一发送者（MemberOpenId 相同；旧数据两侧均为空视为同一）
/// 的条目参与比对；跨学科/跨老师绝不合并。
/// </para>
/// </summary>
public static class HomeworkDocumentMerger
{
    /// <summary>行级 Jaccard 相似度合并阈值：≥ 该值才视为「同一份作业的重发/补充」。</summary>
    public const double LineOverlapThreshold = 0.5;

    /// <summary>
    /// 字符级归一化：全角 → 半角（含全角数字/字母/标点）、去所有空白、ASCII 转小写。
    /// 用于「完全重复」判定与行级比对。
    /// <para>
    /// 只做字符级变换，<b>不</b>处理行结构（行首序号/项目符号的剥离是
    /// <see cref="SplitLines"/> 的职责）。这样整段比对与逐行比对共用同一套字符口径，
    /// 行级比对不会因「序号写在哪一行」而产生差异。
    /// </para>
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var mapped = ch switch
            {
                '。' => '.',
                '、' => ',',
                '【' => '[',
                '】' => ']',
                '《' => '<',
                '》' => '>',
                '“' or '”' or '〝' or '〞' => '"',
                '‘' or '’' => '\'',
                '—' or '–' or '─' => '-',
                // 全角 ASCII 区（U+FF01..U+FF5E，含全角数字、字母与 ，；：！？（）－～ 等）
                >= '！' and <= '～' => (char)(ch - 0xFEE0),
                _ => ch
            };
            if (char.IsWhiteSpace(mapped))
            {
                continue;
            }

            sb.Append(char.ToLowerInvariant(mapped));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 拆行：按换行拆分，去首尾空白与空行；每行再剥离行首序号/项目符号
    /// （「1.」「2、」「-」「•」「(3)」等），保留原始书写（不归一化标点，供展示）。
    /// </summary>
    public static IReadOnlyList<string> SplitLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = StripLeadingMarker(raw.Trim());
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    /// <summary>
    /// 决策：对同一发送者的已有条目做「完全重复 → 跳过」「行级高度重叠 → 并集增量合并」
    /// 「否则新增」三档判定。
    /// </summary>
    public static DocumentMergePlan Plan(
        IReadOnlyList<HomeworkDocumentEntry> entries, string? newText, string? memberOpenId)
    {
        entries ??= [];
        var newLines = SplitLines(newText);
        if (newLines.Count == 0)
        {
            return new DocumentMergePlan(DocumentMergeDecision.Duplicate, -1, "", []);
        }

        var newNormalized = Normalize(newText);

        // ① 完全重复：同发送者已有条目归一化后逐字相同 → 跳过（不新增、不改正文）
        for (var i = 0; i < entries.Count; i++)
        {
            if (!SameSender(entries[i].MemberOpenId, memberOpenId))
            {
                continue;
            }

            if (Normalize(entries[i].Text) == newNormalized)
            {
                return new DocumentMergePlan(DocumentMergeDecision.Duplicate, i, entries[i].Text, []);
            }
        }

        // ② 部分重叠：行级 Jaccard 相似度最高且 ≥ 阈值 → 并集增量合并
        var bestIndex = -1;
        var bestScore = 0.0;
        List<string>? bestMerged = null;
        List<string>? bestNewLines = null;
        for (var i = 0; i < entries.Count; i++)
        {
            if (!SameSender(entries[i].MemberOpenId, memberOpenId))
            {
                continue;
            }

            var oldLines = SplitLines(entries[i].Text);
            if (oldLines.Count == 0)
            {
                continue;
            }

            var score = Overlap(oldLines, newLines);
            if (score < LineOverlapThreshold || score <= bestScore)
            {
                continue;
            }

            var (merged, added) = Union(oldLines, newLines);
            bestIndex = i;
            bestScore = score;
            bestMerged = merged;
            bestNewLines = added;
        }

        if (bestIndex >= 0 && bestMerged is not null)
        {
            return new DocumentMergePlan(
                DocumentMergeDecision.MergeIntoExisting, bestIndex,
                string.Join(Environment.NewLine, bestMerged),
                bestNewLines ?? []);
        }

        return new DocumentMergePlan(DocumentMergeDecision.Append, -1, "", newLines);
    }

    /// <summary>发送者一致性：两侧都非空时要求完全相同；任一侧为空视为可合并（兼容旧数据）。</summary>
    private static bool SameSender(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return true;
        }

        return string.Equals(left, right, StringComparison.Ordinal);
    }

    /// <summary>行级 Jaccard：归一化后的交集 / 并集（空集返回 0）。</summary>
    internal static double Overlap(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var leftSet = left.Select(Normalize).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
        var rightSet = right.Select(Normalize).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (leftSet.Count == 0 || rightSet.Count == 0)
        {
            return 0;
        }

        var intersection = leftSet.Count(rightSet.Contains);
        var union = leftSet.Count + rightSet.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    /// <summary>
    /// 并集：保留 <paramref name="oldLines"/> 原顺序，再按顺序追加 <paramref name="newLines"/>
    /// 中「归一化后尚未出现」的行（返回新增行，供 ManualText 增量追加）。
    /// </summary>
    internal static (List<string> Merged, List<string> Added) Union(
        IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<string>(oldLines.Count + newLines.Count);
        foreach (var line in oldLines)
        {
            if (seen.Add(Normalize(line)))
            {
                merged.Add(line);
            }
        }

        var added = new List<string>();
        foreach (var line in newLines)
        {
            var key = Normalize(line);
            if (key.Length == 0 || !seen.Add(key))
            {
                continue;
            }

            merged.Add(line);
            added.Add(line);
        }

        return (merged, added);
    }

    /// <summary>剥离行首序号/项目符号（展示用原文）。</summary>
    private static string StripLeadingMarker(string line)
    {
        var index = 0;
        while (index < line.Length && (char.IsAsciiDigit(line[index]) || line[index] is '(' or '['))
        {
            index++;
        }

        if (index > 0 && index < line.Length && line[index] is '.' or '、' or ')' or ']' or '：' or ':')
        {
            return line[(index + 1)..].TrimStart();
        }

        if (line.Length > 0 && line[0] is '-' or '*' or '+' or '•' or '·')
        {
            return line[1..].TrimStart();
        }

        return line;
    }
}
