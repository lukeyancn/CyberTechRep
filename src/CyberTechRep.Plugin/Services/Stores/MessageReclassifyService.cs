using System.Text.Json.Serialization;
using CyberTechRep.Plugin.Services.SubjectChain;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.Stores;

/// <summary>message-kind-overrides.json 顶层 DTO（需求 5：人工换类覆盖记录）。</summary>
internal sealed class MessageKindOverrideFileDto
{
    [JsonPropertyName("overrides")]
    public List<MessageKindOverrideEntry> Overrides { get; set; } = [];
}

/// <summary>单条换类覆盖记录（按消息 id 定位；重启后保持）。</summary>
public sealed class MessageKindOverrideEntry
{
    public string MessageId { get; set; } = "";

    /// <summary>人工指定的类型（通知 / 作业）。</summary>
    public MessageKind Kind { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// 消息类型覆盖存储（需求 5）：<c>message-kind-overrides.json</c>，JSON 原子写入 + .bak 损坏恢复。
/// <para>
/// 作用：同一消息被人工在「通知 ↔ 作业」之间换类后，协议端重投、重试重放、断点续传补齐
/// 一律按覆盖后的类型落档，绝不被识别链摆回原类型（重启后保持）。
/// 容量有界（默认最多 5000 条，超出按创建时间淘汰最旧），避免无限增长。
/// </para>
/// </summary>
public sealed class MessageKindOverrideStore
{
    /// <summary>覆盖记录上限（超出淘汰最旧；换类是低频人工操作，5000 条足够长期使用）。</summary>
    public const int MaxEntries = 5000;

    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private List<MessageKindOverrideEntry>? _entries;

    public MessageKindOverrideStore(string dataDirectory, ILogger? logger = null)
    {
        _filePath = Path.Combine(dataDirectory, "message-kind-overrides.json");
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>查询覆盖类型（未记录返回 false）。</summary>
    public bool TryGet(string messageId, out MessageKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        lock (_lock)
        {
            LoadIfNeeded();
            var entry = _entries!.LastOrDefault(e => string.Equals(e.MessageId, messageId, StringComparison.Ordinal));
            if (entry is null)
            {
                return false;
            }

            kind = entry.Kind;
            return true;
        }
    }

    /// <summary>写入/更新覆盖类型并持久化（同消息重复换类以最后一次为准）。</summary>
    public void Set(string messageId, MessageKind kind)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return;
        }

        lock (_lock)
        {
            LoadIfNeeded();
            _entries!.RemoveAll(e => string.Equals(e.MessageId, messageId, StringComparison.Ordinal));
            _entries.Add(new MessageKindOverrideEntry { MessageId = messageId, Kind = kind });

            if (_entries.Count > MaxEntries)
            {
                var removed = _entries.Count - MaxEntries;
                _entries = _entries.OrderByDescending(e => e.CreatedAt).Take(MaxEntries)
                    .OrderBy(e => e.CreatedAt)
                    .ToList();
                _logger.LogInformation("换类覆盖记录超出上限，已淘汰最旧 {Count} 条（上限 {Max}）", removed, MaxEntries);
            }

            Save();
            _logger.LogInformation("消息类型覆盖已记录（MessageId={MessageId}, Kind={Kind}）", messageId, kind);
        }
    }

    /// <summary>
    /// 批量写入覆盖类型（一次加锁、一次落盘）。整篇文档/合并条目换类时一次涉及多条来源消息，
    /// 逐条 <see cref="Set"/> 会重复落盘，且中途失败会留下"半个覆盖集"（其余来源消息重投时会被摆回原类型）。
    /// </summary>
    /// <returns>实际写入的条数（空白 id 被忽略、重复 id 去重）。</returns>
    public int SetMany(IEnumerable<string> messageIds, MessageKind kind)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        var ids = messageIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            return 0;
        }

        lock (_lock)
        {
            LoadIfNeeded();
            foreach (var messageId in ids)
            {
                _entries!.RemoveAll(e => string.Equals(e.MessageId, messageId, StringComparison.Ordinal));
                _entries.Add(new MessageKindOverrideEntry { MessageId = messageId, Kind = kind });
            }

            if (_entries.Count > MaxEntries)
            {
                var removed = _entries.Count - MaxEntries;
                _entries = _entries.OrderByDescending(e => e.CreatedAt).Take(MaxEntries)
                    .OrderBy(e => e.CreatedAt)
                    .ToList();
                _logger.LogInformation("换类覆盖记录超出上限，已淘汰最旧 {Count} 条（上限 {Max}）", removed, MaxEntries);
            }

            Save();
            _logger.LogInformation(
                "消息类型覆盖已批量记录（Count={Count}, Kind={Kind}, MessageIds={Ids}）",
                ids.Count, kind, string.Join(",", ids));
            return ids.Count;
        }
    }

    /// <summary>当前覆盖记录条数（诊断用）。</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                LoadIfNeeded();
                return _entries!.Count;
            }
        }
    }

    private void LoadIfNeeded()
    {
        if (_entries is not null)
        {
            return;
        }

        var dto = JsonStoreFile.LoadOrRestore<MessageKindOverrideFileDto>(_filePath, _logger);
        _entries = dto?.Overrides ?? [];
    }

    private void Save()
    {
        try
        {
            JsonStoreFile.SaveWithBackup(_filePath,
                JsonStoreFile.Serialize(new MessageKindOverrideFileDto { Overrides = _entries! }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "换类覆盖记录持久化失败（内存中状态保留）");
        }
    }
}

/// <summary>
/// 通知 / 作业手动互换服务（需求 5）：误识别的消息一键换类，连同内容、来源、时间元信息
/// 迁移到对方存档，并写入「消息类型覆盖」持久化记录（重启后保持，重投/续传不回摆）。
/// <para>
/// 迁移顺序保证不丢内容：先写入目标存档，成功后再从原存档删除；任一步失败即中止并记日志。
/// 同一消息的换类覆盖使后续重投/重放按覆盖后的类型落档。
/// </para>
/// </summary>
public sealed class MessageReclassifyService : IMessageReclassifyService
{
    private readonly INoticeStore _noticeStore;
    private readonly IHomeworkStore _homeworkStore;
    private readonly MessageKindOverrideStore _overrides;
    private readonly ILogger _logger;

    public MessageReclassifyService(
        INoticeStore noticeStore,
        IHomeworkStore homeworkStore,
        string dataDirectory,
        ILogger? logger = null,
        MessageKindOverrideStore? overrides = null)
    {
        _noticeStore = noticeStore ?? throw new ArgumentNullException(nameof(noticeStore));
        _homeworkStore = homeworkStore ?? throw new ArgumentNullException(nameof(homeworkStore));
        _logger = logger ?? NullLogger.Instance;
        _overrides = overrides ?? new MessageKindOverrideStore(dataDirectory, logger);
    }

    /// <inheritdoc />
    public async Task<bool> MoveNoticeToHomeworkAsync(Guid noticeId, string subject, CancellationToken ct = default)
    {
        var target = string.IsNullOrWhiteSpace(subject) ? HomeworkSubjectResolver.Unclassified : subject.Trim();
        var notice = (await _noticeStore.GetAllAsync(ct).ConfigureAwait(false)).FirstOrDefault(n => n.Id == noticeId);
        if (notice is null)
        {
            _logger.LogWarning("通知→作业换类失败：通知不存在 Id={Id}", noticeId);
            return false;
        }

        try
        {
            // ① 先写目标存档（作业条目 + 学科文档条目）
            await _homeworkStore.UpsertAsync(new HomeworkItem
            {
                MessageId = notice.MessageId,
                MemberOpenId = notice.MemberOpenId,
                Content = notice.Content,
                Subject = target,
                SubjectConfidence = 1.0,
                SubjectSource = SubjectSource.Manual,
                CreatedAt = notice.CreatedAt
            }, ct).ConfigureAwait(false);

            await _homeworkStore.AppendDocumentEntryAsync(target, new HomeworkDocumentEntry
            {
                SourceMessageIds = string.IsNullOrWhiteSpace(notice.MessageId) ? [] : [notice.MessageId],
                MemberOpenId = notice.MemberOpenId,
                SenderLabel = "",
                Text = notice.Content,
                CreatedAt = notice.CreatedAt
            }, ct).ConfigureAwait(false);

            // ② 目标写入成功后再删原存档（失败不丢内容）
            await _noticeStore.RemoveAsync(noticeId, ct).ConfigureAwait(false);

            // ③ 记录覆盖：后续重投/续传按作业落档
            _overrides.Set(notice.MessageId, MessageKind.Homework);
            _logger.LogInformation(
                "通知→作业换类完成（MessageId={MessageId}, Subject={Subject}, 来源通知 Id={NoticeId}）",
                notice.MessageId, target, noticeId);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "通知→作业换类失败（MessageId={MessageId}, 通知 Id={NoticeId}）", notice.MessageId, noticeId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> MoveHomeworkToNoticeAsync(Guid homeworkId, CancellationToken ct = default)
    {
        var item = (await _homeworkStore.GetAllAsync(ct).ConfigureAwait(false)).FirstOrDefault(h => h.Id == homeworkId);
        if (item is null)
        {
            _logger.LogWarning("作业→通知换类失败：作业不存在 Id={Id}", homeworkId);
            return false;
        }

        try
        {
            // ① 先写通知（内容、来源、时间元信息一并迁移；学科前缀由 NoticeStore 既有规则决定）
            await _noticeStore.AddOrUpdateAsync(item.MessageId, item.Content, item.MemberOpenId, null,
                item.CreatedAt, ct).ConfigureAwait(false);

            // ② 再从作业存档与学科文档移除（按来源消息 id 一并清文档条目）
            await _homeworkStore.RemoveByMessageIdAsync(item.MessageId, ct).ConfigureAwait(false);

            _overrides.Set(item.MessageId, MessageKind.Notice);
            _logger.LogInformation(
                "作业→通知换类完成（MessageId={MessageId}, 来源作业 Id={HomeworkId}, 原学科={Subject}）",
                item.MessageId, homeworkId, item.Subject);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "作业→通知换类失败（MessageId={MessageId}, 作业 Id={HomeworkId}）", item.MessageId, homeworkId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<int> MoveDocumentToNoticeAsync(DateOnly date, string subject, CancellationToken ct = default)
    {
        var normalized = string.IsNullOrWhiteSpace(subject) ? HomeworkSubjectResolver.Unclassified : subject.Trim();
        var document = (await _homeworkStore.GetDocumentsAsync(date, ct).ConfigureAwait(false))
            .FirstOrDefault(d => string.Equals(d.Subject, normalized, StringComparison.OrdinalIgnoreCase));
        if (document is null)
        {
            _logger.LogWarning("文档→通知换类失败：文档不存在 Subject={Subject}, Date={Date}", normalized, date);
            return 0;
        }

        var migrated = 0;
        try
        {
            // 覆盖记录的目标：整篇迁移会涉及条目里的**每一条**来源消息 id——
            // 只记第一条时，同一（合并）条目的其余来源消息在重投/断点续传补齐时
            // 会被识别链摆回作业（行为缺陷）。手工文本分支过去完全不记覆盖，
            // 原消息重投即在作业侧复现。
            var sourceIds = document.Entries
                .SelectMany(e => e.SourceMessageIds)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();

            if (document.ManualText is { Length: > 0 } manual)
            {
                // 手工编辑过：整篇作为一条通知（幂等键固定，重复操作不产生重复条目）。
                // 时间元信息取文档内最早条目的原始时间（无条目时回落当前时间）。
                var earliest = document.Entries.Count > 0
                    ? document.Entries.Min(e => e.CreatedAt)
                    : (DateTimeOffset?)null;
                await _noticeStore.AddOrUpdateAsync($"document:{date:yyyyMMdd}:{normalized}", manual, null, null,
                    earliest, ct).ConfigureAwait(false);
                migrated = 1;
            }
            else
            {
                foreach (var entry in document.Entries.Where(e => !string.IsNullOrWhiteSpace(e.Text)))
                {
                    // 幂等键优先用真实来源消息 id；无来源（如常态化作业条目）时用合成键
                    var messageId = entry.SourceMessageIds.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))
                        ?? $"document-entry:{entry.Id:N}";
                    await _noticeStore.AddOrUpdateAsync(messageId, entry.Text, entry.MemberOpenId, null,
                        entry.CreatedAt, ct).ConfigureAwait(false);
                    migrated++;
                }
            }

            // 逐条来源消息记录覆盖：断点续传补齐/重投时按通知落档，不回摆为作业
            _overrides.SetMany(sourceIds, MessageKind.Notice);

            await _homeworkStore.RemoveDocumentAsync(date, normalized, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "文档→通知换类完成（Subject={Subject}, Date={Date}, 迁移 {Count} 条，覆盖 {Overrides} 条来源消息）",
                normalized, date, migrated, sourceIds.Count);
            return migrated;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "文档→通知换类失败（Subject={Subject}, Date={Date}）", normalized, date);
            return migrated;
        }
    }

    /// <inheritdoc />
    public bool TryGetKindOverride(string messageId, out MessageKind kind) => _overrides.TryGet(messageId, out kind);
}
