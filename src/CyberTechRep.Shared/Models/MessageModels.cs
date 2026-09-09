using System.Text.Json.Serialization;

namespace CyberTechRep.Shared.Models;

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

    /// <summary>
    /// 协议端原始时间戳（Unix 秒；0 = 协议端未提供，如 QQ 官方平台事件）。
    /// <para>
    /// 需求 4：续传游标的时间基准必须与启动核对（<c>get_group_msg_history</c> 的 <c>time</c>）
    /// 同源，否则「接收时刻（本地时钟）」与「消息时刻（服务端时钟）」混用会因网络延迟/时钟偏差
    /// 把未入档的旧消息误判为已覆盖而漏补。故 NapCat 规范化器透传 OneBot <c>time</c>，
    /// 游标只在该值 &gt; 0 时推进时间线（缺失时仅按消息 id 去重）。
    /// </para>
    /// </summary>
    public long SourceTimestampUnix { get; init; }

    /// <summary>消息段列表（文本/图片/文件等，已规范化）。</summary>
    public IReadOnlyList<MessageSegment> Segments { get; init; } = [];

    /// <summary>
    /// 被回复消息 id（需求 1 回复链归并）：NapCat <c>reply</c> 段携带；
    /// 官方平台事件不提供该字段（恒为空）。为空 = 非回复消息。
    /// </summary>
    public string ReplyToMessageId { get; init; } = "";

    /// <summary>原始 JSON 快照（已脱敏：不含 AppId/AppSecret/Token）。</summary>
    public string RawJsonSnapshot { get; init; } = "";
}

/// <summary>
/// 消息撤回事件（OneBot 11 <c>notice.group_recall</c> / <c>notice.friend_recall</c>）。
/// <para>
/// 查证结论（详见交付说明）：协议层支持且 NapCat 会下发，但 NapCat 对
/// 「非 API 方式撤回」（如在手机 QQ 上撤回）存在已知缺陷不上报（NapCatQQ issue #1171），
/// 因此本事件只覆盖「能收到上报」的场景；未收到上报时由手动删除与可选核对兜底。
/// </para>
/// </summary>
public sealed class MessageRecallEvent
{
    /// <summary>被撤回消息的 id（与 <see cref="MessageRecord.MessageId"/> 同一命名空间）。</summary>
    public required string MessageId { get; init; }

    /// <summary>来源群 OpenID / 群号（私聊撤回为空）。</summary>
    public string GroupOpenId { get; init; } = "";

    /// <summary>原消息发送者。</summary>
    public string SenderOpenId { get; init; } = "";

    /// <summary>执行撤回者（自己撤回时与发送者相同）。</summary>
    public string OperatorOpenId { get; init; } = "";

    public DateTimeOffset RecalledAt { get; init; } = DateTimeOffset.Now;

    /// <summary>是否来自协议端撤回上报（false = 本地核对兜底推断）。</summary>
    public bool FromProtocolNotice { get; init; } = true;
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

    /// <summary>发送者成员 OpenID（旧数据为空；通知学科前缀与成员绑定回溯按此归因）。</summary>
    public string MemberOpenId { get; init; } = "";

    /// <summary>来源群 OpenID（旧数据为空；群作用域绑定回溯匹配用）。</summary>
    public string GroupOpenId { get; init; } = "";

    /// <summary>
    /// 通知学科（写入/回溯时与前缀同步记录；空 = 未分类）。
    /// 非空即视为已定学科：成员绑定回溯只补「未分类」，绝不覆盖已有学科（人工/显式语义最高）。
    /// </summary>
    public string Subject { get; set; } = "";

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

    /// <summary>发送者成员 OpenID（协议端透传；用于「按发送者记住学科」的永久规则）。</summary>
    public string MemberOpenId { get; init; } = "";

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

/// <summary>
/// 学科作业文档条目：一条（或合并后的多条同源）作业消息在学科文档中的一次追加。
/// <para>
/// <see cref="SourceMessageIds"/> 是撤回联动的锚点：撤回任一来源消息即删除该条目
/// （合并条目会随任一来源被撤回而整体移除——合并语义下无法只删其中一段，
/// 已在交付说明中列为可容忍边界）。
/// </para>
/// </summary>
public sealed class HomeworkDocumentEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>来源消息 id 集合（去重合并后可能包含多条；撤回联动按此匹配）。</summary>
    public IReadOnlyList<string> SourceMessageIds { get; set; } = [];

    /// <summary>发送者成员 OpenID（来源元信息，仅同一发送者才允许合并）。</summary>
    public string MemberOpenId { get; init; } = "";

    /// <summary>发送者展示名（昵称；为空时显示「成员」）。</summary>
    public string SenderLabel { get; init; } = "";

    /// <summary>追加进文档的正文（连续文档样式，可含换行）。</summary>
    public string Text { get; set; } = "";

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>该条目是否为「常态化作业」勾选落档（元信息，供发送/回溯区分）。</summary>
    public bool IsStanding { get; init; }
}

/// <summary>
/// 学科作业文档（每个学科、每个归档日一份连续文档）。
/// <para>
/// 显示口径：<see cref="ManualText"/> 非空时以用户手工编辑结果为准（<b>存档为单一事实源</b>），
/// 否则由 <see cref="Entries"/> 按时间正序拼接渲染。追加写入在 ManualText 非空时
/// 以「末尾增量追加」方式并入 ManualText（既保留用户删改，又不丢新消息）。
/// </para>
/// </summary>
public sealed class HomeworkDocument
{
    /// <summary>归档日（条目 CreatedAt 的本地日期；与作业按天分桶口径一致）。</summary>
    public DateOnly Date { get; init; }

    public required string Subject { get; init; }

    /// <summary>追加写入的条目（时间正序）。</summary>
    public List<HomeworkDocumentEntry> Entries { get; set; } = [];

    /// <summary>用户手工编辑后的整篇文本（null/空 = 未编辑，按 Entries 渲染）。</summary>
    public string? ManualText { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>渲染文本（不参与序列化）：ManualText 优先，否则按条目拼接。</summary>
    [JsonIgnore]
    public string Render => ManualText is { Length: > 0 } manual
        ? manual
        : string.Join(
            Environment.NewLine,
            Entries.Select(e => e.Text).Where(t => !string.IsNullOrWhiteSpace(t)));

    /// <summary>该文档是否为空（无条目且无手工文本）。</summary>
    [JsonIgnore]
    public bool IsEmpty => Entries.Count == 0 && string.IsNullOrWhiteSpace(ManualText);
}

/// <summary>文件归档记录。持久化于 files.json + MD5 索引。</summary>
public sealed class FileRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string MessageId { get; init; }

    /// <summary>发送者成员 OpenID（旧数据为空；成员绑定回溯按此归因，空时经 MessageId→成员映射兜底）。</summary>
    public string MemberOpenId { get; set; } = "";

    /// <summary>来源群 OpenID（旧数据为空；群作用域绑定解析用）。</summary>
    public string GroupOpenId { get; set; } = "";

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
