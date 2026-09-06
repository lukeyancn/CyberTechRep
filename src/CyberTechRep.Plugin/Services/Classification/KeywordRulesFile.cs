using System.Text.Json;
using System.Text.Json.Serialization;
using CyberTechRep.Shared.Models;

namespace CyberTechRep.Plugin.Services.Classification;

/// <summary>外置关键词表 JSON 模型（classification-keywords.json）。</summary>
public sealed class KeywordRulesDto
{
    [JsonPropertyName("noticeKeywords")]
    public List<string> NoticeKeywords { get; set; } = [];

    [JsonPropertyName("homeworkKeywords")]
    public List<string> HomeworkKeywords { get; set; } = [];
}

/// <summary>运行时不可变关键词快照。</summary>
internal sealed record KeywordRulesSnapshot(
    IReadOnlyList<string> NoticeKeywords,
    IReadOnlyList<string> HomeworkKeywords,
    string Source);

/// <summary>关键词表读写（JSON 原子写入）。</summary>
internal static class KeywordRulesFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static KeywordRulesSnapshot FromSettings(ClassificationSettings settings) =>
        new(
            Normalize(settings.NoticeKeywords),
            Normalize(settings.HomeworkKeywords),
            "ClassificationSettings");

    public static KeywordRulesSnapshot LoadOrSeed(string filePath, ClassificationSettings settings)
    {
        if (File.Exists(filePath))
        {
            var json = File.ReadAllText(filePath);
            var dto = JsonSerializer.Deserialize<KeywordRulesDto>(json, JsonOptions)
                      ?? throw new InvalidDataException("关键词 JSON 反序列化为 null");
            return new KeywordRulesSnapshot(
                Normalize(dto.NoticeKeywords),
                Normalize(dto.HomeworkKeywords),
                filePath);
        }

        var seeded = FromSettings(settings);
        Seed(filePath, seeded);
        return seeded with { Source = filePath + " (seeded)" };
    }

    public static void Seed(string filePath, KeywordRulesSnapshot rules)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var dto = new KeywordRulesDto
        {
            NoticeKeywords = rules.NoticeKeywords.ToList(),
            HomeworkKeywords = rules.HomeworkKeywords.ToList()
        };
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, filePath, overwrite: true);
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string>? keywords) =>
        (keywords ?? [])
        .Where(k => !string.IsNullOrWhiteSpace(k))
        .Select(k => k.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
