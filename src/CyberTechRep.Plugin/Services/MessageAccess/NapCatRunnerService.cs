using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
/// 本服务只负责「拉起/停止 NapCat 本体」，不内置 NapCat；消息接入链路由
/// <see cref="MessageIngestService"/>（NapCat 模式网关）独立承担，两者互不阻塞。
/// </para>
/// </summary>
public sealed class NapCatRunnerService : IHostedService, IDisposable
{
    private readonly IngestOptionsProvider _provider;
    private readonly ILogger? _logger;

    /// <summary>查询本插件 NapCat 反向监听当前占用的端口（排除自身误报「端口被占用」）。</summary>
    private readonly Func<int?>? _getSelfListeningPort;

    private readonly object _lock = new();
    private Process? _process;
    private NapCatRunnerStatus _status = NapCatRunnerStatus.NotRunning;
    private volatile bool _stopRequested;

    public NapCatRunnerService(IngestOptionsProvider provider, ILogger? logger = null,
        Func<int?>? getSelfListeningPort = null)
    {
        _provider = provider;
        _logger = logger;
        _getSelfListeningPort = getSelfListeningPort;
    }

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

    // ---- IHostedService：注册为宿主服务仅用于插件关停兜底终止进程，启动逻辑不在此触发 ----

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                WorkingDirectory = string.IsNullOrWhiteSpace(workDirectory)
                    ? Path.GetDirectoryName(exePath)
                    : workDirectory,
                CreateNoWindow = false
            };
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
            SetStatus(NapCatRunnerState.Starting, $"正在启动（pid {process.Id}）…");
            _logger?.LogInformation("NapCat 进程已拉起：pid={Pid} exe={Exe}", process.Id, exePath);

            // 等待窗口期确认存活：立即退出视为失败并暴露退出码
            await Task.Delay(2000).ConfigureAwait(false);
            if (process.HasExited)
            {
                SetStatus(NapCatRunnerState.Failed, $"进程启动后立即退出（退出码 {process.ExitCode}）");
            }
            else
            {
                SetStatus(NapCatRunnerState.Running, $"运行中 [pid {process.Id}]");
            }
        }
        catch (Exception ex)
        {
            SetStatus(NapCatRunnerState.Failed, $"启动失败：{ex.Message}");
        }
    }

    /// <summary>进程退出回调：区分用户主动停止与异常退出。</summary>
    private void OnProcessExited(Process process)
    {
        if (_stopRequested)
        {
            SetStatus(NapCatRunnerState.Stopped, "已停止");
            return;
        }

        try
        {
            var code = process.ExitCode;
            SetStatus(code == 0 ? NapCatRunnerState.Stopped : NapCatRunnerState.Failed,
                code == 0 ? "已停止（进程正常退出）" : $"进程异常退出（退出码 {code}）");
        }
        catch
        {
            SetStatus(NapCatRunnerState.Stopped, "已停止（进程退出）");
        }

        lock (_lock)
        {
            if (ReferenceEquals(_process, process))
            {
                _process.Dispose();
                _process = null;
            }
        }
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
                    process.Dispose();
                    _process = null;
                }
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
