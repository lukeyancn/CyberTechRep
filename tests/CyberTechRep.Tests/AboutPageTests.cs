using System.Reflection;
using System.Text.RegularExpressions;
using CyberTechRep.Plugin.Controls.SettingsPages;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 需求 5：「关于 CyberTechRep」页展示信息（<see cref="AboutInfo"/>）的取值与兜底。
/// 版本号动态读取程序集信息（绝不写死），manifest.yml 仅作补充与兜底；
/// manifest.yml 缺失/损坏时不抛异常，作者与仓库地址回落固定值。
/// </summary>
public sealed class AboutPageTests : IDisposable
{
    /// <summary>版本号形态：x.y.z(.w)(-suffix)，与 csproj &lt;Version&gt; / manifest.yml 约定一致。</summary>
    private static readonly Regex VersionPattern =
        new(@"^\d+\.\d+\.\d+(\.\d+)?(-[0-9A-Za-z.\-]+)?$", RegexOptions.Compiled);

    private readonly string _dir;

    public AboutPageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "about-page", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    [Fact]
    public void Create_ManifestMissing_FallsBackToAssemblyInfoAndFixedValues()
    {
        // manifest.yml 缺失（不存在的基目录）：不抛异常，各字段走兜底
        var missingBaseDir = Path.Combine(_dir, "no-manifest");
        var info = AboutInfo.Create(dataDirectory: _dir, baseDirectory: missingBaseDir);

        Assert.False(info.ManifestExists);
        Assert.Null(info.ManifestVersion);
        Assert.Null(info.ManifestAuthor);
        Assert.Null(info.ManifestUrl);

        // 版本号来自程序集信息：非空且形如 x.y.z(.w)(-suffix)
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.Matches(VersionPattern, info.Version);

        // 固定信息兜底
        Assert.Equal("Chenxuan Yan", info.Author);
        Assert.Equal("https://github.com/lukeyancn/CyberTechRep", info.RepositoryUrl);
        Assert.Equal("classisland.classing", info.PluginIdText);

        // 环境信息：数据目录/设置文件路径按传入目录推导，.NET 与操作系统版本非空
        Assert.Equal(_dir, info.DataDirectory);
        Assert.Equal(Path.Combine(_dir, "settings.json"), info.SettingsFilePath);
        Assert.False(string.IsNullOrWhiteSpace(info.NetVersion));
        Assert.False(string.IsNullOrWhiteSpace(info.OsVersion));
    }

    [Fact]
    public void Create_ManifestPresent_ParsesVersionAuthorUrl()
    {
        // 真实 manifest.yml 形态：其余字段（description 含冒号）不得干扰按行解析
        File.WriteAllText(Path.Combine(_dir, "manifest.yml"),
            """
            id: classisland.classing
            name: CyberTechRep 课堂信息分发
            description: QQ 群消息接管：自动分类通知/作业
            entranceAssembly: "CyberTechRep.Plugin.dll"
            url: https://github.com/lukeyancn/CyberTechRep
            apiVersion: 2.0.0.0
            version: "9.9.9.9-beta.9"
            author: CyberTechRep
            """);

        var info = AboutInfo.Create(dataDirectory: _dir, baseDirectory: _dir);

        Assert.True(info.ManifestExists);
        Assert.Equal("9.9.9.9-beta.9", info.ManifestVersion);
        Assert.Equal("CyberTechRep", info.ManifestAuthor);
        Assert.Equal("https://github.com/lukeyancn/CyberTechRep", info.ManifestUrl);

        // 程序集信息存在时优先于 manifest.yml（版本号仍动态、非硬编码）：
        // manifest 用不可能与真实程序集版本相同的假值，避免与版本升级耦合
        Assert.NotEqual(info.ManifestVersion, info.Version);
        Assert.Matches(VersionPattern, info.Version);
    }

    [Fact]
    public void Create_GarbageManifest_DoesNotThrow_FieldsFallBack()
    {
        File.WriteAllText(Path.Combine(_dir, "manifest.yml"),
            """
            # 只有注释与无冒号/空值的行
            id classisland.classing
            version:
            author:   '   '
            """);

        var info = AboutInfo.Create(dataDirectory: _dir, baseDirectory: _dir);

        Assert.True(info.ManifestExists);
        Assert.Null(info.ManifestVersion);
        Assert.Null(info.ManifestAuthor);
        Assert.Null(info.ManifestUrl);
        Assert.Equal("Chenxuan Yan", info.Author);
        Assert.Matches(VersionPattern, info.Version);
    }

    [Fact]
    public void Version_IsReadFromAssemblyInformationalVersion_NotHardcoded()
    {
        // 版本来源 = 插件程序集（CyberTechRep.Plugin.dll）的 AssemblyInformationalVersion，
        // 由 csproj <Version> 生成；主控升级到 2.1.0.0-beta.1 时本断言自动跟随
        var assembly = typeof(AboutInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.False(string.IsNullOrWhiteSpace(informational));
        Assert.Matches(VersionPattern, AboutInfo.NormalizeVersion(informational)!);

        var info = AboutInfo.Create(dataDirectory: _dir, baseDirectory: _dir);

        Assert.Equal(AboutInfo.NormalizeVersion(informational), info.Version);
    }

    [Fact]
    public void NormalizeVersion_StripsSourceLinkSuffix_AndBlankValues()
    {
        Assert.Equal("2.1.0.0-beta.1", AboutInfo.NormalizeVersion("2.1.0.0-beta.1+9f2a1c3"));
        Assert.Equal("1.0.0", AboutInfo.NormalizeVersion(" 1.0.0 "));
        Assert.Null(AboutInfo.NormalizeVersion(""));
        Assert.Null(AboutInfo.NormalizeVersion("   "));
        Assert.Null(AboutInfo.NormalizeVersion(null));
    }

    [Fact]
    public void ResolveVersion_FallsBackInPriorityOrder()
    {
        // 优先级：第一个非空版本串胜出；空串/空白跳过
        Assert.Equal("2.1.0.0-beta.1", AboutInfo.ResolveVersion(["", null, " 2.1.0.0-beta.1 ", "9.9.9"]));
        Assert.Equal("9.9.9", AboutInfo.ResolveVersion([null, "  ", "9.9.9"]));
        Assert.Equal("1.0.0", AboutInfo.ResolveVersion([null, "1.0.0+abc123"]));
        Assert.Equal(AboutInfo.UnknownVersion, AboutInfo.ResolveVersion([null, "", "   "]));
    }
}
