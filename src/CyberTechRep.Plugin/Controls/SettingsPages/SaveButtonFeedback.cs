using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Animation;
using Avalonia.Styling;

namespace CyberTechRep.Plugin.Controls.SettingsPages;

/// <summary>
/// 设置页「保存并应用」按钮保存反馈（可复用辅助，避免各页面复制粘贴）：
/// 成功 → 按钮文本切换为对勾「✓ 已保存」并渐显，约 1.5 秒后还原原内容；
/// 失败 → 文本切换为「✗ 保存失败」并附错误提示 ToolTip，约 2.5 秒后还原。
/// 原内容经附加属性挂在按钮自身上，重复触发时重置计时器，不泄漏旧按钮引用。
/// </summary>
[SupportedOSPlatform("windows")]
public static class SaveButtonFeedback
{
    /// <summary>成功反馈展示时长。</summary>
    public static readonly TimeSpan SuccessDuration = TimeSpan.FromMilliseconds(1500);

    /// <summary>失败反馈展示时长（略长，给用户时间读错误提示）。</summary>
    public static readonly TimeSpan FailureDuration = TimeSpan.FromMilliseconds(2500);

    private static readonly AttachedProperty<object?> OriginalContentProperty =
        AvaloniaProperty.RegisterAttached<Button, object?>("FeedbackOriginalContent", typeof(SaveButtonFeedback));

    private static readonly AttachedProperty<DispatcherTimer?> FeedbackTimerProperty =
        AvaloniaProperty.RegisterAttached<Button, DispatcherTimer?>("FeedbackTimer", typeof(SaveButtonFeedback));

    /// <summary>展示保存成功反馈（按钮内对勾渐显后还原）。</summary>
    public static void ShowSuccess(Button? button)
    {
        if (button is null)
        {
            return;
        }

        Show(button, "✓ 已保存", Brushes.ForestGreen, SuccessDuration);
    }

    /// <summary>展示保存失败反馈（异常提示，不假装成功）。</summary>
    public static void ShowFailure(Button? button, string? error)
    {
        if (button is null)
        {
            return;
        }

        Show(button, "✗ 保存失败", Brushes.Firebrick, FailureDuration);
        ToolTip.SetTip(button, string.IsNullOrWhiteSpace(error) ? "保存设置时发生异常。" : $"保存失败：{error}");
    }

    private static void Show(Button button, string feedbackText, IBrush foreground, TimeSpan duration)
    {
        // 首次进入反馈态时保存原内容；已在反馈态则沿用（连续保存不丢原文本）
        if (button.GetValue(OriginalContentProperty) is null)
        {
            button.SetValue(OriginalContentProperty, button.Content);
        }

        button.SetValue(Button.ForegroundProperty, foreground);
        button.Content = feedbackText;

        // 渐显：透明度 0.2 → 1.0（约 250ms），用后即弃的轻量动画
        var fadeIn = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(250),
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0.0),
                    Setters = { new Setter(Visual.OpacityProperty, 0.2d) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1.0),
                    Setters = { new Setter(Visual.OpacityProperty, 1.0d) }
                }
            }
        };
        _ = fadeIn.RunAsync(button);

        // 复位计时器：重复触发时停掉上一个，避免提前还原或双触发
        if (button.GetValue(FeedbackTimerProperty) is { } existing)
        {
            existing.Stop();
        }

        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Restore(button);
        };
        button.SetValue(FeedbackTimerProperty, timer);
        timer.Start();
    }

    private static void Restore(Button button)
    {
        button.Content = button.GetValue(OriginalContentProperty) ?? "保存并应用";
        button.ClearValue(Button.ForegroundProperty);
        button.ClearValue(ToolTip.TipProperty);
        button.SetValue(OriginalContentProperty, null);
        button.ClearValue(FeedbackTimerProperty);
    }
}
