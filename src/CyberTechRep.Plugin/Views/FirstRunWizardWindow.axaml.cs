using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CyberTechRep.Plugin.Services.FirstRun;
using CyberTechRep.Plugin.Services.MessageAccess;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;

namespace CyberTechRep.Plugin.Views;

/// <summary>
/// 首次启动引导窗口（Avalonia 无边框置顶窗，风格与悬浮窗一致：圆角 + 标题栏可拖拽）。
/// <para>
/// 步骤与文案全部来自纯逻辑 <see cref="FirstRunWizardFlow"/> / <see cref="FirstRunWizardSteps"/>
/// （可单测），本窗口只做三件事：把控件值读进 <see cref="FirstRunWizardInput"/>、
/// 按校验结果决定是否推进、把每步输入经 <c>ISettingsService.Current</c> + <c>SaveAsync</c> 落盘。
/// </para>
/// <para>
/// 步骤覆盖"必须先配 → 才能用 → 用起来怎么调"：欢迎 → 连接方式 → 接管哪些群 → 作业清单发到哪 →
/// 通知/作业与学科 → 悬浮窗 → 文件与保留 → 日常怎么用 → 完成汇总。
/// 完成或跳过都经 <see cref="IFirstRunService.MarkCompletedAsync"/> 落盘 <c>FirstRunCompleted</c>，
/// 之后不再自动弹出；「维护」设置页的「重新打开引导」可再次唤出（预填当前设置、不丢已有值）。
/// 任何一步失败只在窗口内提示，不崩溃。
/// </para>
/// </summary>
public partial class FirstRunWizardWindow : Window, IFirstRunWizardWindow
{
    /// <summary>等 WebUI 就绪的最长时间（秒）：超时后不再轮询，提示用户手动重试。</summary>
    private const int NapCatWebUiWaitSeconds = 60;

    private readonly ISettingsService? _settingsService;
    private readonly IFirstRunService? _firstRunService;
    private readonly NapCatRunnerService? _napCatRunner;
    private readonly FirstRunWizardFlow _flow = new();
    private FirstRunWizardInput? _input;

    /// <summary>引导里点过「启动 NapCat 并打开 WebUI」没有（没点过时离开第 1 步只提示，不自动启动）。</summary>
    private bool _napCatLaunchClicked;

    /// <summary>本次点「启动」后是否已经看到 NapCat 进入启动中 / 运行中（避免刚点完就被当成「已退出」）。</summary>
    private bool _napCatSawLaunchState;

    /// <summary>等 WebUI 就绪的轮询计时器（每秒一次；离开第 1 步或关窗即停）。</summary>
    private DispatcherTimer? _napCatPollTimer;

    /// <summary>本次轮询的截止时间（到点仍未就绪就停下并提示）。</summary>
    private DateTime _napCatPollDeadline;

    /// <summary>设计时/XAML 预览用；运行时走含服务构造。</summary>
    public FirstRunWizardWindow()
    {
        InitializeComponent();
        RenderStep();
    }

    /// <param name="napCatRunner">
    /// NapCat 一键启动服务（可选）。传入后第 1 步的「启动 NapCat 并打开 WebUI」才可用；
    /// 不传（旧调用 / 预览）时按钮会提示到「CyberTechRep 连接」页启动，其余引导流程不受影响。
    /// </param>
    public FirstRunWizardWindow(ISettingsService settingsService, IFirstRunService firstRunService,
        NapCatRunnerService? napCatRunner = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _firstRunService = firstRunService ?? throw new ArgumentNullException(nameof(firstRunService));
        _napCatRunner = napCatRunner;
        InitializeComponent();

        // 关窗即停掉 WebUI 等待计时器：不在后台空转
        Closed += (_, _) => StopNapCatPolling();

        LoadFromSettings();
        RenderStep();
    }

    // ============ 载入 / 读回控件值 ============

    /// <summary>用当前设置预填全部输入（重新打开引导时不丢已有值；密钥解密失败只回落默认值）。</summary>
    private void LoadFromSettings()
    {
        try
        {
            var settings = _settingsService!.Current;
            var connection = settings.Connection;
            _input = FirstRunWizardFlow.FromSettings(
                settings,
                _settingsService.Unprotect(connection.AppSecretProtected),
                _settingsService.Unprotect(connection.NapCatAccessTokenProtected));
        }
        catch (Exception ex)
        {
            // 预填失败不阻断引导：用默认值继续，用户仍可完成配置
            Debug.WriteLine($"引导预填设置失败（用默认值继续）：{ex.Message}");
            _input = new FirstRunWizardInput();
        }

        ApplyInputToControls();
    }

    /// <summary>输入 → 控件（预填 / 回退步骤时回显）。</summary>
    private void ApplyInputToControls()
    {
        if (_input is null)
        {
            return;
        }

        try
        {
            OfficialModeRadio.IsChecked = _input.Mode == MessageConnectionMode.Official;
            NapCatModeRadio.IsChecked = _input.Mode == MessageConnectionMode.NapCat;
            AppIdBox.Text = _input.AppId;
            SecretBox.Text = _input.AppSecretPlain;
            ApiBaseBox.Text = _input.ApiBase;
            TokenApiBox.Text = _input.TokenApiUrl;
            NapCatExeBox.Text = _input.NapCatExePath;
            NapCatWorkDirBox.Text = _input.NapCatWorkDirectory;
            NapCatWsBox.Text = _input.NapCatWsUrl;
            NapCatPortBox.Value = _input.NapCatReversePort;
            NapCatTokenBox.Text = _input.NapCatAccessTokenPlain;
            HeadlessRadio.IsChecked = _input.NapCatRunMode == NapCatRunMode.Headless;
            FrameworkRadio.IsChecked = _input.NapCatRunMode == NapCatRunMode.Framework;
            ScanQrRadio.IsChecked = _input.NapCatLoginMode == NapCatLoginMode.ScanQrCode;
            QuickLoginRadio.IsChecked = _input.NapCatLoginMode == NapCatLoginMode.QuickLoginQQ;
            QuickLoginQqBox.Text = _input.NapCatQuickLoginQQ;
            NapCatAutoStartSwitch.IsChecked = _input.NapCatAutoStart;

            GroupWhitelistBox.Text = LinesToText(_input.GroupWhitelist);
            TargetGroupsBox.Text = LinesToText(_input.TargetGroupOpenIds);
            HomeworkSendSwitch.IsChecked = _input.HomeworkSendEnabled;

            MemberSelectionRadio.IsChecked = _input.SubjectRecognitionMode == SubjectRecognitionMode.MemberSelection;
            KeywordOnlyRadio.IsChecked = _input.SubjectRecognitionMode == SubjectRecognitionMode.Keyword;
            SelectionWindowSwitch.IsChecked = _input.SelectionWindowEnabled;
            NoticePrefixSwitch.IsChecked = _input.NoticeSubjectPrefix;
            NoticeKeywordText.Text = FormatKeywords("通知", _input.NoticeKeywords,
                FirstRunWizardFlow.DefaultNoticeKeywords);
            HomeworkKeywordText.Text = FormatKeywords("作业", _input.HomeworkKeywords,
                FirstRunWizardFlow.DefaultHomeworkKeywords);

            CircleVisibleBox.IsChecked = _input.CircleVisible;
            NoticeVisibleBox.IsChecked = _input.NoticeVisible;
            HomeworkVisibleBox.IsChecked = _input.HomeworkVisible;
            FilesVisibleBox.IsChecked = _input.FilesVisible;
            ImageVisibleBox.IsChecked = _input.ImageVisible;
            CircleTopmostBox.IsChecked = _input.CircleTopmost;
            NoticeTopmostBox.IsChecked = _input.NoticeTopmost;
            HomeworkTopmostBox.IsChecked = _input.HomeworkTopmost;
            FilesTopmostBox.IsChecked = _input.FilesTopmost;
            ImageTopmostBox.IsChecked = _input.ImageTopmost;
            AutoOpenSwitch.IsChecked = _input.AutoOpenWithClass;
            AutoOpenDelayBox.Value = _input.AutoOpenDelaySeconds;

            DownloadRootBox.Text = _input.DownloadRoot;
            GroupBySubjectSwitch.IsChecked = _input.GroupBySubject;
            MaxDiskBox.Value = _input.MaxDiskUsageMb;
            CleanupPolicyBox.SelectedIndex = _input.CleanupOldestWhenFull ? 1 : 0;
            MaxFileBox.Value = _input.MaxFileSizeMb;
            NoticeRetentionBox.Value = _input.NoticesRetentionDays;
            HomeworkRetentionBox.Value = _input.HomeworkRetentionDays;

            UpdateModePanels();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"引导回填控件失败（保留表单当前值）：{ex.Message}");
        }
    }

    /// <summary>把当前步骤的控件值读进输入（只读本步控件，其余步骤保持已载入的值）。</summary>
    private void CaptureStep(FirstRunWizardStep step)
    {
        if (_input is null)
        {
            return;
        }

        switch (step)
        {
            case FirstRunWizardStep.Connection:
                _input.Mode = NapCatModeRadio.IsChecked == true
                    ? MessageConnectionMode.NapCat
                    : MessageConnectionMode.Official;
                _input.AppId = AppIdBox.Text ?? "";
                _input.AppSecretPlain = SecretBox.Text ?? "";
                _input.ApiBase = ApiBaseBox.Text ?? "";
                _input.TokenApiUrl = TokenApiBox.Text ?? "";
                _input.NapCatExePath = NapCatExeBox.Text ?? "";
                _input.NapCatWorkDirectory = NapCatWorkDirBox.Text ?? "";
                _input.NapCatWsUrl = NapCatWsBox.Text ?? "";
                _input.NapCatReversePort = ToInt(NapCatPortBox.Value, 3001);
                _input.NapCatAccessTokenPlain = NapCatTokenBox.Text ?? "";
                _input.NapCatRunMode = FrameworkRadio.IsChecked == true
                    ? NapCatRunMode.Framework
                    : NapCatRunMode.Headless;
                _input.NapCatLoginMode = QuickLoginRadio.IsChecked == true
                    ? NapCatLoginMode.QuickLoginQQ
                    : NapCatLoginMode.ScanQrCode;
                _input.NapCatQuickLoginQQ = QuickLoginQqBox.Text ?? "";
                _input.NapCatAutoStart = NapCatAutoStartSwitch.IsChecked == true;
                break;

            case FirstRunWizardStep.IngestScope:
                _input.GroupWhitelist = FirstRunWizardFlow.NormalizeLines([GroupWhitelistBox.Text ?? ""]);
                break;

            case FirstRunWizardStep.SendTargets:
                _input.TargetGroupOpenIds = FirstRunWizardFlow.NormalizeLines([TargetGroupsBox.Text ?? ""]);
                _input.HomeworkSendEnabled = HomeworkSendSwitch.IsChecked == true;
                break;

            case FirstRunWizardStep.Classification:
                _input.SubjectRecognitionMode = KeywordOnlyRadio.IsChecked == true
                    ? SubjectRecognitionMode.Keyword
                    : SubjectRecognitionMode.MemberSelection;
                _input.SelectionWindowEnabled = SelectionWindowSwitch.IsChecked == true;
                _input.NoticeSubjectPrefix = NoticePrefixSwitch.IsChecked == true;
                break;

            case FirstRunWizardStep.Overlays:
                _input.CircleVisible = CircleVisibleBox.IsChecked == true;
                _input.NoticeVisible = NoticeVisibleBox.IsChecked == true;
                _input.HomeworkVisible = HomeworkVisibleBox.IsChecked == true;
                _input.FilesVisible = FilesVisibleBox.IsChecked == true;
                _input.ImageVisible = ImageVisibleBox.IsChecked == true;
                _input.CircleTopmost = CircleTopmostBox.IsChecked == true;
                _input.NoticeTopmost = NoticeTopmostBox.IsChecked == true;
                _input.HomeworkTopmost = HomeworkTopmostBox.IsChecked == true;
                _input.FilesTopmost = FilesTopmostBox.IsChecked == true;
                _input.ImageTopmost = ImageTopmostBox.IsChecked == true;
                _input.AutoOpenWithClass = AutoOpenSwitch.IsChecked == true;
                _input.AutoOpenDelaySeconds = ToInt(AutoOpenDelayBox.Value, 0);
                break;

            case FirstRunWizardStep.FilesRetention:
                _input.DownloadRoot = DownloadRootBox.Text ?? "";
                _input.GroupBySubject = GroupBySubjectSwitch.IsChecked == true;
                _input.MaxDiskUsageMb = ToLong(MaxDiskBox.Value, 2048);
                _input.CleanupOldestWhenFull = CleanupPolicyBox.SelectedIndex == 1;
                _input.MaxFileSizeMb = ToLong(MaxFileBox.Value, 512);
                _input.NoticesRetentionDays = ToInt(NoticeRetentionBox.Value, 0);
                _input.HomeworkRetentionDays = ToInt(HomeworkRetentionBox.Value, 0);
                break;

            default:
                // 欢迎 / 日常使用说明 / 完成页没有需要保存的输入
                break;
        }
    }

    // ============ 按钮事件 ============

    private async void OnNextClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var step = _flow.Current;
            CaptureStep(step);

            if (_input is not null)
            {
                var validation = FirstRunWizardFlow.Validate(step, _input);
                if (!validation.IsValid)
                {
                    // 必填没填 / 填错：留在本步并给出明确提示（也可点「跳过这一步」稍后再配）
                    ShowMessage(validation.ToFeedbackText(), isError: true);
                    return;
                }
            }

            if (FirstRunWizardSteps.NeedsSave(step))
            {
                var saveError = await ApplyAndSaveStepAsync(step).ConfigureAwait(true);
                if (saveError is not null)
                {
                    ShowMessage($"保存失败：{saveError}（可以再点一次「下一步」重试，或先点「跳过这一步」稍后到设置页填写）",
                        isError: true);
                    return;
                }
            }

            if (step == FirstRunWizardStep.Usage)
            {
                // 进入完成页：先写收尾默认值（关闭 WebUI 自启动）并落盘，再标记引导完成，最后显示汇总
                await ApplyCompletionDefaultsAsync().ConfigureAwait(true);
                await MarkCompletedAsync().ConfigureAwait(true);
                _flow.Next();
                RenderFinish();
                return;
            }

            if (_flow.AtFinish)
            {
                Close();
                return;
            }

            _flow.Next();
            var message = FirstRunWizardSteps.NeedsSave(step) ? FirstRunWizardSteps.SavedMessage(step) : null;
            var launchHint = NapCatNotLaunchedHint(step);
            if (launchHint is not null)
            {
                message = string.IsNullOrEmpty(message) ? launchHint : $"{message}\n{launchHint}";
            }

            RenderStep(message);
        }
        catch (Exception ex)
        {
            ShowMessage($"操作失败：{ex.Message}", isError: true);
        }
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            // 回退不保存：把本步已填内容留在内存里，再回上一步继续看/改
            CaptureStep(_flow.Current);
            _flow.Back();
            ApplyInputToControls();
            RenderStep();
        }
        catch (Exception ex)
        {
            ShowMessage($"操作失败：{ex.Message}", isError: true);
        }
    }

    private async void OnSkipClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (FirstRunWizardSteps.SkipSkipsWholeWizard(_flow.Current))
            {
                // 欢迎页的跳过 = 整个引导都不看：写收尾默认值（关闭 WebUI 自启动）+ 标记完成，然后关窗
                await ApplyCompletionDefaultsAsync().ConfigureAwait(true);
                await MarkCompletedAsync().ConfigureAwait(true);
                Close();
                return;
            }

            var skipped = _flow.Current;
            _flow.SkipStep();
            RenderStep($"已跳过「{FirstRunWizardSteps.Title(skipped)}」：这一页没有保存，回头可以到" +
                       $"{FirstRunWizardSteps.MakeUpHint(skipped)}补上。");
        }
        catch (Exception ex)
        {
            ShowMessage($"操作失败：{ex.Message}", isError: true);
        }
    }

    /// <summary>标题栏「×」：与跳过引导同义（写收尾默认值 + 标记完成后关闭，之后不再自动弹出）。</summary>
    private async void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await ApplyCompletionDefaultsAsync().ConfigureAwait(true);
            await MarkCompletedAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"关闭引导时收尾失败：{ex.Message}");
        }

        try
        {
            Close();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"关闭引导窗口失败：{ex.Message}");
        }
    }

    private void OnConnectionModeChanged(object? sender, RoutedEventArgs e) => UpdateModePanels();

    private void OnLoginModeChanged(object? sender, RoutedEventArgs e) => UpdateModePanels();

    /// <summary>按连接方式 / 登录方式切换对应输入区的可见性。</summary>
    private void UpdateModePanels()
    {
        try
        {
            var napCat = NapCatModeRadio.IsChecked == true;
            OfficialPanel.IsVisible = !napCat;
            NapCatPanel.IsVisible = napCat;
            QuickLoginPanel.IsVisible = QuickLoginRadio.IsChecked == true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"切换连接方式输入区失败：{ex.Message}");
        }
    }

    // ============ 启动 NapCat 并打开 WebUI（第 1 步） ============

    /// <summary>
    /// 「启动 NapCat 并打开 WebUI」：先把本步填的参数保存下来（启动读的就是保存后的设置），
    /// 再拉起 NapCat，然后每秒看一眼 WebUI 有没有就绪，就绪即用浏览器打开登录页面。
    /// 任何失败只写状态文本，不关窗、不崩溃。
    /// </summary>
    private async void OnNapCatLaunchClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var runner = _napCatRunner;
            if (runner is null)
            {
                SetNapCatLaunchStatus("这次没法启动 NapCat（引导窗口没有拿到启动功能）：请到「CyberTechRep 连接」页用「NapCat 一键启动」。");
                return;
            }

            SetNapCatLaunchBusy(true);
            SetNapCatLaunchStatus("正在启动…");

            // 先保存当前步填的内容：启动用的是设置里的路径 / 端口，不保存就会用上一次的旧值
            var saveError = await ApplyAndSaveStepAsync(FirstRunWizardStep.Connection).ConfigureAwait(true);
            if (saveError is not null)
            {
                SetNapCatLaunchStatus($"没能保存上面的设置，先不启动：{saveError}。改好后可以再点一次这个按钮。");
                SetNapCatLaunchBusy(false);
                return;
            }

            if (_input is not null)
            {
                var validation = FirstRunWizardFlow.Validate(FirstRunWizardStep.Connection, _input);
                if (!validation.IsValid)
                {
                    SetNapCatLaunchStatus("还不能启动：" + validation.ToFeedbackText() + " 改好上面的设置后再点这个按钮。");
                    SetNapCatLaunchBusy(false);
                    return;
                }
            }

            _napCatLaunchClicked = true;
            _napCatSawLaunchState = false;
            NapCatOpenWebUiButton.IsVisible = true;

            // 拉起 NapCat（与「CyberTechRep 连接」页的「NapCat 一键启动」是同一件事）
            await runner.StartNapCatAsync().ConfigureAwait(true);
            SetNapCatLaunchStatus("已发出启动命令，正在等 NapCat 和 WebUI 起来…");
            StartNapCatPolling();
        }
        catch (Exception ex)
        {
            StopNapCatPolling();
            SetNapCatLaunchBusy(false);
            SetNapCatLaunchStatus($"启动失败：{ex.Message}。可以再点一次「启动 NapCat 并打开 WebUI」，或到「CyberTechRep 连接」页启动。");
        }
    }

    /// <summary>「打开 WebUI」：没就绪时提示先启动（或再等等），就绪就直接打开登录页面。</summary>
    private void OnNapCatOpenWebUiClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var runner = _napCatRunner;
            if (runner is null)
            {
                SetNapCatLaunchStatus("这次没法打开 WebUI：请到「CyberTechRep 连接」页点「打开 WebUI」。");
                return;
            }

            if (!runner.WebUiReady)
            {
                SetNapCatLaunchStatus(_napCatPollTimer is not null
                    ? "WebUI 还没就绪：正在等它启动，稍等几秒再点一次。"
                    : "WebUI 还没启动：先点上面的「启动 NapCat 并打开 WebUI」，等它启动后再打开登录页面。");
                return;
            }

            runner.OpenWebUi();
            SetNapCatLaunchStatus("已打开 WebUI，请在浏览器里扫码登录。");
        }
        catch (Exception ex)
        {
            SetNapCatLaunchStatus($"打开 WebUI 失败：{ex.Message}。可以到「CyberTechRep 连接」页重试。");
        }
    }

    /// <summary>开始等 WebUI 就绪：每秒看一眼，最多约 60 秒；就绪 / 超时 / 启动失败都会停下计时器。</summary>
    private void StartNapCatPolling()
    {
        StopNapCatPolling();
        _napCatPollDeadline = DateTime.UtcNow.AddSeconds(NapCatWebUiWaitSeconds);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += OnNapCatPollTick;
        _napCatPollTimer = timer;
        timer.Start();
    }

    private void OnNapCatPollTick(object? sender, EventArgs e)
    {
        try
        {
            PollNapCatOnce();
        }
        catch (Exception ex)
        {
            StopNapCatPolling();
            SetNapCatLaunchBusy(false);
            SetNapCatLaunchStatus($"检查 NapCat 是否启动时出错：{ex.Message}。可以点「打开 WebUI」重试，或到「CyberTechRep 连接」页看 NapCat 后台日志。");
        }
    }

    /// <summary>看一眼 NapCat 现在怎么样：失败 / 退出 / 就绪 / 超时四种结果分别给出下一步该做什么。</summary>
    private void PollNapCatOnce()
    {
        var runner = _napCatRunner;
        if (runner is null)
        {
            StopNapCatPolling();
            return;
        }

        var status = runner.Status;
        if (status.State == NapCatRunnerState.Failed)
        {
            StopNapCatPolling();
            SetNapCatLaunchBusy(false);
            SetNapCatLaunchStatus($"启动失败：{status.Detail}。改好上面的设置后再点一次「启动 NapCat 并打开 WebUI」；" +
                                  "也可以到「CyberTechRep 连接」页看 NapCat 后台日志。");
            return;
        }

        if (status.State is NapCatRunnerState.Starting or NapCatRunnerState.Running)
        {
            _napCatSawLaunchState = true;
            SetNapCatLaunchStatus("已启动，正在等 WebUI 就绪…");
        }
        else if (_napCatSawLaunchState)
        {
            // 起来过又没了（进程自己退出）：继续等也不会等到
            StopNapCatPolling();
            SetNapCatLaunchBusy(false);
            SetNapCatLaunchStatus($"NapCat 已经退出（{status.Detail}）：可以再点一次「启动 NapCat 并打开 WebUI」，" +
                                  "或到「CyberTechRep 连接」页看 NapCat 后台日志。");
            return;
        }

        if (runner.WebUiReady)
        {
            StopNapCatPolling();
            SetNapCatLaunchBusy(false);
            runner.OpenWebUi();
            SetNapCatLaunchStatus("已打开 WebUI，请在浏览器里扫码登录。登录成功后群里发一条消息，就能在「维护」页的排错面板里看到它。");
            return;
        }

        if (DateTime.UtcNow >= _napCatPollDeadline)
        {
            StopNapCatPolling();
            SetNapCatLaunchBusy(false);
            SetNapCatLaunchStatus("还没等到 WebUI：可以点「打开 WebUI」重试，或到「CyberTechRep 连接」页看 NapCat 后台日志。");
        }
    }

    /// <summary>停掉 WebUI 等待计时器（离开第 1 步、关窗、就绪、超时、启动失败都要停，不在后台空转）。</summary>
    private void StopNapCatPolling()
    {
        var timer = _napCatPollTimer;
        _napCatPollTimer = null;
        if (timer is null)
        {
            return;
        }

        try
        {
            timer.Stop();
            timer.Tick -= OnNapCatPollTick;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"停掉 WebUI 等待计时器失败：{ex.Message}");
        }
    }

    /// <summary>轮询期间禁用「启动」按钮（避免连点重复拉起），结束后恢复可点。</summary>
    private void SetNapCatLaunchBusy(bool busy)
    {
        try
        {
            NapCatLaunchButton.IsEnabled = !busy;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"切换 NapCat 启动按钮状态失败：{ex.Message}");
        }
    }

    /// <summary>状态文本（正在启动 / 已启动等 WebUI / 已打开 / 失败原因与下一步）；内容没变就不刷新。</summary>
    private void SetNapCatLaunchStatus(string text)
    {
        try
        {
            if (string.Equals(NapCatLaunchStatus.Text, text, StringComparison.Ordinal))
            {
                return;
            }

            NapCatLaunchStatus.Text = text;
            NapCatLaunchStatus.IsVisible = !string.IsNullOrWhiteSpace(text);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"显示 NapCat 启动状态失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 没点过「启动 NapCat 并打开 WebUI」就离开第 1 步时的提醒（只提示，不自动启动第三方程序）。
    /// 返回 null = 不需要提醒（不是第 1 步 / 点过启动 / 没有启动功能 / 选的是 QQ 官方机器人）。
    /// </summary>
    private string? NapCatNotLaunchedHint(FirstRunWizardStep step)
    {
        if (step != FirstRunWizardStep.Connection || _napCatLaunchClicked || _napCatRunner is null)
        {
            return null;
        }

        return _input?.Mode == MessageConnectionMode.NapCat
            ? "还没启动 NapCat：可以点「上一步」回去用按钮启动并打开 WebUI，或稍后在「CyberTechRep 连接」页启动。"
            : null;
    }

    // ============ 渲染 ============

    /// <summary>
    /// 渲染当前步骤：切换步骤面板、更新按钮文字与步骤序号、显示提示区
    /// （<paramref name="carryMessage"/> 优先，其次本步的动态提示）。
    /// </summary>
    private void RenderStep(string? carryMessage = null)
    {
        try
        {
            var step = _flow.Current;
            StepWelcome.IsVisible = step == FirstRunWizardStep.Welcome;
            StepConnection.IsVisible = step == FirstRunWizardStep.Connection;
            StepIngestScope.IsVisible = step == FirstRunWizardStep.IngestScope;
            StepSendTargets.IsVisible = step == FirstRunWizardStep.SendTargets;
            StepClassification.IsVisible = step == FirstRunWizardStep.Classification;
            StepOverlays.IsVisible = step == FirstRunWizardStep.Overlays;
            StepFilesRetention.IsVisible = step == FirstRunWizardStep.FilesRetention;
            StepUsage.IsVisible = step == FirstRunWizardStep.Usage;
            StepFinish.IsVisible = step == FirstRunWizardStep.Finish;

            StepIndicator.Text = $"第 {_flow.StepNumber} / {_flow.StepCount} 步 · {FirstRunWizardSteps.Title(step)}";
            BackButton.IsVisible = _flow.CanGoBack;
            SkipButton.IsVisible = _flow.CanSkip;
            SkipButton.Content = FirstRunWizardSteps.SkipButtonText(step);
            NextButton.Content = FirstRunWizardSteps.NextButtonText(step);

            // 离开第 1 步就不再等 WebUI（回来时会按当前状态重新显示），按钮恢复可点
            if (step != FirstRunWizardStep.Connection)
            {
                StopNapCatPolling();
                SetNapCatLaunchBusy(false);
            }

            string? message = carryMessage;
            if (message is null && _input is not null)
            {
                var notes = FirstRunWizardFlow.Validate(step, _input).Notes;
                if (notes.Count > 0)
                {
                    message = string.Join("\n", notes);
                }
            }

            ShowMessage(message, isError: false);

            // 切步骤后回到顶部，避免长内容停在上一页的滚动位置
            StepScroll.Offset = new Vector(0, 0);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"渲染引导步骤失败：{ex.Message}");
        }
    }

    /// <summary>完成页：汇总"已配置 / 未配置（含跳过的项）+ 去哪里补"。</summary>
    private void RenderFinish()
    {
        try
        {
            var summary = _input is null
                ? new FirstRunWizardSummary([], [], [])
                : FirstRunWizardFlow.BuildSummary(_input, _flow.SkippedSteps);

            FinishHeadline.Text = summary.HasMissing
                ? $"已经配好了 {summary.Configured.Count} 项，还有 {summary.Missing.Count} 项没配好——下面写清了它们分别到哪里补。"
                : "全部配好了：插件会按下面的设置工作。";
            FinishSummaryBox.Text = summary.ToPlainText();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"生成引导汇总失败：{ex.Message}");
        }

        RenderStep();
    }

    /// <summary>提示区（沿用原引导的提示条风格）：<paramref name="isError"/> 为真时用告警色。</summary>
    private void ShowMessage(string? text, bool isError)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                StepFeedback.Text = "";
                FeedbackBox.IsVisible = false;
                return;
            }

            StepFeedback.Text = text;
            StepFeedback.Foreground = new SolidColorBrush(Color.Parse(isError ? "#C0B3261E" : "#B0000000"));
            FeedbackBox.Background = new SolidColorBrush(Color.Parse(isError ? "#14B3261E" : "#14000000"));
            FeedbackBox.IsVisible = true;
        }
        catch (Exception ex)
        {
            // 提示文案显示失败不影响主流程
            Debug.WriteLine($"显示引导提示失败：{ex.Message}");
        }
    }

    // ============ 保存 / 辅助 ============

    /// <summary>
    /// 把某一步当前填的内容写回设置并落盘（「下一步」与「启动 NapCat」共用这一份收集逻辑，
    /// 避免两处各写一遍）。返回 null = 成功；否则是失败原因，由调用方决定怎么提示。
    /// </summary>
    private async Task<string?> ApplyAndSaveStepAsync(FirstRunWizardStep step)
    {
        CaptureStep(step);
        if (_settingsService is null || _input is null)
        {
            return null; // 预览 / 没有设置服务：不做保存
        }

        try
        {
            FirstRunWizardFlow.ApplyTo(step, _input, _settingsService.Current, _settingsService.Protect);
            await _settingsService.SaveAsync().ConfigureAwait(true);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// 引导收尾：把收尾默认值写进设置并落盘（当前是关闭「启动 NapCat 后自动打开 WebUI」）。
    /// 进入完成页、跳过整份引导、点「×」关闭这三条路都走这里；失败只记调试输出，不打断引导。
    /// </summary>
    private async Task ApplyCompletionDefaultsAsync()
    {
        if (_settingsService is null)
        {
            return;
        }

        try
        {
            FirstRunWizardFlow.ApplyCompletionDefaults(_settingsService.Current);
            await _settingsService.SaveAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"引导收尾设置保存失败（不影响引导完成，需要时可到「CyberTechRep 连接」页手动关闭）：{ex.Message}");
        }
    }

    /// <summary>标记引导完成并落盘（服务本身不外抛；此处再兜一层，保证界面不崩溃）。</summary>
    private async Task MarkCompletedAsync()
    {
        if (_firstRunService is null)
        {
            return;
        }

        try
        {
            await _firstRunService.MarkCompletedAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"标记引导完成失败（不影响继续使用）：{ex.Message}");
        }
    }

    private static string? FormatKeywords(string label, IReadOnlyList<string> current, IReadOnlyList<string> defaults)
    {
        var list = current.Count > 0 ? current : defaults;
        return $"{label}关键词：{string.Join("、", list)}";
    }

    private static string LinesToText(IReadOnlyList<string> lines) => string.Join("\n", lines);

    private static int ToInt(decimal? value, int fallback) => value.HasValue ? (int)value.Value : fallback;

    private static long ToLong(decimal? value, long fallback) => value.HasValue ? (long)value.Value : fallback;

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
