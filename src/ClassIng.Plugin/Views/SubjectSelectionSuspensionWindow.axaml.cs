using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Input;
using ClassIng.Plugin.Services.Overlays;
using ClassIng.Plugin.Services.Pipeline;
using ClassIng.Plugin.Services.Stores;
using ClassIng.Plugin.Services.SubjectChain;

namespace ClassIng.Plugin.Views;

/// <summary>
/// 选择悬浮窗展示信息（绑定后供测试断言的快照）。
/// </summary>
public sealed class SubjectSelectionViewData
{
    public string SenderText { get; init; } = "";

    public string DigestText { get; init; } = "";

    public string ChainText { get; init; } = "";

    public IReadOnlyList<string> Subjects { get; init; } = [];
}

/// <summary>
/// 选择悬浮窗纯逻辑（可脱离 UI 单测）：发送者/摘要/识别候选的展示文案与点选学科列表。
/// 学科候选 = subjects.json 全部学科（与归档目录一致）；识别链命中的候选置顶。
/// </summary>
public static class SubjectSelectionLogic
{
    /// <summary>消息摘要最大长度（超出截断加省略号）。</summary>
    public const int DigestMaxLength = 120;

    public static string FormatSender(string memberOpenId, string senderNickname, string groupOpenId)
    {
        var name = string.IsNullOrWhiteSpace(senderNickname) ? "（未提供昵称）" : senderNickname;
        var member = string.IsNullOrWhiteSpace(memberOpenId) ? "（无 OpenID）" : memberOpenId;
        var group = string.IsNullOrWhiteSpace(groupOpenId) ? "" : $"　群：{Truncate(groupOpenId, 24)}";
        return $"{name}　成员：{member}{group}";
    }

    public static string FormatDigest(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "（消息无文本内容）" : Truncate(text.ReplaceLineEndings(" "), DigestMaxLength);

    public static string FormatChain(string chainSubject, double chainConfidence)
    {
        // 通知消息不经学科识别链（链候选为空）；作业消息即使链已命中也弹窗（未绑定时）。
        return string.IsNullOrWhiteSpace(chainSubject)
            ? "识别链候选：无（本消息不经学科识别链）"
            : $"识别链候选：{chainSubject}（置信度 {chainConfidence:0.00}）";
    }

    /// <summary>
    /// 点选学科列表：识别链候选（非未分类）置顶并标注，其余按 subjects.json 规则顺序去重追加。
    /// </summary>
    public static IReadOnlyList<string> BuildSubjectChoices(
        IEnumerable<string> ruleSubjects, string chainSubject)
    {
        var choices = new List<string>();
        if (!string.IsNullOrWhiteSpace(chainSubject)
            && !HomeworkSubjectResolver.IsUnclassified(chainSubject))
        {
            choices.Add(chainSubject);
        }

        foreach (var subject in ruleSubjects)
        {
            if (!string.IsNullOrWhiteSpace(subject)
                && !choices.Contains(subject, StringComparer.Ordinal))
            {
                choices.Add(subject);
            }
        }

        return choices;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";
}

/// <summary>
/// 未绑定学科选择悬浮窗（第五悬浮窗，需求 3）：消息需要学科分类但发送者未绑定学科时，
/// 由 <see cref="SubjectSelectionCoordinator"/> 装载触发请求并经
/// <see cref="Services.Overlays.SuspensionWindowController"/>（与其他悬浮窗同一路径）显示。
/// 展示发送者昵称/成员与群 OpenID、消息摘要、识别链候选；用户点选学科后经协调器写回
/// 成员绑定存储并修正该消息作业，窗口随即隐藏（Show/Hide 可见性由控制器同步回设置）。
/// </summary>
[SupportedOSPlatform("windows")]
public partial class SubjectSelectionSuspensionWindow : Window
{
    /// <summary>最近一次装载的触发请求（null = 尚未触发过）。</summary>
    private SubjectSelectionRequest? _currentRequest;

    /// <summary>subjects.json 学科名单提供者（点选学科列表数据源；null 时无候选）。</summary>
    private readonly Func<IReadOnlyList<string>>? _ruleSubjectsProvider;

    /// <summary>用户点选学科回调（由协调器注入；参数 = 触发请求 + 所选学科）。</summary>
    private readonly Func<SubjectSelectionRequest, string, Task>? _onSubjectSelected;

    public SubjectSelectionSuspensionWindow()
    {
        // 设计时/XAML 预览用；运行时走含依赖构造
        InitializeComponent();
    }

    public SubjectSelectionSuspensionWindow(
        Func<SubjectSelectionRequest, string, Task>? onSubjectSelected,
        Func<IReadOnlyList<string>>? ruleSubjectsProvider = null)
    {
        _onSubjectSelected = onSubjectSelected;
        _ruleSubjectsProvider = ruleSubjectsProvider;
        InitializeComponent();
    }

    /// <summary>当前展示的请求（测试用）。</summary>
    internal SubjectSelectionRequest? CurrentRequest => _currentRequest;

    /// <summary>装载触发请求并刷新展示（显示由控制器负责；本方法只改内容）。</summary>
    public void ShowRequest(SubjectSelectionRequest request)
    {
        _currentRequest = request ?? throw new ArgumentNullException(nameof(request));
        var subjects = BuildSubjects();
        SenderText.Text = SubjectSelectionLogic.FormatSender(
            request.MemberOpenId, request.SenderNickname, request.GroupOpenId);
        DigestText.Text = SubjectSelectionLogic.FormatDigest(request.MessageDigest);
        ChainText.Text = SubjectSelectionLogic.FormatChain(request.ChainSubject, request.ChainConfidence);
        SubjectList.ItemsSource = subjects;
    }

    /// <summary>当前展示内容快照（测试用）。</summary>
    public SubjectSelectionViewData CaptureView() => new()
    {
        SenderText = SenderText.Text ?? "",
        DigestText = DigestText.Text ?? "",
        ChainText = ChainText.Text ?? "",
        Subjects = SubjectList?.ItemsSource?.Cast<string>().ToList() ?? []
    };

    /// <summary>点选学科（用户点击按钮与测试共用的入口）：回调写回后隐藏窗口。</summary>
    internal async Task SelectSubjectAsync(string subject)
    {
        if (_currentRequest is null)
        {
            return;
        }

        try
        {
            if (_onSubjectSelected is not null)
            {
                await _onSubjectSelected(_currentRequest, subject);
            }
        }
        catch
        {
            // 写回失败不阻断窗口隐藏（协调器侧已记日志；用户可在设置页/词表页手工补绑定）
        }
        finally
        {
            Hide();
        }
    }

    private IReadOnlyList<string> BuildSubjects()
    {
        var chainSubject = _currentRequest?.ChainSubject ?? "";
        var rules = _ruleSubjectsProvider?.Invoke() ?? [];
        return SubjectSelectionLogic.BuildSubjectChoices(rules, chainSubject);
    }

    private async void OnSubjectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Content: string subject })
        {
            await SelectSubjectAsync(subject);
        }
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 固定模式下禁用拖拽（与其他悬浮窗一致）
        if (OverlayBehaviors.GetFixed(this))
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnHideClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Hide();

    private void OnResizeDragDelta(object? sender, VectorEventArgs e)
    {
        if (OverlayBehaviors.GetFixed(this))
        {
            return;
        }

        Width = Math.Max(MinWidth, Width + e.Vector.X);
        Height = Math.Max(MinHeight, Height + e.Vector.Y);
    }
}
