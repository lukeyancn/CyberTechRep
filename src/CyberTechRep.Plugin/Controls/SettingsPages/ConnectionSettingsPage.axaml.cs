using System.Runtime.Versioning;
using Avalonia.Interactivity;
using CyberTechRep.Plugin.Services.MessageAccess;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using ClassIsland.Core.Attributes;

namespace CyberTechRep.Plugin.Controls.SettingsPages;

/// <summary>
/// 连接设置页：接入模式（官方/NapCat）+ AppId/AppSecret（脱敏）/ApiBase/TokenApiUrl/
/// 群白名单/重连参数/历史回溯提示 + NapCat 专属设置与一键启动。
/// 官方模式控件与原版完全一致；NapCat 为可选替代模式，默认官方零回归。
/// </summary>
[SettingsPageInfo("cybertechrep.settings.connection", "CyberTechRep 连接")]
[Group("cybertechrep.settings")]
public partial class ConnectionSettingsPage : CyberTechRepSettingsPageBase
{
    private readonly NapCatRunnerService _napCatRunner;

    public ConnectionSettingsPage(ISettingsService settingsService, NapCatRunnerService napCatRunner)
        : base(settingsService, PluginRuntime.DataDirectory)
    {
        _napCatRunner = napCatRunner;
        _napCatRunner.StatusChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                RaisePropertyChanged(nameof(NapCatRunnerStatusText));
                RaisePropertyChanged(nameof(HasWebUiUrl));
                RaisePropertyChanged(nameof(NapCatWebUiUrlText));
            });
        InitializeComponent();
    }

    /// <summary>AppSecret 明文（绑定视图；写入时 DPAPI 加密持久化）。</summary>
    public string AppSecretPlain
    {
        get => SettingsService.Unprotect(Settings.Connection.AppSecretProtected);
        set => Settings.Connection.AppSecretProtected = SettingsService.Protect(value ?? "");
    }

    /// <summary>NapCat access token 明文（绑定视图；写入时 DPAPI 加密持久化）。</summary>
    public string NapCatAccessTokenPlain
    {
        get => SettingsService.Unprotect(Settings.Connection.NapCatAccessTokenProtected);
        set => Settings.Connection.NapCatAccessTokenProtected = SettingsService.Protect(value ?? "");
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

    // ---- 模式选择（RadioButton 双向绑定桥接 Mode 枚举） ----

    /// <summary>官方模式选中状态（默认；控件显示与 Mode 枚举双向同步）。</summary>
    public bool IsOfficialMode
    {
        get => Settings.Connection.Mode == MessageConnectionMode.Official;
        set
        {
            if (value)
            {
                Settings.Connection.Mode = MessageConnectionMode.Official;
            }

            RaisePropertyChanged(nameof(IsOfficialMode));
            RaisePropertyChanged(nameof(IsNapCatMode));
        }
    }

    /// <summary>NapCat 模式选中状态。</summary>
    public bool IsNapCatMode
    {
        get => Settings.Connection.Mode == MessageConnectionMode.NapCat;
        set
        {
            if (value)
            {
                Settings.Connection.Mode = MessageConnectionMode.NapCat;
            }

            RaisePropertyChanged(nameof(IsOfficialMode));
            RaisePropertyChanged(nameof(IsNapCatMode));
        }
    }

    /// <summary>NapCat 一键启动运行状态（NotRunning / Starting / Running [pid] / Stopped / Failed: 原因）。</summary>
    public string NapCatRunnerStatusText
    {
        get
        {
            var status = _napCatRunner.Status;
            return status.State switch
            {
                NapCatRunnerState.NotRunning => "未运行",
                NapCatRunnerState.Starting => status.Detail,
                NapCatRunnerState.Running => status.Detail,
                NapCatRunnerState.Stopped => status.Detail,
                NapCatRunnerState.Failed => $"失败：{status.Detail}",
                _ => status.Detail
            };
        }
    }

    // ---- 登录方式（RadioButton 双向绑定桥接 NapCatLoginMode 枚举） ----

    /// <summary>扫码登录选中状态（默认；二维码经 NapCat WebUI/控制台展示）。</summary>
    public bool IsScanQrLogin
    {
        get => Settings.Connection.NapCatLoginMode == NapCatLoginMode.ScanQrCode;
        set
        {
            if (value)
            {
                Settings.Connection.NapCatLoginMode = NapCatLoginMode.ScanQrCode;
            }

            RaisePropertyChanged(nameof(IsScanQrLogin));
            RaisePropertyChanged(nameof(IsQuickLogin));
        }
    }

    /// <summary>QQ 号快速登录选中状态（每次启动传 <c>-q QQ号</c> 跳过扫码）。</summary>
    public bool IsQuickLogin
    {
        get => Settings.Connection.NapCatLoginMode == NapCatLoginMode.QuickLoginQQ;
        set
        {
            if (value)
            {
                Settings.Connection.NapCatLoginMode = NapCatLoginMode.QuickLoginQQ;
            }

            RaisePropertyChanged(nameof(IsScanQrLogin));
            RaisePropertyChanged(nameof(IsQuickLogin));
        }
    }

    /// <summary>是否已发现 NapCat WebUI 地址（控制「打开 WebUI」按钮可用性）。</summary>
    public bool HasWebUiUrl => !string.IsNullOrEmpty(_napCatRunner.WebUiUrl);

    /// <summary>WebUI 地址展示文本（未发现时给出指引）。</summary>
    public string NapCatWebUiUrlText => HasWebUiUrl
        ? $"WebUI 地址：{_napCatRunner.WebUiUrl}"
        : "WebUI 地址：启动 NapCat 后自动从其配置中发现（用于扫码登录与账号管理）";

    private void OnSaveClicked(object? sender, RoutedEventArgs e) => SaveNow(sender);

    private void OnStartNapCatClicked(object? sender, RoutedEventArgs e) => _ = _napCatRunner.StartNapCatAsync();

    private void OnStopNapCatClicked(object? sender, RoutedEventArgs e) => _ = _napCatRunner.StopNapCatAsync();

    private void OnOpenWebUiClicked(object? sender, RoutedEventArgs e) => _napCatRunner.OpenWebUi();
}
