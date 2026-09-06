using ClassIng.Shared.Models;

namespace ClassIng.Plugin.Services.SubjectChain;

/// <summary>
/// 学科识别链依赖提供者（解耦设置服务；模块 5 接入 ISettingsService 后替换 GetSettings）。
/// </summary>
public sealed class SubjectChainOptionsProvider
{
    /// <summary>提供当前分类设置（含置信度阈值 / AI 开关 / 云端限额等）。</summary>
    public required Func<ClassificationSettings> GetSettings { get; init; }

    /// <summary>
    /// 提供 CyberTechRep AI 设置（四用途识别模式等）；未接线时为 null，
    /// <see cref="SafeGetAiSettings"/> 回退默认值（SubjectClassifyMode=Backup = 现状链路）。
    /// </summary>
    public Func<AiSettings>? GetAiSettings { get; init; }

    /// <summary>插件数据目录（subjects.json、pending-confirm.json 存放处）。</summary>
    public required string DataDirectory { get; init; }

    /// <summary>
    /// 随插件分发的默认模板目录（AppContext.BaseDirectory/Assets）；
    /// 数据目录缺少 subjects.json 时从这里复制种子。
    /// </summary>
    public string AssetsDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "Assets");

    /// <summary>学科关键词表文件名（相对 DataDirectory）。</summary>
    public string SubjectRulesFileName { get; init; } = "subjects.json";

    /// <summary>人工确认队列持久化文件名（相对 DataDirectory）。</summary>
    public string PendingConfirmFileName { get; init; } = "pending-confirm.json";

    /// <summary>敏感字段解密（云端 ApiKey 为 DPAPI 密文；未接入设置服务前恒等返回）。</summary>
    public Func<string, string> SecretUnprotector { get; init; } = static v => v;

    /// <summary>线程安全读取设置；异常时回退默认值（降级原则：不因配置读取失败中断链）。</summary>
    public ClassificationSettings SafeGetSettings()
    {
        try
        {
            return GetSettings() ?? new ClassificationSettings();
        }
        catch
        {
            return new ClassificationSettings();
        }
    }

    /// <summary>线程安全读取 AI 用途模式设置；异常/未接线时回退默认值（现状行为不变）。</summary>
    public AiSettings SafeGetAiSettings()
    {
        try
        {
            return GetAiSettings?.Invoke() ?? new AiSettings();
        }
        catch
        {
            return new AiSettings();
        }
    }
}
