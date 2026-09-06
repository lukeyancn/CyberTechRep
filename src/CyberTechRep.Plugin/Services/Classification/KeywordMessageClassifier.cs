using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.Classification;

/// <summary>
/// 模块 2：通知/作业关键词规则分类器。
/// <para>
/// 优先级策略：两侧均命中时<strong>作业优先</strong>（作业漏检成本更高）；
/// 仅一侧命中则取该侧；均未命中或文本为空 → <see cref="MessageKind.Unknown"/>。
/// </para>
/// </summary>
public sealed class KeywordMessageClassifier : IMessageClassifier
{
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
            var noticeHits = FindHits(text, rules.NoticeKeywords);
            var homeworkHits = FindHits(text, rules.HomeworkKeywords);

            if (noticeHits.Count == 0 && homeworkHits.Count == 0)
            {
                return Task.FromResult(Unknown(message, "no_keyword_hit"));
            }

            // 作业优先：两侧均命中 → Homework
            if (homeworkHits.Count > 0 && noticeHits.Count > 0)
            {
                return Task.FromResult(new ClassifiedMessage
                {
                    Source = message,
                    Kind = MessageKind.Homework,
                    Confidence = 1.0,
                    MatchReason =
                        $"both_hit_homework_priority; homework=[{string.Join(',', homeworkHits)}]; notice=[{string.Join(',', noticeHits)}]"
                });
            }

            if (homeworkHits.Count > 0)
            {
                return Task.FromResult(new ClassifiedMessage
                {
                    Source = message,
                    Kind = MessageKind.Homework,
                    Confidence = 1.0,
                    MatchReason = $"homework_hit=[{string.Join(',', homeworkHits)}]"
                });
            }

            return Task.FromResult(new ClassifiedMessage
            {
                Source = message,
                Kind = MessageKind.Notice,
                Confidence = 1.0,
                MatchReason = $"notice_hit=[{string.Join(',', noticeHits)}]"
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

    private static List<string> FindHits(string text, IReadOnlyList<string> keywords)
    {
        var hits = new List<string>();
        foreach (var kw in keywords)
        {
            if (kw.Length == 0)
            {
                continue;
            }

            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(kw);
            }
        }

        return hits;
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
