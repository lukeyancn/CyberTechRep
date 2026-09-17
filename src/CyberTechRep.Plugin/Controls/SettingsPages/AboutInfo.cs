using System.Reflection;
using Microsoft.Extensions.Logging;

namespace CyberTechRep.Plugin.Controls.SettingsPages;

/// <summary>
/// 「关于 CyberTechRep」页展示信息（需求 5，纯逻辑可脱离 UI 单测）：
/// <para>
/// 版本号动态读取，绝不写死：优先 <c>typeof(CyberTechRepPlugin).Assembly</c> 的
/// <see cref="AssemblyInformationalVersionAttribute"/>（构建时来自 csproj 的 &lt;Version&gt;，
/// 形如 <c>2.1.0.0-beta.1</c>），为空则回落 <see cref="AssemblyFileVersionAttribute"/> /
/// <see cref="AssemblyName.Version"/>，最后回落随包分发的 manifest.yml（<c>AppContext.BaseDirectory</c> 下）。
/// </para>
/// 作者/仓库地址/插件 ID 为插件固定信息；运行环境取 .NET 版本、操作系统版本与设置文件路径。
/// </summary>
public sealed record AboutInfo
{
    /// <summary>插件作者（需求 5 约定；与 manifest.yml 的 author 字段相互独立）。</summary>
    public const string AuthorName = "Chenxuan Yan";

    /// <summary>GitHub 仓库地址。</summary>
    public const string Repository = "https://github.com/lukeyancn/CyberTechRep";

    /// <summary>插件 ID（宿主 manifest.yml 的 id）。</summary>
    public const string PluginId = "classisland.classing";

    /// <summary>设置文件名（位于数据目录下）。</summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>manifest.yml 文件名（随包分发）。</summary>
    public const string ManifestFileName = "manifest.yml";

    /// <summary>版本号完全无法解析时的兜底展示值。</summary>
    public const string UnknownVersion = "未知";

    /// <summary>展示用版本号（程序集信息优先，manifest.yml 兜底）。</summary>
    public string Version { get; init; } = UnknownVersion;

    /// <summary>作者。</summary>
    public string Author { get; init; } = AuthorName;

    /// <summary>GitHub 仓库地址。</summary>
    public string RepositoryUrl { get; init; } = Repository;

    /// <summary>插件 ID。</summary>
    public string PluginIdText { get; init; } = PluginId;

    /// <summary>插件数据目录（settings.json 等文件根）。</summary>
    public string DataDirectory { get; init; } = "";

    /// <summary>.NET 运行时版本（<see cref="Environment.Version"/>）。</summary>
    public string NetVersion { get; init; } = "";

    /// <summary>操作系统版本字符串（<see cref="Environment.OSVersion"/>）。</summary>
    public string OsVersion { get; init; } = "";

    /// <summary>设置文件完整路径（数据目录下 settings.json）。</summary>
    public string SettingsFilePath { get; init; } = "";

    /// <summary>随包 manifest.yml 路径。</summary>
    public string ManifestPath { get; init; } = "";

    /// <summary>manifest.yml 是否存在且可读（缺失时各字段走兜底，不抛异常）。</summary>
    public bool ManifestExists { get; init; }

    /// <summary>manifest.yml 中的 version（缺失为 null；仅作补充/兜底，不覆盖程序集版本）。</summary>
    public string? ManifestVersion { get; init; }

    /// <summary>manifest.yml 中的 author（缺失为 null）。</summary>
    public string? ManifestAuthor { get; init; }

    /// <summary>manifest.yml 中的 url（缺失为 null）。</summary>
    public string? ManifestUrl { get; init; }

    /// <summary>
    /// 收集关于信息。<paramref name="assembly"/> 默认取本类型所在程序集（即插件程序集
    /// CyberTechRep.Plugin.dll）；设置页显式传入 <c>typeof(CyberTechRepPlugin).Assembly</c>（同一程序集）。
    /// <paramref name="baseDirectory"/> 默认 <see cref="AppContext.BaseDirectory"/>（manifest.yml 所在目录），
    /// <paramref name="dataDirectory"/> 默认 <see cref="PluginRuntime.DataDirectory"/>；
    /// manifest.yml 缺失/损坏只记日志并走兜底，绝不抛异常。
    /// </summary>
    public static AboutInfo Create(
        Assembly? assembly = null, string? dataDirectory = null, string? baseDirectory = null, ILogger? logger = null)
    {
        var source = assembly ?? typeof(AboutInfo).Assembly;
        var baseDir = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
        var dataDir = string.IsNullOrWhiteSpace(dataDirectory) ? PluginRuntime.DataDirectory : dataDirectory!;
        var manifestPath = Path.Combine(baseDir, ManifestFileName);
        var manifest = ReadManifest(manifestPath, logger);

        return new AboutInfo
        {
            Version = ResolveVersion(source, manifest.Version),
            Author = AuthorName,
            RepositoryUrl = Repository,
            PluginIdText = PluginId,
            DataDirectory = dataDir,
            NetVersion = Environment.Version.ToString(),
            OsVersion = Environment.OSVersion.VersionString,
            SettingsFilePath = Path.Combine(dataDir, SettingsFileName),
            ManifestPath = manifestPath,
            ManifestExists = manifest.Exists,
            ManifestVersion = manifest.Version,
            ManifestAuthor = manifest.Author,
            ManifestUrl = manifest.Url
        };
    }

    /// <summary>
    /// 版本号解析：程序集信息版本 → 文件版本 → 程序集版本 → manifest.yml → 未知。
    /// </summary>
    internal static string ResolveVersion(Assembly assembly, string? manifestVersion) =>
        ResolveVersion(
        [
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version,
            assembly.GetName().Version?.ToString(),
            manifestVersion
        ]);

    /// <summary>按优先级取第一个可规整的版本串（空串/空白跳过）；全部为空返回「未知」。</summary>
    internal static string ResolveVersion(IEnumerable<string?> candidates)
    {
        foreach (var candidate in candidates)
        {
            var normalized = NormalizeVersion(candidate);
            if (normalized is not null)
            {
                return normalized;
            }
        }

        return UnknownVersion;
    }

    /// <summary>规整版本串：去空白、去 SourceLink 提交号后缀（<c>1.2.3+abc</c> → <c>1.2.3</c>）；空返回 null。</summary>
    internal static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var plusIndex = trimmed.IndexOf('+');
        if (plusIndex >= 0)
        {
            trimmed = trimmed[..plusIndex].Trim();
        }

        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// 简单按行解析 manifest.yml 的 version/author/url（不引入 YAML 依赖）；
    /// 文件缺失/读取失败返回全空字段（<see cref="ManifestFields.Exists"/> 为 false），不抛异常。
    /// </summary>
    internal static ManifestFields ReadManifest(string manifestPath, ILogger? logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                return ManifestFields.Missing;
            }

            string? version = null, author = null, url = null;
            foreach (var rawLine in File.ReadLines(manifestPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                var key = line[..separator].Trim();
                var value = TrimQuotes(line[(separator + 1)..].Trim());
                if (value.Length == 0)
                {
                    continue;
                }

                if (version is null && key.Equals("version", StringComparison.OrdinalIgnoreCase))
                {
                    version = value;
                }
                else if (author is null && key.Equals("author", StringComparison.OrdinalIgnoreCase))
                {
                    author = value;
                }
                else if (url is null && key.Equals("url", StringComparison.OrdinalIgnoreCase))
                {
                    url = value;
                }
            }

            return new ManifestFields(true, version, author, url);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "读取 manifest.yml 失败（关于页回落程序集信息与固定值）：{Path}", manifestPath);
            return ManifestFields.Missing;
        }
    }

    private static string TrimQuotes(string value) =>
        value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1].Trim()
            : value;
}

/// <summary>manifest.yml 解析结果（缺失时为 <see cref="Missing"/>，各字段 null）。</summary>
internal readonly record struct ManifestFields(bool Exists, string? Version, string? Author, string? Url)
{
    public static ManifestFields Missing => new(false, null, null, null);
}
