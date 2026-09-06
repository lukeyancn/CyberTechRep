using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.Maintenance;

/// <summary>消息日志 dump 服务：导出每条消息的全字段结构化数据（JSONL，每行一条）。</summary>
public interface IMessageDumpService
{
    /// <summary>当前内存中已捕获的消息条数（环形缓冲上限 <see cref="MessageDumpService.MaxCaptured"/>）。</summary>
    int CapturedCount { get; }

    /// <summary>把已捕获消息导出为 JSONL 文件（每行一条 <see cref="MessageDumpLine"/>）。</summary>
    /// <returns>导出行数。</returns>
    Task<int> ExportAsync(string filePath, CancellationToken ct = default);
}

/// <summary>dump 导出行：MessageRecord 全字段 + 关联的分类/学科/文件结果（按 MessageId 关联）。</summary>
public sealed class MessageDumpLine
{
    public required string MessageId { get; init; }

    /// <summary>群 OpenID（官方平台不提供真实群号）。</summary>
    public required string GroupOpenId { get; init; }

    public string MemberOpenId { get; init; } = "";

    public string SenderNickname { get; init; } = "";

    public DateTimeOffset ReceivedAt { get; init; }

    public IReadOnlyList<MessageSegment> Segments { get; init; } = [];

    /// <summary>分类结果（按 MessageId 关联存储侧推导：有作业=Homework，有通知=Notice，否则 Unknown）。</summary>
    public string Kind { get; init; } = nameof(MessageKind.Unknown);

    /// <summary>学科识别结果（来自作业条目；非作业/未入列为 null）。</summary>
    public MessageDumpSubject? Subject { get; init; }

    /// <summary>文件关联（文件管道中同 MessageId 的记录）。</summary>
    public IReadOnlyList<MessageDumpFile> Files { get; init; } = [];

    /// <summary>原始 JSON 快照（接入管道已脱敏：凭据只在 HTTP 头/表单，事件体不含 Secret/Token）。</summary>
    public string RawJsonSnapshot { get; init; } = "";
}

/// <summary>dump 导出行中的学科识别结果。</summary>
public sealed class MessageDumpSubject
{
    public string Subject { get; init; } = "";

    public double Confidence { get; init; }

    /// <summary>来源（KeywordRule/LocalModel/CloudLlm/Manual）。</summary>
    public string Source { get; init; } = "";
}

/// <summary>dump 导出行中的文件关联。</summary>
public sealed class MessageDumpFile
{
    public Guid Id { get; init; }

    public string FileName { get; init; } = "";

    public string Status { get; init; } = "";

    public string? Md5 { get; init; }

    public string? ArchivedRelativePath { get; init; }
}

/// <summary>
/// <see cref="IMessageDumpService"/> 默认实现：订阅 <see cref="IMessageIngestService.MessageReceived"/>
/// 维护最近 N 条完整 <see cref="MessageRecord"/> 环形缓冲；导出时按 MessageId 关联
/// 作业/通知存储与文件管道记录，拼出全字段结构化行。
/// 红线：dump 只含消息侧数据（记录/段/分类/学科/文件），绝不包含 AppId/AppSecret/Token/ApiKey。
/// </summary>
public sealed class MessageDumpService : IMessageDumpService, IDisposable
{
    /// <summary>内存环形缓冲条数上限（导出范围；消息本就不落全量，仅最近窗口）。</summary>
    public const int MaxCaptured = 1000;

    private readonly object _lock = new();
    private readonly IMessageIngestService? _ingestService;
    private readonly IHomeworkStore? _homeworkStore;
    private readonly INoticeStore? _noticeStore;
    private readonly IFilePipelineService? _filePipeline;
    private readonly ILogger _logger;
    private readonly Queue<MessageRecord> _records = new();

    public MessageDumpService(IMessageIngestService? ingestService = null,
        IHomeworkStore? homeworkStore = null,
        INoticeStore? noticeStore = null,
        IFilePipelineService? filePipeline = null,
        ILogger? logger = null)
    {
        _ingestService = ingestService;
        _homeworkStore = homeworkStore;
        _noticeStore = noticeStore;
        _filePipeline = filePipeline;
        _logger = logger ?? NullLogger.Instance;

        if (ingestService is not null)
        {
            ingestService.MessageReceived += OnMessageReceived;
        }
    }

    public int CapturedCount
    {
        get
        {
            lock (_lock)
            {
                return _records.Count;
            }
        }
    }

    /// <inheritdoc />
    public async Task<int> ExportAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        MessageRecord[] records;
        lock (_lock)
        {
            records = [.. _records];
        }

        if (records.Length == 0)
        {
            _logger.LogInformation("消息日志 dump：缓冲为空，导出 0 行（{Path}）", filePath);
            File.WriteAllText(filePath, string.Empty, new UTF8Encoding(false));
            return 0;
        }

        // 按 MessageId 关联作业/通知/文件记录（分类 Kind 由存储侧推导，与排错面板口径一致）
        var homeworkByMessage = await SafeGetAllAsync(
            _homeworkStore is not null, ct, () => _homeworkStore!.GetAllAsync(ct), h => h.MessageId)
            .ConfigureAwait(false);
        var noticeByMessage = await SafeGetAllAsync(
            _noticeStore is not null, ct, () => _noticeStore!.GetAllAsync(ct), n => n.MessageId)
            .ConfigureAwait(false);
        var filesByMessage = await SafeGetAllAsync(
            _filePipeline is not null, ct, () => _filePipeline!.GetRecordsAsync(ct), f => f.MessageId)
            .ConfigureAwait(false);

        var lines = new List<string>(records.Length);
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();

            var kind = homeworkByMessage.ContainsKey(record.MessageId) ? nameof(MessageKind.Homework)
                : noticeByMessage.ContainsKey(record.MessageId) ? nameof(MessageKind.Notice)
                : nameof(MessageKind.Unknown);

            MessageDumpSubject? subject = null;
            if (homeworkByMessage.TryGetValue(record.MessageId, out var homeworkList))
            {
                var homework = homeworkList.FirstOrDefault();
                if (homework is not null)
                {
                    subject = new MessageDumpSubject
                    {
                        Subject = homework.Subject,
                        Confidence = homework.SubjectConfidence,
                        Source = homework.SubjectSource.ToString()
                    };
                }
            }

            var files = filesByMessage.TryGetValue(record.MessageId, out var list)
                ? list.Select(f => new MessageDumpFile
                {
                    Id = f.Id,
                    FileName = f.FileName,
                    Status = f.Status.ToString(),
                    Md5 = f.Md5,
                    ArchivedRelativePath = f.ArchivedRelativePath
                }).ToList()
                : [];

            var line = new MessageDumpLine
            {
                MessageId = record.MessageId,
                GroupOpenId = record.GroupOpenId,
                MemberOpenId = record.MemberOpenId,
                SenderNickname = record.SenderNickname,
                ReceivedAt = record.ReceivedAt,
                Segments = record.Segments,
                Kind = kind,
                Subject = subject,
                Files = files,
                RawJsonSnapshot = record.RawJsonSnapshot
            };
            lines.Add(JsonSerializer.Serialize(line, JsonOptions));
        }

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // UTF-8 无 BOM：每行一条 JSON，供外部工具逐行解析
        await File.WriteAllLinesAsync(filePath, lines, new UTF8Encoding(false), ct).ConfigureAwait(false);
        _logger.LogInformation("消息日志 dump 已导出：{Count} 行 → {Path}", lines.Count, filePath);
        return lines.Count;
    }

    private void OnMessageReceived(object? sender, MessageRecord message)
    {
        if (message is null)
        {
            return;
        }

        lock (_lock)
        {
            _records.Enqueue(message);
            while (_records.Count > MaxCaptured)
            {
                _records.Dequeue();
            }
        }
    }

    /// <summary>存储侧安全读取：存储未注册或读取失败按空关联处理（dump 不因单侧失败整体失败）。</summary>
    private async Task<Dictionary<string, List<T>>> SafeGetAllAsync<T>(
        bool registered,
        CancellationToken ct,
        Func<Task<IReadOnlyList<T>>> getAll,
        Func<T, string> keySelector)
    {
        try
        {
            if (!registered)
            {
                return [];
            }

            var items = await getAll().ConfigureAwait(false);
            var result = new Dictionary<string, List<T>>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                var key = keySelector(item);
                if (!result.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    result[key] = bucket;
                }

                bucket.Add(item);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "消息日志 dump：读取关联记录失败，该侧关联置空（Type={Type}）", typeof(T).Name);
            return [];
        }
    }

    public void Dispose()
    {
        if (_ingestService is not null)
        {
            _ingestService.MessageReceived -= OnMessageReceived;
        }
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
