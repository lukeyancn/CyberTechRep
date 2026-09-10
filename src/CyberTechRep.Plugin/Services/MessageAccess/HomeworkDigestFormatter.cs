using System.Text;
using CyberTechRep.Plugin.Services.Stores;
using CyberTechRep.Shared.Models;

namespace CyberTechRep.Plugin.Services.MessageAccess;

/// <summary>
/// 作业清单格式化纯函数（可单测）：按学科分组 → 「【学科名】」行 + 组内条目行（1. 2. …），
/// 组间空一行；末尾固定换行追加尾注「该消息由CyberTechRep发送，仅供参考，可能缺失」。
/// <para>空学科（空白）与空内容的条目不输出；某学科无条目则不产生空组（学科名行也不发）。</para>
/// </summary>
public static class HomeworkDigestFormatter
{
    /// <summary>清单尾注（每条清单末尾固定追加）。</summary>
    public const string TailNote = "该消息由CyberTechRep发送，仅供参考，可能缺失";

    public static string Format(IEnumerable<HomeworkItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        // 分组：忽略学科名大小写合并，保留首次出现的写法作为组名；组内按 CreatedAt 正序
        var groupOrder = new List<string>();
        var groupLines = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.OrderBy(i => i.CreatedAt))
        {
            var subject = item.Subject?.Trim() ?? "";
            var content = item.Content?.Trim() ?? "";
            if (subject.Length == 0 || content.Length == 0)
            {
                continue; // 空学科 / 空内容不发
            }

            if (!groupLines.TryGetValue(subject, out var lines))
            {
                lines = new List<string>();
                groupLines[subject] = lines;
                groupOrder.Add(subject);
            }

            lines.Add(content);
        }

        var sb = new StringBuilder();
        for (var g = 0; g < groupOrder.Count; g++)
        {
            if (g > 0)
            {
                sb.AppendLine();
            }

            sb.AppendLine($"【{groupOrder[g]}】");
            var lines = groupLines[groupOrder[g]];
            for (var i = 0; i < lines.Count; i++)
            {
                sb.AppendLine($"{i + 1}. {lines[i]}");
            }
        }

        // 末尾固定换行追加尾注（条目行 AppendLine 已带换行，尾注紧随其后不再加空行）
        sb.Append(TailNote);
        return sb.ToString();
    }

    /// <summary>常态化作业在清单中的后缀标记（与 <c>HomeworkDocumentEntry.IsStanding</c> 落档内容一致）。</summary>
    public const string StandingSuffix = "（常态化）";

    /// <summary>
    /// 按<b>学科文档</b>格式化清单（需求 1 文档化 + 需求 3 常态化作业）：
    /// 每个学科一组，组内为该学科文档渲染文本的逐行编号；
    /// <paramref name="standingItems"/> 中勾选的常态化作业行追加到<b>对应学科组末尾</b>
    /// （带「（常态化）」标记）；只有常态化作业、当天无作业文档的学科同样成组，
    /// 与「勾选即写入该学科文档末尾」的落档语义一致。组间空一行，末尾固定尾注。
    /// <para>
    /// 去重：某条常态化作业若已在对应学科文档中出现（含已落档的「…（常态化）」行），
    /// 不再重复追加——保证「预览 = 实际发送」，也避免同一天重复勾选导致清单里出现两遍。
    /// </para>
    /// </summary>
    /// <param name="documents">学科文档（调用方按展示顺序传入；空文档跳过）。</param>
    /// <param name="standingItems">已勾选的常态化作业（null/空 = 无）。</param>
    /// <param name="numberLines">
    /// 是否自动给条目补填序号（<c>1. 2. …</c>；接线连接设置 <c>NumberDigestLines</c>，默认开）。
    /// 关闭时原样输出条目文本，适合老师要自己控制排版的场景。
    /// </param>
    public static string FormatDocuments(
        IEnumerable<HomeworkDocument> documents,
        IReadOnlyList<StandingHomeworkItem>? standingItems = null,
        bool numberLines = true)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var groupOrder = new List<string>();
        var groupLines = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        List<string> GetOrCreateGroup(string subject)
        {
            if (!groupLines.TryGetValue(subject, out var lines))
            {
                lines = [];
                groupLines[subject] = lines;
                groupOrder.Add(subject);
            }

            return lines;
        }

        foreach (var document in documents)
        {
            var subject = document.Subject?.Trim() ?? "";
            if (subject.Length == 0)
            {
                continue;
            }

            var lines = SplitRenderLines(document.Render);
            if (lines.Count == 0)
            {
                continue; // 空文档不产生空组（学科名行也不发）
            }

            GetOrCreateGroup(subject).AddRange(lines);
        }

        foreach (var item in standingItems ?? [])
        {
            var subject = item.Subject?.Trim() ?? "";
            var content = item.Content?.Trim() ?? "";
            if (subject.Length == 0 || content.Length == 0)
            {
                continue;
            }

            var lines = GetOrCreateGroup(subject);
            if (lines.Any(line => IsSameHomeworkLine(line, content)))
            {
                continue; // 已落档/已在文档中：不重复追加
            }

            lines.Add($"{content}{StandingSuffix}");
        }

        var sb = new StringBuilder();
        for (var g = 0; g < groupOrder.Count; g++)
        {
            if (g > 0)
            {
                sb.AppendLine();
            }

            sb.AppendLine($"【{groupOrder[g]}】");
            var lines = groupLines[groupOrder[g]];
            for (var i = 0; i < lines.Count; i++)
            {
                // 自动补填序号可关闭（连接设置 NumberDigestLines）：关掉则原样输出条目文本
                sb.AppendLine(numberLines ? $"{i + 1}. {lines[i]}" : lines[i]);
            }
        }

        sb.Append(TailNote);
        return sb.ToString();
    }

    /// <summary>行内容是否等同（归一化比对；忽略常态化后缀与行首序号）。</summary>
    private static bool IsSameHomeworkLine(string line, string content)
    {
        var stripped = line.Trim();
        if (stripped.EndsWith(StandingSuffix, StringComparison.Ordinal))
        {
            stripped = stripped[..^StandingSuffix.Length].TrimEnd();
        }

        return string.Equals(
            HomeworkDocumentMerger.Normalize(stripped),
            HomeworkDocumentMerger.Normalize(content),
            StringComparison.Ordinal);
    }

    /// <summary>文档渲染文本拆行（去空行；供清单编号输出）。</summary>
    private static List<string> SplitRenderLines(string? render)
    {
        if (string.IsNullOrWhiteSpace(render))
        {
            return [];
        }

        return render
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }
}
