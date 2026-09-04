using System.Runtime.Versioning;
using Avalonia.Interactivity;
using ClassIng.Shared.Abstractions;
using ClassIsland.Core.Attributes;

namespace ClassIng.Plugin.Controls.SettingsPages;

/// <summary>
/// 连接设置页：AppId/AppSecret（脱敏）/ApiBase/TokenApiUrl/群白名单/重连参数/历史回溯提示。
/// </summary>
[SettingsPageInfo("classing.settings.connection", "ClassIng 连接")]
[Group("classing.settings")]
public partial class ConnectionSettingsPage : ClassIngSettingsPageBase
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

    /// <summary>群白名单 ↔ 多行文本。</summary>
    public string GroupWhitelistText
    {
        get => LinesToText([.. Settings.Connection.GroupWhitelist]);
        set => Settings.Connection.GroupWhitelist = TextToLines(value);
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e) => SaveNow();
}
