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
/// <see cref="IHomeworkStore.SetSubjectAsync"/> 人工修正写回（SubjectSource=Manual）。
/// </para>
/// </summary>
public sealed class HomeworkStore : IHomeworkStore
{
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private List<HomeworkItem>? _items;

    public HomeworkStore(string dataDirectory, ILogger? logger = null)
    {
        _filePath = Path.Combine(dataDirectory, "homework.json");
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
                stored = item;
                _items!.Add(item);
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
                Subject = subject ?? "未分类",
                SubjectConfidence = 1.0,
                SubjectSource = SubjectSource.Manual,
                Content = existing.Content,
                AttachmentIds = existing.AttachmentIds,
                CreatedAt = existing.CreatedAt,
                IsResolved = existing.IsResolved
            };
            _items[index] = updated;
            Save();
            _logger.LogInformation("作业学科已人工修正并持久化 Id={Id}, Subject={Subject}", id, updated.Subject);
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

    /// <summary>幂等合并：保留原 Id/CreatedAt/IsResolved，更新学科与正文/附件。</summary>
    private HomeworkItem Merge(HomeworkItem existing, HomeworkItem incoming) =>
        new()
        {
            Id = existing.Id,
            MessageId = existing.MessageId,
            Subject = incoming.Subject,
            SubjectConfidence = incoming.SubjectConfidence,
            SubjectSource = incoming.SubjectSource,
            Content = incoming.Content,
            AttachmentIds = incoming.AttachmentIds,
            CreatedAt = existing.CreatedAt,
            IsResolved = existing.IsResolved
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
