using System.Text.Json;
using ClassIng.Plugin.Services.SubjectChain;
using Microsoft.Extensions.Logging;

namespace ClassIng.Plugin.Services.Stores;

/// <summary>
/// JSON 存储文件持久化助手（模块 5/6 共用）：
/// ① 原子写入（tmp + Move，复用 <see cref="SubjectRuleFile.WriteAtomic"/>）；
/// ② 每次成功保存后同步维护 .bak；
/// ③ 加载时主文件损坏自动从 .bak 恢复（自愈回写主文件），
///    无有效备份时把原文件转存 .corrupt 后以空数据继续（不阻断插件运行）。
/// </summary>
internal static class JsonStoreFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Serialize<T>(T dto) => JsonSerializer.Serialize(dto, JsonOptions);

    /// <summary>原子写入主文件并同步更新 .bak（备份失败不阻断保存）。</summary>
    public static void SaveWithBackup(string filePath, string json)
    {
        SubjectRuleFile.WriteAtomic(filePath, json);
        try
        {
            File.Copy(filePath, filePath + ".bak", overwrite: true);
        }
        catch
        {
            // 备份失败不阻断主流程：下次保存会重试
        }
    }

    /// <summary>
    /// 加载顶层 DTO：文件缺失返回 null（空数据）；
    /// 主文件损坏 → 尝试 .bak（成功则自愈回写主文件）→ 仍失败则转存 .corrupt 并返回 null。
    /// </summary>
    public static T? LoadOrRestore<T>(string filePath, Microsoft.Extensions.Logging.ILogger logger)
        where T : class
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        if (TryDeserialize<T>(filePath, out var dto))
        {
            return dto;
        }

        // 主文件损坏：尝试 .bak
        var bakPath = filePath + ".bak";
        if (File.Exists(bakPath) && TryDeserialize<T>(bakPath, out var restored) && restored is not null)
        {
            logger.LogWarning("存储文件损坏，已从 .bak 恢复 File={File}", filePath);
            try
            {
                SubjectRuleFile.WriteAtomic(filePath, Serialize(restored));
                File.Copy(filePath, bakPath, overwrite: true);
            }
            catch
            {
                // 自愈回写失败不阻断：内存态已恢复，下次保存重建文件
            }

            return restored;
        }

        logger.LogError("存储文件损坏且无有效 .bak 备份，以空数据继续（原文件转存 .corrupt）File={File}", filePath);
        try
        {
            File.Copy(filePath, filePath + ".corrupt", overwrite: true);
            File.Delete(filePath);
        }
        catch
        {
            // 保留现场失败不阻断
        }

        return null;
    }

    private static bool TryDeserialize<T>(string path, out T? dto) where T : class
    {
        try
        {
            dto = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
            return dto is not null;
        }
        catch
        {
            dto = null;
            return false;
        }
    }
}
