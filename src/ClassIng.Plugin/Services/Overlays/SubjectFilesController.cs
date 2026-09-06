using Avalonia.Threading;
using ClassIng.Plugin.Views;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Services.Overlays;

/// <summary>
/// 学科圆圈启动器 ↔ 学科文件悬浮窗交互控制器：
/// <para>
/// 点击圆圈的 toggle 语义——当前文件悬浮窗已显示且展示同一学科 → 隐藏；
/// 展示其他学科或未显示 → 原地切换内容后显示（不闪关：窗口实例复用，仅改数据）。
/// 另负责：圆圈顺序调整持久化（上移/下移 → 写回 <see cref="SubjectCircleBarSettings.Order"/>）
/// 与设置变更后同步文件悬浮窗视图模式（大图标/详细列表）。
/// </para>
/// 全部公开方法线程安全：内部统一经 <see cref="Dispatcher.UIThread"/> 调度。
/// </summary>
public sealed class SubjectFilesController
{
    private readonly ISuspensionWindowController _overlays;
    private readonly ISettingsService _settingsService;
    private readonly Func<SubjectFilesSuspensionWindow> _filesWindowFactory;
    private readonly ILogger _logger;

    /// <summary>惰性共享的学科文件悬浮窗实例（与控制器窗口工厂同源，保证 toggle 判断同窗）。</summary>
    private SubjectFilesSuspensionWindow? _filesWindow;

    /// <summary>
    /// 上课联动来源标记：最近一次由 <see cref="OpenForClassAsync"/> 打开的学科（null=非联动打开）。
    /// 手动（圆圈点击）打开/切换会清空该标记，下课收起时只收联动窗，不误关手动窗。
    /// </summary>
    private string? _autoOpenedSubject;

    public SubjectFilesController(
        ISuspensionWindowController overlays,
        ISettingsService settingsService,
        Func<SubjectFilesSuspensionWindow> filesWindowFactory,
        ILogger? logger = null)
    {
        _overlays = overlays ?? throw new ArgumentNullException(nameof(overlays));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _filesWindowFactory = filesWindowFactory ?? throw new ArgumentNullException(nameof(filesWindowFactory));
        _logger = logger ?? NullLogger.Instance;

        // 设置变更（含导入/恢复默认）后同步视图模式到已创建的文件悬浮窗（无窗口则跳过）
        _settingsService.SettingsChanged += (_, _) =>
        {
            try
            {
                Dispatcher.UIThread.Post(() => _filesWindow?.ApplyViewMode());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "同步文件悬浮窗视图模式失败（已吞掉）");
            }
        };
    }

    /// <summary>
    /// 圆圈点击入口：同学科已显示 → 隐藏（toggle）；否则原地切换内容并显示。
    /// </summary>
    public Task ToggleOrSwitchAsync(string subject) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        try
        {
            var window = _filesWindow ??= _filesWindowFactory();
            var key = SuspensionWindowController.FilesKey;

            if (window.IsVisible && string.Equals(window.CurrentSubject, subject, StringComparison.Ordinal))
            {
                await _overlays.HideAsync(key);
                _autoOpenedSubject = null;
                _logger.LogDebug("圆圈点击：学科 {Subject} 已显示 → 隐藏文件悬浮窗", subject);
                return;
            }

            window.SetSubject(subject);
            window.ApplyViewMode();
            _autoOpenedSubject = null;
            await _overlays.ShowAsync(key);
            _logger.LogInformation("圆圈点击：文件悬浮窗切换到学科 {Subject}", subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "圆圈点击处理失败 Subject={Subject}（已吞掉，不影响圆圈栏运行）", subject);
        }
    });

    /// <summary>
    /// 上课联动打开入口：原地切换内容后显示（无 toggle 语义，重入幂等），
    /// 并记下联动来源标记（<see cref="HideIfAutoOpenedAsync"/> 只收这种窗，手动打开的不误关）。
    /// </summary>
    public Task OpenForClassAsync(string subject) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        try
        {
            var window = _filesWindow ??= _filesWindowFactory();
            window.SetSubject(subject);
            window.ApplyViewMode();
            _autoOpenedSubject = subject;
            await _overlays.ShowAsync(SuspensionWindowController.FilesKey);
            _logger.LogInformation("上课联动：文件悬浮窗已弹出并切换到学科 {Subject}", subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "上课联动打开文件悬浮窗失败 Subject={Subject}（已吞掉，不影响宿主）", subject);
        }
    });

    /// <summary>
    /// 下课/放学/切课收起入口：仅当文件悬浮窗正显示「联动打开」的学科时收起；
    /// 用户手动打开（圆圈点击）的窗不动。未创建过窗口或已隐藏时为 no-op。
    /// </summary>
    /// <param name="reason">收起原因（日志用，如「下课」「放学」「当前学科无文件」）。</param>
    public Task HideIfAutoOpenedAsync(string reason) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        try
        {
            var window = _filesWindow;
            if (window is null
                || !window.IsVisible
                || _autoOpenedSubject is null
                || !string.Equals(window.CurrentSubject, _autoOpenedSubject, StringComparison.Ordinal))
            {
                return;
            }

            _autoOpenedSubject = null;
            await _overlays.HideAsync(SuspensionWindowController.FilesKey);
            _logger.LogInformation("上课联动：已收起文件悬浮窗（{Reason}）", reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "上课联动收起文件悬浮窗失败（{Reason}，已吞掉，不影响宿主）", reason);
        }
    });

    /// <summary>
    /// 圆圈栏「上移/下移」重排（delta&lt;0 上移，delta&gt;0 下移，越界收敛到端点）：
    /// 以圆圈栏当前展示顺序为基准重排后整体写回 Order 并持久化（经 SettingsChanged 热生效）。
    /// </summary>
    public Task ReorderAsync(IReadOnlyList<string> displayedSubjects, string subject, int delta) =>
        Task.Run(async () =>
        {
            try
            {
                var circle = _settingsService.Current.Overlays.SubjectCircle;
                circle.Order = SubjectCircleOrder.Move(displayedSubjects, subject, delta);
                await _settingsService.SaveAsync();
                _logger.LogInformation("圆圈顺序已调整：{Subject} {Delta:+0;-0}", subject, delta);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "圆圈顺序持久化失败（内存态保留）");
            }
        });
}
