using System.Text;
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
}
