using CyberTechRep.Shared.Models;

namespace CyberTechRep.Shared.Abstractions;

// ============ 模块 1：QQ 消息接入层 ============

/// <summary>QQ 消息接入服务（官方机器人平台 WebSocket 网关客户端封装）。</summary>
public interface IMessageIngestService : IAsyncDisposable
{
    /// <summary>当前连接状态。</summary>
    ConnectionStatus Status { get; }

    /// <summary>收到新消息（已做消息 id 幂等去重 + 群白名单过滤）。</summary>
    event EventHandler<MessageRecord>? MessageReceived;

    /// <summary>
    /// 收到消息撤回（NapCat 模式：notice.group_recall / friend_recall；官方模式当前无此事件）。
    /// 与 <see cref="MessageReceived"/> 同样先经群白名单过滤与幂等去重。
    /// </summary>
    event EventHandler<MessageRecallEvent>? MessageRecalled;

    /// <summary>连接状态变化（供托盘/排错面板提示）。</summary>
    event EventHandler<ConnectionStatus>? StatusChanged;

    Task StartAsync(CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// 历史消息回放。官方平台不提供历史补拉 API，当前实现返回空列表并记警告日志（能力缺口已在调研文档声明）。
    /// </summary>
    Task<IReadOnlyList<MessageRecord>> FetchHistoryAsync(int days, CancellationToken ct = default);

    /// <summary>手动重连（排错面板用）。</summary>
    Task ReconnectAsync(CancellationToken ct = default);

    /// <summary>
    /// 经当前协议端连接调用原生 API（仅 NapCat / OneBot 11 模式支持；官方模式返回 null）。
    /// 供断点续传（get_group_msg_history / get_msg）与 NapCat 模式发送（send_group_msg）使用。
    /// 失败/超时返回 null，不抛异常、不断连接。
    /// </summary>
    Task<System.Text.Json.JsonElement?> CallProtocolApiAsync(
        string action, IReadOnlyDictionary<string, object?> parameters, CancellationToken ct = default);
}

// ============ 模块 2：消息分类管道 ============

/// <summary>通知/作业分类器（关键词规则引擎，词表外置）。</summary>
public interface IMessageClassifier
{
    /// <summary>分类一条消息；内部异常不外抛，返回 Kind=Unknown（进降级路径）。</summary>
    Task<ClassifiedMessage> ClassifyAsync(MessageRecord message, CancellationToken ct = default);

    /// <summary>词表更新通知（设置页修改后热生效）。</summary>
    void ReloadRules();
}

// ============ 模块 3：三级学科识别链 ============

/// <summary>学科识别器统一接口：关键词/AI/人工三级实现同一契约，可独立替换与测试。</summary>
public interface ISubjectClassifier
{
    /// <summary>识别器名称（日志用）。</summary>
    string Name { get; }

    /// <summary>该级是否可用（如本地模型文件存在、云端已配置且未超每日限额）。</summary>
    bool IsAvailable { get; }

    /// <summary>执行识别；失败返回 null（链组合器进入下一级），不抛异常。</summary>
    Task<SubjectResult?> ClassifyAsync(string text, CancellationToken ct = default);
}

/// <summary>三级降级链组合器（唯一对外入口；含置信度阈值判定与人工队列投递）。</summary>
public interface ISubjectClassifierChain
{
    /// <summary>
    /// 按 ①关键词 → ②AI（本地优先/云端） → ③人工确认队列 顺序执行，永不返回 null；
    /// 低置信或全部失败时返回 NeedsManualConfirm=true 并投递 PendingConfirmItem。
    /// </summary>
    Task<SubjectResult> ClassifyAsync(string text, string messageId, CancellationToken ct = default);
}

/// <summary>AI 识别提供商接口（本地 ONNX / 云端 LLM 两个实现）。</summary>
public interface IAiProvider
{
    string Name { get; }

    bool IsAvailable { get; }

    /// <summary>返回候选学科与置信度；超时/失败/离线返回 null。</summary>
    Task<SubjectResult?> ClassifyAsync(string text, CancellationToken ct = default);
}

/// <summary>人工确认队列存储与处理。</summary>
public interface IPendingConfirmStore
{
    Task<IReadOnlyList<PendingConfirmItem>> GetWaitingAsync(CancellationToken ct = default);

    Task<PendingConfirmItem> EnqueueAsync(string messageId, string text,
        IReadOnlyList<SubjectResult> candidates, CancellationToken ct = default);

    /// <summary>人工确认：写回 HomeworkItem.Subject（SubjectSource=Manual）。</summary>
    Task ResolveAsync(Guid id, string subject, CancellationToken ct = default);
}

// ============ 模块 4：文件处理管道 ============

/// <summary>文件管道：原子下载（临时文件→MD5 校验→归档）、去重、路径安全、磁盘上限。</summary>
public interface IFilePipelineService
{
    /// <summary>文件记录变化（状态机推进，供作业悬浮窗「附件处理中」提示）。</summary>
    event EventHandler<FileRecord>? FileUpdated;

    /// <summary>入队一个待处理文件（队列化，防文件名冲突与写入竞态）。
    /// <paramref name="memberOpenId"/>/<paramref name="groupOpenId"/>：发送者/群 OpenID，
    /// 随记录持久化供成员绑定回溯归因（旧调用不传为空）。</summary>
    Task<FileRecord> EnqueueAsync(string messageId, string fileName, string? url,
        string? memberOpenId = null, string? groupOpenId = null, CancellationToken ct = default);

    /// <summary>指定文件的学科（识别完成后二次归档：移动到 学科/日期 目录）。</summary>
    Task ReassignSubjectAsync(Guid fileId, string subject, CancellationToken ct = default);

    Task<IReadOnlyList<FileRecord>> GetRecordsAsync(CancellationToken ct = default);
}

// ============ 模块 5：持久化层 ============

/// <summary>通知存储（JSON 原子写入 + 幂等 + 已读持久化）。</summary>
public interface INoticeStore
{
    /// <summary>幂等写入变化通知：悬浮窗据此合并刷新（限流由悬浮窗侧负责）。</summary>
    event EventHandler<NoticeItem>? Changed;
    /// <summary>按消息 id 幂等添加或更新。
    /// <paramref name="memberOpenId"/>：发送者成员 OpenID——该发送者已绑定学科映射且
    /// ClassificationSettings.NoticeSubjectPrefix 开启时，写入内容前附加「学科：」前缀
    /// （无映射不加，已有前缀不重复添加）；更新提示等无发送者场景传 null。
    /// <paramref name="groupOpenId"/>：来源群 OpenID——成员绑定群作用域解析与回溯归因用。
    /// <paramref name="createdAt"/>：原始时间（需求 5：作业→通知互换时连同时间元信息迁移）；
    /// null = 当前时间（常规消息路径行为不变）。</summary>
    Task<NoticeItem> AddOrUpdateAsync(string messageId, string content, string? memberOpenId = null,
        string? groupOpenId = null, DateTimeOffset? createdAt = null, CancellationToken ct = default);

    Task MarkReadAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// 删除单条通知（需求 5：通知→作业互换时从原存档移除；撤回联动同用）。
    /// 返回是否实际删除；删除成功后触发 <see cref="Changed"/> 供悬浮窗刷新。
    /// </summary>
    Task<bool> RemoveAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// 按来源消息 id 删除（撤回联动；同 MessageId 最多一条）。
    /// 返回是否实际删除；删除成功后触发 <see cref="Changed"/>。
    /// </summary>
    Task<bool> RemoveByMessageIdAsync(string messageId, CancellationToken ct = default);

    /// <summary>标记未读（已读视图「标记未读」入口；与 MarkReadAsync 对称，状态持久化）。</summary>
    Task MarkUnreadAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<NoticeItem>> GetUnreadAsync(CancellationToken ct = default);

    Task<IReadOnlyList<NoticeItem>> GetAllAsync(CancellationToken ct = default);

    /// <summary>按归档桶（CreatedAt 本地日期）查询通知（时间倒序）。</summary>
    Task<IReadOnlyList<NoticeItem>> GetByDateAsync(DateOnly date, CancellationToken ct = default);

    /// <summary>
    /// 按保留策略清理过期桶（NoticesRetentionDays=0 时不清理；未读通知绝不动，已读仅保留当天）。
    /// 返回删除条数；删除后逐条触发 <see cref="Changed"/> 供悬浮窗刷新。
    /// </summary>
    Task<int> CleanupAsync(CancellationToken ct = default);
}

/// <summary>作业存储。</summary>
public interface IHomeworkStore
{
    event EventHandler<HomeworkItem>? Changed;

    /// <summary>学科文档变化（追加/编辑/删除/撤回）：悬浮窗据此刷新文档区块。</summary>
    event EventHandler<HomeworkDocument>? DocumentChanged;

    Task<HomeworkItem> UpsertAsync(HomeworkItem item, CancellationToken ct = default);

    /// <summary>手动修正学科（作业悬浮窗入口；同时记录 SubjectSource=Manual）。</summary>
    Task SetSubjectAsync(Guid id, string subject, CancellationToken ct = default);

    Task<IReadOnlyList<HomeworkItem>> GetAllAsync(CancellationToken ct = default);

    Task<IReadOnlyList<HomeworkItem>> GetBySubjectAsync(string subject, CancellationToken ct = default);

    /// <summary>按归档桶（CreatedAt 本地日期）查询作业（时间正序）。供作业悬浮窗「仅显示当天」等日期过滤使用。</summary>
    Task<IReadOnlyList<HomeworkItem>> GetByDateAsync(DateOnly date, CancellationToken ct = default);

    /// <summary>
    /// 按保留策略清理过期桶（HomeworkRetentionDays=0 时不清理；&gt;0 保留最近 N 天含当天）。
    /// 返回删除条数；删除后逐条触发 <see cref="Changed"/> 供悬浮窗刷新。
    /// </summary>
    Task<int> CleanupAsync(CancellationToken ct = default);

    /// <summary>
    /// 删除单条作业（作业悬浮窗「删除」入口；仅删除该条，不影响按发送者的学科映射规则）。
    /// 返回是否实际删除（条目不存在时为 false）；删除成功后触发 <see cref="Changed"/> 供悬浮窗刷新。
    /// </summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    // ---- 需求 1：学科文档（每学科每日一份连续文档，追加写入 + 手工编辑共用存档）----

    /// <summary>按归档日取全部学科文档（时间正序，空文档不返回）。</summary>
    Task<IReadOnlyList<HomeworkDocument>> GetDocumentsAsync(DateOnly date, CancellationToken ct = default);

    /// <summary>
    /// 追加写入一条作业文档条目（<paramref name="entry"/> 的 CreatedAt 本地日期决定归属日）。
    /// 去重合并由存储层调用 <c>HomeworkDocumentMerger</c> 决策：完全重复 → 跳过（返回原文档，
    /// 条目不新增）；部分重叠 → 并入已有条目（并集增量）；否则新增条目。
    /// 文档已有手工编辑文本时，按「末尾增量追加」并入 ManualText，保留用户删改。
    /// 返回写入后的文档（跳过时返回未变化的文档）。
    /// </summary>
    Task<HomeworkDocument> AppendDocumentEntryAsync(
        string subject, HomeworkDocumentEntry entry, CancellationToken ct = default);

    /// <summary>
    /// 手工编辑整篇文档文本并即时回写存档（null/空 = 清除手工文本，回到按条目渲染）。
    /// 文档不存在时按需创建（仅当 manualText 非空）。返回写入后的文档或 null。
    /// <para>
    /// <paramref name="knownEntryIds"/>：进入编辑态那一刻文档里已有的条目 id 集合（编辑锚点）。
    /// 传入后做「三方合并」——只有<b>不在集合中</b>的条目（= 编辑期间新到达并落档的消息）
    /// 才把行按末尾增量并入编辑稿；集合内条目被用户删掉/改写的行一律尊重用户结果
    /// （旧口径用渲染文本做基线，用户「改写」过的旧行会因不在编辑稿里被反复重加）。
    /// 不传则整篇覆盖（旧行为，供非编辑器调用方使用）。
    /// </para>
    /// </summary>
    Task<HomeworkDocument?> SaveDocumentTextAsync(
        DateOnly date, string subject, string? manualText, CancellationToken ct = default,
        IReadOnlyCollection<Guid>? knownEntryIds = null);

    /// <summary>
    /// 按来源消息 id 从作业条目与学科文档中移除（撤回联动；需求 1）。
    /// 返回删除的作业条目数；文档条目命中的来源 id 一并移除（文档为空则整篇删除）。
    /// </summary>
    Task<int> RemoveByMessageIdAsync(string messageId, CancellationToken ct = default);

    /// <summary>
    /// 清空某学科某日的整份文档及其作业条目（需求 5：文档→通知互换时从作业存档移除）。
    /// 返回移除的作业条目数。
    /// </summary>
    Task<int> RemoveDocumentAsync(DateOnly date, string subject, CancellationToken ct = default);
}

/// <summary>
/// 通知 / 作业手动互换（需求 5）：误识别的消息一键换类，连同内容、来源、时间元信息
/// 迁移到对方存档，并写入「消息类型覆盖」持久化记录，重启后保持（且重投/重放不回摆）。
/// </summary>
public interface IMessageReclassifyService
{
    /// <summary>通知 → 作业（指定学科；空学科归入「未分类」）。返回是否成功。</summary>
    Task<bool> MoveNoticeToHomeworkAsync(Guid noticeId, string subject, CancellationToken ct = default);

    /// <summary>作业条目 → 通知。返回是否成功。</summary>
    Task<bool> MoveHomeworkToNoticeAsync(Guid homeworkId, CancellationToken ct = default);

    /// <summary>
    /// 整份学科文档（某日）→ 通知：手工编辑过则整篇作为一条通知，
    /// 否则逐条来源消息各生成一条通知。返回迁移条数。
    /// </summary>
    Task<int> MoveDocumentToNoticeAsync(DateOnly date, string subject, CancellationToken ct = default);

    /// <summary>该消息是否已被人工换类（管道写入前查询，命中即按覆盖后的类型落档）。</summary>
    bool TryGetKindOverride(string messageId, out MessageKind kind);
}

/// <summary>单群发送结果（逐群汇总，供 UI 反馈失败清单）。</summary>
public sealed record GroupSendResult(string GroupOpenId, bool Success, string? Error);

/// <summary>作业清单整理并发送（QQ 官方机器人开放平台群消息，目标 = 连接设置 TargetGroupOpenIds，与消息接管白名单独立）。</summary>
public interface IHomeworkSendService
{
    /// <summary>发送功能是否开启（防误发开关经构造注入，默认开）。</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// 向目标群（连接设置 TargetGroupOpenIds，独立于消息接管白名单）逐一发送文本消息（msg_type=0）。
    /// 默认主动消息（不带 msg_id，受平台频控），
    /// 传入 <paramref name="msgId"/> 则按被动回复发送（5 分钟有效期、同 msg_id 最多回复 5 次）。
    /// 逐群独立尝试、互不影响；单群失败不中断其余群，结果逐群汇总由调用方反馈到 UI（不静默）。
    /// </summary>
    /// <param name="content">文本内容（已含整理后的清单与尾注）。</param>
    /// <param name="msgId">被动回复来源消息 id；null = 主动消息。</param>
    Task<IReadOnlyList<GroupSendResult>> SendTextToTargetGroupsAsync(
        string content, string? msgId = null, CancellationToken ct = default);
}

/// <summary>设置服务：五组配置的加载/保存/导入导出/恢复默认；敏感字段加密存取。</summary>
public interface ISettingsService
{
    AppSettings Current { get; }

    /// <summary>设置变更广播，各模块即时生效。</summary>
    event EventHandler<AppSettings>? SettingsChanged;

    Task SaveAsync(CancellationToken ct = default);

    /// <summary>导入（校验 SchemaVersion；Secret 字段保持本地值不被覆盖）。</summary>
    Task ImportAsync(string json, CancellationToken ct = default);

    /// <summary>导出（脱敏：不含 AppSecret/ApiKey 等敏感字段）。</summary>
    Task<string> ExportAsync(CancellationToken ct = default);

    Task ResetToDefaultsAsync(CancellationToken ct = default);

    /// <summary>敏感信息解密读取（DPAPI）。</summary>
    string Unprotect(string protectedValue);

    /// <summary>敏感信息加密写入（DPAPI）。</summary>
    string Protect(string plainValue);
}

// ============ 模块 6/7：悬浮窗 ============

/// <summary>悬浮窗控制器（两窗共用）：置顶/拖拽/缩放/DPI 与多屏适配/一键复位。</summary>
public interface ISuspensionWindowController
{
    /// <summary>按配置创建并显示指定悬浮窗（UI 线程调度）。overlayKey：notice / homework。</summary>
    Task ShowAsync(string overlayKey, CancellationToken ct = default);

    Task HideAsync(string overlayKey, CancellationToken ct = default);

    /// <summary>拖出屏幕后从设置页一键复位到默认位置。</summary>
    Task ResetPositionAsync(string overlayKey, CancellationToken ct = default);

    /// <summary>设置变更（透明度/字号/位置）即时应用。</summary>
    Task ApplySettingsAsync(string overlayKey, OverlayWindowSettings settings, CancellationToken ct = default);

    /// <summary>窗口是否在任一显示器可视范围内（排错面板自检）。</summary>
    bool IsOnScreen(string overlayKey);

    /// <summary>
    /// 宿主停止通知（SettingsChangeApplier 在 IHostedService.StopAsync 调用，早于宿主关闭所有窗口）：
    /// 通知控制器进入「退出抑制」——此后窗口因宿主退出被批量关闭触发的可见性同步
    /// 不再把 Visible=false 持久化，否则每次正常退出都会把全部悬浮窗的关闭态写回
    /// settings.json，导致重启后所有悬浮窗默认全关。用户主动 ×/设置页隐藏仍正常持久化。
    /// </summary>
    void NotifyHostStopping();

    /// <summary>
    /// 悬浮窗文档编辑态开关（UI 线程调度）：编辑态需要真实键盘输入，控制器会临时允许该窗
    /// 被激活（清掉 <c>WS_EX_NOACTIVATE</c> 并置前台）并暂停 Z 序归位，退出编辑态后恢复
    /// 「不抢焦点、钉在桌面层」的既有契约。非 Windows/窗口未创建时静默忽略。
    /// </summary>
    void SetOverlayEditing(string overlayKey, bool editing);
}

// ============ 模块 8：更新与排错 ============

/// <summary>失败重试队列（指数退避；排错面板可查看与手动重放）。</summary>
public interface IRetryQueueService
{
    Task EnqueueAsync(RetryOperationType type, object payload, CancellationToken ct = default);

    Task<IReadOnlyList<RetryQueueItem>> GetAllAsync(CancellationToken ct = default);

    /// <summary>手动重放（排错面板按钮）。</summary>
    Task ReplayAsync(Guid id, CancellationToken ct = default);

    /// <summary>清空已成功条目。</summary>
    Task PurgeSucceededAsync(CancellationToken ct = default);
}

/// <summary>更新检测与提示（检测结果写入通知悬浮窗）。</summary>
public interface IUpdateNotifyService
{
    /// <summary>检测到新版本。</summary>
    event EventHandler<UpdateInfo>? UpdateDetected;

    Task CheckAsync(CancellationToken ct = default);
}

/// <summary>环境状态探测（磁盘不足/协议端离线 → 悬浮窗或托盘可见提示）。</summary>
public interface IEnvironmentMonitorService
{
    /// <summary>磁盘剩余空间是否低于阈值。</summary>
    bool IsDiskSpaceLow();

    /// <summary>协议端离线时长（在线时返回 null）。</summary>
    TimeSpan? OfflineDuration { get; }

    /// <summary>环境告警（磁盘不足/长期离线等）。</summary>
    event EventHandler<string>? WarningRaised;
}

/// <summary>更新信息。</summary>
public sealed record UpdateInfo(string LatestVersion, string CurrentVersion, string DownloadUrl, string ReleaseNotes);
