using System.Text.Json.Serialization;
using ClassIng.Plugin.Services.SubjectChain;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Services.Stores;

/// <summary>notices.json 顶层 DTO。</summary>
internal sealed class NoticeFileDto
{
    [JsonPropertyName("items")]
    public List<NoticeItem> Items { get; set; } = [];
}

/// <summary>
/// 通知存储（notices.json，JSON 原子写入 + .bak 损坏恢复，进程内锁串行化）。
/// <para>
/// 幂等：同 MessageId 重复 AddOrUpdate 合并为单条（未读时更新正文，已读不复活）；
/// 已读状态持久化：重启后已读条目不复活。<see cref="INoticeStore.Changed"/> 供悬浮窗
/// 合并刷新（限流 debounce 由悬浮窗侧负责）。
/// </para>
/// </summary>
public sealed class NoticeStore : INoticeStore
{
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private List<NoticeItem>? _items;

    public NoticeStore(string dataDirectory, ILogger? logger = null)
    {
        _filePath = Path.Combine(dataDirectory, "notices.json");
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public event EventHandler<NoticeItem>? Changed;

    /// <inheritdoc />
    public Task<NoticeItem> AddOrUpdateAsync(string messageId, string content, CancellationToken ct = default)
    {
        NoticeItem item;
        bool changed = false;
        lock (_lock)
        {
            LoadIfNeeded();
            var existing = _items!.Find(i => i.MessageId == messageId);
            if (existing is null)
            {
                item = new NoticeItem
                {
                    MessageId = messageId ?? "",
                    Content = content ?? "",
                    CreatedAt = DateTimeOffset.Now
                };
                _items!.Add(item);
                changed = true;
                _logger.LogInformation("通知已入列 Id={Id}, MessageId={MessageId}", item.Id, item.MessageId);
            }
            else
            {
                // 幂等：同 MessageId 合并为单条；已读条目只更新正文、不复活为未读
                item = existing;
                if (!string.Equals(existing.Content, content, StringComparison.Ordinal))
                {
                    var updated = Clone(existing, content ?? "");
                    _items![_items.IndexOf(existing)] = updated;
                    item = updated;
                    changed = true;
                    _logger.LogInformation("通知内容更新（幂等复用）Id={Id}, MessageId={MessageId}", item.Id, item.MessageId);
                }
            }

            if (changed)
            {
                Save();
            }
        }

        if (changed)
        {
            RaiseChanged(item);
        }

        return Task.FromResult(item);
    }

    /// <inheritdoc />
    public Task MarkReadAsync(Guid id, CancellationToken ct = default)
    {
        NoticeItem? read = null;
        lock (_lock)
        {
            LoadIfNeeded();
            var index = _items!.FindIndex(i => i.Id == id);
            if (index < 0)
            {
                _logger.LogWarning("标记已读失败：通知不存在 Id={Id}", id);
                return Task.CompletedTask;
            }

            var existing = _items[index];
            if (!existing.IsRead)
            {
                read = Clone(existing, existing.Content, isRead: true, readAt: DateTimeOffset.Now);
                _items[index] = read;
                Save();
                _logger.LogInformation("通知已标记已读并持久化 Id={Id}", id);
            }
        }

        if (read is not null)
        {
            RaiseChanged(read);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<NoticeItem>> GetUnreadAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            LoadIfNeeded();
            IReadOnlyList<NoticeItem> unread = _items!
                .Where(i => !i.IsRead)
                .OrderByDescending(i => i.CreatedAt)
                .ToList();
            return Task.FromResult(unread);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<NoticeItem>> GetAllAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            LoadIfNeeded();
            IReadOnlyList<NoticeItem> all = _items!
                .OrderByDescending(i => i.CreatedAt)
                .ToList();
            return Task.FromResult(all);
        }
    }

    private NoticeItem Clone(NoticeItem source, string content, bool? isRead = null, DateTimeOffset? readAt = null) =>
        new()
        {
            Id = source.Id,
            MessageId = source.MessageId,
            Content = content,
            CreatedAt = source.CreatedAt,
            IsRead = isRead ?? source.IsRead,
            ReadAt = readAt ?? source.ReadAt
        };

    private void LoadIfNeeded()
    {
        if (_items is not null)
        {
            return;
        }

        var dto = JsonStoreFile.LoadOrRestore<NoticeFileDto>(_filePath, _logger);
        _items = dto?.Items ?? [];
    }

    private void Save()
    {
        try
        {
            JsonStoreFile.SaveWithBackup(_filePath, JsonStoreFile.Serialize(new NoticeFileDto { Items = _items! }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "通知持久化失败（内存中状态保留）");
        }
    }

    private void RaiseChanged(NoticeItem item)
    {
        try
        {
            Changed?.Invoke(this, item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NoticeStore.Changed 订阅者异常（已吞掉，不影响存储）");
        }
    }
}
