using ClassIng.Shared.Models;

namespace ClassIng.Plugin.Services.Classification;

/// <summary>
/// 分类器依赖提供者（解耦设置服务，便于单元测试与模块 5 接入前使用默认值）。
/// </summary>
public sealed class ClassifierOptionsProvider
{
    /// <summary>提供当前分类设置（设置页修改后经 ReloadRules 热生效）。</summary>
    public required Func<ClassificationSettings> GetSettings { get; init; }

    /// <summary>关键词 JSON 与数据文件所在目录。</summary>
    public required string DataDirectory { get; init; }

    /// <summary>关键词表文件名（相对 DataDirectory；默认 classification-keywords.json）。</summary>
    public string KeywordsFileName { get; init; } = "classification-keywords.json";
}
