using ClassIng.Plugin.Services.Classification;
using ClassIng.Plugin.Services.SubjectChain;
using ClassIng.Shared.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Services.Maintenance;

/// <summary>
/// 模块 7：设置变更热生效接线器。
/// <para>
/// 订阅 <see cref="ISettingsService.SettingsChanged"/>，调用各服务已存在的热更新方法：
/// - <see cref="KeywordMessageClassifier.ReloadRules"/>：通知/作业关键词表重载；
/// - <see cref="KeywordSubjectClassifier.ReloadRules"/>：学科词表重载；
/// - 连接/文件等设置通过各 OptionsProvider 的 GetSettings 委托读取
///   <see cref="ISettingsService.Current"/>，天然热生效（下次使用时生效）；
/// - 悬浮窗（模块 6）：<see cref="ISuspensionWindowController.ApplySettingsAsync"/> 即时应用
///   透明度/字号/位置等（控制器未注册时跳过）。
/// </para>
/// </summary>
public sealed class SettingsChangeApplier : IHostedService, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly IMessageClassifier? _messageClassifier;
    private readonly KeywordSubjectClassifier? _subjectClassifier;
    private readonly ISuspensionWindowController? _windowController;
    private readonly ILogger _logger;

    private EventHandler<ClassIng.Shared.Models.AppSettings>? _handler;

    public SettingsChangeApplier(ISettingsService settingsService,
        IMessageClassifier? messageClassifier = null,
        KeywordSubjectClassifier? subjectClassifier = null,
        ISuspensionWindowController? windowController = null,
        ILogger? logger = null)
    {
        _settingsService = settingsService;
        _messageClassifier = messageClassifier;
        _subjectClassifier = subjectClassifier;
        _windowController = windowController;
        _logger = logger ?? NullLogger.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _handler = (_, settings) => Apply(settings);
        _settingsService.SettingsChanged += _handler;
        _logger.LogInformation("设置热生效接线器已启动");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_handler is not null)
        {
            _settingsService.SettingsChanged -= _handler;
            _handler = null;
        }

        return Task.CompletedTask;
    }

    private void Apply(ClassIng.Shared.Models.AppSettings settings)
    {
        _logger.LogInformation(
            "设置变更，开始热生效：LogLevel={LogLevel}，AiEnabled={AiEnabled}",
            settings.Maintenance.LogLevel, settings.Classification.AiEnabled);

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
    }

    private async Task ApplyOverlaysAsync(ClassIng.Shared.Models.AppSettings settings)
    {
        try
        {
            await _windowController!.ApplySettingsAsync("notice", settings.Overlays.Notice)
                .ConfigureAwait(false);
            await _windowController.ApplySettingsAsync("homework", settings.Overlays.Homework)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "应用设置失败：悬浮窗 ApplySettingsAsync");
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
