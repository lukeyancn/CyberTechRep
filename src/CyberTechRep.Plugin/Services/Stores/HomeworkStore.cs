using System.Text.Json.Serialization;
using CyberTechRep.Plugin.Services.SubjectChain;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.Stores;

/// <summary>homework.json 顶层 DTO。</summary>
internal sealed class HomeworkFileDto
{
    [JsonPropertyName("items")]
    public List<HomeworkItem> Items { get; set; } = [];

    /// <summary>
    /// 学科文档（需求 1）。旧存档（仅 items）加载时按 items 一次性重建，向后兼容迁移，不丢数据。
    /// </summary>
    [JsonPropertyName("documents")]
    public List<HomeworkDocument> Documents { get; set; } = [];
}

/// <summary>
/// 作业存储（homework.json，JSON 原子写入 + .bak 损坏恢复，进程内锁串行化）。
/// <para>
/// 幂等：同 MessageId 重复 Upsert 合并为单条（保留原 Id/CreatedAt，更新学科与正文）；
/// <see cref="IHomeworkStore.SetSubjectAsync"/> 人工修正写回（SubjectSource=Manual），
/// 人工修正永不回退（协议端重投不覆盖）；「未分类」作业被人工指定学科后，
/// 记录按发送者的永久学科规则（<see cref="UserSubjectRuleStore"/>）。
/// </para>
/// <para>
/// 学科判定优先级矩阵（<see cref="HomeworkSubjectResolver"/>）：
/// ①显式人工修正永不回退；②识别链正常命中 → 采用识别链结果，<b>不用映射覆盖</b>；
/// ③识别链「未分类」且发送者有映射 → 套用映射（SubjectSource=Manual 语义保留）；④映射只在
/// 「人工修正未分类作业」这一个入口学习（SetSubjectAsync 门控保持）。
/// </para>
/// <para>
/// 需求 1：每个学科、每个归档日维护一份<b>连续文档</b>（<see cref="HomeworkDocument"/>），
/// 消息到达时经 <see cref="HomeworkDocumentMerger"/> 决策「新增 / 跳过重复 / 并集合并」后追加；
/// 用户就地编辑的整篇文本存于 <see cref="HomeworkDocument.ManualText"/>，与追加写入共用同一存档，
/// 以存档为单一事实源双向同步（编辑期间到达的新消息按「末尾增量追加」并入 ManualText，不丢不覆盖）。
/// </para>
/// <para>按天归档与保留期清理见 <see cref="RetentionPolicies"/>（逻辑分桶，启动/跨天清理）。</para>
/// </summary>
public sealed class HomeworkStore : IHomeworkStore
{
    private readonly string _filePath;
    private readonly UserSubjectRuleStore _userRules;
    private readonly ILogger _logger;
    private readonly Func<int>? _homeworkRetentionDays;
    private readonly object _lock = new();
    private List<HomeworkItem>? _items;
    private List<HomeworkDocument>? _documents;

    public HomeworkStore(
        string dataDirectory,
        ILogger? logger = null,
        Func<int>? homeworkRetentionDays = null,
        UserSubjectRuleStore? userRules = null)
    {
        _filePath = Path.Combine(dataDirectory, "homework.json");
        _userRules = userRules ?? new UserSubjectRuleStore(dataDirectory, logger);
        _homeworkRetentionDays = homeworkRetentionDays;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public event EventHandler<HomeworkItem>? Changed;

    /// <inheritdoc />
    public event EventHandler<HomeworkDocument>? DocumentChanged;

    /// <inheritdoc />
    public Task<HomeworkItem> UpsertAsync(HomeworkItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        HomeworkItem stored;
        bool changed;
        lock (_lock)
        {
            LoadIfNeeded();

            // 同 Id → 整条替换；同 MessageId（不同 Id，协议端重投）→ 幂等合并为单条
            var existing = _items!.Find(i => i.Id == item.Id)
                ?? _items.Find(i => i.MessageId == item.MessageId);
            if (existing is null)
            {
                stored = ApplyUserRule(item);
                _items!.Add(stored);
                changed = true;
                _logger.LogInformation("作业已入列 Id={Id}, MessageId={MessageId}, Subject={Subject}",
                    item.Id, item.MessageId, item.Subject);
            }
            else
            {
                stored = Merge(existing, item);
                if (ReferenceEquals(stored, existing))
                {
                    changed = false;
                }
                else
                {
                    _items![_items.IndexOf(existing)] = stored;
                    changed = true;
                    _logger.LogInformation("作业更新（幂等复用）Id={Id}, MessageId={MessageId}", stored.Id, stored.MessageId);
                }
            }

            if (changed)
            {
                Save();
            }
        }

        if (changed)
        {
            RaiseChanged(stored);
        }

        return Task.FromResult(stored);
    }

    /// <inheritdoc />
    public Task SetSubjectAsync(Guid id, string subject, CancellationToken ct = default)
    {
        HomeworkItem? updated = null;
        lock (_lock)
        {
            LoadIfNeeded();
            var index = _items!.FindIndex(i => i.Id == id);
            if (index < 0)
            {
                _logger.LogWarning("修正学科失败：作业不存在 Id={Id}", id);
                return Task.CompletedTask;
            }

            var existing = _items[index];
            if (string.Equals(existing.Subject, subject, StringComparison.Ordinal)
                && existing.SubjectSource == SubjectSource.Manual)
            {
                return Task.CompletedTask;
            }

            updated = new HomeworkItem
            {
                Id = existing.Id,
                MessageId = existing.MessageId,
                MemberOpenId = existing.MemberOpenId,
                Subject = subject ?? "未分类",
                SubjectConfidence = 1.0,
                SubjectSource = SubjectSource.Manual,
                Content = existing.Content,
                AttachmentIds = existing.AttachmentIds,
                CreatedAt = existing.CreatedAt,
                IsResolved = existing.IsResolved
            };
            _items[index] = updated;

            // 需求 1：学科变更时把该消息的文档条目一并迁到新学科文档（存档与展示同步）
            if (!string.Equals(existing.Subject, updated.Subject, StringComparison.OrdinalIgnoreCase))
            {
                MoveDocumentEntry(existing.MessageId, existing.Subject, updated.Subject, existing.CreatedAt);
            }

            // 「无法分类（未分类）→ 人工指定」触发按发送者规则学习（唯一学习入口，门控保持），
            // 并同步该成员历史作业。同步范围限定：仅「未分类」条目与此前按旧映射归类的条目
            // （识别链正常命中的历史作业不覆盖——与优先级矩阵②一致）。
            var propagated = 0;
            var wasUnclassified = string.IsNullOrWhiteSpace(existing.Subject)
                || string.Equals(existing.Subject, "未分类", StringComparison.Ordinal);
            if (wasUnclassified && !string.IsNullOrWhiteSpace(existing.MemberOpenId))
            {
                var previousRule = _userRules.Get(existing.MemberOpenId);
                _userRules.Set(existing.MemberOpenId, updated.Subject);
                for (var i = 0; i < _items.Count; i++)
                {
                    if (i == index
                        || !string.Equals(_items[i].MemberOpenId, existing.MemberOpenId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var overwrite = HomeworkSubjectResolver.IsUnclassified(_items[i].Subject)
                        || (previousRule is not null
                            && string.Equals(_items[i].Subject, previousRule, StringComparison.Ordinal));
                    if (!overwrite)
                    {
                        continue;
                    }

                    _items[i] = CloneWithSubject(_items[i], updated.Subject);
                    MoveDocumentEntry(_items[i].MessageId, null, updated.Subject, _items[i].CreatedAt);
                    propagated++;
                }
            }

            Save();
            _logger.LogInformation("作业学科已人工修正并持久化 Id={Id}, Subject={Subject}", id, updated.Subject);
            if (propagated > 0)
            {
                _logger.LogInformation(
                    "按发送者学科规则已同步该成员历史作业 Member={Member}, Subject={Subject}, 同步条数={Count}",
                    existing.MemberOpenId, updated.Subject, propagated);
            }
        }

        if (updated is not null)
        {
            RaiseChanged(updated);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HomeworkItem>> GetAllAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            LoadIfNeeded();
            IReadOnlyList<HomeworkItem> all = _items!
                .OrderBy(i => i.CreatedAt)
                .ToList();
            return Task.FromResult(all);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HomeworkItem>> GetBySubjectAsync(string subject, CancellationToken ct = default)
    {
        lock (_lock)
        {
            LoadIfNeeded();
            IReadOnlyList<HomeworkItem> matched = _items!
                .Where(i => string.Equals(i.Subject, subject, StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => i.CreatedAt)
                .ToList();
            return Task.FromResult(matched);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HomeworkItem>> GetByDateAsync(DateOnly date, CancellationToken ct = default)
    {
        lock (_lock)
        {
            LoadIfNeeded();
            IReadOnlyList<HomeworkItem> matched = _items!
                .Where(i => RetentionPolicies.BucketOf(i.CreatedAt) == date)
                .OrderBy(i => i.CreatedAt)
                .ToList();
            return Task.FromResult(matched);
        }
    }

    /// <inheritdoc />
    public Task<int> CleanupAsync(CancellationToken ct = default)
    {
        List<HomeworkItem> removed = [];
        lock (_lock)
        {
            LoadIfNeeded();
            var retention = _homeworkRetentionDays?.Invoke() ?? 0;
            var today = DateOnly.FromDateTime(DateTime.Now);
            List<HomeworkItem> kept = [];
            foreach (var i in _items!)
            {
                if (RetentionPolicies.ShouldKeepHomework(i, today, retention))
                {
                    kept.Add(i);
                }
                else
                {
                    removed.Add(i);
                }
            }

            var documentsRemoved = 0;
            if (removed.Count > 0)
            {
                _items = kept;

                // 需求 1：文档随条目保留期一起清理（文档日桶过期即整篇移除）
                var keptDocuments = new List<HomeworkDocument>();
                foreach (var doc in _documents!)
                {
                    if (retention > 0 && doc.Date < today.AddDays(-(retention - 1)))
                    {
                        documentsRemoved++;
                        continue;
                    }

                    keptDocuments.Add(doc);
                }

                if (documentsRemoved > 0)
                {
                    _documents = keptDocuments;
                }

                Save();
                _logger.LogInformation(
                    "作业保留期清理完成：删除 {Count} 条过期桶（当天条目不受影响），文档 {Documents} 篇",
                    removed.Count, documentsRemoved);
            }
        }

        // 锁外逐条触发：悬浮窗合并刷新（删除条目从视图移除）
        foreach (var r in removed)
        {
            RaiseChanged(r);
        }

        return Task.FromResult(removed.Count);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        HomeworkItem? removed;
        lock (_lock)
        {
            LoadIfNeeded();
            var index = _items!.FindIndex(i => i.Id == id);
            if (index < 0)
            {
                _logger.LogWarning("删除作业失败：条目不存在 Id={Id}", id);
                return Task.FromResult(false);
            }

            removed = _items[index];
            _items.RemoveAt(index);
            RemoveDocumentEntries([removed.MessageId]);
            Save();
            _logger.LogInformation("作业已删除（仅该条，不影响按发送者学科映射）Id={Id}, MessageId={MessageId}, Subject={Subject}",
                removed.Id, removed.MessageId, removed.Subject);
        }

        // 锁外触发：悬浮窗合并刷新（被删条目从视图移除）
        RaiseChanged(removed);
        return Task.FromResult(true);
    }

    // ============ 需求 1：学科文档 ============

    /// <inheritdoc />
    public Task<IReadOnlyList<HomeworkDocument>> GetDocumentsAsync(DateOnly date, CancellationToken ct = default)
    {
        lock (_lock)
        {
            LoadIfNeeded();
            IReadOnlyList<HomeworkDocument> documents = _documents!
                .Where(d => d.Date == date && !d.IsEmpty)
                .OrderBy(d => d.Subject, StringComparer.CurrentCulture)
                .ToList();
            return Task.FromResult(documents);
        }
    }

    /// <inheritdoc />
    public Task<HomeworkDocument> AppendDocumentEntryAsync(
        string subject, HomeworkDocumentEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var normalizedSubject = string.IsNullOrWhiteSpace(subject)
            ? HomeworkSubjectResolver.Unclassified
            : subject.Trim();
        var date = RetentionPolicies.BucketOf(entry.CreatedAt);
        HomeworkDocument document;
        DocumentMergeDecision decision;
        lock (_lock)
        {
            LoadIfNeeded();
            document = GetOrCreateDocument(date, normalizedSubject);
            var plan = HomeworkDocumentMerger.Plan(document.Entries, entry.Text, entry.MemberOpenId);
            decision = plan.Decision;

            switch (plan.Decision)
            {
                case DocumentMergeDecision.Duplicate:
                    _logger.LogInformation(
                        "作业文档去重合并：完全重复，已跳过（Subject={Subject}, MessageId={MessageId}）",
                        normalizedSubject, entry.SourceMessageIds.FirstOrDefault());
                    break;

                case DocumentMergeDecision.MergeIntoExisting:
                {
                    var target = document.Entries[plan.ExistingIndex];
                    var sourceIds = target.SourceMessageIds
                        .Concat(entry.SourceMessageIds)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    document.Entries[plan.ExistingIndex] = new HomeworkDocumentEntry
                    {
                        Id = target.Id,
                        SourceMessageIds = sourceIds,
                        MemberOpenId = string.IsNullOrEmpty(target.MemberOpenId) ? entry.MemberOpenId : target.MemberOpenId,
                        SenderLabel = string.IsNullOrEmpty(target.SenderLabel) ? entry.SenderLabel : target.SenderLabel,
                        Text = plan.MergedText,
                        CreatedAt = target.CreatedAt,
                        IsStanding = target.IsStanding && entry.IsStanding
                    };

                    // 手工编辑过：新内容按末尾增量追加并入 ManualText，保留用户删改（存档为单一事实源）
                    var appended = AppendToManualText(document, plan.NewLines);
                    document.UpdatedAt = DateTimeOffset.Now;
                    Save();
                    _logger.LogInformation(
                        "作业文档去重合并：部分重叠已并入（Subject={Subject}, MessageId={MessageId}, 新增行={Lines}, 手工文本追加={Appended}）",
                        normalizedSubject, entry.SourceMessageIds.FirstOrDefault(), plan.NewLines.Count, appended);
                    break;
                }

                default:
                {
                    document.Entries.Add(entry);
                    var appended = AppendToManualText(document, HomeworkDocumentMerger.SplitLines(entry.Text));
                    document.UpdatedAt = DateTimeOffset.Now;
                    Save();
                    _logger.LogInformation(
                        "作业文档已追加写入（Subject={Subject}, Date={Date}, MessageId={MessageId}, 手工文本追加={Appended}, 条目数={Count}）",
                        normalizedSubject, date, entry.SourceMessageIds.FirstOrDefault(), appended,
                        document.Entries.Count);
                    break;
                }
            }
        }

        if (decision != DocumentMergeDecision.Duplicate)
        {
            RaiseDocumentChanged(document);
        }

        return Task.FromResult(document);
    }

    /// <inheritdoc />
    public Task<HomeworkDocument?> SaveDocumentTextAsync(
        DateOnly date, string subject, string? manualText, CancellationToken ct = default,
        string? editBaseline = null)
    {
        var normalizedSubject = string.IsNullOrWhiteSpace(subject)
            ? HomeworkSubjectResolver.Unclassified
            : subject.Trim();
        HomeworkDocument? document = null;
        var trimmed = manualText?.Replace("\r\n", "\n").TrimEnd();
        lock (_lock)
        {
            LoadIfNeeded();
            if (string.IsNullOrEmpty(trimmed))
            {
                // 清空手工文本：回到按条目渲染（文档不存在则无需创建）
                var existing = FindDocument(date, normalizedSubject);
                if (existing is null)
                {
                    return Task.FromResult<HomeworkDocument?>(null);
                }

                if (existing.ManualText is null)
                {
                    return Task.FromResult<HomeworkDocument?>(existing);
                }

                existing.ManualText = null;
                existing.UpdatedAt = DateTimeOffset.Now;
                document = existing;
                if (document.IsEmpty)
                {
                    _documents!.Remove(existing);
                }

                Save();
                _logger.LogInformation("作业文档手工文本已清空，回到按条目渲染（Subject={Subject}, Date={Date}）",
                    normalizedSubject, date);
            }
            else
            {
                document = GetOrCreateDocument(date, normalizedSubject);

                // 三方合并（需求 1：编辑期间到达的新消息不丢不覆盖）
                var merged = MergeLateLines(document, trimmed, editBaseline);
                if (string.Equals(document.ManualText, merged, StringComparison.Ordinal))
                {
                    return Task.FromResult<HomeworkDocument?>(document);
                }

                document.ManualText = merged;
                document.UpdatedAt = DateTimeOffset.Now;
                Save();
                _logger.LogInformation("作业文档手工编辑已回写存档（Subject={Subject}, Date={Date}, 长度={Length}）",
                    normalizedSubject, date, merged.Length);
            }
        }

        if (document is not null)
        {
            RaiseDocumentChanged(document);
        }

        return Task.FromResult(document);
    }

    /// <inheritdoc />
    public Task<int> RemoveByMessageIdAsync(string messageId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return Task.FromResult(0);
        }

        List<HomeworkItem> removed = [];
        List<HomeworkDocument> touched = [];
        lock (_lock)
        {
            LoadIfNeeded();
            removed.AddRange(_items!.Where(i => string.Equals(i.MessageId, messageId, StringComparison.Ordinal)));
            if (removed.Count > 0)
            {
                _items.RemoveAll(i => string.Equals(i.MessageId, messageId, StringComparison.Ordinal));
            }

            touched.AddRange(RemoveDocumentEntries([messageId]));
            if (removed.Count > 0 || touched.Count > 0)
            {
                Save();
                _logger.LogInformation(
                    "按消息 id 移除作业完成（MessageId={MessageId}, 作业条目={Items}, 文档={Documents}）",
                    messageId, removed.Count, touched.Count);
            }
        }

        foreach (var item in removed)
        {
            RaiseChanged(item);
        }

        foreach (var document in touched)
        {
            RaiseDocumentChanged(document);
        }

        return Task.FromResult(removed.Count);
    }

    /// <inheritdoc />
    public Task<int> RemoveDocumentAsync(DateOnly date, string subject, CancellationToken ct = default)
    {
        var normalizedSubject = string.IsNullOrWhiteSpace(subject)
            ? HomeworkSubjectResolver.Unclassified
            : subject.Trim();
        List<HomeworkItem> removed = [];
        HomeworkDocument? document = null;
        lock (_lock)
        {
            LoadIfNeeded();
            document = FindDocument(date, normalizedSubject);
            if (document is not null)
            {
                _documents!.Remove(document);
            }

            var messageIds = document?.Entries
                .SelectMany(e => e.SourceMessageIds)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal) ?? [];

            // 条目匹配口径：文档有来源消息 id 时按 id 精确匹配（稳定键，与作业条目的时间桶无关——
            // 补拉/迁移的历史消息其 CreatedAt 可能不落在文档日桶内）；无来源 id（手工编辑出来的空壳文档）
            // 才退回「同一天 + 同科」的粗匹配。
            removed.AddRange(_items!.Where(i => messageIds.Count > 0
                ? messageIds.Contains(i.MessageId)
                : RetentionPolicies.BucketOf(i.CreatedAt) == date
                  && string.Equals(i.Subject, normalizedSubject, StringComparison.OrdinalIgnoreCase)));
            if (removed.Count > 0)
            {
                _items.RemoveAll(i => removed.Any(r => r.Id == i.Id));
            }

            if (document is not null || removed.Count > 0)
            {
                Save();
                _logger.LogInformation(
                    "学科文档已整体移除（Subject={Subject}, Date={Date}, 作业条目={Items}）",
                    normalizedSubject, date, removed.Count);
            }
        }

        foreach (var item in removed)
        {
            RaiseChanged(item);
        }

        if (document is not null)
        {
            RaiseDocumentChanged(document);
        }

        return Task.FromResult(removed.Count);
    }

    /// <summary>取或创建文档（调用方持锁）。</summary>
    private HomeworkDocument GetOrCreateDocument(DateOnly date, string subject)
    {
        var existing = FindDocument(date, subject);
        if (existing is not null)
        {
            return existing;
        }

        var created = new HomeworkDocument
        {
            Date = date,
            Subject = subject,
            UpdatedAt = DateTimeOffset.Now
        };
        _documents!.Add(created);
        return created;
    }

    private HomeworkDocument? FindDocument(DateOnly date, string subject) =>
        _documents!.FirstOrDefault(d =>
            d.Date == date && string.Equals(d.Subject, subject, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 编辑回写时的三方合并（调用方持锁）：把「编辑期间新落档、且不在编辑基线、也不在编辑稿中」
    /// 的条目行按末尾增量并入编辑稿。
    /// <para>
    /// 基线（<paramref name="editBaseline"/>）是进入编辑态那一刻的渲染文本，用于区分
    /// 「用户删掉的旧行」与「编辑期间新到的行」：前者保留删除、后者补齐——这是
    /// 「消息到达 vs 用户编辑中」并发竞争的正解（存档为单一事实源，双向同步不丢内容）。
    /// </para>
    /// <para>
    /// 基线为空（null）= 无三方语义，按整篇覆盖返回编辑稿（旧行为，供非编辑器调用方使用）。
    /// </para>
    /// </summary>
    private static string MergeLateLines(
        HomeworkDocument document, string editorText, string? editBaseline)
    {
        if (editBaseline is null)
        {
            return editorText;
        }

        var baselineKeys = HomeworkDocumentMerger.SplitLines(editBaseline)
            .Select(HomeworkDocumentMerger.Normalize)
            .Where(k => k.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var editorLines = HomeworkDocumentMerger.SplitLines(editorText);
        var seen = editorLines
            .Select(HomeworkDocumentMerger.Normalize)
            .Where(k => k.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var lateLines = new List<string>();
        foreach (var entry in document.Entries.OrderBy(e => e.CreatedAt))
        {
            foreach (var line in HomeworkDocumentMerger.SplitLines(entry.Text))
            {
                var key = HomeworkDocumentMerger.Normalize(line);
                if (key.Length == 0 || baselineKeys.Contains(key) || !seen.Add(key))
                {
                    continue;
                }

                lateLines.Add(line);
            }
        }

        if (lateLines.Count == 0)
        {
            return editorText;
        }

        return editorText.TrimEnd()
            + Environment.NewLine
            + string.Join(Environment.NewLine, lateLines);
    }

    /// <summary>
    /// 手工文本增量追加（调用方持锁）：文档存在手工文本时把新增行并入末尾（归一化去重），
    /// 返回追加行数。未编辑过（ManualText 为空）时不做任何事（展示直接来自 Entries）。
    /// </summary>
    private static int AppendToManualText(HomeworkDocument document, IReadOnlyList<string> newLines)
    {
        if (document.ManualText is not { Length: > 0 } manual || newLines.Count == 0)
        {
            return 0;
        }

        var existingKeys = HomeworkDocumentMerger.SplitLines(manual)
            .Select(HomeworkDocumentMerger.Normalize)
            .ToHashSet(StringComparer.Ordinal);
        var added = newLines
            .Where(line => !string.IsNullOrWhiteSpace(line) && existingKeys.Add(HomeworkDocumentMerger.Normalize(line)))
            .ToList();
        if (added.Count == 0)
        {
            return 0;
        }

        document.ManualText = manual.TrimEnd() + Environment.NewLine + string.Join(Environment.NewLine, added);
        return added.Count;
    }

    /// <summary>
    /// 从文档中移除指定来源消息（调用方持锁）：命中条目整体移除（合并条目随任一来源撤回而移除），
    /// 手工文本按行匹配尽力移除（用户已自行删改过则保持原样）。返回受影响的文档。
    /// </summary>
    private List<HomeworkDocument> RemoveDocumentEntries(IReadOnlyCollection<string> messageIds)
    {
        var touched = new List<HomeworkDocument>();
        if (_documents is null || messageIds.Count == 0)
        {
            return touched;
        }

        var idSet = messageIds.ToHashSet(StringComparer.Ordinal);
        for (var i = _documents.Count - 1; i >= 0; i--)
        {
            var document = _documents[i];
            var removedTexts = new List<string>();
            var before = document.Entries.Count;
            for (var j = document.Entries.Count - 1; j >= 0; j--)
            {
                var entry = document.Entries[j];
                if (entry.SourceMessageIds.Any(idSet.Contains))
                {
                    removedTexts.AddRange(HomeworkDocumentMerger.SplitLines(entry.Text));
                    document.Entries.RemoveAt(j);
                }
            }

            var removed = before - document.Entries.Count;
            if (removed == 0)
            {
                continue;
            }

            RemoveLinesFromManualText(document, removedTexts);
            document.UpdatedAt = DateTimeOffset.Now;
            if (document.IsEmpty)
            {
                _documents.RemoveAt(i);
            }
            else
            {
                touched.Add(document);
            }

            _logger.LogInformation(
                "撤回联动：文档条目已移除（Subject={Subject}, Date={Date}, 移除条目={Removed}, 剩余={Left}）",
                document.Subject, document.Date, removed, document.Entries.Count);
        }

        return touched;
    }

    /// <summary>从手工文本中移除与给定行归一化相同的行（调用方持锁）。</summary>
    private static void RemoveLinesFromManualText(HomeworkDocument document, IReadOnlyList<string> removedLines)
    {
        if (document.ManualText is not { Length: > 0 } manual || removedLines.Count == 0)
        {
            return;
        }

        var keys = removedLines.Select(HomeworkDocumentMerger.Normalize)
            .Where(k => k.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var kept = HomeworkDocumentMerger.SplitLines(manual)
            .Where(line => !keys.Contains(HomeworkDocumentMerger.Normalize(line)))
            .ToList();
        if (kept.Count == HomeworkDocumentMerger.SplitLines(manual).Count)
        {
            return; // 用户已自行删改：不动
        }

        document.ManualText = kept.Count == 0 ? null : string.Join(Environment.NewLine, kept);
    }

    /// <summary>学科变更时把文档条目从旧学科文档迁到新学科文档（调用方持锁）。</summary>
    private void MoveDocumentEntry(string messageId, string? oldSubject, string newSubject, DateTimeOffset createdAt)
    {
        if (_documents is null || string.IsNullOrWhiteSpace(messageId))
        {
            return;
        }

        var date = RetentionPolicies.BucketOf(createdAt);
        for (var i = _documents.Count - 1; i >= 0; i--)
        {
            var document = _documents[i];
            if (document.Date != date
                || (oldSubject is not null && string.Equals(document.Subject, newSubject, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var index = document.Entries.FindIndex(e =>
                e.SourceMessageIds.Contains(messageId, StringComparer.Ordinal));
            if (index < 0)
            {
                continue;
            }

            var entry = document.Entries[index];
            document.Entries.RemoveAt(index);
            RemoveLinesFromManualText(document, HomeworkDocumentMerger.SplitLines(entry.Text));
            document.UpdatedAt = DateTimeOffset.Now;
            if (document.IsEmpty)
            {
                _documents.RemoveAt(i);
            }

            var target = GetOrCreateDocument(date, newSubject);
            target.Entries.Add(entry);
            target.UpdatedAt = DateTimeOffset.Now;
            _logger.LogInformation(
                "作业文档条目随学科修正迁移（MessageId={MessageId}, →{Subject}）", messageId, newSubject);
            return;
        }
    }

    /// <summary>
    /// 幂等合并：保留原 Id/CreatedAt/IsResolved，更新正文/附件。学科取值优先级
    /// （<see cref="HomeworkSubjectResolver"/>）：
    /// 原条目已人工修正（永不回退）＞ 新条目人工修正 ＞ 优先级矩阵（识别链正常命中不被映射覆盖；
    /// 识别链「未分类」且发送者有映射 → 套用映射）。
    /// </summary>
    private HomeworkItem Merge(HomeworkItem existing, HomeworkItem incoming)
    {
        string subject;
        double confidence;
        SubjectSource source;
        if (existing.SubjectSource == SubjectSource.Manual
            && !HomeworkSubjectResolver.IsUnclassified(existing.Subject))
        {
            // ①显式人工修正（已有具体学科）永不回退：协议端重投/重试重放不覆盖人工结果
            subject = existing.Subject;
            confidence = existing.SubjectConfidence;
            source = existing.SubjectSource;
        }
        else if (incoming.SubjectSource == SubjectSource.Manual
            && !HomeworkSubjectResolver.IsUnclassified(incoming.Subject))
        {
            // 写入方人工修正（具体学科）直接采用
            subject = incoming.Subject;
            confidence = incoming.SubjectConfidence;
            source = incoming.SubjectSource;
        }
        else
        {
            // ②③④优先级矩阵（既有条目为「未分类」可被识别链重新命中/映射修正；
            // 识别链命中优先于映射；映射只在识别链「未分类」时套用）
            var rule = _userRules.Get(incoming.MemberOpenId);
            var resolved = HomeworkSubjectResolver.Resolve(
                incoming.Subject, incoming.SubjectConfidence, incoming.SubjectSource, rule);
            subject = resolved.Subject;
            confidence = resolved.Confidence;
            source = resolved.Source;
        }

        return new HomeworkItem
        {
            Id = existing.Id,
            MessageId = existing.MessageId,
            MemberOpenId = string.IsNullOrEmpty(existing.MemberOpenId) ? incoming.MemberOpenId : existing.MemberOpenId,
            Subject = subject,
            SubjectConfidence = confidence,
            SubjectSource = source,
            Content = incoming.Content,
            AttachmentIds = incoming.AttachmentIds,
            CreatedAt = existing.CreatedAt,
            IsResolved = existing.IsResolved
        };
    }

    /// <summary>
    /// 新作业套用优先级矩阵（人工修正来源的条目不受影响）：
    /// 识别链正常命中 → 保留识别链结果（不用映射覆盖）；识别链「未分类」且有映射 → 套用映射。
    /// </summary>
    private HomeworkItem ApplyUserRule(HomeworkItem item)
    {
        // 已带具体学科的人工修正条目不参与矩阵；
        // 「未分类」条目（含识别链兜底返回的 Source=Manual）→ 进入矩阵（③有映射时套用映射）
        if (item.SubjectSource == SubjectSource.Manual
            && !HomeworkSubjectResolver.IsUnclassified(item.Subject))
        {
            return item;
        }

        var rule = _userRules.Get(item.MemberOpenId);
        var resolved = HomeworkSubjectResolver.Resolve(
            item.Subject, item.SubjectConfidence, item.SubjectSource, rule);
        if (!resolved.RuleApplied)
        {
            return item;
        }

        item.Subject = resolved.Subject;
        item.SubjectConfidence = resolved.Confidence;
        item.SubjectSource = resolved.Source;
        _logger.LogInformation("识别链未分类，作业按发送者学科映射归类 Member={Member}, Subject={Subject}",
            item.MemberOpenId, rule);
        return item;
    }

    /// <summary>按发送者规则同步历史作业时克隆条目（仅改学科三件套，其余保持原样）。</summary>
    private static HomeworkItem CloneWithSubject(HomeworkItem item, string subject) => new()
    {
        Id = item.Id,
        MessageId = item.MessageId,
        MemberOpenId = item.MemberOpenId,
        Subject = subject,
        SubjectConfidence = 1.0,
        SubjectSource = SubjectSource.Manual,
        Content = item.Content,
        AttachmentIds = item.AttachmentIds,
        CreatedAt = item.CreatedAt,
        IsResolved = item.IsResolved
    };

    private void LoadIfNeeded()
    {
        if (_items is not null)
        {
            return;
        }

        var dto = JsonStoreFile.LoadOrRestore<HomeworkFileDto>(_filePath, _logger);
        _items = dto?.Items ?? [];
        _documents = dto?.Documents ?? [];

        // 向后兼容迁移：旧存档只有 items（无 documents）时，按学科+日期重建文档，
        // 保证升级后立即可看到「每学科一份连续文档」的历史内容。
        if (_documents.Count == 0 && _items.Count > 0)
        {
            RebuildDocumentsFromItems();
            if (_documents.Count > 0)
            {
                Save();
                _logger.LogInformation("作业文档结构迁移完成：由 {Count} 条历史作业重建 {Documents} 篇学科文档",
                    _items.Count, _documents.Count);
            }
        }
    }

    /// <summary>由历史作业条目重建学科文档（每学科每日一篇，条目按时间正序）。</summary>
    private void RebuildDocumentsFromItems()
    {
        foreach (var group in _items!
                     .Where(i => !string.IsNullOrWhiteSpace(i.Content))
                     .GroupBy(i => (Date: RetentionPolicies.BucketOf(i.CreatedAt),
                         Subject: string.IsNullOrWhiteSpace(i.Subject) ? HomeworkSubjectResolver.Unclassified : i.Subject.Trim()))
                     .OrderBy(g => g.Key.Date)
                     .ThenBy(g => g.Key.Subject, StringComparer.CurrentCulture))
        {
            var document = new HomeworkDocument
            {
                Date = group.Key.Date,
                Subject = group.Key.Subject,
                UpdatedAt = DateTimeOffset.Now
            };
            foreach (var item in group.OrderBy(i => i.CreatedAt))
            {
                document.Entries.Add(new HomeworkDocumentEntry
                {
                    SourceMessageIds = [item.MessageId],
                    MemberOpenId = item.MemberOpenId,
                    SenderLabel = "",
                    Text = item.Content,
                    CreatedAt = item.CreatedAt
                });
            }

            _documents!.Add(document);
        }
    }

    private void Save()
    {
        try
        {
            JsonStoreFile.SaveWithBackup(_filePath, JsonStoreFile.Serialize(new HomeworkFileDto
            {
                Items = _items!,
                Documents = _documents!
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "作业持久化失败（内存中状态保留）");
        }
    }

    private void RaiseChanged(HomeworkItem item)
    {
        try
        {
            Changed?.Invoke(this, item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HomeworkStore.Changed 订阅者异常（已吞掉，不影响存储）");
        }
    }

    private void RaiseDocumentChanged(HomeworkDocument document)
    {
        try
        {
            DocumentChanged?.Invoke(this, document);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HomeworkStore.DocumentChanged 订阅者异常（已吞掉，不影响存储）");
        }
    }
}
