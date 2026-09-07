using CyberTechRep.Plugin.Services.Classification;
using CyberTechRep.Plugin.Services.MessageAccess;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Plugin.Services.SubjectChain;
using CyberTechRep.Shared.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.Maintenance;

/// <summary>
/// 模块 7：设置变更热生效接线器。
/// <para>
/// 订阅 <see cref="ISettingsService.SettingsChanged"/>，调用各服务已存在的热更新方法：
/// - <see cref="KeywordMessageClassifier.ReloadRules"/>：通知/作业关键词表重载；
/// - <see cref="KeywordSubjectClassifier.ReloadRules"/>：学科词表重载；
/// - 连接/文件等设置通过各 OptionsProvider 的 GetSettings 委托读取
///   <see cref="ISettingsService.Current"/>，天然热生效（下次使用时生效）；
/// - 悬浮窗（模块 6）：<see cref="ISuspensionWindowController.ApplySettingsAsync"/> 即时应用
///   透明度/字号/位置等，并按 <c>Visible</c> 调用 Show/Hide（控制器未注册时跳过）；
///   启动时先应用一次（Visible=true 且 LaunchWithHost=true 的窗口随宿主显示）。
/// </para>
/// </summary>
public sealed class SettingsChangeApplier : IHostedService, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly IMessageClassifier? _messageClassifier;
    private readonly KeywordSubjectClassifier? _subjectClassifier;
    private readonly ISuspensionWindowController? _windowController;
    private readonly MessageIngestService? _ingestService;
    private readonly ILogger _logger;

    private EventHandler<CyberTechRep.Shared.Models.AppSettings>? _handler;

    public SettingsChangeApplier(ISettingsService settingsService,
        IMessageClassifier? messageClassifier = null,
        KeywordSubjectClassifier? subjectClassifier = null,
        ISuspensionWindowController? windowController = null,
        MessageIngestService? ingestService = null,
        ILogger? logger = null)
    {
        _settingsService = settingsService;
        _messageClassifier = messageClassifier;
        _subjectClassifier = subjectClassifier;
        _windowController = windowController;
        _ingestService = ingestService;
        _logger = logger ?? NullLogger.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _handler = (_, settings) => Apply(settings);
        _settingsService.SettingsChanged += _handler;

        // 启动即应用一次悬浮窗设置：Visible=true（且开启随宿主启动）的窗口随宿主显示。
        // 此前 ShowAsync 无任何调用点，悬浮窗创建后从未显示。
        try
        {
            var current = _settingsService.Current;
            if (current.Overlays.LaunchWithHost && _windowController is not null)
            {
                _ = ApplyOverlaysAsync(current);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动应用悬浮窗设置失败（不影响宿主启动）");
        }

        _logger.LogInformation("设置热生效接线器已启动");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // 宿主停止：先于宿主 DesktopLifetime.Shutdown() 批量关闭窗口（ClassIsland App.Stop 顺序），
        // 通知控制器抑制退出期间的可见性持久化——否则每次正常退出都会把全部悬浮窗 Visible=false
        // 写回 settings.json，重启后所有悬浮窗默认全关。
        try
        {
            _windowController?.NotifyHostStopping();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "通知悬浮窗控制器宿主停止失败（不影响宿主停止）");
        }

        // 先退订自身广播处理（避免兜底保存触发一轮悬浮窗回放），再兜底保存：
        // 设置页自动保存挂在 DetachedFromVisualTree，宿主直接退出（不经设置窗口关闭/页面分离）
        // 时可能不触发；此处把内存中的最终值落盘一次，保证任何入口的最后编辑不丢。
        // 时机在 NotifyHostStopping 之后：退出期批量关窗触发的可见性同步已被抑制，
        // 保存的是用户最后一次的可见性意图，不会重现「退出把 Visible=false 写回」的旧缺陷。
        var handler = _handler;
        if (handler is not null)
        {
            _settingsService.SettingsChanged -= handler;
            _handler = null;
        }

        _ = FlushSettingsOnShutdownAsync();

        return Task.CompletedTask;
    }

    private async Task FlushSettingsOnShutdownAsync()
    {
        try
        {
            await _settingsService.SaveAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("宿主退出兜底保存设置完成");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "宿主退出兜底保存设置失败（不阻断宿主停止）");
        }
    }

    private void Apply(CyberTechRep.Shared.Models.AppSettings settings)
    {
        _logger.LogInformation(
            "设置变更，开始热生效：LogLevel={LogLevel}，AiEnabled={AiEnabled}",
            settings.Maintenance.LogLevel, settings.Ai.AiEnabled);

        // ① 关键词分类器：词表重载
        try
        {
            _messageClassifier?.ReloadRules();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "应用设置失败：消息分类器 ReloadRules");
        }

        // ② 学科关键词分类器：词表重载
        try
        {
            _subjectClassifier?.ReloadRules();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "应用设置失败：学科分类器 ReloadRules");
        }

        // ③ 悬浮窗（模块 6 控制器）：即时应用外观与位置
        if (_windowController is not null)
        {
            _ = ApplyOverlaysAsync(settings);
        }

        // ④ 连接设置热更新：群白名单即时生效（管道无需重连）；连接参数
        //（AppId/ApiBase/TokenApiUrl）变更才触发重连，无变化时不重连（ApplySettings 内部判定）。
        try
        {
            _ingestService?.ApplySettings(settings.Connection);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "应用设置失败：连接设置热更新");
        }
    }

    private async Task ApplyOverlaysAsync(CyberTechRep.Shared.Models.AppSettings settings)
    {
        try
        {
            await ApplyOverlayAsync("notice", settings.Overlays.Notice);
            await ApplyOverlayAsync("homework", settings.Overlays.Homework);
            // 第三悬浮窗（学科文件）与学科圆圈启动器共用同一套 Apply/Show/Hide 路径
            await ApplyOverlayAsync("files", settings.Overlays.Files);
            await ApplyOverlayAsync("circle", settings.Overlays.Circle);
            // 第五悬浮窗（未绑定学科选择，需求 3）：与其他窗同构；默认 Visible=false 不随宿主显示，
            // 仅在管道触发（成员未绑定）时显示
            await ApplyOverlayAsync(SuspensionWindowController.SubjectSelectionKey, settings.Overlays.Selection);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "应用设置失败：悬浮窗显示/外观");
        }
    }

    /// <summary>单窗应用：先即时应用外观与位置，再按 Visible 调 Show/Hide（此前只应用外观，从不显示窗口）。</summary>
    private async Task ApplyOverlayAsync(string overlayKey, CyberTechRep.Shared.Models.OverlayWindowSettings windowSettings)
    {
        await _windowController!.ApplySettingsAsync(overlayKey, windowSettings);
        if (windowSettings.Visible)
        {
            await _windowController.ShowAsync(overlayKey);
        }
        else
        {
            await _windowController.HideAsync(overlayKey);
        }
    }

    public void Dispose()
    {
        if (_handler is not null)
        {
            _settingsService.SettingsChanged -= _handler;
            _handler = null;
        }
    }
}
