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
/// （每次启动把 <c>-q QQ号</c> 作为启动参数传入，跳过扫码）。进程确认存活后从 NapCat 的
/// <c>webui.json</c> 发现 WebUI 地址（端口/token），可按设置自动用系统浏览器打开。
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

    private readonly IngestOptionsProvider _provider;
    private readonly ILogger? _logger;

    /// <summary>查询本插件 NapCat 反向监听当前占用的端口（排除自身误报「端口被占用」）。</summary>
    private readonly Func<int?>? _getSelfListeningPort;

    /// <summary>查询消息接入当前连接状态（看门狗判断反向 WS 是否断开）。</summary>
    private readonly Func<ConnectionStatus>? _getConnectionStatus;

    private readonly object _lock = new();
    private Process? _process;
    private NapCatRunnerStatus _status = NapCatRunnerStatus.NotRunning;
    private volatile bool _stopRequested;
    private CancellationTokenSource? _watchdogCts;

    /// <summary>本次运行 stdout/stderr 读取循环的取消源（进程退出后延迟取消，先排空管道余量）。</summary>
    private CancellationTokenSource? _logReadCts;

    /// <summary>本次运行期间反向 WS 是否曾连上（看门狗只干预「连上过又断开」的场景）。</summary>
    private volatile bool _everConnectedSinceStart;

    /// <summary>当前断开起始时间（null = 已连接或从未连上）。</summary>
    private DateTime? _disconnectedSinceUtc;

    /// <summary>本次拉起进程发现的 NapCat WebUI 地址（登录/管理页面；null = 未发现）。</summary>
    private volatile string? _webUiUrl;

    public NapCatRunnerService(IngestOptionsProvider provider, ILogger? logger = null,
        Func<int?>? getSelfListeningPort = null, Func<ConnectionStatus>? getConnectionStatus = null)
    {
        _provider = provider;
        _logger = logger;
        _getSelfListeningPort = getSelfListeningPort;
        _getConnectionStatus = getConnectionStatus;

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

    /// <summary>用系统默认浏览器打开当前已发现的 WebUI 地址（未发现时仅记日志，不抛异常）。</summary>
    public void OpenWebUi()
    {
        var url = _webUiUrl;
        if (string.IsNullOrEmpty(url))
        {
            _logger?.LogWarning("打开 WebUI 失败：尚未发现 WebUI 地址（NapCat 启动后才会生成 webui.json）");
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
            if (_process is { HasExited: false })
            {
                return; // 已在运行
            }

            _stopRequested = false;
        }

        var settings = _provider.GetSettings();
        exePath = settings.NapCatExePath;
        workDirectory = settings.NapCatWorkDirectory;
        reversePort = settings.NapCatReversePort;

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

        // 校验 4：QQ 号快速登录模式必须有 QQ 号
        var arguments = BuildArguments(settings.NapCatLoginMode, settings.NapCatQuickLoginQQ, out var argError);
        if (argError is not null)
        {
            SetStatus(NapCatRunnerState.Failed, argError);
            return;
        }

        // 每次拉起重置连接跟踪与 WebUI 地址（属于新一次运行）
        _everConnectedSinceStart = false;
        _disconnectedSinceUtc = null;
        _webUiUrl = null;

        try
        {
            var startInfo = BuildProcessStartInfo(exePath, workDirectory, arguments);
            LogBuffer.Append($"启动 NapCat：{startInfo.FileName} {startInfo.Arguments}".TrimEnd(),
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

            SetStatus(NapCatRunnerState.Starting, $"正在启动（pid {process.Id}）…");
            _logger?.LogInformation("NapCat 进程已拉起：pid={Pid} exe={Exe} args={Args}",
                process.Id, exePath, string.IsNullOrEmpty(arguments) ? "（无，扫码登录）" : arguments);

            // 等待窗口期确认存活：立即退出视为失败并暴露退出码
            await Task.Delay(2000).ConfigureAwait(false);
            if (process.HasExited)
            {
                SetStatus(NapCatRunnerState.Failed, $"进程启动后立即退出（退出码 {process.ExitCode}）");
                return;
            }

            SetStatus(NapCatRunnerState.Running, $"运行中 [pid {process.Id}]");
            LogBuffer.Append($"NapCat 进程已就绪（pid {process.Id}）", NapCatLogStream.System);

            // 发现 WebUI 地址（webui.json 由 NapCat 启动时生成；失败不阻塞运行）
            DiscoverWebUiUrl(exePath, workDirectory);

            // 启动看门狗（进程存活期间监控反向 WS 僵死）
            StartWatchdog(process);
        }
        catch (Exception ex)
        {
            SetStatus(NapCatRunnerState.Failed, $"启动失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 组装启动参数：快速登录模式传 <c>-q QQ号</c>（NapCat 启动器官方参数，跳过扫码）；
    /// 扫码模式不传参。QQ 号为空/非法时经 <paramref name="error"/> 返回明确原因。
    /// </summary>
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

        return $"-q {qq}";
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

    /// <summary>
    /// 从 NapCat 安装目录发现 <c>webui.json</c> 并解析出 WebUI 访问地址
    /// （<c>http://127.0.0.1:{port}/webui?token={token}</c>）；结果存入 <see cref="_webUiUrl"/>，
    /// 按设置自动打开。找不到/解析失败仅记日志，不影响 NapCat 运行。
    /// </summary>
    private void DiscoverWebUiUrl(string exePath, string workDirectory)
    {
        try
        {
            var exeDir = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? "";
            var configPath = FindWebUiConfigPath(exeDir,
                string.IsNullOrWhiteSpace(workDirectory) ? exeDir : workDirectory);
            if (configPath is null)
            {
                _logger?.LogInformation("未找到 NapCat webui.json（WebUI 地址发现跳过；不影响扫码登录）");
                return;
            }

            var json = File.ReadAllText(configPath);
            var (port, token) = ParseWebUiConfig(json);
            if (port <= 0)
            {
                _logger?.LogWarning("NapCat webui.json 解析失败或端口非法：{Path}", configPath);
                return;
            }

            var url = string.IsNullOrEmpty(token)
                ? $"http://127.0.0.1:{port}/webui"
                : $"http://127.0.0.1:{port}/webui?token={Uri.EscapeDataString(token)}";
            _webUiUrl = url;
            _logger?.LogInformation("NapCat WebUI 地址已发现：{Url}", url);
            // 日志流只记地址不记 token（WebUI token 亦属凭据，不落入可复制的面板文本）
            LogBuffer.Append(string.IsNullOrEmpty(token)
                ? $"已发现 NapCat WebUI：http://127.0.0.1:{port}/webui"
                : $"已发现 NapCat WebUI：http://127.0.0.1:{port}/webui（token 已隐藏）",
                NapCatLogStream.System);
            RaisePropertyChangedSafe();

            if (_provider.GetSettings().NapCatOpenWebUiOnStart)
            {
                // 延迟打开：给 NapCat WebUI 服务一点就绪时间
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000).ConfigureAwait(false);
                    OpenWebUi();
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "NapCat WebUI 地址发现失败（不影响运行）");
        }
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
                if (process.HasExited)
                {
                    return;
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
        StopWatchdog();
        StopLogReadLoops();

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
        Process? process;
        lock (_lock)
        {
            process = _process;
        }

        if (process is null || process.HasExited)
        {
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

    public void Dispose()
    {
        StopWatchdog();
        StopLogReadLoops();
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
