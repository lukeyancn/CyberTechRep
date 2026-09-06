using System.Text.Json.Serialization;
using ClassIng.Plugin.Services.SubjectChain;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Services.Stores;

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
/// 记录按发送者的永久学科规则（<see cref="UserSubjectRuleStore"/>）并同步该成员全部历史作业。
/// </para>
/// </summary>
public sealed class HomeworkStore : IHomeworkStore
{
    private readonly string _filePath;
    private readonly UserSubjectRuleStore _userRules;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private List<HomeworkItem>? _items;

    public HomeworkStore(string dataDirectory, ILogger? logger = null)
    {
        _filePath = Path.Combine(dataDirectory, "homework.json");
        _userRules = new UserSubjectRuleStore(dataDirectory, logger);
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

            // 「无法分类（未分类）→ 人工指定」触发按发送者规则：
            // 该成员的全部历史作业都改为该学科，并永久记住（后续作业自动套用）
            var propagated = 0;
            var wasUnclassified = string.IsNullOrWhiteSpace(existing.Subject)
                || string.Equals(existing.Subject, "未分类", StringComparison.Ordinal);
            if (wasUnclassified && !string.IsNullOrWhiteSpace(existing.MemberOpenId))
            {
                _userRules.Set(existing.MemberOpenId, updated.Subject);
                for (var i = 0; i < _items.Count; i++)
                {
                    if (i == index
                        || !string.Equals(_items[i].MemberOpenId, existing.MemberOpenId, StringComparison.Ordinal))
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

    /// <summary>
    /// 幂等合并：保留原 Id/CreatedAt/IsResolved，更新正文/附件。学科取值优先级：
    /// 原条目已人工修正（永不回退）＞ 新条目人工修正 ＞ 按发送者永久规则 ＞ 新条目识别结果。
    /// </summary>
    private HomeworkItem Merge(HomeworkItem existing, HomeworkItem incoming)
    {
        string subject;
        double confidence;
        var source = SubjectSource.Manual;
        if (existing.SubjectSource == SubjectSource.Manual)
        {
            // 人工修正永不回退：协议端重投/重试重放不覆盖人工结果
            subject = existing.Subject;
            confidence = existing.SubjectConfidence;
        }
        else if (incoming.SubjectSource == SubjectSource.Manual)
        {
            subject = incoming.Subject;
            confidence = incoming.SubjectConfidence;
        }
        else
        {
            var rule = _userRules.Get(incoming.MemberOpenId);
            if (rule is not null)
            {
                subject = rule;
                confidence = 1.0;
            }
            else
            {
                subject = incoming.Subject;
                confidence = incoming.SubjectConfidence;
                source = incoming.SubjectSource;
            }
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

    /// <summary>新作业套用「按发送者记住学科」永久规则（人工修正来源的条目不受影响）。</summary>
    private HomeworkItem ApplyUserRule(HomeworkItem item)
    {
        if (item.SubjectSource == SubjectSource.Manual)
        {
            return item;
        }

        var rule = _userRules.Get(item.MemberOpenId);
        if (rule is null)
        {
            return item;
        }

        item.Subject = rule;
        item.SubjectConfidence = 1.0;
        item.SubjectSource = SubjectSource.Manual;
        _logger.LogInformation("作业按发送者学科规则归类 Member={Member}, Subject={Subject}", item.MemberOpenId, rule);
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
