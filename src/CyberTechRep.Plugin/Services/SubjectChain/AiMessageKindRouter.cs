using CyberTechRep.Plugin.Services.Classification;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.SubjectChain;

/// <summary>
/// 「通知/作业二分类」用途的 AI 路由组合器（需求 6 用途②）：包装关键词分类器
/// <see cref="KeywordMessageClassifier"/>，按 <see cref="AiSettings.MessageClassifyMode"/> 路由。
/// <para>
/// 路由矩阵：
/// - <see cref="AiUsageMode.Off"/>（默认）：原样返回关键词结果，AI 不参与（现状行为完全不变）；
/// - <see cref="AiUsageMode.Backup"/>：关键词给出确定结果（关键词命中/平局默认通知/空文本 Unknown）
///   → 原样返回；关键词「未命中默认通知」（<see cref="KeywordMessageClassifier.NoKeywordHitNoticeReason"/>）
///   或关键词 Unknown 且文本非空 → 问 AI（关键词未命中在需求 3 后默认落档为通知，AI 二分类兜底
///   仍先于落档执行，结果照旧覆盖关键词结果）；AI 失败/不可用降级回关键词结果（未命中即默认通知）；
/// - <see cref="AiUsageMode.Primary"/>：跳过关键词直接问 AI（每条消息都调 AI，产生持续 API 费用，
///   设置页有成本提示）；AI 失败降级回关键词分类，不阻塞主流程。
/// </para>
/// </summary>
public sealed class AiMessageKindRouter : IMessageClassifier
{
    private readonly IMessageClassifier _keyword;
    private readonly IMessageKindAiProvider? _ai;
    private readonly SubjectChainOptionsProvider _provider;
    private readonly ILogger _logger;

    public AiMessageKindRouter(
        IMessageClassifier keyword,
        IMessageKindAiProvider? aiProvider,
        SubjectChainOptionsProvider provider,
        ILogger? logger = null)
    {
        _keyword = keyword;
        _ai = aiProvider;
        _provider = provider;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public async Task<ClassifiedMessage> ClassifyAsync(MessageRecord message, CancellationToken ct = default)
    {
        var mode = _provider.SafeGetAiSettings().MessageClassifyMode;
        if (mode == AiUsageMode.Off || _ai is null || !_ai.IsAvailable)
        {
            if (mode != AiUsageMode.Off)
            {
                _logger.LogDebug("消息二分类 AI 不可用，直接走关键词链路（MessageId={MessageId}）",
                    message?.MessageId ?? "(null)");
            }

            return await _keyword.ClassifyAsync(message, ct).ConfigureAwait(false);
        }

        var text = KeywordMessageClassifier.ExtractPlainText(message?.Segments);

        if (mode == AiUsageMode.Primary)
        {
            var aiResult = await TryClassifyWithAiAsync(message, text, mode, ct).ConfigureAwait(false);
            if (aiResult is not null)
            {
                return aiResult;
            }

            // AI 唯一识别失败：降级回关键词，不阻塞主流程
            return await _keyword.ClassifyAsync(message, ct).ConfigureAwait(false);
        }

        // Backup：关键词先走；关键词「未命中默认通知」或 Unknown（空文本除外）才问 AI。
        // 需求 3 后关键词未命中的结果不再是 Unknown 而是默认通知，故触发条件同步放宽；
        // AI 结果照旧覆盖关键词结果，AI 失败/不可用返回关键词结果（未命中即默认通知）。
        var keywordResult = await _keyword.ClassifyAsync(message, ct).ConfigureAwait(false);
        if (!ShouldAskAiInBackupMode(keywordResult, text))
        {
            return keywordResult;
        }

        var backupResult = await TryClassifyWithAiAsync(message, text, mode, ct).ConfigureAwait(false);
        return backupResult ?? keywordResult;
    }

    /// <summary>
    /// Backup 模式的 AI 触发判定（internal 纯逻辑，供单测）：
    /// 文本非空 且（关键词 Unknown，或关键词给出「未命中关键词默认通知」）。
    /// 文本为空（empty_text / empty_text_with_media）不去问 AI：没有可分类文本，AI 也无从判断。
    /// </summary>
    internal static bool ShouldAskAiInBackupMode(ClassifiedMessage keywordResult, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return keywordResult.Kind == MessageKind.Unknown
               || (keywordResult.Kind == MessageKind.Notice
                   && KeywordMessageClassifier.IsNoKeywordHitDefaultReason(keywordResult.MatchReason));
    }

    /// <inheritdoc />
    public void ReloadRules() => _keyword.ReloadRules();

    /// <summary>调用二分类 AI：结构化日志（用途/模式/命中结果/耗时），失败返回 null 不抛出。</summary>
    private async Task<ClassifiedMessage?> TryClassifyWithAiAsync(
        MessageRecord? message, string text, AiUsageMode mode, CancellationToken ct)
    {
        var messageId = message?.MessageId ?? "(null)";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var verdict = await _ai!.ClassifyAsync(text, ct).ConfigureAwait(false);
            sw.Stop();
            _logger.LogInformation(
                "AI 调用完成：用途=MessageClassify, 模式={Mode}, Provider={Provider}, " +
                "命中={Kind}, 置信度={Confidence}, 耗时={ElapsedMs}ms, MessageId={MessageId}",
                mode, _ai!.Name, verdict?.Kind.ToString() ?? "(null)",
                verdict?.Confidence, sw.ElapsedMilliseconds, messageId);

            if (verdict is null)
            {
                return null;
            }

            return new ClassifiedMessage
            {
                Source = message!,
                Kind = verdict.Kind,
                Confidence = verdict.Confidence,
                MatchReason = $"ai_message_kind({mode}); reason={verdict.Reason}"
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex,
                "消息二分类 AI 调用异常（用途=MessageClassify, 模式={Mode}, 耗时={ElapsedMs}ms, MessageId={MessageId}），降级回关键词链路",
                mode, sw.ElapsedMilliseconds, messageId);
            return null;
        }
    }
}
