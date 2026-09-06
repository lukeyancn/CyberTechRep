using System.Diagnostics;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.SubjectChain;

/// <summary>
/// 学科链按用途模式路由的内部抽象（<see cref="NoKeywordFallbackClassifier"/> 等依赖此接口而非具体类，便于单测替身）。
/// </summary>
public interface ISubjectChainModeRouter
{
    /// <summary>按指定 AI 用途模式执行学科识别链（模式语义见 <see cref="AiUsageMode"/>）。</summary>
    Task<SubjectResult> ClassifyWithModeAsync(
        string text, string messageId, AiUsageMode mode, bool suppressManualQueue = false, CancellationToken ct = default);
}

/// <summary>
/// 模块 3：三级学科识别降级链组合器（CyberTechRep AI「学科分类」用途路由在此实现）。
/// <para>
/// 路由矩阵（<see cref="AiSettings.SubjectClassifyMode"/>，经 <see cref="ISubjectChainModeRouter"/> 可显式指定）：
/// - <see cref="AiUsageMode.Off"/>：①关键词 → ③兜底（AI 级整体跳过）；
/// - <see cref="AiUsageMode.Backup"/>（默认 = 现状行为）：①关键词 → ②AI → ③兜底（关键词不中/低置信才 AI）；
/// - <see cref="AiUsageMode.Primary"/>：②AI → ③兜底（跳过关键词，AI 为唯一识别方式；AI 失败降级兜底，不阻塞）。
/// </para>
/// <strong>永不返回 null</strong>：任何一级失败自动进入下一级，全部失败返回
/// Subject=「未分类」且 NeedsManualConfirm=true 的兜底结果。
/// </summary>
public sealed class SubjectClassifierChain : ISubjectClassifierChain, ISubjectChainModeRouter
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
    public Task<SubjectResult> ClassifyAsync(string text, string messageId, CancellationToken ct = default)
    {
        // 「学科分类」用途：模式由 AiSettings.SubjectClassifyMode 热读取（默认 Backup = 现状行为）
        return ClassifyWithModeAsync(
            text, messageId, _provider.SafeGetAiSettings().SubjectClassifyMode,
            suppressManualQueue: false, ct);
    }

    /// <inheritdoc />
    public async Task<SubjectResult> ClassifyWithModeAsync(
        string text, string messageId, AiUsageMode mode, bool suppressManualQueue = false, CancellationToken ct = default)
    {
        var candidates = new List<SubjectResult>();
        try
        {
            ct.ThrowIfCancellationRequested();
            text ??= "";

            // ①关键词规则（Primary 模式跳过：AI 为唯一识别方式）
            if (mode != AiUsageMode.Primary)
            {
                var keywordResult = await SafeInvoke(_keyword, text, ct);
                if (keywordResult is not null)
                {
                    candidates.Add(keywordResult);
                    if (IsConfident(keywordResult))
                    {
                        return keywordResult;
                    }
                }
            }

            // ②AI（按设置排序：本地优先 / 云端优先）；低置信候选继续走完链
            //   Off 模式整体跳过：AI 不参与该用途
            if (mode != AiUsageMode.Off)
            {
                foreach (var ai in OrderAiProviders())
                {
                    if (!ai.IsAvailable)
                    {
                        _logger.LogDebug("AI 级 {Provider} 不可用，跳过", ai.Name);
                        continue;
                    }

                    var sw = Stopwatch.StartNew();
                    var aiResult = await SafeInvoke(ai, text, ct);
                    sw.Stop();

                    // 结构化调用日志：用途/模式/提供者/命中结果/耗时（不含任何密钥信息）
                    _logger.LogInformation(
                        "AI 调用完成：用途=SubjectClassify, 模式={Mode}, Provider={Provider}, " +
                        "命中={Hit}, 置信度={Confidence}, 耗时={ElapsedMs}ms, MessageId={MessageId}",
                        mode, ai.Name,
                        aiResult?.Subject ?? "(null)",
                        aiResult?.Confidence, sw.ElapsedMilliseconds, messageId);

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

        return await FinalizeFallbackAsync(text, messageId, candidates, suppressManualQueue, ct);
    }

    private async Task<SubjectResult> FinalizeFallbackAsync(
        string text, string messageId, List<SubjectResult> candidates, bool suppressManualQueue, CancellationToken ct)
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

        if (suppressManualQueue)
        {
            // 调用方明确不投人工队列（如无关键词兜底：低置信结果直接放弃，保持「消息被忽略」现状）
            _logger.LogInformation(
                "学科识别降级到底部但人工队列被调用方抑制：Subject={Subject}, Confidence={Confidence}, MessageId={MessageId}",
                manualResult.Subject, manualResult.Confidence, messageId);
            return manualResult;
        }

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
        result.Confidence >= _provider.SafeGetAiSettings().ConfidenceThreshold;

    /// <summary>按 PreferLocalModel 排序 AI 提供商（本地 = 实现 ILocalAiProvider 标记者）。</summary>
    internal IReadOnlyList<IAiProvider> OrderAiProviders()
    {
        var preferLocal = _provider.SafeGetAiSettings().PreferLocalModel;
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
