using System.Runtime.Versioning;
using Avalonia.Interactivity;
using ClassIng.Shared.Abstractions;
using ClassIsland.Core.Attributes;

namespace ClassIng.Plugin.Controls.SettingsPages;

/// <summary>
/// 文件设置页：下载根目录/按学科建目录/MD5 去重/磁盘上限/清理策略/并发/限速/单文件上限/文件名长度。
/// </summary>
[SettingsPageInfo("classing.settings.files", "ClassIng 文件")]
[Group("classing.settings")]
public partial class FileSettingsPage : ClassIngSettingsPageBase
{
    public FileSettingsPage(ISettingsService settingsService)
        : base(settingsService, PluginRuntime.DataDirectory)
    {
        InitializeComponent();
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e) => SaveNow();
}
