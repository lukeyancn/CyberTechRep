using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.Classification;

/// <summary>
/// 模块 2：通知/作业关键词规则分类器。
/// <para>
/// 仲裁策略（关键词计数比较，取代旧的「两侧均命中 → 作业优先」规则）：
/// 分别独立统计通知侧与作业侧的<strong>关键词命中总次数</strong>——
/// 每个关键词在文本中的每一次出现都计入（同一关键词出现多次计多次；大小写不敏感；非重叠匹配），
/// 类别总分 = 该类别全部关键词出现次数之和。
/// </para>
/// <para>
/// ① 计数多者胜（两侧都命中时按计数比较）；② 仅一侧命中则取该侧；
/// ③ 计数相等 → 固定平局规则 <see cref="TieBreakNoticeWins"/>：通知（Notice）胜，
/// 并以 reason <c>count_tie_notice_default</c> 标记；
/// ④ 均未命中或文本为空 → <see cref="MessageKind.Unknown"/>（reason 不变）。
/// </para>
/// </summary>
public sealed class KeywordMessageClassifier : IMessageClassifier
{
    /// <summary>
    /// 平局仲裁常量：通知/作业命中计数相等（含非零平局；双零已提前走 no_keyword_hit）时，
    /// 归类为通知（Notice）——true = 通知胜。唯一、显式、有文档的平局规则；
    /// 通知漏检可由用户在通知中心看到，作业误判成本更高，故平局宁可判通知。
    /// </summary>
    public const bool TieBreakNoticeWins = true;


    private readonly ClassifierOptionsProvider _provider;
    private readonly ILogger _logger;
    private readonly object _reloadLock = new();
    private volatile KeywordRulesSnapshot _rules;

    public KeywordMessageClassifier(ClassifierOptionsProvider provider, ILogger? logger = null)
    {
        _provider = provider;
        _logger = logger ?? NullLogger.Instance;
        _rules = KeywordRulesFile.FromSettings(SafeGetSettings());
        ReloadRules();
    }

    /// <inheritdoc />
    public Task<ClassifiedMessage> ClassifyAsync(MessageRecord message, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            if (message is null)
            {
                _logger.LogWarning("ClassifyAsync 收到 null MessageRecord，返回 Unknown");
                return Task.FromResult(Unknown(CreateEmptySource(), "message_null"));
            }

            var text = ExtractPlainText(message.Segments);
            if (string.IsNullOrWhiteSpace(text))
            {
                // 仅图片/文件、无文本：无法做关键词分流 → Unknown（交下游人工/排错）
                var hasMedia = message.Segments.Any(s =>
                    s.Type is SegmentTypes.Image or SegmentTypes.File or SegmentTypes.Video or SegmentTypes.Voice);
                var reason = hasMedia
                    ? "empty_text_with_media"
                    : "empty_text";
                _logger.LogDebug(
                    "消息 {MessageId} 无可分类文本（{Reason}），返回 Unknown",
                    message.MessageId, reason);
                return Task.FromResult(Unknown(message, reason));
            }

            var rules = _rules;
            var notice = TallyHits(text, rules.NoticeKeywords);
            var homework = TallyHits(text, rules.HomeworkKeywords);

            // 仲裁 ①：两侧命中总次数均为 0 → Unknown（保留原 reason，行为不变）
            if (notice.TotalCount == 0 && homework.TotalCount == 0)
            {
                return Task.FromResult(Unknown(message, "no_keyword_hit"));
            }

            // 仲裁 ②：计数比较（取代旧「两侧均命中 → 作业优先」规则）。
            // 仅一侧命中时沿用旧 reason 字符串（notice_hit= / homework_hit=），
            // 两侧都命中时用 *_count_win 并附上两侧计数，便于排查。
            if (homework.TotalCount > notice.TotalCount)
            {
                var reason = notice.TotalCount == 0
                    ? $"homework_hit=[{string.Join(',', homework.Hits)}]"
                    : $"homework_count_win; homework_count={homework.TotalCount}; " +
                      $"notice_count={notice.TotalCount}; " +
                      $"homework=[{string.Join(',', homework.Hits)}]; " +
                      $"notice=[{string.Join(',', notice.Hits)}]";
                return Task.FromResult(new ClassifiedMessage
                {
                    Source = message,
                    Kind = MessageKind.Homework,
                    Confidence = 1.0,
                    MatchReason = reason
                });
            }

            if (notice.TotalCount > homework.TotalCount)
            {
                var reason = homework.TotalCount == 0
                    ? $"notice_hit=[{string.Join(',', notice.Hits)}]"
                    : $"notice_count_win; notice_count={notice.TotalCount}; " +
                      $"homework_count={homework.TotalCount}; " +
                      $"notice=[{string.Join(',', notice.Hits)}]; " +
                      $"homework=[{string.Join(',', homework.Hits)}]";
                return Task.FromResult(new ClassifiedMessage
                {
                    Source = message,
                    Kind = MessageKind.Notice,
                    Confidence = 1.0,
                    MatchReason = reason
                });
            }

            // 仲裁 ③：计数相等（平局）→ 固定规则 TieBreakNoticeWins：通知胜。
            // reason 带上 count_tie_notice_default 以区分普通 notice_hit / notice_count_win。
            return Task.FromResult(new ClassifiedMessage
            {
                Source = message,
                Kind = MessageKind.Notice,
                Confidence = 1.0,
                MatchReason = $"count_tie_notice_default(TieBreakNoticeWins); " +
                              $"notice_count={notice.TotalCount}; " +
                              $"homework_count={homework.TotalCount}; " +
                              $"notice=[{string.Join(',', notice.Hits)}]; " +
                              $"homework=[{string.Join(',', homework.Hits)}]"
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "消息分类异常，返回 Unknown（MessageId={MessageId}）",
                message?.MessageId ?? "(null)");
            return Task.FromResult(Unknown(
                message ?? CreateEmptySource(),
                "classify_exception:" + ex.GetType().Name));
        }
    }

    /// <inheritdoc />
    public void ReloadRules()
    {
        lock (_reloadLock)
        {
            try
            {
                Directory.CreateDirectory(_provider.DataDirectory);
                var path = Path.Combine(_provider.DataDirectory, _provider.KeywordsFileName);
                var settings = SafeGetSettings();
                var snapshot = KeywordRulesFile.LoadOrSeed(path, settings);
                _rules = snapshot;
                _logger.LogInformation(
                    "分类关键词已重载：notice={NoticeCount}, homework={HomeworkCount}, source={Source}",
                    snapshot.NoticeKeywords.Count,
                    snapshot.HomeworkKeywords.Count,
                    snapshot.Source);
            }
            catch (Exception ex)
            {
                // 热重载失败：保留旧规则，不抛出
                _logger.LogError(ex, "ReloadRules 失败，继续使用上一份关键词表");
            }
        }
    }

    /// <summary>从 Segments 拼接全部 text 段（供分类输入；忽略非 text 段）。</summary>
    public static string ExtractPlainText(IReadOnlyList<MessageSegment>? segments)
    {
        if (segments is null || segments.Count == 0)
        {
            return "";
        }

        return string.Concat(
            segments
                .Where(s => string.Equals(s.Type, SegmentTypes.Text, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Text ?? ""));
    }

    private ClassificationSettings SafeGetSettings()
    {
        try
        {
            return _provider.GetSettings() ?? new ClassificationSettings();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取 ClassificationSettings 失败，使用内置默认关键词");
            return new ClassificationSettings();
        }
    }

    /// <summary>关键词命中统计：命中过的关键词（去重列表）+ 出现总次数。</summary>
    private readonly record struct KeywordTally(List<string> Hits, int TotalCount);

    /// <summary>
    /// 统计一组关键词在文本中的命中情况。
    /// <para>
    /// 计数口径（文档化的唯一口径）：每个关键词的每一次出现都计入
    /// （同一关键词出现多次计多次，大小写不敏感，非重叠匹配）；
    /// <see cref="KeywordTally.TotalCount"/> = 全部关键词出现次数之和。
    /// </para>
    /// </summary>
    private static KeywordTally TallyHits(string text, IReadOnlyList<string> keywords)
    {
        var hits = new List<string>();
        var total = 0;
        foreach (var kw in keywords)
        {
            if (kw.Length == 0)
            {
                continue;
            }

            var occurrences = CountOccurrences(text, kw);
            if (occurrences > 0)
            {
                hits.Add(kw);
                total += occurrences;
            }
        }

        return new KeywordTally(hits, total);
    }

    /// <summary>
    /// 统计单个关键词在文本中的出现次数：大小写不敏感（OrdinalIgnoreCase）、
    /// 非重叠匹配（命中后从关键词长度处继续向后扫描）。
    /// </summary>
    private static int CountOccurrences(string text, string keyword)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += keyword.Length;
        }

        return count;
    }

    private static ClassifiedMessage Unknown(MessageRecord source, string reason) => new()
    {
        Source = source,
        Kind = MessageKind.Unknown,
        Confidence = 0,
        MatchReason = reason
    };

    private static MessageRecord CreateEmptySource() => new()
    {
        MessageId = "",
        GroupOpenId = "",
        ReceivedAt = DateTimeOffset.UtcNow
    };
}
