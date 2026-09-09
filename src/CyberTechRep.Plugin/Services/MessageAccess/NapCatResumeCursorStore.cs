using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CyberTechRep.Plugin.Services.Stores;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.MessageAccess;

/// <summary>napcat-resume-cursor.json 顶层 DTO。</summary>
internal sealed class NapCatResumeCursorFileDto
{
    [JsonPropertyName("entries")]
    public List<NapCatResumeCursorEntry> Entries { get; set; } = [];

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// 单群续传游标（需求 4）。
/// <para>
/// <see cref="LastMessageId"/>/<see cref="LastTimestampUnix"/> 是「已成功入档的最后一条消息」；
/// <see cref="RecentKeys"/> 是最近若干条消息的稳定键（<c>时间:内容哈希</c>），
/// 用于「协议端未给 id / id 不稳定」时的时间+内容哈希兜底去重。
/// </para>
/// </summary>
public sealed class NapCatResumeCursorEntry
{
    /// <summary>群号（NapCat 数字群号）或私聊占位键。</summary>
    [JsonPropertyName("groupId")]
    public string GroupId { get; set; } = "";

    /// <summary>已入档的最后一条消息 id（稳定键，优先使用）。</summary>
    [JsonPropertyName("lastMessageId")]
    public string LastMessageId { get; set; } = "";

    /// <summary>已入档的最后一条消息的原始时间戳（Unix 秒；无时间时为 0）。</summary>
    [JsonPropertyName("lastTimestampUnix")]
    public long LastTimestampUnix { get; set; }

    /// <summary>最近入档消息的稳定键（时间:内容哈希），环形有界。</summary>
    [JsonPropertyName("recentKeys")]
    public List<string> RecentKeys { get; set; } = [];

    /// <summary>最近入档消息 id（环形有界；协议端重投/续传重复的 id 级去重）。</summary>
    [JsonPropertyName("recentMessageIds")]
    public List<string> RecentMessageIds { get; set; } = [];

    /// <summary>游标最后更新时间（诊断用）。</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// NapCat 消息断点续传游标（需求 4，<c>napcat-resume-cursor.json</c>）。
/// <para>
/// <b>语义</b>：记录「每个群已成功入档到哪一条消息」。重启后从游标继续读取，
/// 启动核对（<c>get_group_msg_history</c>）以游标为界补齐缺口。
/// </para>
/// <para>
/// <b>原子性</b>：写入走 <see cref="JsonStoreFile.SaveWithBackup"/>（临时文件 + 原子替换 + .bak 备份），
/// 崩溃/强杀最多丢「最后一次刷新之前的增量」；配合启动核对窗口（默认每群回溯 100 条）
/// 保证不丢消息。刷新为 1 秒防抖合并（高频消息不产生每消息一次磁盘写）。
/// </para>
/// <para>
/// <b>偏差处理</b>：
/// <list type="bullet">
/// <item>游标<b>落后</b>于实际（崩溃丢增量）→ 启动核对重读一段，重复消息由幂等存储（MessageId）与
/// 本游标的 recentKeys 双重去重，只补不重；</item>
/// <item>游标<b>超前</b>于实际（消息入档但游标写成功后进程被杀、后续消息未入档）——本实现
/// 只在「消息成功交给管道后」推进游标，且推进是单调的，因此不会出现超前；若人为编辑存档
/// 导致超前，核对窗口会重读最后 N 条并靠幂等去重收敛。</item>
/// </list>
/// </para>
/// <para>
/// <b>可容忍边界</b>：单群缺口 &gt; 核对窗口（默认 100 条）时无法补齐——这是「插件长时间离线
/// 且群内消息超过窗口」的场景，属已知边界（窗口可经设置调大，代价是首次同步耗时）。
/// </para>
/// </summary>
public sealed class NapCatResumeCursorStore : IDisposable
{
    /// <summary>单群游标保留的最近稳定键条数（环形，防无限增长）。</summary>
    public const int RecentKeyCapacity = 64;

    /// <summary>游标记录的最多群数（超出淘汰最久未更新者）。</summary>
    public const int MaxGroups = 200;

    /// <summary>磁盘刷新防抖（毫秒）：高频消息合并为一次原子写。</summary>
    public const int FlushDebounceMs = 1000;

    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, NapCatResumeCursorEntry> _entries = new(StringComparer.Ordinal);
    private Timer? _flushTimer;
    private volatile bool _dirty;
    private volatile bool _disposed;

    public NapCatResumeCursorStore(string dataDirectory, ILogger? logger = null)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "napcat-resume-cursor.json");
        _logger = logger ?? NullLogger.Instance;
        Load();
    }

    /// <summary>已记录游标的群数（诊断用）。</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>取指定群的游标（无记录返回 null）。</summary>
    public NapCatResumeCursorEntry? Get(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return null;
        }

        lock (_lock)
        {
            return _entries.TryGetValue(groupId, out var entry) ? Clone(entry) : null;
        }
    }

    /// <summary>
    /// 该消息是否已被游标覆盖（= 已入档过，无需再次写入）。
    /// 判定顺序：①消息 id 命中最近键/末条 id；②时间戳早于或等于游标时间戳
    /// （同一秒用稳定键 <paramref name="stableKey"/> 区分，稳定键取自 <see cref="ContentHashKey"/>）。
    /// </summary>
    public bool IsCovered(string groupId, string messageId, long timestampUnix, string? stableKey = null)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return false;
        }

        lock (_lock)
        {
            if (!_entries.TryGetValue(groupId, out var entry))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(messageId)
                && (string.Equals(entry.LastMessageId, messageId, StringComparison.Ordinal)
                    || entry.RecentMessageIds.Contains(messageId, StringComparer.Ordinal)))
            {
                return true;
            }

            if (timestampUnix > 0 && entry.LastTimestampUnix > 0)
            {
                if (timestampUnix < entry.LastTimestampUnix)
                {
                    return true;
                }

                if (timestampUnix == entry.LastTimestampUnix && !string.IsNullOrEmpty(stableKey))
                {
                    return entry.RecentKeys.Contains(stableKey, StringComparer.Ordinal);
                }
            }

            return false;
        }
    }

    /// <summary>
    /// 推进游标（消息已成功入档后调用）。单调推进：更旧的 id/时间不会回退游标。
    /// <paramref name="stableKey"/> 取 <see cref="ContentHashKey"/>（时间+内容哈希），
    /// 原样存入环形键集，不做二次拼接。
    /// </summary>
    public void Record(string groupId, string messageId, long timestampUnix, string? stableKey = null)
    {
        if (string.IsNullOrWhiteSpace(groupId) || _disposed)
        {
            return;
        }

        lock (_lock)
        {
            if (!_entries.TryGetValue(groupId, out var entry))
            {
                entry = new NapCatResumeCursorEntry { GroupId = groupId };
                _entries[groupId] = entry;
                TrimGroups();
            }

            if (timestampUnix > 0 && timestampUnix < entry.LastTimestampUnix)
            {
                return; // 迟到消息不回退游标（避免核对窗口被反向拉大）
            }

            if (!string.IsNullOrEmpty(messageId))
            {
                entry.LastMessageId = messageId;
                entry.RecentMessageIds.RemoveAll(id => string.Equals(id, messageId, StringComparison.Ordinal));
                entry.RecentMessageIds.Add(messageId);
                if (entry.RecentMessageIds.Count > RecentKeyCapacity)
                {
                    entry.RecentMessageIds.RemoveRange(0, entry.RecentMessageIds.Count - RecentKeyCapacity);
                }
            }

            if (timestampUnix > 0)
            {
                entry.LastTimestampUnix = timestampUnix;
            }

            if (!string.IsNullOrEmpty(stableKey))
            {
                entry.RecentKeys.RemoveAll(k => string.Equals(k, stableKey, StringComparison.Ordinal));
                entry.RecentKeys.Add(stableKey);
                if (entry.RecentKeys.Count > RecentKeyCapacity)
                {
                    entry.RecentKeys.RemoveRange(0, entry.RecentKeys.Count - RecentKeyCapacity);
                }
            }

            entry.UpdatedAt = DateTimeOffset.Now;
            _dirty = true;
        }

        ScheduleFlush();
    }

    /// <summary>内容稳定键（时间 + 内容哈希；时间缺失时只用哈希）。</summary>
    public static string ContentHashKey(long timestampUnix, string? content)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content ?? "")))[..16];
        return timestampUnix > 0 ? $"{timestampUnix}:{hash}" : hash;
    }

    /// <summary>立即把游标原子落盘（进程关停/手动重连前调用）。</summary>
    public Task FlushAsync()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;
        SaveIfDirty();
        return Task.CompletedTask;
    }

    /// <summary>防抖刷新：1 秒内的多次推进合并为一次原子写。</summary>
    private void ScheduleFlush()
    {
        if (_disposed)
        {
            return;
        }

        _flushTimer ??= new Timer(_ => SaveIfDirty(), null, FlushDebounceMs, Timeout.Infinite);
        try
        {
            _flushTimer.Change(FlushDebounceMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // 与 FlushAsync/Dispose 竞争：忽略，最终由 FlushAsync 兜底落盘
        }
    }

    private void SaveIfDirty()
    {
        if (!_dirty || _disposed)
        {
            return;
        }

        List<NapCatResumeCursorEntry> snapshot;
        lock (_lock)
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            snapshot = _entries.Values.Select(Clone).ToList();
        }

        try
        {
            JsonStoreFile.SaveWithBackup(_filePath, JsonStoreFile.Serialize(new NapCatResumeCursorFileDto
            {
                Entries = snapshot,
                UpdatedAt = DateTimeOffset.Now
            }));
        }
        catch (Exception ex)
        {
            _dirty = true; // 写失败：下次刷新重试
            _logger.LogWarning(ex, "NapCat 续传游标写入失败（内存态保留，下次刷新重试）");
        }
    }

    private void Load()
    {
        var dto = JsonStoreFile.LoadOrRestore<NapCatResumeCursorFileDto>(_filePath, _logger);
        if (dto is null)
        {
            return;
        }

        lock (_lock)
        {
            foreach (var entry in dto.Entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.GroupId))
                {
                    _entries[entry.GroupId] = entry;
                }
            }
        }

        _logger.LogInformation("NapCat 续传游标已加载：{Count} 个群", _entries.Count);
    }

    /// <summary>淘汰最久未更新的群游标（容量有界）。</summary>
    private void TrimGroups()
    {
        if (_entries.Count <= MaxGroups)
        {
            return;
        }

        foreach (var stale in _entries.Values
                     .OrderBy(e => e.UpdatedAt)
                     .Take(_entries.Count - MaxGroups)
                     .ToList())
        {
            _entries.Remove(stale.GroupId);
        }
    }

    private static NapCatResumeCursorEntry Clone(NapCatResumeCursorEntry entry) => new()
    {
        GroupId = entry.GroupId,
        LastMessageId = entry.LastMessageId,
        LastTimestampUnix = entry.LastTimestampUnix,
        RecentKeys = [.. entry.RecentKeys],
        RecentMessageIds = [.. entry.RecentMessageIds],
        UpdatedAt = entry.UpdatedAt
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        SaveIfDirty();
        _disposed = true;
        _flushTimer?.Dispose();
        _flushTimer = null;
    }
}
