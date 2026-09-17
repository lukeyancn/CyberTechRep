using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Input;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Plugin.Services.Pipeline;
using CyberTechRep.Plugin.Services.Stores;
using CyberTechRep.Plugin.Services.SubjectChain;

namespace CyberTechRep.Plugin.Views;

/// <summary>
/// 选择悬浮窗展示信息（绑定后供测试断言的快照）。
/// </summary>
public sealed class SubjectSelectionViewData
{
    public string SenderText { get; init; } = "";

    public string DigestText { get; init; } = "";

    public string ChainText { get; init; } = "";

    /// <summary>待处理条数提示文本（需求 7：无待处理时为空串且不可见）。</summary>
    public string PendingText { get; init; } = "";

    /// <summary>待处理条数提示是否可见（0 条时隐藏）。</summary>
    public bool PendingVisible { get; init; }

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

    /// <summary>
    /// 「跳过（暂不绑定）」约定（需求 7）：点选回调的学科参数为空串 = 不写任何绑定，直接推进队列。
    /// 窗口不新增回调（保持构造签名不变），经既有 <c>onSubjectSelected</c> 回调传递该约定。
    /// </summary>
    public const string SkipSubject = "";

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

    /// <summary>待处理条数提示文案（需求 7）：让用户知道后面还有多少条会依次弹出。</summary>
    public static string FormatPendingCount(int pendingCount) =>
        $"待处理 {Math.Max(0, pendingCount)} 条（按到达顺序逐条处理）";

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
/// 未绑定学科选择悬浮窗（第五悬浮窗，需求 3 + 需求 7）：消息需要学科分类但发送者未绑定学科时，
/// 由 <see cref="SubjectSelectionCoordinator"/> 按到达顺序装载触发请求并经
/// <see cref="Services.Overlays.SuspensionWindowController"/>（与其他悬浮窗同一路径）显示。
/// 展示发送者昵称/成员与群 OpenID、消息摘要、识别链候选与待处理条数；用户点选学科后经协调器
/// 写回成员绑定存储并修正该消息作业，然后由协调器决定「装载下一条」或「收起窗口」。
/// 需求 7：窗口一次只展示队首请求——后面的请求排队等待，绝不覆盖本窗口内容；
/// 底部「跳过（暂不绑定）」只丢弃当前条（不写任何绑定），后续请求继续依次弹出。
/// </summary>
[SupportedOSPlatform("windows")]
public partial class SubjectSelectionSuspensionWindow : Window
{
    /// <summary>最近一次装载的触发请求（null = 尚未触发过）。</summary>
    private SubjectSelectionRequest? _currentRequest;

    /// <summary>subjects.json 学科名单提供者（点选学科列表数据源；null 时无候选）。</summary>
    private readonly Func<IReadOnlyList<string>>? _ruleSubjectsProvider;

    /// <summary>
    /// 用户点选学科回调（由协调器注入；参数 = 触发请求 + 所选学科）。
    /// 学科为空串表示「跳过（暂不绑定）」（见 <see cref="SubjectSelectionLogic.SkipSubject"/>）。
    /// </summary>
    private readonly Func<SubjectSelectionRequest, string, Task>? _onSubjectSelected;

    /// <summary>回调处理中标记：等待写回/推进期间忽略重复点击，避免同一请求重复推进队列。</summary>
    private bool _submitting;

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

    /// <summary>
    /// 装载队首触发请求并刷新展示（显示由控制器负责；本方法只改内容）。
    /// 需求 7：只由协调器在「队列推进到下一条」时调用，绝不覆盖正在处理中的请求。
    /// </summary>
    public void ShowRequest(SubjectSelectionRequest request)
    {
        _currentRequest = request ?? throw new ArgumentNullException(nameof(request));
        var subjects = BuildSubjects();
        SenderText.Text = SubjectSelectionLogic.FormatSender(
            request.MemberOpenId, request.SenderNickname, request.GroupOpenId);
        DigestText.Text = SubjectSelectionLogic.FormatDigest(request.MessageDigest);
        ChainText.Text = SubjectSelectionLogic.FormatChain(request.ChainSubject, request.ChainConfidence);
        SubjectList.ItemsSource = subjects;
        // 待处理条数由协调器装载后立即刷新；先清零避免短暂显示上一条的残留计数
        UpdatePendingCount(0);
    }

    /// <summary>
    /// 刷新「待处理 N 条」提示（需求 7）：count &gt; 0 时显示，0 条时隐藏（当前仅此一条）。
    /// </summary>
    public void UpdatePendingCount(int pendingCount)
    {
        var count = Math.Max(0, pendingCount);
        PendingText.Text = SubjectSelectionLogic.FormatPendingCount(count);
        PendingText.IsVisible = count > 0;
    }

    /// <summary>当前展示内容快照（测试用）。</summary>
    public SubjectSelectionViewData CaptureView() => new()
    {
        SenderText = SenderText.Text ?? "",
        DigestText = DigestText.Text ?? "",
        ChainText = ChainText.Text ?? "",
        PendingText = PendingText.Text ?? "",
        PendingVisible = PendingText.IsVisible,
        Subjects = SubjectList?.ItemsSource?.Cast<string>().ToList() ?? []
    };

    /// <summary>
    /// 点选学科（用户点击按钮与测试共用的入口）：回调写回后**不**自行隐藏——
    /// 需求 7 由协调器决定「装载下一条（内容替换）或收起窗口」，避免隐藏后队列停在无人可见的条目上。
    /// </summary>
    internal Task SelectSubjectAsync(string subject) => InvokeSelectionCallbackAsync(subject);

    /// <summary>「跳过（暂不绑定）」：经既有回调以空学科通知协调器（不写绑定，直接推进队列）。</summary>
    internal Task SkipAsync() => InvokeSelectionCallbackAsync(SubjectSelectionLogic.SkipSubject);

    private async Task InvokeSelectionCallbackAsync(string subject)
    {
        if (_currentRequest is null || _submitting)
        {
            return;
        }

        _submitting = true;
        try
        {
            if (_onSubjectSelected is not null)
            {
                await _onSubjectSelected(_currentRequest, subject);
            }
        }
        catch
        {
            // 写回/推进失败不阻断窗口（协调器侧已记日志；用户可在设置页/词表页手工补绑定）
        }
        finally
        {
            _submitting = false;
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

    private async void OnSkipClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await SkipAsync();
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
