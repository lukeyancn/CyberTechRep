using System.Text.Json;
using System.Text.Json.Serialization;
using ClassIng.Shared.Models;

namespace ClassIng.Plugin.Services.SubjectChain;

/// <summary>subjects.json 顶层 DTO（外置学科关键词表）。</summary>
public sealed class SubjectRulesDto
{
    [JsonPropertyName("rules")]
    public List<SubjectRule> Rules { get; set; } = [];
}

/// <summary>
/// 学科关键词表读写（JSON 原子写入）。加载优先级：
/// ① 数据目录 subjects.json（用户可编辑、热重载） → ② Assets 模板复制种子 → ③ 内置默认规则。
/// </summary>
internal static class SubjectRuleFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>内置兜底规则（Assets 模板也缺失时使用，保证链可运行）。</summary>
    public static IReadOnlyList<SubjectRule> BuiltInDefaults { get; } =
    [
        new SubjectRule { Subject = "数学", Keywords = ["数学", "代数", "几何", "方程"], Priority = 10 },
        new SubjectRule { Subject = "语文", Keywords = ["语文", "作文", "古诗词"], Priority = 10 },
        new SubjectRule { Subject = "英语", Keywords = ["英语", "单词", "听力", "语法"], Priority = 10 },
        new SubjectRule { Subject = "物理", Keywords = ["物理", "力学", "电学"], Priority = 10 },
        new SubjectRule { Subject = "化学", Keywords = ["化学", "元素", "实验"], Priority = 10 }
    ];

    /// <summary>加载或种子；任何失败回退内置默认（不抛出，保证链不中断）。</summary>
    public static (IReadOnlyList<SubjectRule> Rules, string Source) LoadOrSeed(
        SubjectChainOptionsProvider provider)
    {
        var dataPath = Path.Combine(provider.DataDirectory, provider.SubjectRulesFileName);
        try
        {
            if (File.Exists(dataPath))
            {
                return (Normalize(LoadFile(dataPath)), dataPath);
            }

            // 种子：优先复制 Assets 模板（保留模板排版），否则写内置默认
            var assetPath = Path.Combine(provider.AssetsDirectory, provider.SubjectRulesFileName);
            Directory.CreateDirectory(provider.DataDirectory);
            if (File.Exists(assetPath))
            {
                File.Copy(assetPath, dataPath, overwrite: false);
                return (Normalize(LoadFile(dataPath)), dataPath + " (seeded from Assets)");
            }

            var seeded = new SubjectRulesDto { Rules = [.. BuiltInDefaults] };
            WriteAtomic(dataPath, JsonSerializer.Serialize(seeded, JsonOptions));
            return (Normalize(seeded.Rules), dataPath + " (seeded built-in)");
        }
        catch (Exception)
        {
            return (Normalize(BuiltInDefaults), "built-in (load failed)");
        }
    }

    private static List<SubjectRule> LoadFile(string path) =>
        JsonSerializer.Deserialize<SubjectRulesDto>(File.ReadAllText(path), JsonOptions)?.Rules
        ?? throw new InvalidDataException("subjects.json 反序列化为 null");

    internal static void WriteAtomic(string filePath, string json)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, filePath, overwrite: true);
    }

    private static IReadOnlyList<SubjectRule> Normalize(IEnumerable<SubjectRule>? rules) =>
        (rules ?? [])
        .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Subject))
        .Select(r => new SubjectRule
        {
            Subject = r.Subject.Trim(),
            Keywords = (r.Keywords ?? [])
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Priority = r.Priority
        })
        .ToArray();
}
