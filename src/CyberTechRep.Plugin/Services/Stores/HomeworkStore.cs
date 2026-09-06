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

            if (removed.Count > 0)
            {
                _items = kept;
                Save();
                _logger.LogInformation(
                    "作业保留期清理完成：删除 {Count} 条过期桶（当天条目不受影响）", removed.Count);
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
            Save();
            _logger.LogInformation("作业已删除（仅该条，不影响按发送者学科映射）Id={Id}, MessageId={MessageId}, Subject={Subject}",
                removed.Id, removed.MessageId, removed.Subject);
        }

        // 锁外触发：悬浮窗合并刷新（被删条目从视图移除）
        RaiseChanged(removed);
        return Task.FromResult(true);
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
    }

    private void Save()
    {
        try
        {
            JsonStoreFile.SaveWithBackup(_filePath, JsonStoreFile.Serialize(new HomeworkFileDto { Items = _items! }));
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
}
