using System.Runtime.Versioning;
using Avalonia.Interactivity;
using CyberTechRep.Shared.Abstractions;
using ClassIsland.Core.Attributes;

namespace CyberTechRep.Plugin.Controls.SettingsPages;

/// <summary>
/// 连接设置页：AppId/AppSecret（脱敏）/ApiBase/TokenApiUrl/群白名单/重连参数/历史回溯提示。
/// </summary>
[SettingsPageInfo("cybertechrep.settings.connection", "CyberTechRep 连接")]
[Group("cybertechrep.settings")]
public partial class ConnectionSettingsPage : CyberTechRepSettingsPageBase
{
    public ConnectionSettingsPage(ISettingsService settingsService)
        : base(settingsService, PluginRuntime.DataDirectory)
    {
        InitializeComponent();
    }

    /// <summary>AppSecret 明文（绑定视图；写入时 DPAPI 加密持久化）。</summary>
    public string AppSecretPlain
    {
        get => SettingsService.Unprotect(Settings.Connection.AppSecretProtected);
        set => Settings.Connection.AppSecretProtected = SettingsService.Protect(value ?? "");
    }

    /// <summary>群白名单 ↔ 多行文本（消息接管白名单，与发送目标群相互独立）。</summary>
    public string GroupWhitelistText
    {
        get => LinesToText([.. Settings.Connection.GroupWhitelist]);
        set => Settings.Connection.GroupWhitelist = TextToLines(value);
    }

    /// <summary>作业清单发送目标群 ↔ 多行文本（独立于消息接管白名单，需求 4）。</summary>
    public string TargetGroupsText
    {
        get => LinesToText([.. Settings.Connection.TargetGroupOpenIds]);
        set => Settings.Connection.TargetGroupOpenIds = TextToLines(value);
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e) => SaveNow(sender);
}
