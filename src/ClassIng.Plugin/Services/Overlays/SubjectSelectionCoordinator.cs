using System.Runtime.Versioning;
using ClassIng.Plugin.Services.Files;
using ClassIng.Plugin.Services.Pipeline;
using ClassIng.Plugin.Services.Stores;
using ClassIng.Plugin.Views;
using ClassIng.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Services.Overlays;

/// <summary>
/// 未绑定学科选择协调器（需求 3 集成收口）：
/// 订阅 <see cref="MessageDispatchService.SubjectSelectionRequired"/>（MemberSelection 模式下
/// 消息需学科分类但发送者未绑定学科时触发），把请求转交 <see cref="SubjectSelectionSuspensionWindow"/>
/// 并经 <see cref="ISuspensionWindowController.ShowAsync"/> 显示（与其他悬浮窗同一路径）。
/// 用户点选学科后：
/// ① 写回 <see cref="MemberSubjectBindingStore"/>（全局绑定，A 的绑定存储）；
/// ② 该消息的作业条目人工写回（<see cref="IHomeworkStore.SetSubjectAsync"/>，SubjectSource=Manual）；
/// ③ 该消息文件二次归档到所选学科目录。
/// 全程 try/catch：任何失败只记日志，绝不影响消息主流程（消息早已按现有降级语义落库）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SubjectSelectionCoordinator : IDisposable
{
    private readonly MessageDispatchService? _dispatch;
    private readonly ISuspensionWindowController? _controller;
    private readonly Func<SubjectSelectionSuspensionWindow?> _windowAccessor;
    private readonly MemberSubjectBindingStore? _memberBindings;
    private readonly IHomeworkStore? _homeworkStore;
    private readonly IFilePipelineService? _filePipeline;
    private readonly ILogger _logger;
    private EventHandler<SubjectSelectionRequest>? _handler;

    public SubjectSelectionCoordinator(
        MessageDispatchService? dispatch = null,
        ISuspensionWindowController? controller = null,
        Func<SubjectSelectionSuspensionWindow?>? windowAccessor = null,
        MemberSubjectBindingStore? memberBindings = null,
        IHomeworkStore? homeworkStore = null,
        IFilePipelineService? filePipeline = null,
        ILogger? logger = null)
    {
        _dispatch = dispatch;
        _controller = controller;
        _windowAccessor = windowAccessor ?? (() => null);
        _memberBindings = memberBindings;
        _homeworkStore = homeworkStore;
        _filePipeline = filePipeline;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>接线事件订阅（由 Plugin 启动时调用；重复调用先退订旧订阅）。</summary>
    public void Attach()
    {
        if (_dispatch is null || _handler is not null)
        {
            return;
        }

        _handler = (_, request) =>
        {
            if (request is not null)
            {
                _ = ShowAsync(request, CancellationToken.None);
            }
        };
        _dispatch.SubjectSelectionRequired += _handler;
    }

    public void Dispose()
    {
        if (_dispatch is not null && _handler is not null)
        {
            _dispatch.SubjectSelectionRequired -= _handler;
            _handler = null;
        }
    }

    /// <summary>展示选择悬浮窗（窗口实例转交请求内容；显示失败只记日志）。</summary>
    public async Task ShowAsync(SubjectSelectionRequest request, CancellationToken ct = default)
    {
        var window = _windowAccessor();
        if (window is null)
        {
            _logger.LogWarning("未绑定学科选择悬浮窗实例不可用，本次触发跳过（MessageId={MessageId}）", request.MessageId);
            return;
        }

        try
        {
            window.ShowRequest(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "选择悬浮窗装载请求失败（MessageId={MessageId}）", request.MessageId);
        }

        if (_controller is not null)
        {
            try
            {
                await _controller.ShowAsync(SuspensionWindowController.SubjectSelectionKey, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "选择悬浮窗显示失败（MessageId={MessageId}）", request.MessageId);
            }
        }
    }

    /// <summary>
    /// 用户点选学科（悬浮窗回调）：写回成员绑定 + 该消息作业人工修正 + 文件二次归档。
    /// 绑定写入失败不阻断作业修正（各自独立、尽力而为）。
    /// </summary>
    public async Task ApplySelectionAsync(SubjectSelectionRequest request, string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return;
        }

        // ① 显式绑定写回（全局作用域：发送者学科身份按人生效；群作用域覆盖可经设置页管理 UI 手工补）
        if (_memberBindings is not null && !string.IsNullOrWhiteSpace(request.MemberOpenId))
        {
            try
            {
                _memberBindings.Set(request.MemberOpenId, subject);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "成员学科绑定写入失败（Member={Member}, Subject={Subject}）",
                    request.MemberOpenId, subject);
            }
        }

        // ② 该消息作业人工写回（与人工确认 Resolve 同一语义：SubjectSource=Manual 永不回退）
        if (_homeworkStore is not null)
        {
            try
            {
                var all = await _homeworkStore.GetAllAsync().ConfigureAwait(false);
                var homework = all.FirstOrDefault(h => h.MessageId == request.MessageId);
                if (homework is not null)
                {
                    await _homeworkStore.SetSubjectAsync(homework.Id, subject).ConfigureAwait(false);
                    _logger.LogInformation(
                        "选择悬浮窗已写回作业学科（MessageId={MessageId}, Subject={Subject}）",
                        request.MessageId, subject);
                }
                else
                {
                    _logger.LogInformation(
                        "选择悬浮窗写回：同 MessageId 作业条目不存在，跳过写回（MessageId={MessageId}）",
                        request.MessageId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "选择悬浮窗写回作业学科失败（MessageId={MessageId}）", request.MessageId);
            }
        }

        // ③ 文件二次归档（幂等；失败仅记日志）
        if (_filePipeline is not null)
        {
            try
            {
                var records = await _filePipeline.GetRecordsAsync().ConfigureAwait(false);
                foreach (var record in records.Where(r => r.MessageId == request.MessageId))
                {
                    try
                    {
                        await _filePipeline.ReassignSubjectAsync(record.Id, subject).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "选择悬浮窗文件二次归档失败（RecordId={RecordId}, Subject={Subject}）；该操作幂等",
                            record.Id, subject);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "选择悬浮窗读取文件记录失败（MessageId={MessageId}）", request.MessageId);
            }
        }
    }
}
