using ClassIng.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Services.SubjectChain;

/// <summary>无关键词消息兜底识别（需求 6 用途③）抽象，供 <c>MessageDispatchService</c> 与单测注入。</summary>
public interface INoKeywordFallbackClassifier
{
    /// <summary>
    /// 对「无任何通知/作业关键词命中（消息将被忽略）」的文本做兜底学科识别；
    /// 未启用/未命中/低置信/失败返回 null（调用方保持现状：忽略该消息），不抛异常。
    /// </summary>
    Task<SubjectResult?> ClassifyAsync(string text, string messageId, CancellationToken ct = default);
}

/// <summary>
/// 无关键词消息兜底识别器（需求 6 用途③）：文本中无任何通知/作业关键词、消息即将被忽略时，
/// 按 <see cref="AiSettings.NoKeywordFallbackMode"/> 兜底走学科识别链：
/// - <see cref="AiUsageMode.Backup"/>：完整学科链（学科关键词级可无 AI 直接命中；不中才 AI）；
/// - <see cref="AiUsageMode.Primary"/>：跳过学科关键词级直接 AI（AI 为唯一识别方式）。
/// 两种模式仅在消息已被关键词二分判定为 Unknown 时触发（触发条件本身即「后补」性质，
/// 二者差异体现为链内是否先尝试学科关键词级，见交付报告取舍说明）。
/// <para>
/// 纪律：识别结果低置信（NeedsManualConfirm）或「未分类」时返回 null（保持现状忽略该消息），
/// 且链调用抑制人工确认队列投递，避免无 UI 的待确认队列被堆积。
/// </para>
/// </summary>
public sealed class NoKeywordFallbackClassifier : INoKeywordFallbackClassifier
{
    private readonly ISubjectChainModeRouter _chain;
    private readonly SubjectChainOptionsProvider _provider;
    private readonly ILogger _logger;

    public NoKeywordFallbackClassifier(
        ISubjectChainModeRouter chain,
        SubjectChainOptionsProvider provider,
        ILogger? logger = null)
    {
        _chain = chain;
        _provider = provider;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public async Task<SubjectResult?> ClassifyAsync(string text, string messageId, CancellationToken ct = default)
    {
        var mode = _provider.SafeGetAiSettings().NoKeywordFallbackMode;
        if (mode == AiUsageMode.Off)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            // 纯图片/文件、无可分类文本：AI 也无从判断，保持现状（忽略）
            return null;
        }

        SubjectResult result;
        try
        {
            result = await _chain
                .ClassifyWithModeAsync(text, messageId, mode, suppressManualQueue: true, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "无关键词兜底识别失败（MessageId={MessageId}），保持现状忽略该消息", messageId);
            return null;
        }

        if (result.NeedsManualConfirm ||
            string.IsNullOrWhiteSpace(result.Subject) ||
            string.Equals(result.Subject, "未分类", StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "无关键词兜底识别未得到可信结果（MessageId={MessageId}, Subject={Subject}, " +
                "NeedsManualConfirm={NeedsManualConfirm}），保持现状忽略该消息",
                messageId, result.Subject, result.NeedsManualConfirm);
            return null;
        }

        _logger.LogInformation(
            "无关键词兜底识别命中（MessageId={MessageId}, Subject={Subject}, Source={Source}, " +
            "Confidence={Confidence}, 模式={Mode}），按作业归档",
            messageId, result.Subject, result.Source, result.Confidence, mode);
        return result;
    }
}
