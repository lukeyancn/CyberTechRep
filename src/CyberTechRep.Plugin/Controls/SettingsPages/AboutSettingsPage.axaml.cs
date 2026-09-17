using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CyberTechRep.Shared.Abstractions;
using ClassIsland.Core.Attributes;

namespace CyberTechRep.Plugin.Controls.SettingsPages;

/// <summary>
/// 「关于 CyberTechRep」设置页（需求 5）：展示插件标题与一句话说明、动态版本号（程序集信息优先，
/// 随包 manifest.yml 兜底，见 <see cref="AboutInfo"/>）、作者、GitHub 仓库（可复制文本 + 打开仓库）、
/// 插件 ID、数据目录（可复制路径 + 打开目录，不存在时先创建）、运行环境（.NET/操作系统/设置文件路径）。
/// <para>
/// 本页只读、不改配置：打开外部链接/目录与复制到剪贴板失败只给出提示文本，绝不抛异常。
/// </para>
/// </summary>
[SettingsPageInfo("cybertechrep.settings.about", "CyberTechRep 关于")]
[Group("cybertechrep.settings")]
[SupportedOSPlatform("windows")]
public partial class AboutSettingsPage : CyberTechRepSettingsPageBase
{
    private string _feedback = "";

    public AboutSettingsPage(ISettingsService settingsService)
        : base(settingsService, PluginRuntime.DataDirectory)
    {
        // 版本/环境信息在构造时取一次：程序集版本与数据目录运行期不变
        // （程序集显式取 CyberTechRepPlugin 所在程序集，即 csproj <Version> 生成的程序集信息）
        Info = AboutInfo.Create(typeof(CyberTechRepPlugin).Assembly, PluginRuntime.DataDirectory);
        InitializeComponent();
    }

    /// <summary>关于信息快照（版本/作者/仓库/插件 ID/数据目录/运行环境）。</summary>
    public AboutInfo Info { get; }

    /// <summary>操作反馈文本（打开仓库/打开数据目录/复制结果）。</summary>
    public string Feedback
    {
        get => _feedback;
        private set
        {
            _feedback = value;
            RaisePropertyChanged(nameof(Feedback));
        }
    }

    private void OnOpenRepositoryClicked(object? sender, RoutedEventArgs e) =>
        OpenByShell(AboutInfo.Repository, "GitHub 仓库");

    private void OnOpenDataDirectoryClicked(object? sender, RoutedEventArgs e)
    {
        var directory = Info.DataDirectory;
        try
        {
            // 目录不存在时先创建再打开，避免「打开无反应」且无任何提示
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        catch (Exception ex)
        {
            Feedback = $"创建数据目录失败：{ex.Message}（路径：{directory}）";
            return;
        }

        OpenByShell(directory, "数据目录");
    }

    private async void OnCopyRepositoryClicked(object? sender, RoutedEventArgs e) =>
        await CopyToClipboardAsync(AboutInfo.Repository, "仓库地址");

    private async void OnCopyDataDirectoryClicked(object? sender, RoutedEventArgs e) =>
        await CopyToClipboardAsync(Info.DataDirectory, "数据目录路径");

    /// <summary>经系统外壳打开 URL/目录（UseShellExecute）；失败只提示不抛。</summary>
    private void OpenByShell(string target, string label)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            Feedback = $"已打开{label}：{target}";
        }
        catch (Exception ex)
        {
            Feedback = $"打开{label}失败：{ex.Message}（路径：{target}）";
        }
    }

    /// <summary>复制文本到剪贴板（失败只提示不抛）。</summary>
    private async Task CopyToClipboardAsync(string text, string label)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                Feedback = $"复制{label}失败：剪贴板不可用";
                return;
            }

            await clipboard.SetTextAsync(text);
            Feedback = $"已复制{label}到剪贴板：{text}";
        }
        catch (Exception ex)
        {
            Feedback = $"复制{label}失败：{ex.Message}";
        }
    }
}
