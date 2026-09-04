using System.Runtime.Versioning;
using Avalonia.Threading;
using ClassIng.Shared.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Services.FirstRun;

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
