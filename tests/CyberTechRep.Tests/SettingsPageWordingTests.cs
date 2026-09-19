using System.Text.RegularExpressions;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 设置页文案「去工程化」扫描测试：读取 <c>src\CyberTechRep.Plugin\Controls\SettingsPages</c> 下所有
/// <c>.axaml</c>，只检查**用户可见属性**（Text / Content / Header / Watermark / ToolTip.Tip /
/// Description / Title）的字面量取值，断言其中不出现内部术语与开发口吻（模块 N、需求 N、占位、幂等、
/// 热生效、宿主、协议端…）。
/// <para>
/// 扫描范围与口径：绑定路径（<c>{Binding ...}</c>）、标记扩展、XML 注释、<c>x:Name</c>、资源键与
/// 代码标识符都不算用户可见文案，一律不参与断言——避免把 <c>CloudProviderIndex</c> 这类绑定路径误判成文案。
/// </para>
/// </summary>
public sealed class SettingsPageWordingTests
{
    /// <summary>会直接呈现给用户的 XAML 属性名（不含绑定路径等非文案属性）。</summary>
    private static readonly string[] VisibleAttributeNames =
        ["Text", "Content", "Header", "Watermark", "ToolTip.Tip", "Description", "Title"];

    /// <summary>
    /// 禁用词表：设置页的**用户可见文案**里不得出现的内部术语 / 工程化表述。
    /// 说明：这些词只允许出现在代码注释、日志文本、代码标识符与绑定路径里。
    /// </summary>
    private static readonly string[] ForbiddenWords =
    [
        // 任务点名的内部语言
        "模块",           // 模块编号（如「模块 9」）
        "需求",           // 需求编号（如「需求 6 去重」）
        "预留",           // 「（预留）」占位说明
        "占位",           // 「占位实现」「占位提示」
        "TODO",
        "幂等",           // 含「幂等键」
        "幂等键",
        "热读取",
        "热生效",         // 统一改为「保存后立即生效」
        "结构化日志",
        "Provider",       // 统一改为「服务商 / 接口」
        "UI 入口",
        "无 UI 入口",
        "取舍",           // 「边界/取舍」等设计讨论
        "红线",           // 验收口径用语
        // 同批需要清除的开发术语（任务「必须清除的内部语言」清单）
        "投递重试队列",
        "重试队列",
        "重试队列条目",
        "ONNX",
        "集成收口",
        "收口",
        "协议端",         // 统一改为「QQ 消息通道 / NapCat」
        "宿主",           // 统一改为「ClassIsland」
        "字段",           // 统一改为「参数 / 信息」
        "快照",
        "广播",
        "订阅",
        "回调",
        "脱敏",           // 统一改为「不含密钥」等直白说法
        "现状",           // 「现状行为」「纯关键词（现状）」
        "dump",           // 统一改为「消息记录」
        "DIP"             // 逻辑坐标术语，统一改为「坐标」
    ];

    /// <summary>
    /// 显式白名单：确实必须保留在用户可见文案里的技术词（词级豁免），每项都要写明理由。
    /// <para>
    /// 当前为空——用户操作必需的技术名词（AppID、AppSecret、AccessToken、OpenID、群号、NapCat、
    /// OneBot、QQ 官方机器人、WebUI、API 地址、下载目录、磁盘占用、保留天数等）都不在禁用词表内，
    /// 无需豁免。若将来确需保留某个禁用词，请在此处补一条并写明「为什么用户必须看到它」。
    /// </para>
    /// </summary>
    private static readonly (string Term, string Reason)[] AllowedTechnicalTerms = [];

    /// <summary>XML 注释不参与扫描（注释里保留模块/需求编号不影响用户看到的文案）。</summary>
    private static readonly Regex XmlCommentRegex =
        new("<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>用户可见属性的字面量取值（<c>属性名="值"</c>，值内不允许出现双引号）。</summary>
    private static readonly Regex VisibleAttributeRegex = new(
        $"(?<name>{string.Join("|", VisibleAttributeNames.Select(Regex.Escape))})\\s*=\\s*\"(?<value>[^\"]*)\"",
        RegexOptions.Compiled);

    [Fact]
    public void 设置页目录_可定位且能扫描到用户可见文案()
    {
        var pagesDir = ResolveSettingsPagesDirectory();
        var axamlFiles = Directory.EnumerateFiles(pagesDir, "*.axaml").ToList();

        // 9 个设置页（连接 / 分类 / AI / 悬浮窗 / 文件 / 维护 / 常态化作业 / 词表编辑 / 关于）
        Assert.True(axamlFiles.Count >= 9, $"设置页数量异常（{axamlFiles.Count} 个）：{pagesDir}");

        foreach (var file in axamlFiles)
        {
            var visibleTexts = ExtractVisibleTexts(File.ReadAllText(file));
            Assert.True(visibleTexts.Count > 0, $"未从设置页提取到任何用户可见文案（提取口径可能失效）：{file}");
        }
    }

    [Fact]
    public void 用户可见文案_不出现内部术语与开发口吻()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(ResolveSettingsPagesDirectory(), "*.axaml"))
        {
            offenders.AddRange(FindWordingOffenders(File.ReadAllText(file))
                .Select(offender => $"{Path.GetFileName(file)}｜{offender}"));
        }

        Assert.True(offenders.Count == 0,
            "设置页用户可见文案里仍然出现内部术语：" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void 文案扫描逻辑_对合成片段能命中禁用词()
    {
        // 反向自证：合成片段里的内部术语必须被检出，避免扫描口径失效后测试「假绿」
        const string xaml = """
            <StackPanel>
                <TextBlock Text="本功能为占位实现（模块 9）" ShowGridLines="True"/>
                <Button Content="导出投递重试队列" ToolTip.Tip="{Binding SomePath}"/>
            </StackPanel>
            """;

        var offenders = FindWordingOffenders(xaml);

        Assert.Contains(offenders, o => o.Contains("占位"));
        Assert.Contains(offenders, o => o.Contains("模块"));
        Assert.Contains(offenders, o => o.Contains("投递重试队列"));
    }

    [Fact]
    public void 文案提取_只取字面量_忽略注释与绑定路径()
    {
        const string xaml = """
            <!-- 注释里的 Text="模块 1" 不算用户可见文案 -->
            <StackPanel>
                <TextBlock Text="占位实现"/>
                <TextBlock Text="{Binding Settings.Ai.CloudProviderIndex}"/>
                <TextBlock x:Name="CloudProviderBox" TextWrapping="Wrap"/>
                <Button Content="保存并应用" ToolTip.Tip="忽略绑定 {Binding X}"/>
            </StackPanel>
            """;

        var extracted = ExtractVisibleTexts(xaml);

        Assert.Equal(3, extracted.Count);
        Assert.Contains(extracted, t => t.Attribute == "Text" && t.Value == "占位实现");
        Assert.Contains(extracted, t => t.Attribute == "Content" && t.Value == "保存并应用");
        Assert.Contains(extracted, t => t.Attribute == "ToolTip.Tip" && t.Value == "忽略绑定 {Binding X}");
        // 注释与绑定路径不得进入扫描结果
        Assert.DoesNotContain(extracted, t => t.Value.Contains("模块"));
        Assert.DoesNotContain(extracted, t => t.Value.StartsWith('{'));
        // 代码标识符（x:Name / 属性名）不算文案
        Assert.DoesNotContain(extracted, t => t.Value.Contains("CloudProviderBox"));
    }

    [Theory]
    [InlineData("模块")]
    [InlineData("需求")]
    [InlineData("预留")]
    [InlineData("占位")]
    [InlineData("TODO")]
    [InlineData("幂等")]
    [InlineData("热读取")]
    [InlineData("结构化日志")]
    [InlineData("Provider")]
    [InlineData("无 UI 入口")]
    [InlineData("取舍")]
    public void 禁用词表_覆盖任务要求的必查词(string requiredTerm)
    {
        Assert.Contains(requiredTerm, EffectiveForbiddenWords());
    }

    [Fact]
    public void 禁用词表_无空白与重复项()
    {
        Assert.All(ForbiddenWords, word => Assert.False(string.IsNullOrWhiteSpace(word)));
        Assert.Equal(ForbiddenWords.Length, ForbiddenWords.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(AllowedTechnicalTerms,
            entry => Assert.False(string.IsNullOrWhiteSpace(entry.Term) || string.IsNullOrWhiteSpace(entry.Reason)));
    }

    /// <summary>禁用词表扣除白名单后的生效词表（白名单需写明理由）。</summary>
    private static IReadOnlyList<string> EffectiveForbiddenWords() =>
        ForbiddenWords
            .Where(word => !AllowedTechnicalTerms.Any(allowed =>
                allowed.Term.Equals(word, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    /// <summary>扫描一段 XAML 的用户可见文案，返回命中禁用词的描述（供真实扫描与合成自证用例共用）。</summary>
    internal static IReadOnlyList<string> FindWordingOffenders(string xaml)
    {
        var offenders = new List<string>();
        foreach (var (attribute, value) in ExtractVisibleTexts(xaml))
        {
            foreach (var word in EffectiveForbiddenWords())
            {
                if (value.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{attribute}=\"{value}\"｜命中禁用词「{word}」");
                }
            }
        }

        return offenders;
    }

    /// <summary>
    /// 提取用户可见文案：先剔除 XML 注释，再取用户可见属性的字面量取值；
    /// 空值、绑定路径/标记扩展（以 <c>{</c> 开头）跳过。
    /// </summary>
    internal static IReadOnlyList<(string Attribute, string Value)> ExtractVisibleTexts(string xaml)
    {
        var withoutComments = XmlCommentRegex.Replace(xaml, "");
        var result = new List<(string, string)>();
        foreach (Match match in VisibleAttributeRegex.Matches(withoutComments))
        {
            var value = match.Groups["value"].Value.Trim();
            if (value.Length == 0 || value.StartsWith('{'))
            {
                continue;
            }

            result.Add((match.Groups["name"].Value, value));
        }

        return result;
    }

    /// <summary>
    /// 定位仓库内设置页目录：从测试程序集输出目录向上查找含
    /// <c>src\CyberTechRep.Plugin</c> 的仓库根，再拼出 <c>Controls\SettingsPages</c>。
    /// </summary>
    internal static string ResolveSettingsPagesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var pageDir = Path.Combine(dir.FullName, "src", "CyberTechRep.Plugin", "Controls", "SettingsPages");
            if (Directory.Exists(pageDir))
            {
                return pageDir;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"未能从 {AppContext.BaseDirectory} 向上找到 src\\CyberTechRep.Plugin\\Controls\\SettingsPages。");
    }
}
