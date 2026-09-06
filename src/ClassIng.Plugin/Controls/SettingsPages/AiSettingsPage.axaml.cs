using System.Runtime.Versioning;
using Avalonia.Interactivity;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using ClassIsland.Core.Attributes;

namespace ClassIng.Plugin.Controls.SettingsPages;

/// <summary>
/// CyberTechRep AI 设置页（需求 6）：四种 AI 用途的识别模式选择
/// （关闭/唯一识别/后补识别）+ 云端采样参数。
/// <para>
/// 云端凭据（端点/API Key/模型名/每日限额）与置信度阈值在「ClassIng 分类」页配置，
/// 本页只做引用说明不重复字段。保存走既有 SaveNow 动效链路；
/// AiSettings 经 OptionsProvider 的 GetAiSettings 委托热读取，保存广播 SettingsChanged 后即生效。
/// </para>
/// </summary>
[SettingsPageInfo("classing.settings.ai", "CyberTechRep AI")]
[Group("classing.settings")]
public partial class AiSettingsPage : ClassIngSettingsPageBase
{
    public AiSettingsPage(ISettingsService settingsService)
        : base(settingsService, PluginRuntime.DataDirectory)
    {
        InitializeComponent();
    }

    /// <summary>用途① 通知作业学科分类：下拉索引 ↔ AiUsageMode（0=关闭，1=唯一识别，2=后补识别）。</summary>
    public int SubjectClassifyModeIndex
    {
        get => (int)Settings.Ai.SubjectClassifyMode;
        set => Settings.Ai.SubjectClassifyMode = (AiUsageMode)value;
    }

    /// <summary>用途② 通知/作业二分类：下拉索引 ↔ AiUsageMode。</summary>
    public int MessageClassifyModeIndex
    {
        get => (int)Settings.Ai.MessageClassifyMode;
        set => Settings.Ai.MessageClassifyMode = (AiUsageMode)value;
    }

    /// <summary>用途③ 无关键词消息兜底识别：下拉索引 ↔ AiUsageMode。</summary>
    public int NoKeywordFallbackModeIndex
    {
        get => (int)Settings.Ai.NoKeywordFallbackMode;
        set => Settings.Ai.NoKeywordFallbackMode = (AiUsageMode)value;
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e) => SaveNow(sender);
}
