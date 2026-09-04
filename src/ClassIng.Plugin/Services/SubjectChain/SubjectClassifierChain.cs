using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Services.SubjectChain;

/// <summary>
/// 模块 3：三级学科识别降级链组合器。
/// 执行顺序：①关键词 → ②AI（本地 ONNX 优先 / 云端可配，任一可用且置信度达标即返回）
/// → ③人工确认队列投递。<strong>永不返回 null</strong>：任何一级失败自动进入下一级，
/// 全部失败返回 Subject=「未分类」且 NeedsManualConfirm=true 的兜底结果。
/// </summary>
public sealed class SubjectClassifierChain : ISubjectClassifierChain
{
    private readonly ISubjectClassifier _keyword;
    private readonly IReadOnlyList<IAiProvider> _aiProviders;
    private readonly IPendingConfirmStore _pendingStore;
    private readonly SubjectChainOptionsProvider _provider;
    private readonly ILogger _logger;

    public SubjectClassifierChain(
        ISubjectClassifier keyword,
        IEnumerable<IAiProvider> aiProviders,
        IPendingConfirmStore pendingStore,
        SubjectChainOptionsProvider provider,
        ILogger? logger = null)
    {
        _keyword = keyword;
        _aiProviders = (aiProviders ?? []).ToArray();
        _pendingStore = pendingStore;
        _provider = provider;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public async Task<SubjectResult> ClassifyAsync(string text, string messageId, CancellationToken ct = default)
    {
        var candidates = new List<SubjectResult>();
        try
        {
            ct.ThrowIfCancellationRequested();
            text ??= "";

            // ①关键词规则
            var keywordResult = await SafeInvoke(_keyword, text, ct);
            if (keywordResult is not null)
            {
                candidates.Add(keywordResult);
                if (IsConfident(keywordResult))
                {
                    return keywordResult;
                }
            }

            // ②AI（按设置排序：本地优先 / 云端优先）；低置信候选继续走完链
            foreach (var ai in OrderAiProviders())
            {
                if (!ai.IsAvailable)
                {
                    _logger.LogDebug("AI 级 {Provider} 不可用，跳过", ai.Name);
                    continue;
                }

                var aiResult = await SafeInvoke(ai, text, ct);
                if (aiResult is null)
                {
                    continue;
                }

                candidates.Add(aiResult);
                if (IsConfident(aiResult))
                {
                    return aiResult;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 链自身异常也不静默丢失：进入兜底路径
            _logger.LogError(ex, "学科识别链执行异常（MessageId={MessageId}），进入兜底", messageId);
        }

        return await FinalizeFallbackAsync(text, messageId, candidates, ct);
    }

    private async Task<SubjectResult> FinalizeFallbackAsync(
        string text, string messageId, List<SubjectResult> candidates, CancellationToken ct)
    {
        var settings = _provider.SafeGetSettings();

        // 低置信候选取最高者作为返回值（保留线索）；全失败则「未分类」兜底
        var best = candidates
            .OrderByDescending(r => r.Confidence)
            .FirstOrDefault();

        var fallback = best ?? new SubjectResult
        {
            Subject = "未分类",
            Confidence = 0,
            Source = SubjectSource.Manual,
            Reason = "chain_all_failed"
        };

        var manualResult = new SubjectResult
        {
            Subject = fallback.Subject,
            Confidence = fallback.Confidence,
            Source = fallback.Source,
            Reason = fallback.Reason,
            NeedsManualConfirm = true
        };

        if (settings.ManualConfirmQueueEnabled)
        {
            try
            {
                await _pendingStore.EnqueueAsync(messageId ?? "", text ?? "", candidates, ct);
            }
            catch (Exception ex)
            {
                // 投递失败也不静默：结果仍返回 NeedsManualConfirm=true，供上游重试
                _logger.LogError(ex, "人工确认队列投递失败（MessageId={MessageId}）", messageId);
            }
        }
        else
        {
            _logger.LogWarning(
                "人工确认队列已禁用；低置信结果仅返回不投递（MessageId={MessageId}, Subject={Subject}）",
                messageId, manualResult.Subject);
        }

        _logger.LogInformation(
            "学科识别降级到底部：Subject={Subject}, Confidence={Confidence}, candidates={Count}, MessageId={MessageId}",
            manualResult.Subject, manualResult.Confidence, candidates.Count, messageId);
        return manualResult;
    }

    private bool IsConfident(SubjectResult result) =>
        result.Confidence >= _provider.SafeGetSettings().ConfidenceThreshold;

    /// <summary>按 PreferLocalModel 排序 AI 提供商（本地 = 实现 ILocalAiProvider 标记者）。</summary>
    internal IReadOnlyList<IAiProvider> OrderAiProviders()
    {
        var preferLocal = _provider.SafeGetSettings().PreferLocalModel;
        var locals = _aiProviders.OfType<ILocalAiProvider>().ToArray();
        var clouds = _aiProviders.Where(p => p is not ILocalAiProvider).ToArray();
        return preferLocal
            ? locals.Cast<IAiProvider>().Concat(clouds).ToArray()
            : clouds.Concat(locals.Cast<IAiProvider>()).ToArray();
    }

    /// <summary>统一调用包装：实现级异常已各自吞掉，此处再兜一层（返回 null 进下一级）。</summary>
    private static async Task<SubjectResult?> SafeInvoke(object classifier, string text, CancellationToken ct)
    {
        try
        {
            return classifier switch
            {
                ISubjectClassifier c => await c.ClassifyAsync(text, ct),
                IAiProvider p => await p.ClassifyAsync(text, ct),
                _ => null
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
