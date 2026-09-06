using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassIng.Plugin.Services.Classification;
using ClassIng.Plugin.Services.SubjectChain;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Services.Pipeline;

/// <summary>
/// 集成收口层：消息主数据流统一接线器（集成层组合器，不改变各模块自身实现）。
/// <para>
/// 数据流：<see cref="IMessageIngestService.MessageReceived"/> →
/// <see cref="IMessageClassifier.ClassifyAsync"/>（通知/作业二分）→
/// - 通知：<see cref="INoticeStore.AddOrUpdateAsync"/>；
/// - 作业：<see cref="ISubjectClassifierChain.ClassifyAsync"/> → <see cref="IHomeworkStore.UpsertAsync"/>
///   → 文件/图片/视频段 <see cref="IFilePipelineService.EnqueueAsync"/> → 学科确定后
///   <see cref="IFilePipelineService.ReassignSubjectAsync"/>；
/// - 人工确认 Resolve（<see cref="JsonPendingConfirmStore.Resolved"/>）→ <see cref="IHomeworkStore.SetSubjectAsync"/>
///   写回 + 文件二次归档；
/// - <see cref="IUpdateNotifyService.UpdateDetected"/> → <see cref="INoticeStore"/> 通知条目。
/// </para>
/// <para>
/// 容错纪律：任一环节失败只记结构化日志并将可重试操作投递 <see cref="IRetryQueueService"/>
/// （FileDownload / SubjectClassify / StoreWrite），绝不向事件源抛异常、绝不崩溃。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MessageDispatchService : IHostedService, IDisposable
{
    // 注意：RetryQueueService 以默认命名策略序列化 payload，此处保持一致（PascalCase），否则反序列化为空
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly IMessageIngestService? _ingest;
    private readonly IMessageClassifier? _classifier;
    private readonly ISubjectClassifierChain? _subjectChain;
    private readonly INoticeStore? _noticeStore;
    private readonly IHomeworkStore? _homeworkStore;
    private readonly IFilePipelineService? _filePipeline;
    private readonly Services.Maintenance.RetryQueueService? _retryQueue;
    private readonly IUpdateNotifyService? _updateNotify;
    private readonly JsonPendingConfirmStore? _pendingConfirmStore;
    private readonly INoKeywordFallbackClassifier? _noKeywordFallback;
    private readonly ILogger _logger;

    private EventHandler<MessageRecord>? _messageHandler;
    private EventHandler<UpdateInfo>? _updateHandler;
    private Func<PendingConfirmItem, CancellationToken, Task>? _resolvedHook;
    private SemaphoreSlim? _processingGate;
    private bool _disposed;

    public MessageDispatchService(
        IMessageIngestService? ingest = null,
        IMessageClassifier? classifier = null,
        ISubjectClassifierChain? subjectChain = null,
        INoticeStore? noticeStore = null,
        IHomeworkStore? homeworkStore = null,
        IFilePipelineService? filePipeline = null,
        Services.Maintenance.RetryQueueService? retryQueue = null,
        IUpdateNotifyService? updateNotify = null,
        JsonPendingConfirmStore? pendingConfirmStore = null,
        INoKeywordFallbackClassifier? noKeywordFallback = null,
        ILogger? logger = null)
    {
        _ingest = ingest;
        _classifier = classifier;
        _subjectChain = subjectChain;
        _noticeStore = noticeStore;
        _homeworkStore = homeworkStore;
        _filePipeline = filePipeline;
        _retryQueue = retryQueue;
        _updateNotify = updateNotify;
        _pendingConfirmStore = pendingConfirmStore;
        _noKeywordFallback = noKeywordFallback;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // ① 重试执行器注册（模块 8 预留注册点 → 真实执行器）
        _retryQueue?.RegisterExecutor(RetryOperationType.FileDownload, OnFileDownloadRetryAsync);
        _retryQueue?.RegisterExecutor(RetryOperationType.SubjectClassify, OnSubjectClassifyRetryAsync);
        _retryQueue?.RegisterExecutor(RetryOperationType.StoreWrite, OnStoreWriteRetryAsync);

        // ② 模块 3 预留的人工确认写回钩子 → HomeworkStore.SetSubjectAsync + 文件二次归档
        if (_pendingConfirmStore is not null)
        {
            _resolvedHook = OnPendingResolvedAsync;
            _pendingConfirmStore.Resolved += _resolvedHook;
        }

        // ③ 消息主数据流
        if (_ingest is not null)
        {
            _messageHandler = (_, message) =>
            {
                if (message is null)
                {
                    return;
                }

                // 事件源为同步回调：异步处理，异常在 ProcessMessageAsync 内部消化，绝不外抛
                _ = ProcessMessageAsync(message, CancellationToken.None);
            };
            _ingest.MessageReceived += _messageHandler;
        }

        // ④ 更新检测 → 通知悬浮窗
        if (_updateNotify is not null)
        {
            _updateHandler = (_, info) =>
            {
                if (info is not null)
                {
                    _ = HandleUpdateDetectedAsync(info, CancellationToken.None);
                }
            };
            _updateNotify.UpdateDetected += _updateHandler;
        }

        _processingGate = new SemaphoreSlim(1, 1);

        // ⑤ 拉起消息接入网关（此前无宿主启动点；失败不阻断插件启动）
        if (_ingest is not null)
        {
            _ = SafeStartIngestAsync(cancellationToken);
        }

        _logger.LogInformation(
            "消息主数据流接线器已启动：Ingest={Ingest}, Classifier={Classifier}, SubjectChain={Chain}, NoticeStore={Notice}, HomeworkStore={Homework}, FilePipeline={Files}, RetryQueue={Retry}, UpdateNotify={Update}",
            _ingest is not null, _classifier is not null, _subjectChain is not null,
            _noticeStore is not null, _homeworkStore is not null, _filePipeline is not null,
            _retryQueue is not null, _updateNotify is not null);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_ingest is not null && _messageHandler is not null)
        {
            _ingest.MessageReceived -= _messageHandler;
        }

        if (_updateNotify is not null && _updateHandler is not null)
        {
            _updateNotify.UpdateDetected -= _updateHandler;
        }

        if (_pendingConfirmStore is not null && _resolvedHook is not null)
        {
            _pendingConfirmStore.Resolved -= _resolvedHook;
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _processingGate?.Dispose();
    }

    // ============ 主数据流 ============

    /// <summary>
    /// 单条消息的完整处理流程。所有环节内部消化异常：任何失败只记日志 + 按需投递重试队列。
    /// internal 供单元测试直接驱动（事件订阅侧为 fire-and-forget 调用本方法）。
    /// </summary>
    internal async Task ProcessMessageAsync(MessageRecord message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var gate = _processingGate;
        if (gate is not null)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            await ProcessMessageCoreAsync(message, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("消息处理被取消（MessageId={MessageId}）", message.MessageId);
        }
        catch (Exception ex)
        {
            // 兜底：任何未预期异常只记日志，不向事件源传播
            _logger.LogError(ex, "消息处理发生未预期异常（MessageId={MessageId}），已忽略", message.MessageId);
        }
        finally
        {
            gate?.Release();
        }
    }

    private async Task ProcessMessageCoreAsync(MessageRecord message, CancellationToken ct)
    {
        // ① 通知/作业二分（模块 2；内部异常已返回 Unknown）
        ClassifiedMessage classified;
        try
        {
            classified = await _classifier!.ClassifyAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "消息分类失败（MessageId={MessageId}, GroupOpenId={GroupOpenId}），本条消息跳过",
                message.MessageId, message.GroupOpenId);
            return;
        }

        var text = KeywordMessageClassifier.ExtractPlainText(message.Segments);

        // ② 文件/图片/视频段 → 文件管道（与通知/作业类型无关，均入队）
        var attachmentIds = await EnqueueFileSegmentsAsync(message, ct).ConfigureAwait(false);

        switch (classified.Kind)
        {
            case MessageKind.Notice:
                await WriteNoticeAsync(message, text, ct).ConfigureAwait(false);
                break;

            case MessageKind.Homework:
                await ProcessHomeworkAsync(message, text, attachmentIds, ct)
                    .ConfigureAwait(false);
                break;

            default:
                // 需求 6 用途③：无关键词消息兜底识别（AiSettings.NoKeywordFallbackMode，默认 Off = 现状忽略）
                if (!await TryNoKeywordFallbackAsync(
                        message, text, attachmentIds, ct).ConfigureAwait(false))
                {
                    _logger.LogDebug(
                        "消息未分类，忽略（MessageId={MessageId}, GroupOpenId={GroupOpenId}, Reason={Reason}）",
                        message.MessageId, message.GroupOpenId, classified.MatchReason);
                }

                break;
        }
    }

    /// <summary>作业：学科链识别 → HomeworkStore 写入 → 文件二次归档到学科目录。</summary>
    private async Task ProcessHomeworkAsync(
        MessageRecord message, string text, IReadOnlyList<Guid> attachmentIds, CancellationToken ct)
    {
        var messageId = message.MessageId;
        var memberOpenId = message.MemberOpenId;
        SubjectResult subject;
        try
        {
            subject = await _subjectChain!.ClassifyAsync(text, messageId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "学科识别链失败（MessageId={MessageId}, GroupOpenId={GroupOpenId}），投递 SubjectClassify 重试",
                messageId, message.GroupOpenId);
            await EnqueueRetrySafeAsync(
                RetryOperationType.SubjectClassify,
                BuildSubjectClassifyPayload(messageId, text, memberOpenId),
                messageId).ConfigureAwait(false);
            return;
        }

        try
        {
            var item = new HomeworkItem
            {
                MessageId = messageId,
                MemberOpenId = memberOpenId,
                Content = text,
                Subject = subject.Subject,
                SubjectConfidence = subject.Confidence,
                SubjectSource = subject.Source,
                AttachmentIds = attachmentIds,
                CreatedAt = DateTimeOffset.Now
            };
            await _homeworkStore!.UpsertAsync(item, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "作业已写入存储（MessageId={MessageId}, GroupOpenId={GroupOpenId}, Subject={Subject}, Source={Source}, Confidence={Confidence}, Attachments={Count}）",
                messageId, message.GroupOpenId, subject.Subject, subject.Source, subject.Confidence, attachmentIds.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "作业写入存储失败（MessageId={MessageId}, GroupOpenId={GroupOpenId}），投递 StoreWrite 重试",
                messageId, message.GroupOpenId);
            await EnqueueRetrySafeAsync(
                RetryOperationType.StoreWrite,
                new StoreWritePayload(StoreWriteKind.HomeworkUpsert, messageId, text, subject.Subject, memberOpenId),
                messageId).ConfigureAwait(false);
        }

        await ReassignFilesSafeAsync(messageId, subject.Subject, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 需求 6 用途③：无关键词消息兜底识别。AI/学科链给出可信结果时按作业归档（含文件二次归档），
    /// 否则返回 false（调用方保持现状：忽略该消息）。识别失败不投重试队列——兜底不改变
    /// 「消息被忽略」的现状语义，避免对同一条无关键词消息无限重试。
    /// </summary>
    private async Task<bool> TryNoKeywordFallbackAsync(
        MessageRecord message, string text, IReadOnlyList<Guid> attachmentIds, CancellationToken ct)
    {
        var messageId = message.MessageId;
        var memberOpenId = message.MemberOpenId;
        if (_noKeywordFallback is null || _homeworkStore is null || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        SubjectResult? subject;
        try
        {
            subject = await _noKeywordFallback.ClassifyAsync(text, messageId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "无关键词兜底识别异常（MessageId={MessageId}, GroupOpenId={GroupOpenId}），保持现状忽略该消息",
                messageId, message.GroupOpenId);
            return false;
        }

        if (subject is null)
        {
            return false;
        }

        try
        {
            await _homeworkStore.UpsertAsync(new HomeworkItem
            {
                MessageId = messageId,
                MemberOpenId = memberOpenId,
                Content = text,
                Subject = subject.Subject,
                SubjectConfidence = subject.Confidence,
                SubjectSource = subject.Source,
                AttachmentIds = attachmentIds,
                CreatedAt = DateTimeOffset.Now
            }, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "无关键词兜底作业已写入存储（MessageId={MessageId}, GroupOpenId={GroupOpenId}, Subject={Subject}, Source={Source}, Confidence={Confidence}）",
                messageId, message.GroupOpenId, subject.Subject, subject.Source, subject.Confidence);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "无关键词兜底作业写入存储失败（MessageId={MessageId}, GroupOpenId={GroupOpenId}），投递 StoreWrite 重试",
                messageId, message.GroupOpenId);
            await EnqueueRetrySafeAsync(
                RetryOperationType.StoreWrite,
                new StoreWritePayload(StoreWriteKind.HomeworkUpsert, messageId, text, subject.Subject, memberOpenId),
                messageId).ConfigureAwait(false);
        }

        await ReassignFilesSafeAsync(messageId, subject.Subject, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>通知：写 NoticeStore（悬浮窗经 Changed 自动刷新）。memberOpenId 用于学科前缀（有映射时写入内容前附加「学科：」）。</summary>
    private async Task WriteNoticeAsync(MessageRecord message, string text, CancellationToken ct)
    {
        var messageId = message.MessageId;
        var memberOpenId = message.MemberOpenId;
        try
        {
            await _noticeStore!.AddOrUpdateAsync(messageId, text, memberOpenId, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "通知已写入存储（MessageId={MessageId}, GroupOpenId={GroupOpenId}, Length={Length}）",
                messageId, message.GroupOpenId, text.Length);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "通知写入存储失败（MessageId={MessageId}, GroupOpenId={GroupOpenId}），投递 StoreWrite 重试",
                messageId, message.GroupOpenId);
            await EnqueueRetrySafeAsync(
                RetryOperationType.StoreWrite,
                // 携带 MemberOpenId：重放时学科前缀语义与首次写入一致
                new StoreWritePayload(StoreWriteKind.NoticeUpsert, messageId, text, null, memberOpenId),
                messageId).ConfigureAwait(false);
        }
    }

    /// <summary>消息中的文件/图片/视频段（携带直链）→ 文件管道入队；失败投递 FileDownload 重试。</summary>
    private async Task<IReadOnlyList<Guid>> EnqueueFileSegmentsAsync(MessageRecord message, CancellationToken ct)
    {
        var ids = new List<Guid>();
        var index = 0;
        foreach (var segment in message.Segments)
        {
            index++;
            if (segment?.Url is null || string.IsNullOrWhiteSpace(segment.Url))
            {
                continue;
            }

            var isAttachment = segment.Type is SegmentTypes.File or SegmentTypes.Image or SegmentTypes.Video;
            if (!isAttachment)
            {
                continue;
            }

            var fileName = ResolveFileName(segment, message.MessageId, index);
            try
            {
                var record = await _filePipeline!.EnqueueAsync(message.MessageId, fileName, segment.Url, ct)
                    .ConfigureAwait(false);
                ids.Add(record.Id);
                _logger.LogInformation(
                    "附件已入队文件管道（MessageId={MessageId}, GroupOpenId={GroupOpenId}, File={File}, RecordId={RecordId}, Status={Status}）",
                    message.MessageId, message.GroupOpenId, fileName, record.Id, record.Status);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "附件入队失败（MessageId={MessageId}, GroupOpenId={GroupOpenId}, File={File}），投递 FileDownload 重试",
                    message.MessageId, message.GroupOpenId, fileName);
                await EnqueueRetrySafeAsync(
                    RetryOperationType.FileDownload,
                    BuildFileDownloadPayload(message.MessageId, fileName, segment.Url),
                    message.MessageId).ConfigureAwait(false);
            }
        }

        return ids;
    }

    /// <summary>学科确定后把该消息的文件二次归档到学科目录（幂等；失败仅记日志，可由后续 Resolve/重试再次触发）。</summary>
    private async Task ReassignFilesSafeAsync(string messageId, string subject, CancellationToken ct)
    {
        if (_filePipeline is null || string.IsNullOrWhiteSpace(subject))
        {
            return;
        }

        try
        {
            var records = await _filePipeline.GetRecordsAsync(ct).ConfigureAwait(false);
            foreach (var record in records.Where(r => r.MessageId == messageId))
            {
                try
                {
                    await _filePipeline.ReassignSubjectAsync(record.Id, subject, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "文件二次归档失败（RecordId={RecordId}, Subject={Subject}）；该操作幂等，将由后续人工确认/重试再次触发",
                        record.Id, subject);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "文件记录读取失败，跳过二次归档（MessageId={MessageId}）", messageId);
        }
    }

    // ============ 人工确认写回（模块 3 Resolved 钩子）============

    private async Task OnPendingResolvedAsync(PendingConfirmItem item, CancellationToken ct)
    {
        var subject = item.ResolvedSubject;
        if (string.IsNullOrWhiteSpace(subject) || _homeworkStore is null)
        {
            return;
        }

        try
        {
            // 同 MessageId 定位作业条目并人工写回（SetSubjectAsync 记 SubjectSource=Manual）
            var all = await _homeworkStore.GetAllAsync(ct).ConfigureAwait(false);
            var homework = all.FirstOrDefault(h => h.MessageId == item.MessageId);
            if (homework is null)
            {
                _logger.LogWarning(
                    "人工确认写回：未找到同 MessageId 作业条目（MessageId={MessageId}, Subject={Subject}）",
                    item.MessageId, subject);
                return;
            }

            await _homeworkStore.SetSubjectAsync(homework.Id, subject, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "人工确认已写回作业（HomeworkId={HomeworkId}, Subject={Subject}）", homework.Id, subject);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "人工确认写回作业失败（MessageId={MessageId}），投递 StoreWrite 重试", item.MessageId);
            await EnqueueRetrySafeAsync(
                RetryOperationType.StoreWrite,
                new StoreWritePayload(StoreWriteKind.HomeworkSetSubject, item.MessageId, null, subject),
                item.MessageId).ConfigureAwait(false);
        }

        // 学科最终确定：该消息文件二次归档
        await ReassignFilesSafeAsync(item.MessageId, subject, ct).ConfigureAwait(false);
    }

    // ============ 更新检测 → 通知 ============

    private async Task HandleUpdateDetectedAsync(UpdateInfo info, CancellationToken ct)
    {
        if (_noticeStore is null)
        {
            return;
        }

        var content = string.IsNullOrWhiteSpace(info.ReleaseNotes)
            ? $"发现新版本 v{info.LatestVersion}（当前 v{info.CurrentVersion}），请前往发布页下载：{info.DownloadUrl}"
            : $"发现新版本 v{info.LatestVersion}（当前 v{info.CurrentVersion}）：{info.ReleaseNotes}";
        try
        {
            // 幂等键：同版本只产生一条通知
            await _noticeStore.AddOrUpdateAsync($"update:{info.LatestVersion}", content, null, ct).ConfigureAwait(false);
            _logger.LogInformation("更新提示已写入通知（Version={Version}）", info.LatestVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新提示写入通知失败（Version={Version}），投递 StoreWrite 重试", info.LatestVersion);
            await EnqueueRetrySafeAsync(
                RetryOperationType.StoreWrite,
                new StoreWritePayload(StoreWriteKind.NoticeUpsert, $"update:{info.LatestVersion}", content, null),
                $"update:{info.LatestVersion}").ConfigureAwait(false);
        }
    }

    // ============ 重试执行器（模块 8 注册点）============

    private async Task<bool> OnFileDownloadRetryAsync(string payloadJson, CancellationToken ct)
    {
        FileDownloadPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<FileDownloadPayload>(payloadJson, PayloadJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "FileDownload 重试载荷解析失败，条目作废：{Payload}", payloadJson);
            return true; // 载荷不可恢复：标记成功避免无限退避（已在日志留痕）
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Url) || _filePipeline is null)
        {
            _logger.LogWarning("FileDownload 重试载荷不完整（缺 Url 或文件管道未注册）");
            return false;
        }

        var record = await _filePipeline
            .EnqueueAsync(payload.MessageId, payload.FileName, payload.Url, ct)
            .ConfigureAwait(false);
        var success = record.Status is FileStatus.Archived or FileStatus.Duplicate;
        _logger.LogInformation(
            "FileDownload 重试执行（MessageId={MessageId}, File={File}）→ {Status}",
            payload.MessageId, payload.FileName, record.Status);
        return success;
    }

    private async Task<bool> OnSubjectClassifyRetryAsync(string payloadJson, CancellationToken ct)
    {
        SubjectClassifyPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SubjectClassifyPayload>(payloadJson, PayloadJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "SubjectClassify 重试载荷解析失败，条目作废：{Payload}", payloadJson);
            return true;
        }

        if (payload is null || _subjectChain is null || _homeworkStore is null)
        {
            _logger.LogWarning("SubjectClassify 重试前置条件不满足（链/存储未注册）");
            return false;
        }

        SubjectResult result;
        try
        {
            result = await _subjectChain.ClassifyAsync(payload.Text ?? "", payload.MessageId ?? "", ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SubjectClassify 重试执行异常（MessageId={MessageId}）", payload.MessageId);
            return false;
        }

        try
        {
            var attachments = await GetAttachmentIdsSafeAsync(payload.MessageId ?? "", ct).ConfigureAwait(false);
            var item = new HomeworkItem
            {
                MessageId = payload.MessageId ?? "",
                MemberOpenId = payload.MemberOpenId ?? "",
                Content = payload.Text ?? "",
                Subject = result.Subject,
                SubjectConfidence = result.Confidence,
                SubjectSource = result.Source,
                AttachmentIds = attachments,
                CreatedAt = DateTimeOffset.Now
            };
            await _homeworkStore.UpsertAsync(item, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SubjectClassify 重试写回作业失败（MessageId={MessageId}）", payload.MessageId);
            return false;
        }

        // 仍需人工确认 → 视为未完成（继续退避重试；投递人工队列由链内幂等处理）
        return !result.NeedsManualConfirm;
    }

    /// <summary>StoreWrite 重试执行器：按载荷种类重放通知/作业写入。</summary>
    private async Task<bool> OnStoreWriteRetryAsync(string payloadJson, CancellationToken ct)
    {
        StoreWritePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<StoreWritePayload>(payloadJson, PayloadJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "StoreWrite 重试载荷解析失败，条目作废：{Payload}", payloadJson);
            return true;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.MessageId))
        {
            _logger.LogWarning("StoreWrite 重试载荷不完整，条目作废");
            return true;
        }

        try
        {
            switch (payload.Kind)
            {
                case StoreWriteKind.NoticeUpsert when _noticeStore is not null:
                    await _noticeStore.AddOrUpdateAsync(payload.MessageId, payload.Content ?? "", payload.MemberOpenId, ct)
                        .ConfigureAwait(false);
                    break;

                case StoreWriteKind.HomeworkUpsert when _subjectChain is not null && _homeworkStore is not null:
                {
                    var result = await _subjectChain
                        .ClassifyAsync(payload.Content ?? "", payload.MessageId, ct).ConfigureAwait(false);
                    var attachments = await GetAttachmentIdsSafeAsync(payload.MessageId, ct).ConfigureAwait(false);
                    await _homeworkStore.UpsertAsync(new HomeworkItem
                    {
                        MessageId = payload.MessageId,
                        MemberOpenId = payload.MemberOpenId ?? "",
                        Content = payload.Content ?? "",
                        Subject = result.Subject,
                        SubjectConfidence = result.Confidence,
                        SubjectSource = result.Source,
                        AttachmentIds = attachments,
                        CreatedAt = DateTimeOffset.Now
                    }, ct).ConfigureAwait(false);
                    break;
                }

                case StoreWriteKind.HomeworkSetSubject when _homeworkStore is not null:
                {
                    var all = await _homeworkStore.GetAllAsync(ct).ConfigureAwait(false);
                    var homework = all.FirstOrDefault(h => h.MessageId == payload.MessageId);
                    if (homework is not null && !string.IsNullOrWhiteSpace(payload.Subject))
                    {
                        await _homeworkStore.SetSubjectAsync(homework.Id, payload.Subject!, ct)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "StoreWrite 重试：作业条目不存在或学科为空（MessageId={MessageId}）", payload.MessageId);
                        return false;
                    }

                    break;
                }

                default:
                    _logger.LogWarning(
                        "StoreWrite 重试：载荷种类 {Kind} 与已注册服务不匹配，无法重放", payload.Kind);
                    return false;
            }

            _logger.LogInformation("StoreWrite 重放成功（Kind={Kind}, MessageId={MessageId}）",
                payload.Kind, payload.MessageId);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StoreWrite 重放失败（Kind={Kind}, MessageId={MessageId}）",
                payload.Kind, payload.MessageId);
            return false;
        }
    }

    private async Task<IReadOnlyList<Guid>> GetAttachmentIdsSafeAsync(string messageId, CancellationToken ct)
    {
        try
        {
            if (_filePipeline is null)
            {
                return [];
            }

            var records = await _filePipeline.GetRecordsAsync(ct).ConfigureAwait(false);
            return records.Where(r => r.MessageId == messageId).Select(r => r.Id).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取附件记录失败（MessageId={MessageId}），作业条目不含附件", messageId);
            return [];
        }
    }

    /// <summary>重试队列入队（尽力而为：队列本身失败只记日志，不阻断主流程）。</summary>
    private async Task EnqueueRetrySafeAsync(RetryOperationType type, object payload, string messageId)
    {
        if (_retryQueue is null)
        {
            _logger.LogWarning("重试队列未注册，失败操作无法重试（Type={Type}, MessageId={MessageId}）", type, messageId);
            return;
        }

        try
        {
            await _retryQueue.EnqueueAsync(type, payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重试队列入队失败（Type={Type}, MessageId={MessageId}）", type, messageId);
        }
    }

    // ============ 辅助 ============

    private async Task SafeStartIngestAsync(CancellationToken ct)
    {
        try
        {
            await _ingest!.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "消息接入服务启动失败（不影响插件其余功能，可经排错面板重连）");
        }
    }

    private static string ResolveFileName(MessageSegment segment, string messageId, int index)
    {
        if (!string.IsNullOrWhiteSpace(segment.FileName))
        {
            return segment.FileName.Trim();
        }

        // 无文件名段（如图片）：从 URL 末段推断，再做管道侧安全校验兜底
        try
        {
            var path = new Uri(segment.Url!).AbsolutePath;
            var name = Uri.UnescapeDataString(Path.GetFileName(path));
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        catch
        {
            // URL 非法：走序号兜底名
        }

        return $"attachment-{messageId}-{index}";
    }

    // ============ 重试载荷（脱敏快照；internal 供测试构造同构 JSON）============

    internal enum StoreWriteKind
    {
        NoticeUpsert,
        HomeworkUpsert,
        HomeworkSetSubject
    }

    internal sealed record FileDownloadPayload(string MessageId, string FileName, string Url);

    internal sealed record SubjectClassifyPayload(string MessageId, string Text, string? MemberOpenId = null);

    internal sealed record StoreWritePayload(
        StoreWriteKind Kind, string MessageId, string? Content, string? Subject, string? MemberOpenId = null);

    internal static string BuildFileDownloadPayload(string messageId, string fileName, string url) =>
        JsonSerializer.Serialize(new FileDownloadPayload(messageId, fileName, url), PayloadJsonOptions);

    internal static string BuildSubjectClassifyPayload(string messageId, string text, string memberOpenId = "") =>
        JsonSerializer.Serialize(new SubjectClassifyPayload(messageId, text, memberOpenId), PayloadJsonOptions);

    /// <summary>重试载荷序列化选项（internal 供单元测试复用）。</summary>
    internal static JsonSerializerOptions PayloadSerializerOptions => PayloadJsonOptions;
}
