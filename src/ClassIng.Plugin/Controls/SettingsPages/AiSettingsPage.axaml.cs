using System.Runtime.Versioning;
using Avalonia.Interactivity;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using ClassIsland.Core.Attributes;

namespace ClassIng.Plugin.Controls.SettingsPages;

/// <summary>
/// CyberTechRep AI 设置页（需求 5+6）：AI 相关设置的集中页。
/// <para>
/// 云端 API 接口配置（总开关/提供者类型/端点/API Key/模型名/每日限额/置信度阈值）
/// 自「CyberTechRep 分类」页迁入本页，为全插件唯一凭据入口（settings.json 旧位置字段由
/// SettingsService 一次性迁移，已配置密钥的用户无需重新填写）；
/// 本地 ONNX 为占位实现，不提供设置入口。四种 AI 用途的识别模式（关闭/唯一识别/后补识别）
/// 与云端采样参数沿用本页原有控件。保存走既有 SaveNow 动效链路；
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

    /// <summary>云端 API 提供者：下拉索引 ↔ CloudAiProvider（0=OpenAI 兼容，1=Anthropic）。</summary>
    public int CloudProviderIndex
    {
        get => (int)Settings.Ai.CloudProvider;
        set => Settings.Ai.CloudProvider = (CloudAiProvider)value;
    }

    /// <summary>云端 API Key 明文（绑定视图；写入时 DPAPI 加密持久化，绝不入日志）。</summary>
    public string CloudApiKeyPlain
    {
        get => SettingsService.Unprotect(Settings.Ai.CloudApiKeyProtected);
        set => Settings.Ai.CloudApiKeyProtected = SettingsService.Protect(value ?? "");
    }

    /// <summary>置信度阈值显示。</summary>
    public string ConfidenceThresholdText => $"当前阈值：{Settings.Ai.ConfidenceThreshold:0.00}";

    private void OnSaveClicked(object? sender, RoutedEventArgs e) => SaveNow(sender);
}
