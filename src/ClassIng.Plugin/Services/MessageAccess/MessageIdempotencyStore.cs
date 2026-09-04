using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ClassIng.Plugin.Services.MessageAccess;

/// <summary>
/// 消息幂等存储：持久化已处理消息 id（FIFO 容量上限），协议端重发/重复推送不产生重复条目。
/// 崩溃安全：写入采用「临时文件 + 原子替换」。
/// </summary>
public sealed class MessageIdempotencyStore
{
    private const int Capacity = 5000;

    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly ILogger? _logger;
    private readonly Queue<string> _seen = new();
    private readonly HashSet<string> _seenSet = new();

    public MessageIdempotencyStore(string dataDirectory, ILogger? logger = null)
    {
        _logger = logger;
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "seen-message-ids.json");
        Load();
    }

    /// <summary>标记一条消息为已处理。返回 false 表示此前已见过（重复推送）。</summary>
    public bool TryMarkSeen(string messageId)
    {
        if (string.IsNullOrEmpty(messageId))
        {
            return false;
        }

        bool isNew;
        lock (_lock)
        {
            isNew = _seenSet.Add(messageId);
            if (isNew)
            {
                _seen.Enqueue(messageId);
                while (_seen.Count > Capacity)
                {
                    var old = _seen.Dequeue();
                    _seenSet.Remove(old);
                }
            }
        }

        if (isNew)
        {
            Save();
        }

        return isNew;
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _seen.Count;
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json = File.ReadAllText(_filePath);
            var ids = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            lock (_lock)
            {
                foreach (var id in ids.TakeLast(Capacity))
                {
                    if (_seenSet.Add(id))
                    {
                        _seen.Enqueue(id);
                    }
                }
            }

            _logger?.LogDebug("幂等存储已加载 {Count} 条消息 id", _seen.Count);
        }
        catch (Exception ex)
        {
            // 损坏时保留损坏文件并空库启动（排错面板可见警告）
            _logger?.LogWarning(ex, "幂等存储加载失败，将以空库启动：{File}", _filePath);
            _seen.Clear();
            _seenSet.Clear();
        }
    }

    private void Save()
    {
        try
        {
            string json;
            lock (_lock)
            {
                json = JsonSerializer.Serialize(_seen.ToArray());
            }

            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // 写失败不中断消息流（内存态仍在；下次成功写入会覆盖）
            _logger?.LogWarning(ex, "幂等存储写入失败（内存态不受影响）");
        }
    }
}
