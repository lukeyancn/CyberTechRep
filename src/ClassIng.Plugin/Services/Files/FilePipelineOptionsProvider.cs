using ClassIng.Shared.Models;

namespace ClassIng.Plugin.Services.Files;

/// <summary>
/// 文件管道选项提供器（模块 5 接入 ISettingsService 后由真实设置源替换，含配置热更新）。
/// </summary>
public sealed class FilePipelineOptionsProvider
{
    /// <summary>提供当前文件设置（DownloadRoot / 限制 / 清理策略等）。</summary>
    public required Func<FileSettings> GetSettings { get; init; }

    /// <summary>
    /// 插件数据目录（files.json 存放处；相对 DownloadRoot 以此为基准目录解析）。
    /// </summary>
    public required string DataDirectory { get; init; }
}
