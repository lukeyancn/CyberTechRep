using System.Runtime.Versioning;
using Avalonia.Interactivity;
using CyberTechRep.Shared.Abstractions;
using ClassIsland.Core.Attributes;

namespace CyberTechRep.Plugin.Controls.SettingsPages;

/// <summary>
/// 分类设置页（需求 6 去重后）：人工确认队列开关与设置归属指引。
/// <para>
/// 此前本页承载的 AI 配置（总开关/本地模型/云端端点/Key/模型名/每日限额/置信度阈值）
/// 已全部迁至「CyberTechRep AI」页；通知/作业关键词与学科词表（subjects.json）编辑
/// 统一收口到「学科词表编辑」页（此前两处可改同一份配置，易混淆），本页不再重复。
/// </para>
/// </summary>
[SettingsPageInfo("cybertechrep.settings.classification", "CyberTechRep 分类")]
[Group("cybertechrep.settings")]
public partial class ClassificationSettingsPage : CyberTechRepSettingsPageBase
{
    public ClassificationSettingsPage(ISettingsService settingsService)
        : base(settingsService, PluginRuntime.DataDirectory)
    {
        InitializeComponent();
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e) => SaveNow(sender);
}
