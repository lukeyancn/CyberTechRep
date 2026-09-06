using System.Text.Json;
using System.Text.Json.Serialization;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.SubjectChain;

/// <summary>pending-confirm.json 顶层 DTO。</summary>
internal sealed class PendingConfirmFileDto
{
    [JsonPropertyName("items")]
    public List<PendingConfirmItem> Items { get; set; } = [];
}

/// <summary>
/// 模块 3 第③级：人工确认队列（JSON 文件持久化，原子写入，进程内锁串行化）。
/// <para>
/// <see cref="Resolved"/> 回调供模块 5 接入 IHomeworkStore.SetSubjectAsync 写回
/// HomeworkItem.Subject（SubjectSource=Manual）；未接入前仅持久化 ResolvedSubject。
/// </para>
/// </summary>
public sealed class JsonPendingConfirmStore : IPendingConfirmStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SubjectChainOptionsProvider _provider;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private List<PendingConfirmItem>? _items;

    public JsonPendingConfirmStore(SubjectChainOptionsProvider provider, ILogger? logger = null)
    {
        _provider = provider;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// 人工确认写回钩子（模块 5 接线：更新 HomeworkItem.Subject 与 SubjectSource）。
    /// null 时仅持久化，不外抛异常。
    /// </summary>
    public Func<PendingConfirmItem, CancellationToken, Task>? Resolved { get; set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<PendingConfirmItem>> GetWaitingAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            LoadIfNeeded();
            return Task.FromResult<IReadOnlyList<PendingConfirmItem>>(
                _items!.Where(i => i.Status == PendingConfirmStatus.Waiting)
                    .OrderBy(i => i.CreatedAt)
                    .ToList());
        }
    }

    /// <inheritdoc />
    public Task<PendingConfirmItem> EnqueueAsync(
        string messageId, string text, IReadOnlyList<SubjectResult> candidates, CancellationToken ct = default)
    {
        lock (_lock)
        {
            LoadIfNeeded();

            // 同一消息重复投递幂等：复用既有 Waiting 条目（首次候选即最后候选）
            var existing = _items!.FirstOrDefault(i =>
                i.Status == PendingConfirmStatus.Waiting && i.MessageId == messageId);
            if (existing is not null)
            {
                _logger.LogInformation("人工确认条目已存在（幂等复用）Item={Id}", existing.Id);
                return Task.FromResult(existing);
            }

            var item = new PendingConfirmItem
            {
                MessageId = messageId ?? "",
                Text = text ?? "",
                Candidates = candidates ?? [],
                CreatedAt = DateTimeOffset.Now
            };
            _items!.Add(item);
            Save();
            _logger.LogInformation(
                "已投递人工确认队列 Item={Id}, MessageId={MessageId}, candidates={Count}",
                item.Id, item.MessageId, item.Candidates.Count);
            return Task.FromResult(item);
        }
    }

    /// <inheritdoc />
    public async Task ResolveAsync(Guid id, string subject, CancellationToken ct = default)
    {
        PendingConfirmItem? resolved;
        lock (_lock)
        {
            LoadIfNeeded();
            var item = _items!.FirstOrDefault(i => i.Id == id);
            if (item is null)
            {
                _logger.LogWarning("人工确认条目不存在：{Id}", id);
                return;
            }

            item.Status = PendingConfirmStatus.Resolved;
            item.ResolvedSubject = subject;
            Save();
            resolved = item;
        }

        // 写回钩子在锁外执行（可能触达模块 5 存储）
        if (Resolved is not null)
        {
            try
            {
                await Resolved(resolved, ct);
            }
            catch (Exception ex)
            {
                // 已持久化确认结果；写回失败不吞确认本身
                _logger.LogError(ex, "人工确认写回钩子执行失败（已持久化 ResolvedSubject）");
            }
        }
    }

    private string FilePath => Path.Combine(_provider.DataDirectory, _provider.PendingConfirmFileName);

    private void LoadIfNeeded()
    {
        if (_items is not null)
        {
            return;
        }

        try
        {
            if (File.Exists(FilePath))
            {
                var dto = JsonSerializer.Deserialize<PendingConfirmFileDto>(
                    File.ReadAllText(FilePath), JsonOptions);
                _items = dto?.Items ?? [];
            }
            else
            {
                _items = [];
            }
        }
        catch (Exception ex)
        {
            // 损坏文件不阻断链：备份后以空队列继续（原始内容保留在 .corrupt）
            _logger.LogError(ex, "人工确认队列文件损坏，以空队列继续");
            try
            {
                File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
                File.Delete(FilePath);
            }
            catch
            {
                // 备份失败忽略
            }

            _items = [];
        }
    }

    private void Save()
    {
        try
        {
            var dto = new PendingConfirmFileDto { Items = _items! };
            SubjectRuleFile.WriteAtomic(FilePath, JsonSerializer.Serialize(dto, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "人工确认队列持久化失败（内存中状态保留）");
        }
    }
}
