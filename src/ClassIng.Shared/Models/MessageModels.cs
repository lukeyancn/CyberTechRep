namespace ClassIng.Shared.Models;

/// <summary>一条已幂等去重后的群消息记录（持久化最小单元）。</summary>
/// <remarks>契约修订 v1.1（2026-09-03）：协议端选型定为 QQ 官方机器人开放平台，
/// 标识符体系由 OneBot 数字 ID 改为官方 OpenID 字符串体系。</remarks>
public sealed class MessageRecord
{
    /// <summary>幂等键：官方平台消息事件 id（字符串，协议端重发不会产生重复条目）。</summary>
    public required string MessageId { get; init; }

    /// <summary>群 OpenID（官方平台不提供真实群号）。</summary>
    public required string GroupOpenId { get; init; }

    /// <summary>发送者成员 OpenID。</summary>
    public string MemberOpenId { get; init; } = "";

    /// <summary>发送者昵称（官方事件若未携带则为空）。</summary>
    public string SenderNickname { get; init; } = "";

    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>消息段列表（文本/图片/文件等，已规范化）。</summary>
    public IReadOnlyList<MessageSegment> Segments { get; init; } = [];

    /// <summary>原始 JSON 快照（已脱敏：不含 AppId/AppSecret/Token）。</summary>
    public string RawJsonSnapshot { get; init; } = "";
}

/// <summary>规范化消息段（屏蔽协议端具体段格式差异）。</summary>
public sealed class MessageSegment
{
    /// <summary>见 <see cref="SegmentTypes"/>：text / image / file / video / voice / at。</summary>
    public string Type { get; init; } = SegmentTypes.Text;

    /// <summary>text 段文本内容。</summary>
    public string Text { get; init; } = "";

    /// <summary>附件下载 URL（官方平台 attachments 携带直链，含签名 rkey，有时效）。</summary>
    public string? Url { get; init; }

    /// <summary>原始文件名（文件管道需做路径安全校验）。</summary>
    public string? FileName { get; init; }

    /// <summary>文件大小（字节；事件未携带时为 null）。</summary>
    public long? FileSize { get; init; }
}

/// <summary>模块 2 的分类输出。</summary>
public sealed class ClassifiedMessage
{
    public required MessageRecord Source { get; init; }

    public MessageKind Kind { get; init; }

    /// <summary>分类置信度 0~1（规则命中为 1.0）。</summary>
    public double Confidence { get; init; }

    /// <summary>命中的规则/模型说明（写日志与排错面板）。</summary>
    public string MatchReason { get; init; } = "";
}

// ============ 业务实体 ============

/// <summary>通知条目（通知悬浮窗数据源）。持久化于 notices.json。</summary>
public sealed class NoticeItem
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>幂等来源：对应 MessageId，重复推送不产生新条目。</summary>
    public required string MessageId { get; init; }

    public string Content { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>已读状态持久化：重启不复活。</summary>
    public bool IsRead { get; set; }

    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>作业条目（作业悬浮窗数据源）。持久化于 homework.json。</summary>
public sealed class HomeworkItem
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string MessageId { get; init; }

    /// <summary>识别出的学科（可为「未分类」，经修正后更新）。</summary>
    public string Subject { get; set; } = "未分类";

    public double SubjectConfidence { get; set; }

    public SubjectSource SubjectSource { get; set; }

    public string Content { get; init; } = "";

    /// <summary>关联附件（FileRecord.Id）。</summary>
    public IReadOnlyList<Guid> AttachmentIds { get; set; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>人工标记已完成/已处理。</summary>
    public bool IsResolved { get; set; }
}

/// <summary>文件归档记录。持久化于 files.json + MD5 索引。</summary>
public sealed class FileRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string MessageId { get; init; }

    /// <summary>原始文件名（已做路径穿越校验与长度限制）。</summary>
    public string FileName { get; init; } = "";

    public long Size { get; set; }

    /// <summary>去重键：文件内容 MD5（十六进制小写）。</summary>
    public string? Md5 { get; set; }

    /// <summary>归档相对路径：下载文件/&lt;学科&gt;/&lt;yyyy-MM-dd&gt;/&lt;文件名&gt;。</summary>
    public string? ArchivedRelativePath { get; set; }

    public FileStatus Status { get; set; } = FileStatus.Pending;

    public int AttemptCount { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>学科关键词规则（外置 JSON：subjects.json，可编辑、支持导入导出）。</summary>
public sealed class SubjectRule
{
    /// <summary>学科名称（同时是归档目录名，如「数学」）。</summary>
    public required string Subject { get; init; }

    /// <summary>关键词列表（任一命中即得分；大小写不敏感）。</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>同一条消息多学科命中时，按分值降序、再按本优先级取最高。</summary>
    public int Priority { get; init; }
}

/// <summary>学科识别结果（三级链统一返回）。</summary>
public sealed class SubjectResult
{
    public required string Subject { get; init; }

    /// <summary>置信度 0~1。</summary>
    public double Confidence { get; init; }

    public SubjectSource Source { get; init; }

    /// <summary>判定依据（关键词列表/模型标签/LLM 理由），写日志。</summary>
    public string Reason { get; init; } = "";

    /// <summary>是否低于阈值需要进人工确认队列（由链组合器统一判定后置位）。</summary>
    public bool NeedsManualConfirm { get; init; }
}

/// <summary>人工确认队列条目（低置信度兜底，永不静默丢失）。</summary>
public sealed class PendingConfirmItem
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string MessageId { get; init; }

    public string Text { get; init; } = "";

    /// <summary>候选学科与置信度（来自降级链最后一级）。</summary>
    public IReadOnlyList<SubjectResult> Candidates { get; init; } = [];

    public PendingConfirmStatus Status { get; set; } = PendingConfirmStatus.Waiting;

    /// <summary>人工选择的学科（确认后写回 HomeworkItem.Subject）。</summary>
    public string? ResolvedSubject { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>重试队列条目（指数退避；排错面板可查看/重放）。</summary>
public sealed class RetryQueueItem
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public RetryOperationType OperationType { get; init; }

    /// <summary>操作参数快照（脱敏 JSON）。</summary>
    public string PayloadJson { get; init; } = "";

    public RetryItemStatus Status { get; set; } = RetryItemStatus.Waiting;

    public int AttemptCount { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
