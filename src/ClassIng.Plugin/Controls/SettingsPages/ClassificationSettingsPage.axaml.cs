using System.Runtime.Versioning;
using Avalonia.Interactivity;
using ClassIng.Shared.Abstractions;
using ClassIsland.Core.Attributes;

namespace ClassIng.Plugin.Controls.SettingsPages;

/// <summary>
/// 分类设置页：通知/作业关键词表增删改、学科词表（路径 + 打开编辑）、
/// AI 开关/本地路径/云端端点与 Key（脱敏）/每日限额/置信度阈值/人工队列开关。
/// </summary>
[SettingsPageInfo("classing.settings.classification", "CyberTechRep 分类")]
[Group("classing.settings")]
public partial class ClassificationSettingsPage : ClassIngSettingsPageBase
{
    public ClassificationSettingsPage(ISettingsService settingsService)
        : base(settingsService, PluginRuntime.DataDirectory)
    {
        InitializeComponent();
    }

    /// <summary>通知关键词 ↔ 多行文本。</summary>
    public string NoticeKeywordsText
    {
        get => LinesToText([.. Settings.Classification.NoticeKeywords]);
        set => Settings.Classification.NoticeKeywords = TextToLines(value);
    }

    /// <summary>作业关键词 ↔ 多行文本。</summary>
    public string HomeworkKeywordsText
    {
        get => LinesToText([.. Settings.Classification.HomeworkKeywords]);
        set => Settings.Classification.HomeworkKeywords = TextToLines(value);
    }

    /// <summary>云端 API Key 明文（绑定视图；写入时 DPAPI 加密持久化）。</summary>
    public string CloudApiKeyPlain
    {
        get => SettingsService.Unprotect(Settings.Classification.CloudApiKeyProtected);
        set => Settings.Classification.CloudApiKeyProtected = SettingsService.Protect(value ?? "");
    }

    /// <summary>置信度阈值显示。</summary>
    public string ConfidenceThresholdText => $"当前阈值：{Settings.Classification.ConfidenceThreshold:0.00}";

    private void OnSaveClicked(object? sender, RoutedEventArgs e) => SaveNow(sender);

    private void OnOpenSubjectRulesClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = ResolveSubjectRulesPath();
            if (!File.Exists(path))
            {
                // 文件缺失时先落一份空模板（关键词数组），避免打开失败
                File.WriteAllText(path, "[]");
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"打开学科词表失败：{ex.Message}");
        }
    }

    /// <summary>解析学科词表绝对路径（相对路径 → 插件数据目录）。</summary>
    private string ResolveSubjectRulesPath()
    {
        var path = Settings.Classification.SubjectRulesPath;
        return Path.IsPathRooted(path) ? path : Path.Combine(DataDirectory, path);
    }
}
