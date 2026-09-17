using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CyberTechRep.Shared.Models;

namespace CyberTechRep.Plugin.Services.MessageAccess;

/// <summary>NapCat 一键启动运行状态（设置页实时展示）。</summary>
public enum NapCatRunnerState
{
    /// <summary>未运行（默认）。</summary>
    NotRunning,

    /// <summary>启动中（进程已拉起，等待确认存活）。</summary>
    Starting,

    /// <summary>运行中（详情见 <see cref="NapCatRunnerStatus.Detail"/>，含 PID）。</summary>
    Running,

    /// <summary>已停止（用户主动停止或进程正常退出）。</summary>
    Stopped,

    /// <summary>失败（原因见 <see cref="NapCatRunnerStatus.Detail"/>）。</summary>
    Failed
}

/// <summary>NapCat 运行状态快照（设置页绑定显示）。</summary>
public sealed record NapCatRunnerStatus(NapCatRunnerState State, string Detail)
{
    public static readonly NapCatRunnerStatus NotRunning = new(NapCatRunnerState.NotRunning, "未运行");
}

/// <summary>
/// NapCat 一键启动服务：以配置的可执行文件路径拉起 NapCat 进程（启动前校验路径与
/// 反向 WS 端口占用），跟踪退出状态，停止/插件关停时终止整个进程树。
/// <para>
/// 登录方式（设置页可选）：扫码（默认，二维码经 NapCat WebUI 展示）或 QQ 号快速登录
/// （每次启动把 QQ 号作为启动参数传给 NapCat 启动器，由启动器转成 NTQQ 的 <c>-q QQ号</c>
/// 跳过扫码；实测直接传 <c>-q</c> 会被启动器丢弃，故此处必须是裸 QQ 号）。进程确认存活后从
/// NapCat 的 <c>webui.json</c> 发现 WebUI 地址（端口/token）并探活，可按设置自动用浏览器打开。
/// </para>
/// <para>
/// NapCat 以注入方式运行在 QQ 进程内，需独占 QQ 实例：QQ 已在运行时新实例会被单实例机制顶掉，
/// NapCat 拿不到已登录会话（表现为只弹二维码、始终不接入）。设置项
/// <see cref="ConnectionSettings.NapCatEndExistingQq"/>（默认开启）在拉起 NapCat 前结束已有 QQ 进程；
/// 若检测到 NapCat 已在运行（反向 WS 已连接或 WebUI 端口已在监听）则跳过重复启动。
/// </para>
/// <para>
/// <b>运行形态</b>（<see cref="ConnectionSettings.NapCatRunMode"/>）：
/// <b>无头</b>（默认）= 官方 Shell/OneKey 包，不显示 QQ 界面，运行判定与日志都基于启动进程（stdout）；
/// <b>有头</b> = 官方 Framework 有头包（一键有头版的 <c>NapCatWinBootMain.exe</c>）
/// 或手动 LiteLoader 形态的官方 <c>QQ.exe</c>，会显示完整 QQ 登录/聊天界面——
/// 此时注入启动器把 QQ 拉起后自身会退出，因此运行判定与「停止」以 <b>QQ 进程</b>为准
/// （停止 = 关闭 QQ 界面），日志改为跟随 NapCat 的 <c>logs\*.log</c>；
/// 入口是否接受「裸 QQ 号」快速登录参数按入口名判定（官方 QQ.exe 不吃该参数，登录在界面里完成）。
/// </para>
/// <para>
/// 「进程存活」不等于「已就绪」：启动结果以反向 WS 连接确认，未接入时给出排查指引；
/// 「打开 WebUI」同样先探活，避免打开死页面。
/// </para>
/// <para>
/// 另含连接僵死看门狗：进程存活但本插件反向 WS 曾连上后断开超过阈值（5 分钟）时自动
/// 重启 NapCat。未登录期（从未连上）不干预，避免扫码等待期间误杀。
/// </para>
/// <para>
/// 后台无控制台窗口运行：stdout/stderr 以 UTF-8 异步重定向进插件，逐行写入
/// <see cref="LogBuffer"/>（有界环形缓冲），排错面板据此实时展示；.bat/.cmd 启动器
/// 经 cmd.exe 包装以保持无窗口。
/// </para>
/// <para>
/// 本服务只负责「拉起/停止 NapCat 本体」，不内置 NapCat；消息接入链路由
/// <see cref="MessageIngestService"/>（NapCat 模式网关）独立承担，两者互不阻塞。
/// </para>
/// </summary>
public sealed class NapCatRunnerService : IHostedService, IDisposable
{
    /// <summary>看门狗轮询间隔（秒）。</summary>
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(30);

    /// <summary>反向 WS 断开多久后判定「连接僵死」并自动重启 NapCat。</summary>
    private static readonly TimeSpan WatchdogRestartDelay = TimeSpan.FromMinutes(5);

    /// <summary>有头形态日志文件跟随的轮询间隔。</summary>
    private static readonly TimeSpan LogFileTailInterval = TimeSpan.FromSeconds(1);

    private readonly IngestOptionsProvider _provider;
    private readonly ILogger? _logger;

    /// <summary>查询本插件 NapCat 反向监听当前占用的端口（排除自身误报「端口被占用」）。</summary>
    private readonly Func<int?>? _getSelfListeningPort;

    /// <summary>查询消息接入当前连接状态（看门狗判断反向 WS 是否断开）。</summary>
    private readonly Func<ConnectionStatus>? _getConnectionStatus;

    /// <summary>端口探活委托（WebUI 可用性判断；未接线时用默认 TCP 回环连接探测）。</summary>
    private readonly Func<int, bool>? _probePort;

    /// <summary>结束已在运行的 QQ 进程委托（NapCat 需独占 QQ 实例；未接线时用默认实现）。</summary>
    private readonly Func<int>? _endExistingQqProcesses;

    /// <summary>
    /// 查询 QQ 进程是否在运行（有头形态的运行判定与「停止」依据；未接线时用默认实现探测 QQ/QQEX）。
    /// 有头形态的注入启动器拉起 QQ 后会自行退出，只有 QQ 进程才是 NapCat 真正活着的标志。
    /// </summary>
    private readonly Func<bool>? _isQqProcessRunning;

    private readonly object _lock = new();
    private Process? _process;
    private NapCatRunnerStatus _status = NapCatRunnerStatus.NotRunning;
    private volatile bool _stopRequested;
    private CancellationTokenSource? _watchdogCts;

    /// <summary>本次运行的取消源（停止/进程退出/释放时取消连接确认与 WebUI 探活）。</summary>
    private CancellationTokenSource? _runCts;

    /// <summary>本次运行 stdout/stderr 读取循环的取消源（进程退出后延迟取消，先排空管道余量）。</summary>
    private CancellationTokenSource? _logReadCts;

    /// <summary>
    /// 有头形态的 NapCat 日志文件跟随器（无头形态不用——stdout 已足够）。
    /// 有头形态启动的是官方 QQ，进程不写 stdout，日志只在 NapCat 的 <c>logs\*.log</c> 里。
    /// </summary>
    private NapCatLogFileTailer? _logFileTailer;

    /// <summary>有头形态日志跟随循环的取消源。</summary>
    private CancellationTokenSource? _logFileTailCts;

    /// <summary>「未发现 NapCat 日志文件」提示是否已写入日志流（避免每秒刷屏）。</summary>
    private volatile bool _logFileHintLogged;

    /// <summary>本次运行期间反向 WS 是否曾连上（看门狗只干预「连上过又断开」的场景）。</summary>
    private volatile bool _everConnectedSinceStart;

    /// <summary>当前断开起始时间（null = 已连接或从未连上）。</summary>
    private DateTime? _disconnectedSinceUtc;

    /// <summary>本次拉起进程发现的 NapCat WebUI 地址（登录/管理页面；null = 未发现）。</summary>
    private volatile string? _webUiUrl;

    /// <summary>本次拉起进程发现的 NapCat WebUI 端口（0 = 未解析到）。</summary>
    private int _webUiPort;

    /// <summary>WebUI 是否已探活通过（端口可连接；false = WebUI 未启动或端口不可绑定）。</summary>
    private volatile bool _webUiReady;

    /// <summary>「WebUI 端口未监听」排查提示是否已写入日志流（避免看门狗重试时重复刷屏）。</summary>
    private volatile bool _webUiHintLogged;

    /// <summary>本次运行的可执行文件路径与工作目录（看门狗重试 WebUI 发现时复用）。</summary>
    private string _runningExePath = "";

    private string? _runningWorkDirectory;

    public NapCatRunnerService(IngestOptionsProvider provider, ILogger? logger = null,
        Func<int?>? getSelfListeningPort = null, Func<ConnectionStatus>? getConnectionStatus = null,
        Func<int, bool>? probePort = null, Func<int>? endExistingQqProcesses = null,
        Func<bool>? isQqProcessRunning = null)
    {
        _provider = provider;
        _logger = logger;
        _getSelfListeningPort = getSelfListeningPort;
        _getConnectionStatus = getConnectionStatus;
        _probePort = probePort;
        _endExistingQqProcesses = endExistingQqProcesses;
        _isQqProcessRunning = isQqProcessRunning;

        // 容量经设置读取（热生效）；提供者异常/非法值由缓冲内部回退，不影响启动
        LogBuffer = new NapCatLogBuffer(() => provider.GetSettings().NapCatLogBufferLines);
    }

    /// <summary>
    /// NapCat stdout/stderr 环形缓冲（排错面板实时展示）。
    /// 无控制台窗口运行时的唯一日志出口：stdout/stderr 与插件系统行都汇入此处。
    /// </summary>
    public NapCatLogBuffer LogBuffer { get; }

    /// <summary>当前运行状态快照（设置页绑定显示）。</summary>
    public NapCatRunnerStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    /// <summary>状态变化通知（设置页据此刷新显示）。</summary>
    public event EventHandler? StatusChanged;

    /// <summary>当前已发现的 NapCat WebUI 地址（null = 未发现；进程启动后从 webui.json 解析）。</summary>
    public string? WebUiUrl => _webUiUrl;

    /// <summary>WebUI 当前是否可访问（端口探活通过）。false 时「打开 WebUI」不会打开死页面，而是写入失败原因。</summary>
    public bool WebUiReady => _webUiReady;

    // ---- IHostedService：插件关停兜底终止进程 + 可选自动启动 NapCat ----

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = _provider.GetSettings();
        if (settings is { Mode: MessageConnectionMode.NapCat, NapCatAutoStart: true }
            && !string.IsNullOrWhiteSpace(settings.NapCatExePath))
        {
            // 延迟启动：等反向 WS 监听与消息接入服务就绪（NapCat 自身也有重连兜底）
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        _logger?.LogInformation("NapCat 自动启动（设置已开启）");
                        await StartNapCatAsync().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "NapCat 自动启动失败");
                }
            }, cancellationToken);
        }

        return Task.CompletedTask;
    }

    /// <summary>插件关停：终止 NapCat 进程树（不阻塞关停超过 ~5 秒）。</summary>
    public Task StopAsync(CancellationToken cancellationToken)
        => StopInternalAsync(waitExit: true);

    /// <summary>一键启动 NapCat。失败原因经状态（Failed + Detail）与日志明确暴露，不静默。</summary>
    public Task StartNapCatAsync()
    {
        _ = Task.Run(() => StartCoreAsync());
        return Task.CompletedTask;
    }

    /// <summary>停止 NapCat（终止整个进程树）。</summary>
    public Task StopNapCatAsync() => StopInternalAsync(waitExit: false);

    /// <summary>
    /// 用系统默认浏览器打开当前已发现的 WebUI 地址。打开前先探活：WebUI 未监听时不打开死页面，
    /// 而是把明确原因写入日志流（常见原因：端口落在系统保留端口段内，NapCat 报 EACCES 无法绑定）。
    /// </summary>
    public void OpenWebUi()
    {
        var url = _webUiUrl;
        if (string.IsNullOrEmpty(url))
        {
            _logger?.LogWarning("打开 WebUI 失败：尚未发现 WebUI 地址（NapCat 启动后才会生成 webui.json）");
            LogBuffer.Append("打开 WebUI 失败：尚未发现 WebUI 地址（NapCat 启动后才会生成 webui.json 并监听 WebUI 端口）",
                NapCatLogStream.System);
            return;
        }

        // 迟到就绪兜底：WebUI 起得慢时，点击即重新探活一次
        if (!_webUiReady && _webUiPort > 0 && ProbePort(_webUiPort))
        {
            _webUiReady = true;
            RaisePropertyChangedSafe();
        }

        if (!_webUiReady)
        {
            _logger?.LogWarning("打开 WebUI 失败：端口 {Port} 未监听（WebUI 未启动或端口不可绑定）", _webUiPort);
            LogBuffer.Append($"打开 WebUI 失败：端口 {_webUiPort} 未监听。"
                + "WebUI 未启动，或该端口被系统保留端口段占用（NapCat 日志会出现「host或port不可用 EACCES」）——"
                + @"可把 napcat\config\webui.json 的 port 改到未被保留的端口后重启 NapCat",
                NapCatLogStream.System);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            _logger?.LogInformation("已用系统浏览器打开 NapCat WebUI");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "打开 NapCat WebUI 失败：{Url}", url);
        }
    }

    private async Task StartCoreAsync()
    {
        string exePath, workDirectory;
        int reversePort;
        lock (_lock)
        {
            // 已在运行：启动器进程还活着；或有头形态下本次托管运行中且 QQ 进程仍在
            // （有头形态的启动器拉起 QQ 后自身会退出，只看进程会重复拉起第二个实例）
            if (_process is { HasExited: false }
                || (IsFrameworkMode()
                    && _status.State is NapCatRunnerState.Running or NapCatRunnerState.Starting
                    && IsQqProcessRunning()))
            {
                return;
            }

            _stopRequested = false;
        }

        var settings = _provider.GetSettings();
        exePath = settings.NapCatExePath;
        workDirectory = settings.NapCatWorkDirectory;
        reversePort = settings.NapCatReversePort;

        // 已在运行检测（先于路径校验）：NapCat 由外部启动（插件未托管）或上一次实例仍在时不重复拉起——
        // 重复拉起会出现第二个 QQ 实例，后启动的拿不到已登录会话（只弹二维码、始终不接入）。
        var external = DetectRunningNapCat(exePath, workDirectory);
        if (external is not null)
        {
            SetStatus(NapCatRunnerState.Running, $"已在运行（{external}），跳过重复启动");
            LogBuffer.Append($"检测到 NapCat 已在运行（{external}），跳过重复启动", NapCatLogStream.System);
            return;
        }

        // 校验 1：可执行文件路径
        if (string.IsNullOrWhiteSpace(exePath))
        {
            SetStatus(NapCatRunnerState.Failed, "未配置 NapCat 可执行文件路径（请先在上方填写）");
            return;
        }

        if (!File.Exists(exePath))
        {
            SetStatus(NapCatRunnerState.Failed, $"找不到可执行文件：{exePath}");
            return;
        }

        // 校验 2：工作目录（可选，默认 exe 所在目录）
        if (!string.IsNullOrWhiteSpace(workDirectory) && !Directory.Exists(workDirectory))
        {
            SetStatus(NapCatRunnerState.Failed, $"工作目录不存在：{workDirectory}");
            return;
        }

        // 校验 3：反向 WS 端口占用（本插件自身监听占用时不算冲突，NapCat 恰好应连上来）
        if (reversePort > 0 && _getSelfListeningPort?.Invoke() != reversePort && IsPortOccupied(reversePort))
        {
            SetStatus(NapCatRunnerState.Failed,
                $"端口 {reversePort} 已被其他程序占用：请更换 NapCat 反向 WS 端口（设置页与 NapCat 配置同步修改）");
            return;
        }

        // 校验 4：启动参数（有头形态下按入口决定是否传裸 QQ 号：官方 QQ.exe 不吃该参数）
        var arguments = BuildArguments(
            settings.NapCatRunMode, settings.NapCatLoginMode, exePath, settings.NapCatQuickLoginQQ,
            out var argError);
        if (argError is not null)
        {
            SetStatus(NapCatRunnerState.Failed, argError);
            return;
        }

        var frameworkMode = settings.NapCatRunMode == NapCatRunMode.Framework;
        if (frameworkMode && !AcceptsQuickLoginArgument(exePath))
        {
            LogBuffer.Append(
                "有头形态：入口不是 NapCat 注入启动器，快速登录设置对该入口不适用，"
                + "请在 QQ 界面里完成登录（登录态由 QQ 客户端自己保存）",
                NapCatLogStream.System);
        }

        // 结束已有 QQ 进程：NapCat 注入在 QQ 进程内运行，需独占 QQ 实例（QQ 已在运行时注入拿不到会话）
        if (settings.NapCatEndExistingQq)
        {
            var killed = (_endExistingQqProcesses ?? EndQqProcesses)();
            LogBuffer.Append(killed > 0
                    ? $"已结束 {killed} 个已在运行的 QQ 进程（NapCat 需独占 QQ 实例）"
                    : "未发现已在运行的 QQ 进程（无需结束）",
                NapCatLogStream.System);
            if (killed > 0)
            {
                await Task.Delay(1500).ConfigureAwait(false); // 等 QQ 释放登录会话与单实例锁
            }
        }

        // 每次拉起重置连接跟踪与 WebUI 状态（属于新一次运行）
        _everConnectedSinceStart = false;
        _disconnectedSinceUtc = null;
        _webUiUrl = null;
        _webUiPort = 0;
        _webUiReady = false;
        _webUiHintLogged = false;
        _runningExePath = exePath;
        _runningWorkDirectory = workDirectory;

        var runCts = new CancellationTokenSource();
        Interlocked.Exchange(ref _runCts, runCts)?.Cancel();

        try
        {
            var startInfo = BuildProcessStartInfo(exePath, workDirectory, arguments);
            LogBuffer.Append(
                $"启动 NapCat（{(frameworkMode ? "有头：会显示 QQ 界面" : "无头")}）："
                + $"{startInfo.FileName} {startInfo.Arguments}".TrimEnd(),
                NapCatLogStream.System);

            var process = Process.Start(startInfo);
            if (process is null)
            {
                SetStatus(NapCatRunnerState.Failed, "进程启动失败（系统未返回进程句柄）");
                return;
            }

            lock (_lock)
            {
                _process = process;
            }

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => OnProcessExited(process);

            // 无控制台窗口运行时 stdout/stderr 是唯一日志出口；必须持续读取，否则管道写满会阻塞 NapCat
            StartLogReadLoops(process);

            // 有头形态：启动的是官方 QQ（界面程序不写 stdout），日志改从 NapCat 日志文件跟随
            if (frameworkMode)
            {
                StartLogFileTail();
            }

            SetStatus(NapCatRunnerState.Starting, $"正在启动（pid {process.Id}）…");
            _logger?.LogInformation("NapCat 进程已拉起：pid={Pid} exe={Exe} args={Args} mode={Mode}",
                process.Id, exePath, string.IsNullOrEmpty(arguments) ? "（无，扫码登录）" : arguments,
                settings.NapCatRunMode);

            // 等待窗口期确认存活：立即退出视为失败并暴露退出码
            await Task.Delay(2000).ConfigureAwait(false);
            if (!IsRunAlive(process))
            {
                SetStatus(NapCatRunnerState.Failed, $"进程启动后立即退出（退出码 {process.ExitCode}）");
                return;
            }

            // 有头形态：注入启动器把 QQ 拉起后自身退出属正常现象（NapCat 活在 QQ 进程里）
            SetStatus(NapCatRunnerState.Running, RunningDetail(process, "等待 NapCat 接入…"));
            LogBuffer.Append(process.HasExited
                    ? "NapCat 启动器已退出、QQ 进程已接管（有头形态的正常现象）；等待反向 WS 接入"
                    : $"NapCat 进程已就绪（pid {process.Id}）",
                NapCatLogStream.System);

            // WebUI 地址发现 + 探活（与接入确认并行；未监听时写入可操作提示，不阻塞接入）
            var webUiTask = Task.Run(() => DiscoverWebUiUrlAsync(process, runCts.Token), runCts.Token);

            // 启动看门狗（进程存活期间监控反向 WS 僵死）
            StartWatchdog(process);

            // 启动结果以反向 WS 连接确认（进程存活 ≠ 已就绪）
            await ConfirmConnectionAsync(process, runCts.Token).ConfigureAwait(false);
            await webUiTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetStatus(NapCatRunnerState.Failed, $"启动失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 组装启动参数：快速登录模式传<b>裸 QQ 号</b>，扫码模式不传参。
    /// <para>
    /// Q 号必须是裸数字：NapCat 的启动器（<c>NapCatWinBootMain.exe</c>）只认裸 QQ 号并转成
    /// NTQQ 的 <c>-q QQ号</c> 启动参数；实测直接传 <c>-q QQ号</c> 会被启动器丢弃，
    /// NapCat 侧仍走扫码登录（4.18.x 实测：命令行不带 <c>-q</c>，日志输出「没有 -q 指令指定快速登录」）。
    /// </para>
    /// QQ 号为空/非法时经 <paramref name="error"/> 返回明确原因。
    /// </summary>
    // ============ 运行形态（无头 Shell / 有头 Framework）============

    /// <summary>当前是否为「有头」形态（Framework/LiteLoader：显示官方 QQ 完整界面）。</summary>
    private bool IsFrameworkMode()
    {
        try
        {
            return _provider.GetSettings().NapCatRunMode == NapCatRunMode.Framework;
        }
        catch
        {
            return false; // 设置读取失败按无头（既有行为）处理
        }
    }

    /// <summary>QQ 进程是否在运行（有头形态的运行判定依据；未接线时用默认探测）。</summary>
    private bool IsQqProcessRunning() => (_isQqProcessRunning ?? AnyQqProcessRunning)();

    /// <summary>默认 QQ 进程探测（`QQ` 与 `QQEX`，与结束 QQ 进程用的进程名一致）。</summary>
    private static bool AnyQqProcessRunning()
    {
        foreach (var name in new[] { "QQ", "QQEX" })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            try
            {
                if (processes.Length > 0)
                {
                    return true;
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 「本次运行是否还活着」：无头形态看启动进程（stdout 泵与进程树都挂在它上面）；
    /// 有头形态看启动进程<b>或</b> QQ 进程——注入启动器把 QQ 拉起后自身会退出，
    /// QQ 进程还在就说明 NapCat 仍在运行（否则会被误判为「已停止」而丢掉看门狗与状态）。
    /// </summary>
    private bool IsRunAlive(Process? process)
    {
        if (process is { HasExited: false })
        {
            return true;
        }

        return IsFrameworkMode() && IsQqProcessRunning();
    }

    /// <summary>
    /// 运行状态详情：有头形态下启动器可能已退出（QQ 进程接管），此时不显示启动器 pid，
    /// 改成「QQ 进程内」以免用户以为进程没了。
    /// </summary>
    private string RunningDetail(Process process, string suffix)
    {
        var who = process.HasExited ? "QQ 进程内（有头）" : $"pid {process.Id}";
        return $"运行中 [{who}]（{suffix}）";
    }

    /// <summary>
    /// 组装启动参数（按运行形态与入口类型）：
    /// <list type="bullet">
    /// <item>无头形态：沿用既有规则（快速登录传裸 QQ 号，扫码不传）；</item>
    /// <item>有头形态且入口是 NapCat 注入启动器（<c>NapCatWinBootMain.exe</c>、<c>launcher*.bat</c>）：
    /// 同样传裸 QQ 号（官方一键有头版的 quick 用法即 <c>NapCatWinBootMain.exe 10001</c>）；</item>
    /// <item>有头形态且入口是官方 <c>QQ.exe</c> 等不吃该参数的入口：不传参（登录在 QQ 界面里完成），
    /// 也不因「QQ 号为空/非法」报错——快速登录设置对该入口不适用。</item>
    /// </list>
    /// </summary>
    internal static string BuildArguments(NapCatRunMode runMode, NapCatLoginMode loginMode, string exePath,
        string quickLoginQQ, out string? error)
    {
        if (runMode == NapCatRunMode.Framework && !AcceptsQuickLoginArgument(exePath))
        {
            error = null;
            return "";
        }

        return BuildArguments(loginMode, quickLoginQQ, out error);
    }

    /// <summary>
    /// 该入口是否接受「裸 QQ 号」快速登录参数：NapCat 注入启动器（<c>NapCatWinBootMain*.exe</c>）
    /// 与 Shell/一键包的 <c>launcher*.bat</c> 认；官方 <c>QQ.exe</c> 与普通 LiteLoader 启动器不认
    /// （参数会被忽略，甚至引发客户端异常）。
    /// </summary>
    internal static bool AcceptsQuickLoginArgument(string? exePath)
    {
        var name = Path.GetFileName(exePath ?? "");
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        return name.StartsWith("NapCatWinBootMain", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("launcher", StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildArguments(NapCatLoginMode mode, string quickLoginQQ, out string? error)
    {
        error = null;
        if (mode != NapCatLoginMode.QuickLoginQQ)
        {
            return "";
        }

        var qq = quickLoginQQ?.Trim() ?? "";
        if (qq.Length is < 5 or > 12 || !qq.All(char.IsAsciiDigit))
        {
            error = "快速登录 QQ 号为空或格式非法（应为 5–12 位数字）：请在设置页填写后重试";
            return "";
        }

        return qq;
    }

    /// <summary>
    /// 构造 NapCat 进程启动信息：后台无控制台窗口运行，stdout/stderr 以 UTF-8 重定向进插件。
    /// <para>
    /// .exe 直接启动；.bat/.cmd 在 <c>UseShellExecute=false</c> 下无法直接执行，改经
    /// <c>%ComSpec%</c>（缺失时回退 cmd.exe）以 <c>/d /s /c "脚本路径 原参数"</c> 形式包装，
    /// 使窗口仍由 <see cref="ProcessStartInfo.CreateNoWindow"/> 抑制。
    /// </para>
    /// <para>工作目录为空时回退可执行文件所在目录（相对路径先解析为绝对路径）。</para>
    /// </summary>
    internal static ProcessStartInfo BuildProcessStartInfo(string exePath, string? workDirectory, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = ResolveWorkingDirectory(exePath, workDirectory)
        };

        var extension = Path.GetExtension(exePath);
        if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec
                ? comSpec
                : "cmd.exe";
            var command = string.IsNullOrWhiteSpace(arguments)
                ? $"\"{exePath}\""
                : $"\"{exePath}\" {arguments}";
            startInfo.Arguments = $"/d /s /c \"{command}\"";
        }
        else
        {
            startInfo.FileName = exePath;
            if (!string.IsNullOrEmpty(arguments))
            {
                startInfo.Arguments = arguments;
            }
        }

        return startInfo;
    }

    /// <summary>工作目录：配置优先；为空时回退可执行文件所在目录（相对路径解析为绝对路径）。</summary>
    private static string ResolveWorkingDirectory(string exePath, string? workDirectory)
    {
        if (!string.IsNullOrWhiteSpace(workDirectory))
        {
            return workDirectory;
        }

        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? "";
        }
        catch
        {
            // 路径非法等极端情况：退回原始字符串的目录部分，不让启动信息构造抛异常
            return Path.GetDirectoryName(exePath) ?? "";
        }
    }

    /// <summary>
    /// 启动 stdout/stderr 两个后台异步读取循环，逐行写入 <see cref="LogBuffer"/>。
    /// 读取在独立线程池任务中执行，不阻塞进程启动与看门狗。
    /// </summary>
    private void StartLogReadLoops(Process process)
    {
        StopLogReadLoops();
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _logReadCts, cts);

        // 提前取出流对象：避免进程对象被释放后访问 StandardOutput 抛异常
        var stdout = process.StandardOutput;
        var stderr = process.StandardError;
        _ = Task.Run(() => PumpLogAsync(stdout, NapCatLogStream.StdOut, cts.Token));
        _ = Task.Run(() => PumpLogAsync(stderr, NapCatLogStream.StdErr, cts.Token));
    }

    /// <summary>
    /// 单条输出流的读取循环：<see cref="StreamReader.ReadLineAsync(CancellationToken)"/> 读到 null（EOF）
    /// 即结束；取消/管道关闭等异常只写一条系统行，绝不向线程池逃逸。
    /// </summary>
    private async Task PumpLogAsync(StreamReader reader, NapCatLogStream stream, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return; // 进程退出后主动取消（管道余量已排空）
                }

                if (line is null)
                {
                    return; // EOF：进程已退出且管道写端全部关闭
                }

                LogBuffer.Append(line, stream);
            }
        }
        catch (Exception ex)
        {
            LogBuffer.Append($"读取 NapCat {stream} 输出失败：{ex.Message}", NapCatLogStream.System);
        }
    }

    /// <summary>
    /// 停止读取循环：先给 2 秒排空管道余量（进程刚退出时仍可能残留最后几行），
    /// 再取消等待中的读取，避免子进程残留句柄导致读取线程永久阻塞。
    /// </summary>
    private void StopLogReadLoops()
    {
        var cts = Interlocked.Exchange(ref _logReadCts, null);
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.CancelAfter(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 竞态下可能已取消/释放：忽略，读取循环自身有异常兜底
        }
    }

    // ============ 有头形态：NapCat 日志文件跟随 ============

    /// <summary>
    /// 启动有头形态的日志跟随：启动的是官方 QQ（界面程序不写 stdout），NapCat 日志只在安装目录的
    /// <c>logs\*.log</c> 里，因此逐秒把新增行写入 <see cref="LogBuffer"/>——与无头形态的 stdout 泵
    /// 共用同一个日志缓冲，排错面板体验一致。
    /// </summary>
    private void StartLogFileTail()
    {
        StopLogFileTail();
        var cts = new CancellationTokenSource();
        _logFileTailCts = cts;
        _logFileTailer = new NapCatLogFileTailer();
        _logFileHintLogged = false;
        _ = Task.Run(() => LogFileTailLoopAsync(cts.Token));
    }

    private void StopLogFileTail()
    {
        try
        {
            _logFileTailCts?.Cancel();
            _logFileTailCts?.Dispose();
        }
        catch
        {
            // 取消/释放竞态：忽略
        }

        _logFileTailCts = null;
        _logFileTailer?.Dispose();
        _logFileTailer = null;
    }

    private async Task LogFileTailLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(LogFileTailInterval);
        string? followedPath = null;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var path = FindNewestNapCatLogFile(_runningWorkDirectory ?? "", _runningExePath);
                if (path is null)
                {
                    if (!_logFileHintLogged && !string.IsNullOrEmpty(_runningExePath))
                    {
                        _logFileHintLogged = true;
                        LogBuffer.Append(
                            "未发现 NapCat 日志文件（logs\\*.log）：有头形态不读 stdout，"
                            + "日志请在 NapCat WebUI 或安装目录的 logs 下查看；"
                            + "若日志目录不在默认位置，可在设置里把工作目录指向 NapCat 安装目录",
                            NapCatLogStream.System);
                    }

                    continue;
                }

                if (!string.Equals(path, followedPath, StringComparison.OrdinalIgnoreCase))
                {
                    followedPath = path;
                    LogBuffer.Append($"开始跟随 NapCat 日志文件：{path}", NapCatLogStream.System);
                }

                foreach (var line in _logFileTailer!.Poll(path))
                {
                    LogBuffer.Append(line, NapCatLogStream.StdOut);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "有头形态 NapCat 日志文件跟随异常退出（不影响运行）");
        }
    }

    /// <summary>
    /// 从 NapCat 安装目录发现 <c>webui.json</c>，解析出 WebUI 访问地址
    /// （<c>http://127.0.0.1:{port}/webui?token={token}</c>）并<b>探活</b>：
    /// 地址解析成功即缓存（<see cref="WebUiUrl"/>），但只要端口未真正监听就不标记为可用
    /// （<see cref="WebUiReady"/>），以免把陈旧 webui.json 当作已就绪、或打开死页面。
    /// 探活失败时写入可操作提示（最常见原因：端口落在 Windows 保留端口段内，NapCat 报 EACCES）。
    /// 找不到/解析失败仅记日志，不影响 NapCat 运行。
    /// </summary>
    private async Task DiscoverWebUiUrlAsync(Process process, CancellationToken ct)
    {
        try
        {
            var (port, token) = ReadWebUiConfig(_runningExePath, _runningWorkDirectory, out var configPath);
            if (configPath is null)
            {
                _logger?.LogInformation("未找到 NapCat webui.json（WebUI 地址发现跳过；不影响登录/接入）");
                return;
            }

            if (port <= 0)
            {
                _logger?.LogWarning("NapCat webui.json 解析失败或端口非法：{Path}", configPath);
                return;
            }

            _webUiPort = port;
            _webUiUrl = string.IsNullOrEmpty(token)
                ? $"http://127.0.0.1:{port}/webui"
                : $"http://127.0.0.1:{port}/webui?token={Uri.EscapeDataString(token)}";

            // 探活重试：NapCat 启动后 WebUI 需要数百毫秒到数秒
            for (var attempt = 0; attempt < 6; attempt++)
            {
                if (!IsRunAlive(process) || ct.IsCancellationRequested)
                {
                    return;
                }

                if (ProbePort(port))
                {
                    _webUiReady = true;
                    _logger?.LogInformation("NapCat WebUI 可用：{Url}", _webUiUrl);
                    // 日志流只记地址不记 token（WebUI token 亦属凭据，不落入可复制的面板文本）
                    LogBuffer.Append($"NapCat WebUI 可用：http://127.0.0.1:{port}/webui"
                        + (string.IsNullOrEmpty(token) ? "" : "（token 已隐藏）"), NapCatLogStream.System);
                    RaisePropertyChangedSafe();

                    if (_provider.GetSettings().NapCatOpenWebUiOnStart)
                    {
                        // 延迟打开：给 NapCat WebUI 服务一点就绪时间
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(1500, CancellationToken.None).ConfigureAwait(false);
                            OpenWebUi();
                        }, CancellationToken.None);
                    }

                    return;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            _logger?.LogWarning("NapCat WebUI 端口 {Port} 未监听（WebUI 暂不可用）", port);
            LogBuffer.Append($"NapCat WebUI 未在端口 {port} 监听（WebUI 暂不可用）。"
                + "若 NapCat 日志出现「host或port不可用 EACCES」，说明该端口落在系统保留端口段内："
                + "可用 netsh int ipv4 show excludedportrange protocol=tcp 查看保留段，"
                + @"并把 napcat\config\webui.json 的 port 改到未被保留的端口后重启 NapCat",
                NapCatLogStream.System);
            _webUiHintLogged = true;
            RaisePropertyChangedSafe();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "NapCat WebUI 地址发现失败（不影响运行）");
        }
    }

    /// <summary>
    /// 读取 NapCat webui.json 得到（端口, token）；<paramref name="configPath"/> 为命中的配置文件路径
    /// （null = 未找到）。读盘/解析异常按「未找到」处理，不向调用方抛。
    /// </summary>
    private static (int Port, string Token) ReadWebUiConfig(string exePath, string? workDirectory, out string? configPath)
    {
        configPath = null;
        try
        {
            var exeDir = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? "";
            configPath = FindWebUiConfigPath(exeDir,
                string.IsNullOrWhiteSpace(workDirectory) ? exeDir : workDirectory);
            return configPath is null ? (0, "") : ParseWebUiConfig(File.ReadAllText(configPath));
        }
        catch
        {
            return (0, "");
        }
    }

    /// <summary>
    /// 外部 NapCat 运行检测（避免重复拉起第二个实例）：反向 WS 已连接，或 webui.json 端口已在监听。
    /// 返回判定依据文本（用于状态展示）；未检测到返回 null。
    /// </summary>
    private string? DetectRunningNapCat(string exePath, string? workDirectory)
    {
        try
        {
            if (_getConnectionStatus?.Invoke() == ConnectionStatus.Connected)
            {
                return "反向 WS 已连接";
            }
        }
        catch
        {
            // 状态查询失败不阻断启动流程
        }

        try
        {
            var (port, _) = ReadWebUiConfig(exePath, workDirectory, out _);
            if (port > 0 && ProbePort(port))
            {
                return $"NapCat WebUI（端口 {port}）已在监听";
            }
        }
        catch
        {
            // 同上：读取失败按「未运行」处理
        }

        return null;
    }

    /// <summary>端口探活：能否建立 TCP 连接（WebUI 可能仅绑 IPv6，故 IPv4/IPv6 回环各试一次）。</summary>
    private bool ProbePort(int port)
        => (_probePort ?? DefaultProbePort)(port);

    private static bool DefaultProbePort(int port)
        => IsPortConnectable(IPAddress.Loopback, port) || IsPortConnectable(IPAddress.IPv6Loopback, port);

    private static bool IsPortConnectable(IPAddress address, int port)
    {
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
            client.ConnectAsync(address, port, cts.Token).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 结束已在运行的 QQ 进程（默认实现，`QQ` 与 `QQEX`）。NapCat 注入在 QQ 进程内运行，
    /// 需独占 QQ 实例；单个进程结束失败（权限/已退出）不阻断后续启动。返回成功结束的进程数。
    /// </summary>
    private static int EndQqProcesses()
    {
        var killed = 0;
        foreach (var name in new[] { "QQ", "QQEX" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        killed++;
                    }
                    catch
                    {
                        // 单个进程不可结束：忽略（其余进程继续尝试）
                    }
                }
            }
        }

        return killed;
    }

    /// <summary>
    /// 以反向 WS 连接确认启动结果：进程存活只说明「启动器没退出」，真正就绪以 NapCat 接入为准。
    /// 轮询最长约 35 秒，未接入时把排查指引写入日志流（登录方式 / QQ 是否被独占 / WebUI 端口）。
    /// </summary>
    private async Task ConfirmConnectionAsync(Process process, CancellationToken ct)
    {
        if (_getConnectionStatus is null)
        {
            return; // 未接线（如单元测试场景）：不做连接确认
        }

        var deadline = DateTime.UtcNow.AddSeconds(35);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (!IsRunAlive(process))
            {
                return; // 进程（与有头形态的 QQ）都不在：由 OnProcessExited 结算状态
            }

            if (_getConnectionStatus() == ConnectionStatus.Connected)
            {
                SetStatus(NapCatRunnerState.Running, RunningDetail(process, "已接入：反向 WS 已连接"));
                LogBuffer.Append("NapCat 已接入（反向 WS 已连接）", NapCatLogStream.System);
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (!IsRunAlive(process) || ct.IsCancellationRequested)
        {
            return;
        }

        SetStatus(NapCatRunnerState.Running, RunningDetail(process, "尚未接入：等待登录或反向连接"));
        LogBuffer.Append("NapCat 进程已就绪但反向 WS 尚未连接：首次登录请在 WebUI 扫码；"
            + "已配置快速登录时请核对 QQ 号；并确认启动前已结束其他 QQ 进程（NapCat 需独占 QQ 实例）",
            NapCatLogStream.System);
    }

    /// <summary>按常见目录布局查找 webui.json（exe 目录/工作目录及其上级 napcat\config）。</summary>
    internal static string? FindWebUiConfigPath(params string[] baseDirectories)
    {
        foreach (var dir in baseDirectories.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var rel in new[]
                     {
                         Path.Combine("napcat", "config", "webui.json"),
                         Path.Combine("config", "webui.json")
                     })
            {
                var candidate = Path.GetFullPath(Path.Combine(dir, rel));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // exe 可能直接位于 napcat 目录内（如指向 NapCatWinBootMain.exe），向上找一层
            var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            if (!string.IsNullOrEmpty(parent))
            {
                foreach (var rel in new[]
                         {
                             Path.Combine("napcat", "config", "webui.json"),
                             Path.Combine("config", "webui.json")
                         })
                {
                    var candidate = Path.GetFullPath(Path.Combine(parent, rel));
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 按常见目录布局查找 NapCat 日志目录下的<b>最新日志文件</b>（有头形态的日志来源）。
    /// 候选：给定目录及其上级目录下的 <c>logs</c> 与 <c>napcat\logs</c>；找不到返回 null
    /// （调用方据此提示「日志请在 WebUI 或安装目录查看」，不影响运行）。
    /// </summary>
    internal static string? FindNewestNapCatLogFile(params string[] baseDirectories)
    {
        string? newestPath = null;
        var newestWriteTime = DateTime.MinValue;
        foreach (var directory in EnumerateCandidateLogDirectories(baseDirectories))
        {
            try
            {
                // 在所有候选目录里取「最后写入时间最晚」的日志文件（候选目录本身有先后，
                // 但不能因为先命中一个旧目录就放弃更晚写的日志）
                foreach (var path in Directory.EnumerateFiles(directory, "*.log"))
                {
                    DateTime writeTime;
                    try
                    {
                        writeTime = File.GetLastWriteTimeUtc(path);
                    }
                    catch (IOException)
                    {
                        continue;
                    }

                    if (writeTime > newestWriteTime)
                    {
                        newestWriteTime = writeTime;
                        newestPath = path;
                    }
                }
            }
            catch (IOException)
            {
                // 目录被占用/枚举竞态：换下一个候选
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return newestPath;
    }

    /// <summary>候选日志目录（去重，只返回真实存在的）：各基目录及其上级目录下的 logs、napcat\logs。</summary>
    private static IEnumerable<string> EnumerateCandidateLogDirectories(IEnumerable<string> baseDirectories)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseDir in baseDirectories.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            var roots = new[]
            {
                baseDir,
                Path.GetDirectoryName(baseDir.TrimEnd(Path.DirectorySeparatorChar))
            };
            foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
            {
                foreach (var relative in new[] { "logs", Path.Combine("napcat", "logs") })
                {
                    string candidate;
                    try
                    {
                        candidate = Path.GetFullPath(Path.Combine(root!, relative));
                    }
                    catch
                    {
                        continue;
                    }

                    if (seen.Add(candidate) && Directory.Exists(candidate))
                    {
                        yield return candidate;
                    }
                }
            }
        }
    }

    /// <summary>解析 webui.json 的 port/token（NapCat 结构：{"port":6099,"token":"...","host":"..."}）。</summary>
    internal static (int Port, string Token) ParseWebUiConfig(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var port = root.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number
                && p.TryGetInt32(out var pv) ? pv : 0;
            var token = root.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? ""
                : "";
            return (port, token);
        }
        catch (JsonException)
        {
            return (0, "");
        }
    }

    /// <summary>启动看门狗：进程存活但反向 WS「曾连上后断开」超过阈值时自动重启。</summary>
    private void StartWatchdog(Process process)
    {
        if (_getConnectionStatus is null)
        {
            return; // 未接线（如单元测试场景），看门狗不启用
        }

        StopWatchdog();
        var cts = new CancellationTokenSource();
        _watchdogCts = cts;
        _ = Task.Run(() => WatchdogLoopAsync(process, cts.Token));
    }

    private void StopWatchdog()
    {
        try
        {
            _watchdogCts?.Cancel();
            _watchdogCts?.Dispose();
        }
        catch
        {
            // 忽略取消/释放竞态
        }

        _watchdogCts = null;
    }

    private async Task WatchdogLoopAsync(Process process, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(WatchdogInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!IsRunAlive(process))
                {
                    return;
                }

                // WebUI 起得晚（NapCat 就绪慢）：未探活通过时重试发现，避免「地址一直不可用」
                if (!_webUiReady && !_webUiHintLogged && !string.IsNullOrEmpty(_runningExePath))
                {
                    await DiscoverWebUiUrlAsync(process, ct).ConfigureAwait(false);
                }

                var connected = _getConnectionStatus?.Invoke() == ConnectionStatus.Connected;
                if (connected)
                {
                    _everConnectedSinceStart = true;
                    _disconnectedSinceUtc = null;
                    continue;
                }

                // 未登录期/等待扫码（从未连上）：不干预，避免误杀
                if (!_everConnectedSinceStart)
                {
                    continue;
                }

                _disconnectedSinceUtc ??= DateTime.UtcNow;
                if (DateTime.UtcNow - _disconnectedSinceUtc.Value > WatchdogRestartDelay)
                {
                    _logger?.LogWarning(
                        "NapCat 进程存活但反向 WS 断开超过 {Minutes} 分钟，自动重启 NapCat",
                        WatchdogRestartDelay.TotalMinutes);
                    SetStatus(NapCatRunnerState.Starting, "连接僵死，自动重启中…");
                    await StopInternalAsync(waitExit: false).ConfigureAwait(false);
                    await StartNapCatAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "NapCat 看门狗异常退出（不影响运行）");
        }
    }

    /// <summary>安全读取退出码（进程对象已释放/不可用时返回 null，不抛异常）。</summary>
    private static int? TryReadExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }

    private void RaisePropertyChangedSafe()
    {
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 订阅方异常不阻断服务逻辑
        }
    }

    /// <summary>
    /// 进程退出回调：写入系统日志行、停止读取循环（先排空管道余量），
    /// 区分用户主动停止与异常退出；进程对象延迟释放以免中断仍在收尾的输出读取。
    /// </summary>
    private void OnProcessExited(Process process)
    {
        // 有头形态：注入启动器把 QQ 拉起后自身退出属正常现象——NapCat 活在 QQ 进程里，
        // 此时必须保留看门狗与日志文件跟随、状态继续按「运行中」，否则会被误判为已停止。
        // 保留 _process 引用（已退出的对象不再 dispose）：启动按钮据此判「已在运行」不重复拉起，
        // 停止按钮走「结束 QQ 进程」路径。
        if (IsFrameworkMode() && IsQqProcessRunning())
        {
            StopLogReadLoops();
            LogBuffer.Append(
                $"NapCat 启动器进程已退出（退出码 {TryReadExitCode(process)?.ToString() ?? "未知"}）；"
                + "QQ 进程仍在运行——有头形态按「运行中」继续（停止请点「停止 NapCat」，会关闭 QQ 界面）",
                NapCatLogStream.System);
            SetStatus(NapCatRunnerState.Running, RunningDetail(process, "等待 NapCat 接入…"));
            return;
        }

        StopWatchdog();
        StopLogReadLoops();
        StopLogFileTail();
        CancelRun();

        if (_stopRequested)
        {
            LogBuffer.Append("NapCat 已停止（用户请求终止进程树）", NapCatLogStream.System);
            SetStatus(NapCatRunnerState.Stopped, "已停止");
        }
        else
        {
            try
            {
                var code = process.ExitCode;
                LogBuffer.Append($"NapCat 进程已退出（退出码 {code}）", NapCatLogStream.System);
                SetStatus(code == 0 ? NapCatRunnerState.Stopped : NapCatRunnerState.Failed,
                    code == 0 ? "已停止（进程正常退出）" : $"进程异常退出（退出码 {code}）");
            }
            catch
            {
                LogBuffer.Append("NapCat 进程已退出（退出码未知）", NapCatLogStream.System);
                SetStatus(NapCatRunnerState.Stopped, "已停止（进程退出）");
            }
        }

        // 延迟释放：立即 Dispose 会关闭 stdout/stderr 读取器，可能丢掉管道中最后几行输出
        ScheduleProcessDispose(process);
    }

    /// <summary>延迟释放进程对象：给 stdout/stderr 读取循环排空管道余量的时间（约 2.5 秒）。</summary>
    private void ScheduleProcessDispose(Process process)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2500).ConfigureAwait(false);
            }
            catch
            {
                // 延迟被取消等竞态：继续释放
            }

            lock (_lock)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                }
            }

            try
            {
                process.Dispose();
            }
            catch
            {
                // 已释放/竞态：忽略
            }
        });
    }

    private async Task StopInternalAsync(bool waitExit)
    {
        CancelRun();

        Process? process;
        lock (_lock)
        {
            process = _process;
        }

        if (process is null || process.HasExited)
        {
            // 有头形态：注入启动器拉起 QQ 后自身已退出，「停止」必须结束 QQ 进程——
            // 否则 NapCat 仍在 QQ 进程里运行、反向 WS 也不会断，而插件已认为停止。
            if (IsFrameworkMode() && IsQqProcessRunning())
            {
                var killed = (_endExistingQqProcesses ?? EndQqProcesses)();
                LogBuffer.Append(killed > 0
                        ? $"有头形态停止：已结束 {killed} 个 QQ 进程（NapCat 随之退出，QQ 界面关闭）"
                        : "有头形态停止：QQ 进程已不在运行",
                    NapCatLogStream.System);
                if (killed > 0)
                {
                    try
                    {
                        await Task.Delay(1000).ConfigureAwait(false); // 等 QQ 释放 NapCat 句柄与端口
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }

            if (process is not null)
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_process, process))
                    {
                        _process = null;
                    }
                }

                // 不立即释放：让读取循环先排空 stdout/stderr 最后几行
                ScheduleProcessDispose(process);
            }

            SetStatus(NapCatRunnerState.NotRunning, "未运行");
            return;
        }

        _stopRequested = true;
        try
        {
            // 终止整个进程树（NapCat 常经 bat/启动器拉起 node 子进程，仅杀根进程会残留）
            process.Kill(entireProcessTree: true);
            _logger?.LogInformation("NapCat 进程树终止请求已发送：pid={Pid}", process.Id);
            if (waitExit)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "NapCat 进程树终止失败：pid={Pid}", process.Id);
            SetStatus(NapCatRunnerState.Failed, $"停止失败：{ex.Message}");
        }
    }

    private void SetStatus(NapCatRunnerState state, string detail)
    {
        lock (_lock)
        {
            _status = new NapCatRunnerStatus(state, detail);
        }

        _logger?.LogInformation("NapCat 运行状态：{State}（{Detail}）", state, detail);
        if (state == NapCatRunnerState.Failed)
        {
            // 失败原因同时写入排错面板日志流：NapCat 自身不打印时也能看到插件侧的判定
            LogBuffer.Append($"启动/运行失败：{detail}", NapCatLogStream.System);
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>端口占用探测：尝试绑定 127.0.0.1:port，失败即视为被占用。</summary>
    private static bool IsPortOccupied(int port)
    {
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    /// <summary>取消本次运行的后台任务（连接确认 / WebUI 探活重试）；停止、进程退出与释放时调用。</summary>
    private void CancelRun()
    {
        try
        {
            _runCts?.Cancel();
        }
        catch
        {
            // 取消竞态：忽略
        }
    }

    public void Dispose()
    {
        StopWatchdog();
        StopLogReadLoops();
        StopLogFileTail();
        CancelRun();

        // 有头形态：宿主关停时若 QQ 仍在运行，一并结束（与无头形态「关闭宿主即结束机器人进程树」一致），
        // 否则 NapCat 会在后台留着 QQ 界面继续运行。
        if (IsFrameworkMode() && IsQqProcessRunning())
        {
            var killed = (_endExistingQqProcesses ?? EndQqProcesses)();
            if (killed > 0)
            {
                _logger?.LogInformation("宿主关停：已结束 {Count} 个 QQ 进程（有头形态 NapCat 随之停止）", killed);
            }
        }
        Process? process;
        lock (_lock)
        {
            process = _process;
            _process = null;
        }

        if (process is { HasExited: false })
        {
            _stopRequested = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 关停兜底，失败只记日志
                _logger?.LogWarning("Dispose 时终止 NapCat 进程失败：pid={Pid}", process.Id);
            }
        }

        process?.Dispose();
    }
}
