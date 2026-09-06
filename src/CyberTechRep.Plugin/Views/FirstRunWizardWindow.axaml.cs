using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CyberTechRep.Plugin.Services.FirstRun;
using CyberTechRep.Shared.Abstractions;

namespace CyberTechRep.Plugin.Views;

/// <summary>
/// 首次启动引导窗口（模块 9，Avalonia 无边框置顶窗，风格与模块 6 悬浮窗一致）：
/// 欢迎/说明 → 连接配置（AppID/AppSecret/API 地址，写入 ISettingsService）→ 完成。
/// 完成/跳过均经 <see cref="IFirstRunService.MarkCompletedAsync"/> 落盘 FirstRunCompleted，
/// 之后不再自动弹出；维护设置页「重新打开引导」可再次唤出（预填当前连接配置）。
/// 任何一步失败只显示反馈文案，不崩溃。
/// </summary>
public partial class FirstRunWizardWindow : Window, IFirstRunWizardWindow
{
    private readonly ISettingsService? _settingsService;
    private readonly IFirstRunService? _firstRunService;
    private int _step;

    public FirstRunWizardWindow()
    {
        // 设计时/XAML 预览用；运行时走含服务构造
        InitializeComponent();
    }

    public FirstRunWizardWindow(ISettingsService settingsService, IFirstRunService firstRunService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _firstRunService = firstRunService ?? throw new ArgumentNullException(nameof(firstRunService));
        InitializeComponent();
        ShowStep(WelcomeStep);
        LoadCurrentConnection();
    }

    private const int WelcomeStep = 0;
    private const int ConnectionStep = 1;
    private const int FinishStep = 2;

    /// <summary>预填当前连接配置（重新打开引导时不丢失已有值）。</summary>
    private void LoadCurrentConnection()
    {
        try
        {
            if (_settingsService is null)
            {
                return;
            }

            var connection = _settingsService.Current.Connection;
            AppIdBox.Text = connection.AppId;
            SecretBox.Text = _settingsService.Unprotect(connection.AppSecretProtected);
            ApiBaseBox.Text = connection.ApiBase;
            TokenApiBox.Text = connection.TokenApiUrl;
        }
        catch (Exception ex)
        {
            // 预填失败不阻断引导（保留空表单）
            System.Diagnostics.Debug.WriteLine($"引导预填连接配置失败：{ex.Message}");
        }
    }

    private void ShowStep(int step)
    {
        _step = step;
        StepWelcome.IsVisible = step == WelcomeStep;
        StepConnection.IsVisible = step == ConnectionStep;
        StepFinish.IsVisible = step == FinishStep;
        BackButton.IsVisible = step == ConnectionStep;
        SkipButton.IsVisible = step != FinishStep;
        NextButton.Content = step switch
        {
            WelcomeStep => "下一步",
            ConnectionStep => "完成",
            _ => "关闭"
        };
    }

    private async void OnNextClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            switch (_step)
            {
                case WelcomeStep:
                    ShowStep(ConnectionStep);
                    break;
                case ConnectionStep:
                    await CompleteAsync().ConfigureAwait(true);
                    break;
                default:
                    Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowFeedback($"操作失败：{ex.Message}");
        }
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_step == ConnectionStep)
            {
                ShowStep(WelcomeStep);
            }
        }
        catch (Exception ex)
        {
            ShowFeedback($"操作失败：{ex.Message}");
        }
    }

    private async void OnSkipClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            // 跳过同样标记完成：用户明确表示暂不配置，之后不再自动弹出
            if (_firstRunService is not null)
            {
                await _firstRunService.MarkCompletedAsync().ConfigureAwait(true);
            }

            Close();
        }
        catch (Exception ex)
        {
            ShowFeedback($"关闭引导失败：{ex.Message}");
            try
            {
                Close();
            }
            catch
            {
                // 关闭失败保持窗口，不影响插件其余功能
            }
        }
    }

    /// <summary>完成：连接配置写入 ISettingsService + 标记 FirstRunCompleted 持久化。</summary>
    private async Task CompleteAsync()
    {
        if (_settingsService is null || _firstRunService is null)
        {
            ShowStep(FinishStep);
            return;
        }

        try
        {
            var connection = _settingsService.Current.Connection;
            connection.AppId = AppIdBox.Text?.Trim() ?? "";

            var secret = SecretBox.Text ?? "";
            if (secret.Length > 0)
            {
                connection.AppSecretProtected = _settingsService.Protect(secret);
            }

            // 地址留空时回退官方默认值，避免写空串导致协议端不可用
            var defaults = new Shared.Models.ConnectionSettings();
            connection.ApiBase = NormalizeOrDefault(ApiBaseBox.Text, defaults.ApiBase);
            connection.TokenApiUrl = NormalizeOrDefault(TokenApiBox.Text, defaults.TokenApiUrl);

            await _firstRunService.MarkCompletedAsync().ConfigureAwait(true);

            ConnectionFeedback.IsVisible = false;
            FinishSummary.Text = string.IsNullOrEmpty(connection.AppId)
                ? "未填写 AppID，CyberTechRep 暂不会连接协议端。可稍后在「CyberTechRep 连接」设置页补全。"
                : "连接参数与引导状态已保存，之后可在 ClassIsland 设置的 CyberTechRep 分组中随时调整。";
            ShowStep(FinishStep);
        }
        catch (Exception ex)
        {
            // 保存失败不崩溃：留在本步，提示可稍后到设置页重试
            ShowFeedback($"保存连接配置失败：{ex.Message}（可稍后在「CyberTechRep 连接」设置页重试）");
        }
    }

    private void ShowFeedback(string message)
    {
        try
        {
            ConnectionFeedback.Text = message;
            ConnectionFeedback.IsVisible = true;
        }
        catch
        {
            // 反馈文案显示失败不影响主流程
        }
    }

    private static string NormalizeOrDefault(string? value, string defaultValue)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? defaultValue : trimmed;
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
