using System.Runtime.Versioning;
using Avalonia.Threading;
using CyberTechRep.Plugin.Services.Files;
using CyberTechRep.Plugin.Services.Pipeline;
using CyberTechRep.Plugin.Services.Stores;
using CyberTechRep.Plugin.Views;
using CyberTechRep.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.Overlays;

/// <summary>
/// 未绑定学科选择待处理队列（需求 7，纯逻辑可脱离 UI 单测）：
/// FIFO 保存多位老师同时发消息触发的选择请求，保证「后到不覆盖先到、一条都不丢」。
/// <para>
/// 语义：
/// ① 入队按到达顺序；同一 MessageId（已在队列中或正在显示）不重复入队；
/// ② 待处理上限 <see cref="Capacity"/> 条，超出丢弃最旧一条（是否记日志由调用方决定）；
/// ③ 「正在显示」的条目单独持有（<see cref="Current"/>），不计入 <see cref="PendingCount"/>；
/// ④ 全部方法加锁，可从后台线程（消息管道）与 UI 线程（用户点选回调）并发调用。
/// </para>
/// </summary>
public sealed class SubjectSelectionQueue
{
    /// <summary>待处理请求上限：超出后丢弃最旧一条（防止无人值守时无限堆积）。</summary>
    public const int Capacity = 100;

    private readonly object _gate = new();
    private readonly Queue<SubjectSelectionRequest> _pending = new();
    private readonly HashSet<string> _knownMessageIds = new(StringComparer.Ordinal);
    private SubjectSelectionRequest? _current;

    /// <summary>待处理条数（不含正在显示的条目）。</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>是否有请求正在显示（显示中不覆盖内容，只排队）。</summary>
    public bool IsDisplaying
    {
        get
        {
            lock (_gate)
            {
                return _current is not null;
            }
        }
    }

    /// <summary>正在显示的请求（无则 null）。</summary>
    public SubjectSelectionRequest? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// 按到达顺序入队。同一 MessageId 已在队列中或正在显示时不重复入队；
    /// 待处理已达上限时先丢弃最旧一条（经 <see cref="SubjectSelectionEnqueueResult.Evicted"/> 返回）。
    /// </summary>
    public SubjectSelectionEnqueueResult Enqueue(SubjectSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            // 空 MessageId 无法判重（理论不会出现）：照常入队，不做去重
            var trackable = !string.IsNullOrWhiteSpace(request.MessageId);
            if (trackable && !_knownMessageIds.Add(request.MessageId))
            {
                return new SubjectSelectionEnqueueResult(Enqueued: false, IsDuplicate: true, Evicted: null);
            }

            SubjectSelectionRequest? evicted = null;
            if (_pending.Count >= Capacity)
            {
                evicted = _pending.Dequeue();
                _knownMessageIds.Remove(evicted.MessageId);
            }

            _pending.Enqueue(request);
            return new SubjectSelectionEnqueueResult(Enqueued: true, IsDuplicate: false, Evicted: evicted);
        }
    }

    /// <summary>
    /// 取出下一条待处理请求并标记为「正在显示」。
    /// 已有条目正在显示（<see cref="IsDisplaying"/>）或队列为空时返回 false（不改变状态）。
    /// </summary>
    public bool TryBeginDisplay(out SubjectSelectionRequest? request)
    {
        lock (_gate)
        {
            if (_current is not null || _pending.Count == 0)
            {
                request = null;
                return false;
            }

            _current = _pending.Dequeue();
            request = _current;
            return true;
        }
    }

    /// <summary>
    /// 完成正在显示的条目（用户点选/跳过，或装载失败丢弃）。
    /// 仅当 <paramref name="request"/> 与当前显示条目为同一 MessageId 时生效，避免错序推进。
    /// </summary>
    public bool TryComplete(SubjectSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            if (_current is null
                || !string.Equals(_current.MessageId, request.MessageId, StringComparison.Ordinal))
            {
                return false;
            }

            _knownMessageIds.Remove(_current.MessageId);
            _current = null;
            return true;
        }
    }
}

/// <summary>选择请求入队结果：是否入队、是否因重复 MessageId 被拒、因上限淘汰的最旧请求（无则 null）。</summary>
public readonly record struct SubjectSelectionEnqueueResult(
    bool Enqueued, bool IsDuplicate, SubjectSelectionRequest? Evicted);

/// <summary>
/// 未绑定学科选择协调器（需求 3 集成收口 + 需求 7 FIFO 排队）：
/// 订阅 <see cref="MessageDispatchService.SubjectSelectionRequired"/>（MemberSelection 模式下
/// 消息需学科分类但发送者未绑定学科时触发），把请求按到达顺序入队，逐条转交
/// <see cref="SubjectSelectionSuspensionWindow"/> 并经 <see cref="ISuspensionWindowController.ShowAsync"/>
/// 显示（与其他悬浮窗同一路径）。
/// <para>
/// 需求 7：后到请求不再覆盖先到请求的窗口内容——窗口一次只展示队首请求，其余排队
/// （窗口显示「待处理 N 条」）；用户点选/跳过当前条后自动装载下一条，全部处理完经控制器收起窗口。
/// </para>
/// 用户点选学科后：
/// ① 写回 <see cref="MemberSubjectBindingStore"/>（全局绑定，A 的绑定存储）；
/// ② 该消息的作业条目人工写回（<see cref="IHomeworkStore.SetSubjectAsync"/>，SubjectSource=Manual）；
/// ③ 该消息文件二次归档到所选学科目录。
/// ①②③ 全部完成（成功或异常）后才推进队列；全程 try/catch：任何失败只记日志，绝不影响消息主流程
/// （消息早已按现有降级语义落库），也绝不因为写回失败卡住后面的老师。
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

    /// <summary>UI 线程调度器（窗口实例与控件属性均为 UI 线程亲和对象；注入以便单测同步执行）。</summary>
    private readonly Func<Action, Task> _uiMarshal;
    private readonly ILogger _logger;

    /// <summary>待处理队列（FIFO + 去重 + 上限淘汰，内部自锁）。</summary>
    private readonly SubjectSelectionQueue _queue = new();

    /// <summary>「显示下一条 / 推进队列」串行闸：并发触发时保证同一时刻只有一条在处理。</summary>
    private readonly SemaphoreSlim _advanceGate = new(1, 1);

    private EventHandler<SubjectSelectionRequest>? _handler;

    public SubjectSelectionCoordinator(
        MessageDispatchService? dispatch = null,
        ISuspensionWindowController? controller = null,
        Func<SubjectSelectionSuspensionWindow?>? windowAccessor = null,
        MemberSubjectBindingStore? memberBindings = null,
        IHomeworkStore? homeworkStore = null,
        IFilePipelineService? filePipeline = null,
        ILogger? logger = null,
        Func<Action, Task>? uiMarshal = null)
    {
        _dispatch = dispatch;
        _controller = controller;
        _windowAccessor = windowAccessor ?? (() => null);
        _memberBindings = memberBindings;
        _homeworkStore = homeworkStore;
        _filePipeline = filePipeline;
        _uiMarshal = uiMarshal ?? (action => Dispatcher.UIThread.InvokeAsync(action).GetTask());
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

    /// <summary>
    /// 触发一次未绑定学科选择（需求 7：入队后逐条处理，绝不覆盖正在显示的请求）。
    /// <para>
    /// 流程：按到达顺序入队（同一 MessageId 去重、上限 100 淘汰最旧）→
    /// 当前没有正在显示的请求 → 出队装载并显示；已有正在显示的 → 只刷新窗口「待处理 N 条」计数
    /// （窗口若已被用户 × 收起，则重新弹出当前条，保证队列不卡死）。
    /// </para>
    /// <para>
    /// 窗口实例（DI 首次解析 <see cref="SubjectSelectionSuspensionWindow"/>）与控件属性均为
    /// UI 线程亲和对象：真机上消息处理在后台线程触发本方法，此前 <c>_windowAccessor()</c>
    /// 直接在后台线程构造 Avalonia Window 且位于 try 之外——抛出的「Call from invalid thread」
    /// 被 fire-and-forget 调用静默吞掉，表现为日志「已触发选择悬浮窗」但窗口永不显示
    /// （2026-09-06 真机日志缺陷）。统一经 UI 线程调度装载 + try/catch 留痕。
    /// 窗口为单例：内容随队首请求替换，同一时刻只存在一个实例。
    /// </para>
    /// </summary>
    public async Task ShowAsync(SubjectSelectionRequest request, CancellationToken ct = default)
    {
        if (request is null)
        {
            return;
        }

        var enqueue = _queue.Enqueue(request);
        if (enqueue.Evicted is { } evicted)
        {
            _logger.LogWarning(
                "未绑定学科选择待处理队列已达上限 {Capacity}，丢弃最旧请求（MessageId={MessageId}）",
                SubjectSelectionQueue.Capacity, evicted.MessageId);
        }

        if (!enqueue.Enqueued)
        {
            _logger.LogInformation(
                "未绑定学科选择请求已在队列中或正在显示，不重复入队（MessageId={MessageId}）", request.MessageId);
            return;
        }

        try
        {
            await _advanceGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_queue.IsDisplaying)
            {
                // 已有正在显示的请求：绝不覆盖窗口内容，只刷新待处理条数（并按需重新弹出）
                await RefreshPendingCountAsync(ct).ConfigureAwait(false);
                return;
            }

            await DisplayNextAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "选择悬浮窗装载请求失败（MessageId={MessageId}）", request.MessageId);
        }
        finally
        {
            _advanceGate.Release();
        }
    }

    /// <summary>
    /// 用户点选学科（悬浮窗回调）：写回成员绑定 + 该消息作业人工修正 + 文件二次归档，
    /// 全部完成后推进队列（装载下一条或收起窗口）。
    /// 绑定写入失败不阻断作业修正（各自独立、尽力而为）；学科为空视为「跳过」（不写绑定，仅推进）。
    /// </summary>
    public async Task ApplySelectionAsync(SubjectSelectionRequest request, string subject)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(subject))
            {
                // 空学科 = 用户点了「跳过（暂不绑定）」：不写任何绑定，仍须推进队列
                _logger.LogInformation(
                    "选择悬浮窗未选择学科（跳过，不写绑定；MessageId={MessageId}）", request.MessageId);
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
        finally
        {
            // 无论写回成功/失败/抛异常都要推进队列：否则后面的老师永远轮不到（需求 7「一条都不丢」）
            await AdvanceQueueAsync(request).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 用户点击「跳过（暂不绑定）」：不写任何绑定，直接完成当前条并推进队列
    /// （还有待处理 → 装载下一条；没有 → 经控制器收起窗口）。
    /// </summary>
    public Task SkipAsync(SubjectSelectionRequest request, CancellationToken ct = default) =>
        AdvanceQueueAsync(request, ct);

    /// <summary>
    /// 完成当前显示条目并推进队列（点选写回后 / 跳过 / 装载失败丢弃共用）。
    /// 全程 try/catch 只记日志：调用方是 UI 回调，任何异常都不允许打断队列。
    /// </summary>
    private async Task AdvanceQueueAsync(SubjectSelectionRequest request, CancellationToken ct = default)
    {
        if (request is null)
        {
            return;
        }

        try
        {
            await _advanceGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (!_queue.TryComplete(request))
            {
                // 非当前显示条目（重复点击/已被替换）：不推进，避免跳过后面的老师
                _logger.LogInformation(
                    "选择悬浮窗推进队列：该请求不是当前显示条目，忽略（MessageId={MessageId}）", request.MessageId);
                return;
            }

            await DisplayNextAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "选择悬浮窗推进队列失败（MessageId={MessageId}）", request.MessageId);
        }
        finally
        {
            _advanceGate.Release();
        }
    }

    /// <summary>
    /// 取出下一条待处理请求装载显示；队列已空时经控制器收起窗口。
    /// 装载失败（窗口实例不可用）的条目直接丢弃并尝试下一条，绝不让队列卡在无效条目上。
    /// </summary>
    private async Task DisplayNextAsync(CancellationToken ct)
    {
        while (_queue.TryBeginDisplay(out var request) && request is not null)
        {
            if (!await TryLoadRequestAsync(request).ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "未绑定学科选择请求装载失败，跳过该条（MessageId={MessageId}）", request.MessageId);
                _queue.TryComplete(request);
                continue;
            }

            await ShowWindowViaControllerAsync(request, ct).ConfigureAwait(false);
            return;
        }

        // 队列已空：用户已处理完所有待绑定请求 → 收起窗口
        await HideWindowViaControllerAsync(ct).ConfigureAwait(false);
    }

    /// <summary>经 UI 线程装载请求内容（窗口内容替换为队首请求 + 刷新待处理条数）。</summary>
    private async Task<bool> TryLoadRequestAsync(SubjectSelectionRequest request)
    {
        var loaded = false;
        try
        {
            // 已在 TryBeginDisplay 中出队（当前条不计入待处理），此处读到的是它后面的条数
            var pendingCount = _queue.PendingCount;
            await _uiMarshal(() =>
            {
                var window = _windowAccessor();
                if (window is null)
                {
                    return;
                }

                window.ShowRequest(request);
                window.UpdatePendingCount(pendingCount);
                loaded = true;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "选择悬浮窗装载请求失败（MessageId={MessageId}）", request.MessageId);
        }

        return loaded;
    }

    /// <summary>
    /// 已有请求正在显示时刷新「待处理 N 条」计数；
    /// 若窗口已被用户 × 收起，则重新弹出当前条（与标题栏「下次触发自动再弹出」语义一致，
    /// 同时避免队列停在「正在显示但窗口不可见」而卡死后续请求）。
    /// </summary>
    private async Task RefreshPendingCountAsync(CancellationToken ct)
    {
        var pendingCount = _queue.PendingCount;
        var needReshow = false;
        try
        {
            await _uiMarshal(() =>
            {
                var window = _windowAccessor();
                if (window is null)
                {
                    return;
                }

                window.UpdatePendingCount(pendingCount);
                needReshow = !window.IsVisible;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新选择悬浮窗待处理条数失败");
            return;
        }

        if (needReshow && _queue.Current is { } current)
        {
            await ShowWindowViaControllerAsync(current, ct).ConfigureAwait(false);
        }
    }

    private async Task ShowWindowViaControllerAsync(SubjectSelectionRequest request, CancellationToken ct)
    {
        if (_controller is null)
        {
            return;
        }

        try
        {
            await _controller.ShowAsync(SuspensionWindowController.SubjectSelectionKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "选择悬浮窗显示失败（MessageId={MessageId}）", request.MessageId);
        }
    }

    private async Task HideWindowViaControllerAsync(CancellationToken ct)
    {
        if (_controller is null)
        {
            return;
        }

        try
        {
            await _controller.HideAsync(SuspensionWindowController.SubjectSelectionKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "选择悬浮窗收起失败");
        }
    }
}
