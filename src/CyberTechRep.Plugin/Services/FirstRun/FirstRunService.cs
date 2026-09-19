using System.Runtime.Versioning;
using System.Text;
using Avalonia.Threading;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.FirstRun;

/// <summary>
/// 引导窗口抽象（模块 9）：逻辑层（<see cref="FirstRunService"/>）只依赖该接口，
/// 不直接依赖 XAML 窗口实例，单元测试可注入假窗口验证显示/激活/关闭行为。
/// <see cref="Views.FirstRunWizardWindow"/> 本身已具备同签名成员，声明接口即可实现。
/// （public：FirstRunService 公共构造函数的工厂参数类型可访问性需不低于其自身。）
/// </summary>
public interface IFirstRunWizardWindow
{
    /// <summary>窗口关闭（用户点关闭/完成关闭按钮）。</summary>
    event EventHandler? Closed;

    /// <summary>显示窗口。</summary>
    void Show();

    /// <summary>把已打开的窗口带到前台。</summary>
    void Activate();
}

/// <summary>首次启动引导服务（模块 9）：首启检测 / 完成标记持久化 / 安全弹出引导窗口。</summary>
public interface IFirstRunService
{
    /// <summary>是否尚未完成首次启动引导（settings.json 中 FirstRunCompleted=false）。</summary>
    bool IsFirstRun { get; }

    /// <summary>引导窗口当前是否处于打开状态。</summary>
    bool IsWizardOpen { get; }

    /// <summary>
    /// 标记引导完成并持久化（写入 settings.json 的 FirstRunCompleted=true）。
    /// 任何异常只记日志、不外抛（引导失败不影响插件其余功能）。
    /// </summary>
    Task MarkCompletedAsync(CancellationToken ct = default);

    /// <summary>
    /// 显示引导窗口（重新打开引导入口同样走此方法，不检查 IsFirstRun）。
    /// 窗口创建/显示/调度任何一步失败均返回 false 并记日志，绝不外抛。
    /// </summary>
    Task<bool> ShowWizardAsync(CancellationToken ct = default);
}

/// <summary>
/// 首次启动引导服务实现（模块 9）。
/// <para>
/// 首启判定依据：settings.json 中的 <c>FirstRunCompleted</c> 标志（模块 7 的
/// <see cref="ISettingsService"/> 持久化机制复用：原子写入 + 损坏回退默认值）。相比
/// "是否已有连接配置" 的启发式判定，显式标志在用户跳过引导、或只填了部分连接字段时
/// 不会重复弹出。窗口实例由工厂委托创建（Plugin.cs 注册时注入），服务自身不做 XAML
/// 依赖；UI 线程调度经 <c>UiInvoker</c> 委托（internal，测试注入同步执行）。
/// </para>
/// </summary>
public sealed class FirstRunService : IFirstRunService
{
    private readonly ISettingsService _settingsService;
    private readonly ILogger _logger;
    private readonly Func<IFirstRunWizardWindow?>? _windowFactory;
    private IFirstRunWizardWindow? _wizardWindow;

    /// <param name="settingsService">设置服务（FirstRunCompleted 标志与连接配置的读写源）。</param>
    /// <param name="logger">可选结构化日志。</param>
    /// <param name="windowFactory">引导窗口工厂（DI 注册时注入真实 XAML 窗口；null = 无法显示引导）。</param>
    public FirstRunService(ISettingsService settingsService, ILogger? logger = null,
        Func<IFirstRunWizardWindow?>? windowFactory = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _logger = logger ?? NullLogger.Instance;
        _windowFactory = windowFactory;
    }

    /// <summary>
    /// UI 线程调度委托（internal，测试可注入同步执行）：
    /// 默认投递到 Avalonia UI 线程；调度失败由 ShowWizardAsync 捕获记日志。
    /// </summary>
    internal Func<Action, Task> UiInvoker { get; set; } =
        action => Dispatcher.UIThread.InvokeAsync(action).GetTask();

    /// <inheritdoc />
    public bool IsFirstRun => !_settingsService.Current.FirstRunCompleted;

    /// <summary>设置服务实例（internal，单元测试读取 Current / 订阅 SettingsChanged 用）。</summary>
    internal ISettingsService SettingsService => _settingsService;

    /// <inheritdoc />
    public bool IsWizardOpen => _wizardWindow is not null;

    /// <inheritdoc />
    public async Task MarkCompletedAsync(CancellationToken ct = default)
    {
        // 失败策略：内存中先标记（本次会话不再弹窗），写盘失败仅记日志——
        // 下次启动会重新引导，比"假装成功"更安全；绝不外抛阻断引导流程。
        _settingsService.Current.FirstRunCompleted = true;
        try
        {
            await _settingsService.SaveAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("首次启动引导完成标记已写入 settings.json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入首次启动引导完成标记失败（内存中已标记，不影响插件其余功能）");
        }
    }

    /// <inheritdoc />
    public async Task<bool> ShowWizardAsync(CancellationToken ct = default)
    {
        var shown = false;
        try
        {
            await UiInvoker(() => shown = ShowCore()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 调度失败（如无 Avalonia 平台）：记日志不崩溃，插件其余功能不受影响
            _logger.LogError(ex, "调度首次启动引导窗口失败（不影响插件其余功能）");
        }

        return shown;
    }

    /// <summary>UI 线程内执行：复用已打开窗口（激活）或经工厂创建新窗口并显示。</summary>
    private bool ShowCore()
    {
        try
        {
            if (_wizardWindow is { } open)
            {
                open.Activate();
                _logger.LogInformation("首次启动引导窗口已存在，激活置前");
                return true;
            }

            var window = _windowFactory?.Invoke();
            if (window is null)
            {
                _logger.LogWarning("引导窗口工厂未注入或返回 null，本次不显示引导（不影响插件其余功能）");
                return false;
            }

            window.Closed += OnWizardClosed;
            _wizardWindow = window;
            window.Show();
            _logger.LogInformation("首次启动引导窗口已显示");
            return true;
        }
        catch (Exception ex)
        {
            // 窗口创建/显示失败：不崩溃，引导可在设置页再次尝试
            _wizardWindow = null;
            _logger.LogError(ex, "创建/显示首次启动引导窗口失败（不影响插件其余功能）");
            return false;
        }
    }

    private void OnWizardClosed(object? sender, EventArgs e)
    {
        if (sender is IFirstRunWizardWindow window)
        {
            window.Closed -= OnWizardClosed;
        }

        _wizardWindow = null;
        _logger.LogInformation("首次启动引导窗口已关闭");
    }
}

/// <summary>
/// 宿主启动钩子（模块 9）：启动后检测首次启动并自动弹出引导窗口（fire-and-forget）。
/// 检测/调度任何一步失败只记日志，不阻断宿主启动与插件其余功能。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FirstRunStartupService(IFirstRunService firstRun, ILogger<FirstRunStartupService>? logger = null)
    : IHostedService
{
    private readonly IFirstRunService _firstRun = firstRun;
    private readonly ILogger _logger = logger ?? NullLogger<FirstRunStartupService>.Instance;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_firstRun.IsFirstRun)
            {
                _logger.LogDebug("非首次启动（FirstRunCompleted=true），跳过引导");
                return Task.CompletedTask;
            }

            // fire-and-forget：引导失败只记日志，不阻断宿主启动
            _ = ShowSafelyAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "首次启动检测失败（不影响插件其余功能）");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ShowSafelyAsync()
    {
        try
        {
            await _firstRun.ShowWizardAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "自动弹出首次启动引导失败（不影响插件其余功能）");
        }
    }
}

// ==========================================================================================
// 首次启动引导流程（纯逻辑：步骤顺序 / 每步校验 / 写入设置 / 完成页汇总文案）
//
// 为什么要单独抽出来：引导要"把必填参数配全 + 教会用户实际怎么用"，步骤多、校验多、文案多，
// 全塞进窗口代码里既没法单测也容易各处文案对不上。这里全部是纯逻辑（不引用任何界面类型），
// 窗口只负责把控件值读进来、按校验结果推进、把汇总文案显示出来。
//
// 唯一事实来源（避免文案与实现脱节）：
//  · 群白名单留空的真实含义读自 MessageIngestPipeline：非空只收白名单群；空 = 接收全部已接入群
//    （并记一条告警日志），即"留空 = 不限制"，不是"不接管任何群"。
//  · 整理并发送的目标群与消息接管白名单相互独立（HomeworkSendService / ConnectionSettings 注释）。
//  · NapCat 模式的群号是纯数字，官方模式是群 OpenID（HomeworkSendService 显式校验并给出提示）。
// ==========================================================================================

/// <summary>
/// 引导步骤（枚举值即展示顺序）：
/// 先配"必须配才能用"的连接 → 接管范围 → 发送目标，再配分类 / 悬浮窗 / 文件与保留，
/// 然后教用户日常怎么用，最后给出"已配置 / 未配置 + 去哪补"的汇总。
/// </summary>
public enum FirstRunWizardStep
{
    /// <summary>欢迎：一句话说明插件做什么、接下来会配哪些东西。</summary>
    Welcome = 0,

    /// <summary>连接方式：QQ 官方机器人 / NapCat 二选一 + 对应参数。</summary>
    Connection = 1,

    /// <summary>消息接管范围：群白名单（留空 = 不限制）。</summary>
    IngestScope = 2,

    /// <summary>发送目标群：作业清单"整理并发送"发到哪个群。</summary>
    SendTargets = 3,

    /// <summary>通知、作业与学科：识别模式、关键词、学科前缀。</summary>
    Classification = 4,

    /// <summary>悬浮窗：五个窗口是否显示、是否置顶、上课自动弹出。</summary>
    Overlays = 5,

    /// <summary>文件与保留：下载目录、上限、保留天数。</summary>
    FilesRetention = 6,

    /// <summary>日常怎么用：照着做一遍的操作手册（无需配置）。</summary>
    Usage = 7,

    /// <summary>完成页：已配置 / 未配置（含跳过的项）+ 去哪里补。</summary>
    Finish = 8
}

/// <summary>
/// 引导步骤的静态文案与按钮语义（窗口只按这些值渲染，测试可直接断言）：
/// 标题、跳过后去哪里补、按钮文字、是否需要保存、保存后的"怎么验证"提示。
/// </summary>
public static class FirstRunWizardSteps
{
    private static readonly FirstRunWizardStep[] OrderedSteps =
    [
        FirstRunWizardStep.Welcome,
        FirstRunWizardStep.Connection,
        FirstRunWizardStep.IngestScope,
        FirstRunWizardStep.SendTargets,
        FirstRunWizardStep.Classification,
        FirstRunWizardStep.Overlays,
        FirstRunWizardStep.FilesRetention,
        FirstRunWizardStep.Usage,
        FirstRunWizardStep.Finish
    ];

    /// <summary>全部步骤（顺序即展示顺序）。</summary>
    public static IReadOnlyList<FirstRunWizardStep> Order => OrderedSteps;

    /// <summary>步骤标题（窗口顶部"第 N / M 步 · 标题"用）。</summary>
    public static string Title(FirstRunWizardStep step) => step switch
    {
        FirstRunWizardStep.Welcome => "欢迎",
        FirstRunWizardStep.Connection => "连接方式",
        FirstRunWizardStep.IngestScope => "接管哪些群",
        FirstRunWizardStep.SendTargets => "作业清单发到哪",
        FirstRunWizardStep.Classification => "通知、作业与学科",
        FirstRunWizardStep.Overlays => "悬浮窗",
        FirstRunWizardStep.FilesRetention => "文件与保留",
        FirstRunWizardStep.Usage => "日常怎么用",
        _ => "完成"
    };

    /// <summary>该步未配置时的补配位置（完成页"去哪里补"与跳过提示共用）。</summary>
    public static string MakeUpHint(FirstRunWizardStep step) => step switch
    {
        FirstRunWizardStep.Connection => "「CyberTechRep 连接」设置页",
        FirstRunWizardStep.IngestScope => "「CyberTechRep 连接」设置页的「群白名单」",
        FirstRunWizardStep.SendTargets => "「CyberTechRep 连接」设置页的「作业清单发送目标群」",
        FirstRunWizardStep.Classification => "「CyberTechRep 词表编辑」页",
        FirstRunWizardStep.Overlays => "「CyberTechRep 悬浮窗」设置页",
        FirstRunWizardStep.FilesRetention => "「CyberTechRep 文件」设置页",
        _ => "ClassIsland 设置 → CyberTechRep 分组"
    };

    /// <summary>该步是否允许"跳过这一步"（使用说明与完成页无需跳过）。</summary>
    public static bool CanSkip(FirstRunWizardStep step) =>
        step is not (FirstRunWizardStep.Usage or FirstRunWizardStep.Finish);

    /// <summary>欢迎页的跳过 = 整个引导都不看（直接标记完成并关窗）。</summary>
    public static bool SkipSkipsWholeWizard(FirstRunWizardStep step) => step == FirstRunWizardStep.Welcome;

    /// <summary>主按钮文字。</summary>
    public static string NextButtonText(FirstRunWizardStep step) => step switch
    {
        FirstRunWizardStep.Usage => "完成",
        FirstRunWizardStep.Finish => "关闭",
        _ => "下一步"
    };

    /// <summary>次要按钮（跳过）文字。</summary>
    public static string SkipButtonText(FirstRunWizardStep step) =>
        SkipSkipsWholeWizard(step) ? "跳过引导（稍后再配）" : "跳过这一步";

    /// <summary>该步是否有需要写回设置的输入（欢迎/使用说明/完成页不需要）。</summary>
    public static bool NeedsSave(FirstRunWizardStep step) => step is
        FirstRunWizardStep.Connection or FirstRunWizardStep.IngestScope or FirstRunWizardStep.SendTargets
        or FirstRunWizardStep.Classification or FirstRunWizardStep.Overlays or FirstRunWizardStep.FilesRetention;

    /// <summary>
    /// 该步保存成功后的提示（含"怎么验证"）：让用户立刻知道配好了没有、在哪里能看到效果。
    /// </summary>
    public static string SavedMessage(FirstRunWizardStep step) => step switch
    {
        FirstRunWizardStep.Connection =>
            "已保存连接设置。怎么验证：让群里发一条消息，然后打开「CyberTechRep 维护」页，展开「排错面板」点「刷新」——" +
            "「最近消息」里能看到这条消息就说明接通了；看不到就看同一个面板里的连接状态。",
        FirstRunWizardStep.IngestScope =>
            "已保存消息接管范围。怎么验证：在群里发一条消息，排错面板的「最近消息」里出现它就说明这个群被接管了；" +
            "没出现就回来检查群号 / 群 OpenID 是否填对。",
        FirstRunWizardStep.SendTargets =>
            "已保存发送设置。怎么验证：作业悬浮窗里出现作业后，点左下角「整理并发送」，确认窗里会写明要发到几个群，" +
            "发送结果也会直接显示在窗口上。",
        FirstRunWizardStep.Classification =>
            "已保存。怎么验证：随便发一条带「作业」字样的消息，看它是否出现在作业悬浮窗里、并且归到了正确学科。",
        FirstRunWizardStep.Overlays =>
            "已保存。怎么验证：桌面上马上会按刚才的选择显示或隐藏对应的悬浮窗（被其他窗口挡住时按 Win+D 显示桌面就能看到）。",
        FirstRunWizardStep.FilesRetention =>
            "已保存。怎么验证：在群里发一个文件，下载完成后打开「CyberTechRep 关于」页点「打开数据目录」，" +
            "在下载目录里就能看到它按学科 / 日期放好了。",
        _ => "已保存。"
    };
}

/// <summary>引导里反复出现的关键说明（集中一处，避免同一语义在多个步骤里写法不一致）。</summary>
public static class FirstRunWizardText
{
    /// <summary>群白名单留空的真实含义（读自接入管道：留空 = 接收全部已接入群）。</summary>
    public const string WhitelistEmptyMeaning =
        "群白名单留空 = 不限制：机器人已经在的每个群，消息都会被接管（插件不会自己去加群）。" +
        "刚上手可以留空先试试；长期使用建议只填班级群，别把无关群的消息也收进来。";

    /// <summary>白名单与发送目标相互独立。</summary>
    public const string TargetGroupsIndependent =
        "「群白名单」和「发送目标群」是两件独立的事：白名单决定「收哪些群的消息」，这里决定「把作业清单发到哪个群」；" +
        "改白名单不会影响发送目标。";

    /// <summary>悬浮窗层级语义（默认钉桌面层 vs 置顶）。</summary>
    public const string OverlayLayering =
        "悬浮窗默认钉在桌面最底层：其他窗口都会挡住它，按 Win+D 显示桌面也不会消失；打开「置顶」才会浮在所有窗口之上。" +
        "「固定」= 不能拖动和缩放；「鼠标穿透」= 鼠标点击直接穿过这个窗口。";

    /// <summary>换连接方式后需要重启（连接设置页同口径：切换模式不自动重建连接）。</summary>
    public const string RestartAfterModeChange =
        "换过连接方式（QQ 官方机器人 ↔ NapCat）后，要重启一次 ClassIsland，才会按新的方式连接。";

    /// <summary>重新打开引导的位置。</summary>
    public const string ReopenWizardHint =
        "以后想再看一遍这份引导：「CyberTechRep 维护」页 →「首次启动引导」→「重新打开引导」。";

    /// <summary>关键词修改位置（关键词实际生效值来自词表编辑页保存的文件）。</summary>
    public const string KeywordEditHint =
        "通知 / 作业关键词在「CyberTechRep 词表编辑」页 →「消息分类关键词」里修改，保存后立即生效。";

    /// <summary>官方平台群 OpenID 的获取方式（与 NapCat 群号区分）。</summary>
    public const string OfficialGroupIdSource =
        "怎么拿到群 OpenID：把机器人拉进群后，在群里发一条消息，然后打开「CyberTechRep 维护」页 →「排错面板」→「刷新」，" +
        "「最近消息」里 group= 后面那串值就是，复制进来即可（官方模式的群 OpenID 不是群号）。";

    /// <summary>NapCat 群号的获取方式。</summary>
    public const string NapCatGroupIdSource =
        "怎么拿到群号：NapCat 模式下群号就是平时说的 QQ 群号（纯数字），可以在 NapCat 的 WebUI 或日志里看到，" +
        "也可以从排错面板「最近消息」里的 group= 值直接复制。";

    /// <summary>
    /// 引导收尾会关掉「启动 NapCat 后自动打开 WebUI」：引导里已经带用户打开过一次登录页面，
    /// 以后每次开机都弹浏览器反而打扰；需要时到「连接」页点「打开 WebUI」。
    /// </summary>
    public const string WebUiAutoOpenClosedOnFinish =
        "已关闭「启动 NapCat 后自动打开 WebUI」：引导里已经带你打开过一次登录页面，以后需要时到「CyberTechRep 连接」页点「打开 WebUI」。";
}

/// <summary>单步校验结果：<see cref="Errors"/> 非空 = 阻止进入下一步（可点「跳过这一步」）；<see cref="Notes"/> 只提示。</summary>
public sealed record FirstRunWizardValidation(IReadOnlyList<string> Errors, IReadOnlyList<string> Notes)
{
    /// <summary>无错误无提示。</summary>
    public static FirstRunWizardValidation Pass { get; } = new([], []);

    /// <summary>是否通过必填校验。</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>界面提示区文本（错误优先；都没有则为空串）。</summary>
    public string ToFeedbackText() => Errors.Count > 0
        ? string.Join("\n", Errors)
        : string.Join("\n", Notes);
}

/// <summary>
/// 引导窗口的全部输入（窗口控件 ↔ 纯逻辑的中转）：
/// 默认值 = 出厂设置，<see cref="FirstRunWizardFlow.FromSettings"/> 用当前设置覆盖（重新打开引导时预填、不丢已有值）。
/// </summary>
public sealed class FirstRunWizardInput
{
    // ---- 连接方式 ----

    /// <summary>接入方式：QQ 官方机器人 / NapCat。</summary>
    public MessageConnectionMode Mode { get; set; } = MessageConnectionMode.Official;

    public string AppId { get; set; } = "";

    /// <summary>AppSecret 明文（仅在内存与保存时使用；写回设置时经 Protect 加密）。</summary>
    public string AppSecretPlain { get; set; } = "";

    /// <summary>设置里已有保存过的 AppSecret（预填解密失败时也不能误判为"没配"）。</summary>
    public bool HasSavedAppSecret { get; set; }

    public string ApiBase { get; set; } = "";

    public string TokenApiUrl { get; set; } = "";

    public string NapCatExePath { get; set; } = "";

    public string NapCatWorkDirectory { get; set; } = "";

    public string NapCatWsUrl { get; set; } = "";

    public int NapCatReversePort { get; set; } = 3001;

    /// <summary>NapCat access token 明文（同上：写回时经 Protect 加密）。</summary>
    public string NapCatAccessTokenPlain { get; set; } = "";

    /// <summary>设置里已有保存过的 NapCat access token。</summary>
    public bool HasSavedNapCatAccessToken { get; set; }

    public NapCatRunMode NapCatRunMode { get; set; } = NapCatRunMode.Headless;

    public NapCatLoginMode NapCatLoginMode { get; set; } = NapCatLoginMode.ScanQrCode;

    public string NapCatQuickLoginQQ { get; set; } = "";

    public bool NapCatAutoStart { get; set; }

    // ---- 消息接管范围 / 发送目标 ----

    public IReadOnlyList<string> GroupWhitelist { get; set; } = [];

    public IReadOnlyList<string> TargetGroupOpenIds { get; set; } = [];

    public bool HomeworkSendEnabled { get; set; } = true;

    // ---- 通知、作业与学科 ----

    public SubjectRecognitionMode SubjectRecognitionMode { get; set; } = SubjectRecognitionMode.MemberSelection;

    public bool SelectionWindowEnabled { get; set; } = true;

    public bool NoticeSubjectPrefix { get; set; } = true;

    /// <summary>当前生效的通知关键词（只读展示，修改在词表编辑页）。</summary>
    public IReadOnlyList<string> NoticeKeywords { get; set; } = [];

    /// <summary>当前生效的作业关键词（只读展示）。</summary>
    public IReadOnlyList<string> HomeworkKeywords { get; set; } = [];

    // ---- 悬浮窗 ----

    public bool CircleVisible { get; set; } = true;

    public bool NoticeVisible { get; set; } = true;

    public bool HomeworkVisible { get; set; } = true;

    public bool FilesVisible { get; set; }

    public bool ImageVisible { get; set; }

    public bool CircleTopmost { get; set; }

    public bool NoticeTopmost { get; set; }

    public bool HomeworkTopmost { get; set; }

    public bool FilesTopmost { get; set; }

    public bool ImageTopmost { get; set; }

    public bool AutoOpenWithClass { get; set; }

    public int AutoOpenDelaySeconds { get; set; }

    // ---- 文件与保留 ----

    public string DownloadRoot { get; set; } = "下载文件";

    public bool GroupBySubject { get; set; } = true;

    public long MaxDiskUsageMb { get; set; } = 2048;

    /// <summary>磁盘到上限时的处理：false = 停止接收新文件并告警；true = 清理最旧文件。</summary>
    public bool CleanupOldestWhenFull { get; set; }

    public long MaxFileSizeMb { get; set; } = 512;

    public int NoticesRetentionDays { get; set; }

    public int HomeworkRetentionDays { get; set; }
}

/// <summary>完成页一行汇总：<see cref="Text"/> 是内容，<see cref="MakeUpHint"/> 非空 = 去哪里补。</summary>
public sealed record FirstRunWizardSummaryLine(string Text, string MakeUpHint)
{
    /// <summary>展示用整行（未配置项带上"到哪里补"）。</summary>
    public string ToLine() => string.IsNullOrEmpty(MakeUpHint) ? $"• {Text}" : $"• {Text} → 到{MakeUpHint}补";
}

/// <summary>完成页汇总：已配置 / 未配置（含跳过的项）+ 提醒。</summary>
public sealed record FirstRunWizardSummary(
    IReadOnlyList<FirstRunWizardSummaryLine> Configured,
    IReadOnlyList<FirstRunWizardSummaryLine> Missing,
    IReadOnlyList<string> Notes)
{
    /// <summary>是否有漏配项（完成页据此决定"可以开始用了"还是"还差几项"）。</summary>
    public bool HasMissing => Missing.Count > 0;

    /// <summary>汇总全文（完成页直接显示这一段，用户可整段复制）。</summary>
    public string ToPlainText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("已配置：");
        if (Configured.Count == 0)
        {
            builder.AppendLine("• （暂无）");
        }

        foreach (var line in Configured)
        {
            builder.AppendLine(line.ToLine());
        }

        builder.AppendLine();
        builder.AppendLine(HasMissing ? "还没配好（可以稍后补）：" : "还没配好：无，全部配好了。");
        foreach (var line in Missing)
        {
            builder.AppendLine(line.ToLine());
        }

        if (Notes.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("提醒：");
            foreach (var note in Notes)
            {
                builder.AppendLine($"• {note}");
            }
        }

        return builder.ToString().TrimEnd();
    }
}

/// <summary>
/// 引导流程（纯逻辑，窗口只做渲染与绑定）：
/// 步骤推进 / 回退 / 跳过、每步必填校验、把输入写回设置、完成页汇总。
/// 步骤语义（与既有 <c>FirstRunCompleted</c> 约定一致）：完成或跳过都会由窗口调用
/// <see cref="IFirstRunService.MarkCompletedAsync"/>，已完成的用户不会被重复弹窗，
/// 「维护」页的「重新打开引导」入口随时可再次唤出。
/// </summary>
public sealed class FirstRunWizardFlow
{
    private readonly HashSet<FirstRunWizardStep> _skipped = [];
    private int _index;

    /// <summary>当前步骤。</summary>
    public FirstRunWizardStep Current => FirstRunWizardSteps.Order[_index];

    /// <summary>当前步骤序号（1 起，用于"第 N / M 步"）。</summary>
    public int StepNumber => _index + 1;

    /// <summary>步骤总数。</summary>
    public int StepCount => FirstRunWizardSteps.Order.Count;

    /// <summary>是否可回退（第 1 步不可）。</summary>
    public bool CanGoBack => _index > 0;

    /// <summary>当前步骤是否可跳过。</summary>
    public bool CanSkip => FirstRunWizardSteps.CanSkip(Current);

    /// <summary>是否已到完成页。</summary>
    public bool AtFinish => Current == FirstRunWizardStep.Finish;

    /// <summary>被跳过的步骤（完成页汇总"含跳过的项"用）。</summary>
    public IReadOnlyCollection<FirstRunWizardStep> SkippedSteps => _skipped;

    /// <summary>前进一步（已是最后一步则不动）。</summary>
    public FirstRunWizardStep Next()
    {
        if (_index < StepCount - 1)
        {
            _index++;
        }

        return Current;
    }

    /// <summary>后退一步（已是第一步则不动）。</summary>
    public FirstRunWizardStep Back()
    {
        if (_index > 0)
        {
            _index--;
        }

        return Current;
    }

    /// <summary>跳过当前步骤：记下"这一步被跳过"（内容保持原样）并前进一步。</summary>
    public FirstRunWizardStep SkipStep()
    {
        if (!CanSkip)
        {
            return Current;
        }

        _skipped.Add(Current);
        return Next();
    }

    /// <summary>指定步骤是否被跳过。</summary>
    public bool IsSkipped(FirstRunWizardStep step) => _skipped.Contains(step);

    /// <summary>把当前设置读成引导输入（重新打开引导时预填；密钥明文由调用方解密后传入）。</summary>
    public static FirstRunWizardInput FromSettings(AppSettings settings, string appSecretPlain = "",
        string napCatAccessTokenPlain = "")
    {
        ArgumentNullException.ThrowIfNull(settings);
        var connection = settings.Connection;
        var overlays = settings.Overlays;
        var files = settings.Files;
        var maintenance = settings.Maintenance;
        var classification = settings.Classification;
        var recognition = settings.SubjectRecognition;

        return new FirstRunWizardInput
        {
            Mode = connection.Mode,
            AppId = connection.AppId,
            AppSecretPlain = appSecretPlain,
            HasSavedAppSecret = !string.IsNullOrEmpty(connection.AppSecretProtected),
            ApiBase = connection.ApiBase,
            TokenApiUrl = connection.TokenApiUrl,
            NapCatExePath = connection.NapCatExePath,
            NapCatWorkDirectory = connection.NapCatWorkDirectory,
            NapCatWsUrl = connection.NapCatWsUrl,
            NapCatReversePort = connection.NapCatReversePort,
            NapCatAccessTokenPlain = napCatAccessTokenPlain,
            HasSavedNapCatAccessToken = !string.IsNullOrEmpty(connection.NapCatAccessTokenProtected),
            NapCatRunMode = connection.NapCatRunMode,
            NapCatLoginMode = connection.NapCatLoginMode,
            NapCatQuickLoginQQ = connection.NapCatQuickLoginQQ,
            NapCatAutoStart = connection.NapCatAutoStart,
            GroupWhitelist = NormalizeLines(connection.GroupWhitelist),
            TargetGroupOpenIds = NormalizeLines(connection.TargetGroupOpenIds),
            HomeworkSendEnabled = connection.HomeworkSendEnabled,
            SubjectRecognitionMode = recognition.Mode,
            SelectionWindowEnabled = recognition.SelectionWindowEnabled,
            NoticeSubjectPrefix = classification.NoticeSubjectPrefix,
            NoticeKeywords = NormalizeLines(classification.NoticeKeywords),
            HomeworkKeywords = NormalizeLines(classification.HomeworkKeywords),
            CircleVisible = overlays.Circle.Visible,
            NoticeVisible = overlays.Notice.Visible,
            HomeworkVisible = overlays.Homework.Visible,
            FilesVisible = overlays.Files.Visible,
            ImageVisible = overlays.Image.Visible,
            CircleTopmost = overlays.Circle.Topmost,
            NoticeTopmost = overlays.Notice.Topmost,
            HomeworkTopmost = overlays.Homework.Topmost,
            FilesTopmost = overlays.Files.Topmost,
            ImageTopmost = overlays.Image.Topmost,
            AutoOpenWithClass = overlays.SubjectCircle.AutoOpenWithClass,
            AutoOpenDelaySeconds = overlays.SubjectCircle.AutoOpenDelaySeconds,
            DownloadRoot = files.DownloadRoot,
            GroupBySubject = files.GroupBySubject,
            MaxDiskUsageMb = files.MaxDiskUsageMb,
            CleanupOldestWhenFull = files.CleanupPolicy == 1,
            MaxFileSizeMb = files.MaxFileSizeMb,
            NoticesRetentionDays = maintenance.NoticesRetentionDays,
            HomeworkRetentionDays = maintenance.HomeworkRetentionDays
        };
    }

    /// <summary>
    /// 单步必填校验。<see cref="FirstRunWizardValidation.Errors"/> 非空时窗口不前进（用户可点「跳过这一步」），
    /// <see cref="FirstRunWizardValidation.Notes"/> 是"当前状态"提示（如白名单留空的真实含义）。
    /// </summary>
    public static FirstRunWizardValidation Validate(FirstRunWizardStep step, FirstRunWizardInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new List<string>();
        var notes = new List<string>();

        switch (step)
        {
            case FirstRunWizardStep.Connection:
                ValidateConnection(input, errors, notes);
                break;

            case FirstRunWizardStep.IngestScope:
            {
                var groups = NormalizeLines(input.GroupWhitelist);
                if (input.Mode == MessageConnectionMode.NapCat)
                {
                    var invalid = groups.FirstOrDefault(group => !IsDigits(group));
                    if (invalid is not null)
                    {
                        errors.Add($"NapCat 模式的群白名单要填群号（纯数字，例如 123456789），「{invalid}」看起来不是群号。" +
                                   $"如果这个群是官方机器人模式，请先在第一步把连接方式改成「QQ 官方机器人」。");
                    }
                }

                notes.Add(groups.Count == 0
                    ? FirstRunWizardText.WhitelistEmptyMeaning
                    : $"当前只接管 {groups.Count} 个群：{string.Join("、", groups.Take(3))}{(groups.Count > 3 ? "…" : "")}");
                notes.Add(input.Mode == MessageConnectionMode.NapCat
                    ? FirstRunWizardText.NapCatGroupIdSource
                    : FirstRunWizardText.OfficialGroupIdSource);
                break;
            }

            case FirstRunWizardStep.SendTargets:
            {
                var targets = NormalizeLines(input.TargetGroupOpenIds);
                if (input.HomeworkSendEnabled && targets.Count == 0)
                {
                    errors.Add("已经打开「整理并发送」，但目标群是空的：这样一来点「发送」会直接失败并提示未配置目标群。" +
                               "请填上目标群；暂时不想发就点「跳过这一步」，或在这里把「整理并发送」关掉（关掉后作业悬浮窗不显示发送入口）。");
                }

                if (input.Mode == MessageConnectionMode.NapCat)
                {
                    var invalid = targets.FirstOrDefault(target => !IsDigits(target));
                    if (invalid is not null)
                    {
                        errors.Add($"NapCat 模式的发送目标群要填群号（纯数字，例如 123456789），「{invalid}」看起来不是群号。");
                    }

                    notes.Add("NapCat 模式填群号（纯数字）；QQ 官方机器人模式填群 OpenID。");
                }
                else
                {
                    notes.Add("官方机器人模式填群 OpenID（不是群号）：" + FirstRunWizardText.OfficialGroupIdSource);
                }

                notes.Add(FirstRunWizardText.TargetGroupsIndependent);
                notes.Add("发送用的还是同一个机器人 / QQ 账号，所以这个账号必须已经在目标群里。");
                break;
            }

            case FirstRunWizardStep.Classification:
                notes.Add("关键词判定规则：哪边命中的关键词多就算哪边；两边一样多或都没命中，按通知处理（不会丢消息）。");
                notes.Add(FirstRunWizardText.KeywordEditHint);
                break;

            case FirstRunWizardStep.Overlays:
                notes.Add(AnyOverlayVisible(input)
                    ? $"当前会显示：{string.Join("、", VisibleOverlayNames(input))}。"
                    : "当前五个窗口全部关掉了：消息照常收、文件照常归档，但桌面上什么都不会显示（完成页会再提醒一次）。");
                notes.Add(FirstRunWizardText.OverlayLayering);
                break;

            case FirstRunWizardStep.FilesRetention:
                if (string.IsNullOrWhiteSpace(input.DownloadRoot))
                {
                    errors.Add("下载目录不能空：留空文件就没地方放了。填「下载文件」（默认，位于插件的「数据目录」下）或一个绝对路径（例如 D:\\班级文件）。");
                }

                if (input.MaxFileSizeMb <= 0)
                {
                    errors.Add("单文件大小上限要大于 0 MB：否则任何文件都会被拒绝接收。");
                }

                if (input.MaxDiskUsageMb < 0)
                {
                    errors.Add("磁盘占用上限不能是负数（0 = 不限）。");
                }

                if (input.NoticesRetentionDays < 0 || input.HomeworkRetentionDays < 0)
                {
                    errors.Add("保留天数不能是负数（0 = 永久保留）。");
                }

                if (input.MaxDiskUsageMb > 0 && input.MaxFileSizeMb > input.MaxDiskUsageMb)
                {
                    notes.Add("单文件上限比磁盘占用上限还大：单个文件就可能把磁盘用到上限，建议把磁盘上限调大或把单文件上限调小。");
                }

                notes.Add("默认「下载文件」在插件的「数据目录」里：在「CyberTechRep 关于」页可以一键打开数据目录看实际位置。");
                break;
        }

        return errors.Count == 0 && notes.Count == 0
            ? FirstRunWizardValidation.Pass
            : new FirstRunWizardValidation(errors, notes);
    }

    /// <summary>
    /// 把某一步的输入写回设置对象（调用方随后必须 <c>SaveAsync</c> 落盘；<paramref name="protect"/> 传
    /// <c>ISettingsService.Protect</c> 加密密钥，测试可传恒等函数）。密钥留空时不覆盖已保存的值。
    /// </summary>
    public static void ApplyTo(FirstRunWizardStep step, FirstRunWizardInput input, AppSettings settings,
        Func<string, string>? protect = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(settings);
        var protector = protect ?? (plain => plain);
        var connection = settings.Connection;

        switch (step)
        {
            case FirstRunWizardStep.Connection:
                connection.Mode = input.Mode;
                if (input.Mode == MessageConnectionMode.NapCat)
                {
                    connection.NapCatExePath = input.NapCatExePath.Trim();
                    connection.NapCatWorkDirectory = input.NapCatWorkDirectory.Trim();
                    connection.NapCatWsUrl = input.NapCatWsUrl.Trim();
                    connection.NapCatReversePort = Math.Clamp(input.NapCatReversePort, 0, 65535);
                    if (!string.IsNullOrEmpty(input.NapCatAccessTokenPlain))
                    {
                        connection.NapCatAccessTokenProtected = protector(input.NapCatAccessTokenPlain);
                    }

                    connection.NapCatRunMode = input.NapCatRunMode;
                    connection.NapCatLoginMode = input.NapCatLoginMode;
                    connection.NapCatQuickLoginQQ = input.NapCatQuickLoginQQ.Trim();
                    connection.NapCatAutoStart = input.NapCatAutoStart;
                }
                else
                {
                    connection.AppId = input.AppId.Trim();
                    if (!string.IsNullOrEmpty(input.AppSecretPlain))
                    {
                        connection.AppSecretProtected = protector(input.AppSecretPlain);
                    }

                    // 地址留空 = 官方默认地址（避免写空串后连不上）
                    var defaults = new ConnectionSettings();
                    connection.ApiBase = NormalizeOrDefault(input.ApiBase, defaults.ApiBase);
                    connection.TokenApiUrl = NormalizeOrDefault(input.TokenApiUrl, defaults.TokenApiUrl);
                }

                break;

            case FirstRunWizardStep.IngestScope:
                connection.GroupWhitelist = NormalizeLines(input.GroupWhitelist);
                break;

            case FirstRunWizardStep.SendTargets:
                connection.TargetGroupOpenIds = NormalizeLines(input.TargetGroupOpenIds);
                connection.HomeworkSendEnabled = input.HomeworkSendEnabled;
                break;

            case FirstRunWizardStep.Classification:
                settings.SubjectRecognition.Mode = input.SubjectRecognitionMode;
                settings.SubjectRecognition.SelectionWindowEnabled = input.SelectionWindowEnabled;
                settings.Classification.NoticeSubjectPrefix = input.NoticeSubjectPrefix;
                break;

            case FirstRunWizardStep.Overlays:
                settings.Overlays.Circle.Visible = input.CircleVisible;
                settings.Overlays.Notice.Visible = input.NoticeVisible;
                settings.Overlays.Homework.Visible = input.HomeworkVisible;
                settings.Overlays.Files.Visible = input.FilesVisible;
                settings.Overlays.Image.Visible = input.ImageVisible;
                settings.Overlays.Circle.Topmost = input.CircleTopmost;
                settings.Overlays.Notice.Topmost = input.NoticeTopmost;
                settings.Overlays.Homework.Topmost = input.HomeworkTopmost;
                settings.Overlays.Files.Topmost = input.FilesTopmost;
                settings.Overlays.Image.Topmost = input.ImageTopmost;
                settings.Overlays.SubjectCircle.AutoOpenWithClass = input.AutoOpenWithClass;
                settings.Overlays.SubjectCircle.AutoOpenDelaySeconds = Math.Clamp(input.AutoOpenDelaySeconds, -600, 600);
                break;

            case FirstRunWizardStep.FilesRetention:
                settings.Files.DownloadRoot = NormalizeOrDefault(input.DownloadRoot, new FileSettings().DownloadRoot);
                settings.Files.GroupBySubject = input.GroupBySubject;
                settings.Files.MaxDiskUsageMb = Math.Max(0, input.MaxDiskUsageMb);
                settings.Files.CleanupPolicy = input.CleanupOldestWhenFull ? 1 : 0;
                settings.Files.MaxFileSizeMb = Math.Max(1, input.MaxFileSizeMb);
                settings.Maintenance.NoticesRetentionDays = Math.Max(0, input.NoticesRetentionDays);
                settings.Maintenance.HomeworkRetentionDays = Math.Max(0, input.HomeworkRetentionDays);
                break;
        }
    }

    /// <summary>
    /// 引导收尾的固定处理（进入完成页 / 跳过整份引导 / 点「×」关闭三条路都要调用，随后由调用方保存）：
    /// 关掉「启动 NapCat 后自动打开 WebUI」——引导里已经带用户打开过一次登录页面，
    /// 不关的话以后每次开机都会弹浏览器。只动这一个设置，其余保持原样。
    /// </summary>
    public static void ApplyCompletionDefaults(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Connection.NapCatOpenWebUiOnStart = false;
    }

    /// <summary>通知/作业关键词的默认示例（与随包默认词表一致，供引导里给用户看）。</summary>
    public static IReadOnlyList<string> DefaultNoticeKeywords => new ClassificationSettings().NoticeKeywords;

    /// <summary>作业关键词的默认示例。</summary>
    public static IReadOnlyList<string> DefaultHomeworkKeywords => new ClassificationSettings().HomeworkKeywords;

    /// <summary>至少有一个悬浮窗会显示（全部关掉时完成页会提醒）。</summary>
    public static bool AnyOverlayVisible(FirstRunWizardInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.CircleVisible || input.NoticeVisible || input.HomeworkVisible
            || input.FilesVisible || input.ImageVisible;
    }

    /// <summary>当前会显示的悬浮窗名称（顺序固定：圆圈栏 / 通知 / 作业 / 学科文件 / 图片）。</summary>
    public static IReadOnlyList<string> VisibleOverlayNames(FirstRunWizardInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var names = new List<string>(5);
        if (input.CircleVisible)
        {
            names.Add("圆圈栏");
        }

        if (input.NoticeVisible)
        {
            names.Add("通知");
        }

        if (input.HomeworkVisible)
        {
            names.Add("作业");
        }

        if (input.FilesVisible)
        {
            names.Add("学科文件");
        }

        if (input.ImageVisible)
        {
            names.Add("图片");
        }

        return names;
    }

    /// <summary>连接是否已配到"能用"的程度（官方 = AppID + 密钥；NapCat = 可执行文件 + 一种连接方式）。</summary>
    public static bool IsConnectionConfigured(FirstRunWizardInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Mode == MessageConnectionMode.NapCat)
        {
            return !string.IsNullOrWhiteSpace(input.NapCatExePath) && HasNapCatTransport(input);
        }

        return !string.IsNullOrWhiteSpace(input.AppId)
            && (!string.IsNullOrWhiteSpace(input.AppSecretPlain) || input.HasSavedAppSecret);
    }

    /// <summary>
    /// 完成页汇总：已配置 / 未配置（<paramref name="skippedSteps"/> 里的项会标出"跳过"）+ 提醒。
    /// 未配置项都带"到哪里补"，供用户回头补配。
    /// </summary>
    public static FirstRunWizardSummary BuildSummary(FirstRunWizardInput input,
        IReadOnlyCollection<FirstRunWizardStep>? skippedSteps = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var skipped = skippedSteps ?? [];
        var configured = new List<FirstRunWizardSummaryLine>();
        var missing = new List<FirstRunWizardSummaryLine>();
        var notes = new List<string>();

        // ① 连接方式
        if (IsConnectionConfigured(input))
        {
            configured.Add(new(
                input.Mode == MessageConnectionMode.NapCat
                    ? $"连接方式：NapCat（已填可执行文件路径；运行方式：{DescribeRunMode(input.NapCatRunMode)}；" +
                      $"登录方式：{(input.NapCatLoginMode == NapCatLoginMode.QuickLoginQQ ? $"QQ 号快速登录（{input.NapCatQuickLoginQQ}）" : "扫码登录")}）"
                    : "连接方式：QQ 官方机器人（AppID 已填，机器人密钥已加密保存在本机）",
                ""));
        }
        else
        {
            missing.Add(new(
                SkipMark(skipped.Contains(FirstRunWizardStep.Connection)) + DescribeConnectionGap(input),
                FirstRunWizardSteps.MakeUpHint(FirstRunWizardStep.Connection)));
        }

        // ② 消息接管范围（留空 = 不限制，是合法选择，不算漏配，但必须让用户知道）
        var groups = NormalizeLines(input.GroupWhitelist);
        if (groups.Count > 0)
        {
            configured.Add(new($"消息接管范围：只接管 {groups.Count} 个群的消息", ""));
        }
        else
        {
            notes.Add((skipped.Contains(FirstRunWizardStep.IngestScope) ? "（这一步跳过了）" : "") +
                      "消息接管范围：群白名单是空的 = 不限制，机器人在的每个群都会被接管；建议补成班级群。" +
                      $"补配位置：{FirstRunWizardSteps.MakeUpHint(FirstRunWizardStep.IngestScope)}。");
        }

        // ③ 发送目标群
        var targets = NormalizeLines(input.TargetGroupOpenIds);
        if (!input.HomeworkSendEnabled)
        {
            notes.Add("整理并发送：已关闭，作业悬浮窗不会显示发送入口（想发作业清单时到「CyberTechRep 连接」页打开）。");
        }
        else if (targets.Count > 0)
        {
            configured.Add(new($"整理并发送：作业清单会发到 {targets.Count} 个目标群", ""));
        }
        else
        {
            missing.Add(new(
                SkipMark(skipped.Contains(FirstRunWizardStep.SendTargets)) +
                "整理并发送：开关开着但目标群是空的，点「发送」会直接失败",
                FirstRunWizardSteps.MakeUpHint(FirstRunWizardStep.SendTargets)));
        }

        // ④ 通知、作业与学科（都有默认值，直接算已配置；关键词修改在词表编辑页）
        configured.Add(new(
            $"通知、作业与学科：{(input.SubjectRecognitionMode == SubjectRecognitionMode.MemberSelection ? "按发送者绑定优先识别学科" : "只按关键词识别学科")}；" +
            $"未绑定学科选择窗：{(input.SelectionWindowEnabled ? "开" : "关")}；通知加学科前缀：{(input.NoticeSubjectPrefix ? "开" : "关")}",
            ""));

        // ⑤ 悬浮窗
        if (AnyOverlayVisible(input))
        {
            configured.Add(new($"悬浮窗：会显示 {string.Join("、", VisibleOverlayNames(input))}", ""));
        }
        else
        {
            missing.Add(new(
                SkipMark(skipped.Contains(FirstRunWizardStep.Overlays)) +
                "悬浮窗：五个窗口全关掉了，桌面上不会显示任何内容（消息照常收、文件照常归档）",
                FirstRunWizardSteps.MakeUpHint(FirstRunWizardStep.Overlays)));
        }

        // ⑥ 文件与保留
        configured.Add(new(
            $"文件与保留：下载到「{input.DownloadRoot}」{(input.GroupBySubject ? "（按学科分组）" : "（不按学科分组）")}；" +
            $"磁盘上限 {(input.MaxDiskUsageMb == 0 ? "不限" : $"{input.MaxDiskUsageMb} MB")}；单文件上限 {input.MaxFileSizeMb} MB；" +
            $"通知保留 {(input.NoticesRetentionDays == 0 ? "永久" : $"{input.NoticesRetentionDays} 天")}；" +
            $"作业保留 {(input.HomeworkRetentionDays == 0 ? "永久" : $"{input.HomeworkRetentionDays} 天")}",
            ""));

        // ---- 通用提醒 ----
        notes.Add(FirstRunWizardText.WebUiAutoOpenClosedOnFinish);
        notes.Add(FirstRunWizardText.RestartAfterModeChange);
        notes.Add(FirstRunWizardText.KeywordEditHint);
        notes.Add(FirstRunWizardText.ReopenWizardHint);
        if (skipped.Count > 0)
        {
            var skippedTitles = FirstRunWizardSteps.Order.Where(skipped.Contains).Select(FirstRunWizardSteps.Title);
            notes.Add($"被跳过的步骤（内容保持原样，可随时到设置页改）：{string.Join("、", skippedTitles)}。");
        }

        return new FirstRunWizardSummary(configured, missing, notes);
    }

    /// <summary>把多行输入规整成"去空白、去空行、去重（保持顺序）"的列表。</summary>
    public static IReadOnlyList<string> NormalizeLines(IEnumerable<string>? lines)
    {
        if (lines is null)
        {
            return [];
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            if (raw is null)
            {
                continue;
            }

            foreach (var part in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.Length > 0 && seen.Add(part))
                {
                    result.Add(part);
                }
            }
        }

        return result;
    }

    /// <summary>是否纯数字（NapCat 的群号 / QQ 号形态校验）。</summary>
    public static bool IsDigits(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().All(char.IsAsciiDigit);

    private static void ValidateConnection(FirstRunWizardInput input, List<string> errors, List<string> notes)
    {
        if (input.Mode == MessageConnectionMode.NapCat)
        {
            if (string.IsNullOrWhiteSpace(input.NapCatExePath))
            {
                errors.Add("还没填 NapCat 可执行文件路径：插件靠它一键启动 NapCat（你也可以自己启动 NapCat，只填下面的连接方式）。" +
                           "无头包填 napcat\\launcher-user.bat，有头包填 NapCatWinBootMain.exe 或 QQ.exe。" +
                           "填好后点「下一步」；暂时不想配就点「跳过这一步」，稍后在「CyberTechRep 连接」设置页补。");
            }

            if (!HasNapCatTransport(input))
            {
                errors.Add("NapCat 需要一种连接方式：填「正向 WS 地址」（例如 ws://127.0.0.1:3001），" +
                           "或者把「反向监听端口」设为 1~65535（让 NapCat 连进来）。两个都不填插件就连不上 NapCat。");
            }

            if (input.NapCatLoginMode == NapCatLoginMode.QuickLoginQQ && !IsDigits(input.NapCatQuickLoginQQ))
            {
                errors.Add("选了「QQ 号快速登录」，但 QQ 号没填或不是纯数字（例如 123456789）。" +
                           "新电脑第一次登录建议先用「扫码登录」，成功一次之后再用快速登录。");
            }

            if (!string.IsNullOrWhiteSpace(input.NapCatWsUrl)
                && !input.NapCatWsUrl.Trim().StartsWith("ws", StringComparison.OrdinalIgnoreCase))
            {
                notes.Add("正向 WS 地址通常以 ws:// 开头（例如 ws://127.0.0.1:3001）。");
            }

            notes.Add("NapCat 本体需要自己下载安装（插件不自带）；装好后可以在「CyberTechRep 连接」页一键启动、打开 WebUI。");
            notes.Add(FirstRunWizardText.RestartAfterModeChange);
            return;
        }

        if (string.IsNullOrWhiteSpace(input.AppId))
        {
            errors.Add("还没填 AppID：去 q.qq.com（QQ 机器人开放平台）注册并创建机器人，创建后就能看到 AppID。" +
                       "填好后点「下一步」；暂时不想配就点「跳过这一步」，稍后在「CyberTechRep 连接」设置页补。");
        }

        if (string.IsNullOrWhiteSpace(input.AppSecretPlain) && !input.HasSavedAppSecret)
        {
            errors.Add("还没填 AppSecret：它和 AppID 在同一个页面（机器人详情里可以重置查看）。" +
                       "AppSecret 只保存在本机、加密存放，也不会写进日志。");
        }

        notes.Add("机器人要被拉进班级群、并且有读群消息的权限，插件才收得到消息。");
        notes.Add("接口地址通常不用改：留空 = 用官方默认地址。");
        notes.Add(FirstRunWizardText.RestartAfterModeChange);
    }

    private static bool HasNapCatTransport(FirstRunWizardInput input) =>
        !string.IsNullOrWhiteSpace(input.NapCatWsUrl) || input.NapCatReversePort > 0;

    private static string DescribeConnectionGap(FirstRunWizardInput input)
    {
        if (input.Mode == MessageConnectionMode.NapCat)
        {
            if (string.IsNullOrWhiteSpace(input.NapCatExePath))
            {
                return "连接方式：NapCat 还没填可执行文件路径";
            }

            return "连接方式：NapCat 填了可执行文件路径，但既没填「正向 WS 地址」也没设「反向监听端口」，插件连不上它";
        }

        var missingAppId = string.IsNullOrWhiteSpace(input.AppId);
        var missingSecret = string.IsNullOrWhiteSpace(input.AppSecretPlain) && !input.HasSavedAppSecret;
        return (missingAppId, missingSecret) switch
        {
            (true, true) => "连接方式：QQ 官方机器人还没填 AppID 和 AppSecret",
            (true, false) => "连接方式：QQ 官方机器人还没填 AppID",
            _ => "连接方式：QQ 官方机器人还没填 AppSecret（机器人密钥）"
        };
    }

    private static string DescribeRunMode(NapCatRunMode mode) =>
        mode == NapCatRunMode.Framework ? "有头（显示 QQ 界面）" : "无头（不显示 QQ 界面）";

    private static string SkipMark(bool skipped) => skipped ? "（这一步跳过了）" : "";

    private static string NormalizeOrDefault(string? value, string defaultValue)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? defaultValue : trimmed;
    }
}
