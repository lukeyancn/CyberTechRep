using System.Runtime.Versioning;
using Avalonia.Interactivity;
using CyberTechRep.Shared.Abstractions;
using ClassIsland.Core.Attributes;

namespace CyberTechRep.Plugin.Controls.SettingsPages;

/// <summary>
/// 文件设置页：下载根目录/按学科建目录/MD5 去重/磁盘上限/清理策略/并发/限速/单文件上限/文件名长度。
/// </summary>
[SettingsPageInfo("cybertechrep.settings.files", "CyberTechRep 文件")]
[Group("cybertechrep.settings")]
public partial class FileSettingsPage : CyberTechRepSettingsPageBase
{
    public FileSettingsPage(ISettingsService settingsService)
        : base(settingsService, PluginRuntime.DataDirectory)
    {
        InitializeComponent();
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e) => SaveNow(sender);
}
