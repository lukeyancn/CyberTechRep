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
/// <para>
/// 学科前缀：已绑定「发送者→学科」映射的发送者发出通知时，写入内容前附加「学科：」前缀
/// （NoticeStore 写入时生效；无映射不加；已有前缀不重复添加；映射查询与作业存储共用
/// <see cref="UserSubjectRuleStore"/>）。前缀纯逻辑见 <see cref="NoticeSubjectPrefixer"/>。
/// </para>
/// <para>
/// 按天归档：条目按 CreatedAt 本地日期逻辑分桶（见 <see cref="RetentionPolicies"/>）；
/// <see cref="CleanupAsync"/> 由清理任务在启动/跨天/设置变更时调用，只删过期桶，绝不动当天与未读。
/// </para>
/// </summary>
public sealed class NoticeStore : INoticeStore
{
    private readonly string _filePath;
    private readonly UserSubjectRuleStore? _userRules;
    private readonly Func<bool>? _noticePrefixEnabled;
    private readonly Func<int>? _noticesRetentionDays;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private List<NoticeItem>? _items;

    /// <summary>测试与既有调用兼容：仅数据目录。</summary>
    public NoticeStore(string dataDirectory)
        : this(dataDirectory, userRules: null, noticePrefixEnabled: null, noticesRetentionDays: null, logger: null)
    {
    }

    /// <summary>
    /// 完整构造。
    /// </summary>
    /// <param name="dataDirectory">插件数据目录（notices.json 所在目录）。</param>
    /// <param name="userRules">发送者→学科映射（与 HomeworkStore 共享单例；null = 不加前缀）。</param>
    /// <param name="noticePrefixEnabled">前缀开关（读 ClassificationSettings.NoticeSubjectPrefix；null = 默认开）。</param>
    /// <param name="noticesRetentionDays">通知保留天数（读 MaintenanceSettings.NoticesRetentionDays；0=永久不清理；null = 默认「已读仅当天」）。</param>
    /// <param name="logger">日志。</param>
    public NoticeStore(
        string dataDirectory,
        UserSubjectRuleStore? userRules = null,
        Func<bool>? noticePrefixEnabled = null,
        Func<int>? noticesRetentionDays = null,
        ILogger? logger = null)
    {
        _filePath = Path.Combine(dataDirectory, "notices.json");
        _userRules = userRules;
        _noticePrefixEnabled = noticePrefixEnabled;
        _noticesRetentionDays = noticesRetentionDays;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public event EventHandler<NoticeItem>? Changed;

    /// <inheritdoc />
    public Task<NoticeItem> AddOrUpdateAsync(string messageId, string content, string? memberOpenId = null, CancellationToken ct = default)
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
                    Content = ApplySubjectPrefix(content ?? "", memberOpenId),
                    CreatedAt = DateTimeOffset.Now
                };
                _items!.Add(item);
                changed = true;
                _logger.LogInformation("通知已入列 Id={Id}, MessageId={MessageId}", item.Id, item.MessageId);
            }
            else
            {
                // 幂等：同 MessageId 合并为单条；已读条目只更新正文、不复活为未读
                var prefixed = ApplySubjectPrefix(content ?? "", memberOpenId);
                item = existing;
                if (!string.Equals(existing.Content, prefixed, StringComparison.Ordinal))
                {
                    var updated = Clone(existing, prefixed);
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
    public Task MarkUnreadAsync(Guid id, CancellationToken ct = default)
    {
        NoticeItem? unread = null;
        lock (_lock)
        {
            LoadIfNeeded();
            var index = _items!.FindIndex(i => i.Id == id);
            if (index < 0)
            {
                _logger.LogWarning("标记未读失败：通知不存在 Id={Id}", id);
                return Task.CompletedTask;
            }

            var existing = _items[index];
            if (existing.IsRead)
            {
                unread = new NoticeItem
                {
                    Id = existing.Id,
                    MessageId = existing.MessageId,
                    Content = existing.Content,
                    CreatedAt = existing.CreatedAt,
                    IsRead = false,
                    ReadAt = null
                };
                _items[index] = unread;
                Save();
                _logger.LogInformation("通知已标记未读并持久化 Id={Id}", id);
            }
        }

        if (unread is not null)
        {
            RaiseChanged(unread);
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

    /// <inheritdoc />
    public Task<IReadOnlyList<NoticeItem>> GetByDateAsync(DateOnly date, CancellationToken ct = default)
    {
        lock (_lock)
        {
            LoadIfNeeded();
            IReadOnlyList<NoticeItem> matched = _items!
                .Where(i => RetentionPolicies.BucketOf(i.CreatedAt) == date)
                .OrderByDescending(i => i.CreatedAt)
                .ToList();
            return Task.FromResult(matched);
        }
    }

    /// <inheritdoc />
    public Task<int> CleanupAsync(CancellationToken ct = default)
    {
        List<NoticeItem> removed = [];
        lock (_lock)
        {
            LoadIfNeeded();
            // 未显式接线保留天数（null）→ 默认「已读仅当天」（通知天然易逝，过期已读不留存）；
            // 显式 0 = 永久（MaintenanceSettings.NoticesRetentionDays=0 默认，不清理）。
            var retention = _noticesRetentionDays?.Invoke() ?? 1;
            var today = DateOnly.FromDateTime(DateTime.Now);
            List<NoticeItem> kept = [];
            foreach (var i in _items!)
            {
                if (RetentionPolicies.ShouldKeepNotice(i, today, retention))
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
                    "通知保留期清理完成：删除 {Count} 条过期桶（未读与当天条目不受影响）", removed.Count);
            }
        }

        // 锁外逐条触发：悬浮窗合并刷新（删除条目从视图移除）
        foreach (var r in removed)
        {
            RaiseChanged(r);
        }

        return Task.FromResult(removed.Count);
    }

    /// <summary>
    /// 按发送者映射附加学科前缀（写入时生效）。无映射/开关关闭/无发送者 → 原样返回；
    /// 已带前缀不重复添加（<see cref="NoticeSubjectPrefixer.Apply"/> 幂等保证）。
    /// </summary>
    private string ApplySubjectPrefix(string content, string? memberOpenId)
    {
        if (_userRules is null
            || string.IsNullOrWhiteSpace(memberOpenId)
            || string.IsNullOrEmpty(content)
            || _noticePrefixEnabled?.Invoke() == false)
        {
            return content;
        }

        var subject = _userRules.Get(memberOpenId);
        var prefixed = NoticeSubjectPrefixer.Apply(content, subject);
        if (!string.Equals(prefixed, content, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "通知已附加学科前缀 Member={Member}, Subject={Subject}", memberOpenId, subject);
        }

        return prefixed;
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
