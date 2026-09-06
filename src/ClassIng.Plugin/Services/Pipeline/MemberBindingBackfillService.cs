using System.Runtime.Versioning;
using ClassIng.Plugin.Services.Stores;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Services.Pipeline;

/// <summary>
/// 成员学科绑定回溯器：订阅 <see cref="MemberSubjectBindingStore.BindingSet"/>（唯一收口点，
/// 悬浮窗点选与设置页写入均经 <c>Set</c>），对历史「未分类」数据做绑定回溯——
/// <list type="bullet">
/// <item><b>通知回溯</b>：经 <see cref="NoticeStore.BackfillMemberSubjectAsync"/> 把该成员
/// （按 MemberOpenId，逐条按群作用域解析：先该群绑定再全局）学科为空/未分类的通知补记绑定学科
/// 并持久化、广播 <see cref="INoticeStore.Changed"/> 刷新悬浮窗；已有学科（含人工修正）不覆盖。</item>
/// <item><b>文件回溯</b>：扫描该成员「未分类」的已归档文件记录，按绑定学科走既有二次归档路径
/// <see cref="IFilePipelineService.ReassignSubjectAsync"/>（下载文件/&lt;学科&gt;/&lt;yyyy-MM-dd&gt;/）；
/// 已归档到其他学科的记录不动。旧记录无发送者归因时经 MessageId→成员映射（作业+通知存储）兜底。</item>
/// </list>
/// 容错纪律：回溯为 fire-and-forget 后台任务，全程 try/catch 只记日志（发送者脱敏口径：
/// 昵称+短哈希，绝不输出 OpenID 明文），绝不抛异常、绝不阻塞消息主流程与绑定写入。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MemberBindingBackfillService : IHostedService, IDisposable
{
    private readonly MemberSubjectBindingStore? _bindings;
    private readonly NoticeStore? _noticeStore;
    private readonly IHomeworkStore? _homeworkStore;
    private readonly IFilePipelineService? _filePipeline;
    private readonly ILogger _logger;
    private EventHandler<MemberBindingChangedEventArgs>? _handler;

    public MemberBindingBackfillService(
        MemberSubjectBindingStore? bindings = null,
        NoticeStore? noticeStore = null,
        IHomeworkStore? homeworkStore = null,
        IFilePipelineService? filePipeline = null,
        ILogger? logger = null)
    {
        _bindings = bindings;
        _noticeStore = noticeStore;
        _homeworkStore = homeworkStore;
        _filePipeline = filePipeline;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Attach();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Detach();
        return Task.CompletedTask;
    }

    /// <summary>接线事件订阅（重复调用先退订旧订阅）。</summary>
    public void Attach()
    {
        if (_bindings is null || _handler is not null)
        {
            return;
        }

        _handler = (_, args) =>
        {
            if (args is not null)
            {
                // fire-and-forget：回溯失败只记日志，绝不阻塞绑定写入与消息主流程
                _ = RunBackfillAsync(args.MemberOpenId, args.GroupOpenId, args.Subject, CancellationToken.None);
            }
        };
        _bindings.BindingSet += _handler;
        _logger.LogInformation("成员绑定回溯器已接线（BindingSet → 通知/文件未分类回溯）");
    }

    public void Detach()
    {
        if (_bindings is not null && _handler is not null)
        {
            _bindings.BindingSet -= _handler;
            _handler = null;
        }
    }

    public void Dispose() => Detach();

    /// <summary>
    /// 单次绑定写入的完整回溯（通知 + 文件）。所有环节内部消化异常。
    /// internal 供单元测试直接驱动。
    /// </summary>
    internal async Task RunBackfillAsync(string memberOpenId, string groupOpenId, string subject,
        CancellationToken ct = default)
    {
        var masked = _bindings is null ? memberOpenId : MessageDispatchService.MaskMember(null, memberOpenId);
        try
        {
            var noticeCount = await BackfillNoticesAsync(memberOpenId, ct).ConfigureAwait(false);
            var fileCount = await BackfillFilesAsync(memberOpenId, ct).ConfigureAwait(false);
            if (noticeCount > 0 || fileCount > 0)
            {
                _logger.LogInformation(
                    "成员绑定回溯完成（Member={Member}, Group={Group}, Subject={Subject}）：通知补记 {Notices} 条，文件重归档 {Files} 条",
                    masked, groupOpenId.Length == 0 ? "(全局)" : groupOpenId, subject, noticeCount, fileCount);
            }
            else
            {
                _logger.LogDebug(
                    "成员绑定回溯：无未分类历史数据可回补（Member={Member}, Subject={Subject}）", masked, subject);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("成员绑定回溯被取消（Member={Member}）", masked);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "成员绑定回溯失败（Member={Member}）；不影响绑定与主流程", masked);
        }
    }

    /// <summary>通知回溯：委托 <see cref="NoticeStore.BackfillMemberSubjectAsync"/>（未分类补记，已有学科不覆盖）。</summary>
    private async Task<int> BackfillNoticesAsync(string memberOpenId, CancellationToken ct)
    {
        if (_noticeStore is null)
        {
            return 0;
        }

        try
        {
            return await _noticeStore.BackfillMemberSubjectAsync(memberOpenId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "通知学科回溯失败（Member 已脱敏），跳过通知回溯");
            return 0;
        }
    }

    /// <summary>
    /// 文件回溯：该成员的已归档「未分类」记录 → 按当前生效绑定（群作用域 → 全局）二次归档。
    /// 已归档到其他学科的记录不动（红线）；非已归档状态（下载中/失败/重复）不可移动，跳过。
    /// </summary>
    private async Task<int> BackfillFilesAsync(string memberOpenId, CancellationToken ct)
    {
        if (_filePipeline is null || _bindings is null)
        {
            return 0;
        }

        IReadOnlyList<FileRecord> records;
        try
        {
            records = await _filePipeline.GetRecordsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "文件记录读取失败，跳过文件绑定回溯");
            return 0;
        }

        // 旧记录归因兜底：MessageId → (MemberOpenId, GroupOpenId)（作业 + 通知存储）
        Dictionary<string, (string Member, string Group)> attribution;
        try
        {
            attribution = await BuildMessageAttributionAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MessageId→成员归因映射构建失败，仅按文件记录自带发送者回溯");
            attribution = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        }

        var count = 0;
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();

            // 归因：优先记录自带发送者（新记录）；空则经映射兜底（旧记录）
            var member = record.MemberOpenId;
            var group = record.GroupOpenId;
            if (string.IsNullOrEmpty(member)
                && attribution.TryGetValue(record.MessageId, out var mapped))
            {
                member = mapped.Member;
                group = mapped.Group;
            }

            if (!string.Equals(member, memberOpenId, StringComparison.Ordinal))
            {
                continue;
            }

            // 只回补「未分类」：当前学科（归档路径首段）为未分类才动；已归档其他学科绝不覆盖
            if (record.Status != FileStatus.Archived || string.IsNullOrEmpty(record.ArchivedRelativePath))
            {
                continue;
            }

            var currentSubject = record.ArchivedRelativePath.Split('/', '\\')[0];
            if (!string.Equals(currentSubject, HomeworkSubjectResolver.Unclassified, StringComparison.Ordinal))
            {
                continue;
            }

            // 逐条按群作用域解析生效绑定（先该群绑定再全局），保证群/全局混绑语义正确
            if (!_bindings.TryGetSubject(member, group, out var subject)
                || string.IsNullOrWhiteSpace(subject))
            {
                continue;
            }

            try
            {
                await _filePipeline.ReassignSubjectAsync(record.Id, subject, ct).ConfigureAwait(false);
                count++;
                _logger.LogInformation(
                    "文件绑定回溯：未分类文件已按成员绑定重归档（RecordId={RecordId}, File={File}, Subject={Subject}）",
                    record.Id, record.FileName, subject);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "文件绑定回溯单条失败（RecordId={RecordId}, Subject={Subject}）；该操作幂等，可由后续绑定写入再次触发",
                    record.Id, subject);
            }
        }

        return count;
    }

    /// <summary>MessageId → (MemberOpenId, GroupOpenId) 归因映射（作业存储含发送者；通知存储含发送者与群）。</summary>
    private async Task<Dictionary<string, (string Member, string Group)>> BuildMessageAttributionAsync(
        CancellationToken ct)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        if (_homeworkStore is not null)
        {
            foreach (var homework in await _homeworkStore.GetAllAsync(ct).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(homework.MessageId) && !string.IsNullOrWhiteSpace(homework.MemberOpenId))
                {
                    map.TryAdd(homework.MessageId, (homework.MemberOpenId, ""));
                }
            }
        }

        if (_noticeStore is not null)
        {
            foreach (var notice in await _noticeStore.GetAllAsync(ct).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(notice.MessageId) && !string.IsNullOrWhiteSpace(notice.MemberOpenId))
                {
                    map.TryAdd(notice.MessageId, (notice.MemberOpenId, notice.GroupOpenId));
                }
            }
        }

        return map;
    }
}
