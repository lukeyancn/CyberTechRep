using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Services.SubjectChain;

/// <summary>
/// 模块 3 第①级：学科关键词规则识别器（subjects.json 外置词表）。
/// 命中返回 Confidence=1.0（与模块 2 关键词分类口径一致）；未命中返回 null（链进下一级）。
/// </summary>
public sealed class KeywordSubjectClassifier : ISubjectClassifier
{
    private readonly SubjectChainOptionsProvider _provider;
    private readonly ILogger _logger;
    private readonly object _reloadLock = new();
    private volatile IReadOnlyList<SubjectRule> _rules = [];
    private volatile string _source = "not-loaded";

    public KeywordSubjectClassifier(SubjectChainOptionsProvider provider, ILogger? logger = null)
    {
        _provider = provider;
        _logger = logger ?? NullLogger.Instance;
        ReloadRules();
    }

    /// <inheritdoc />
    public string Name => "KeywordRule";

    /// <inheritdoc />
    public bool IsAvailable => _rules.Count > 0;

    /// <summary>词表热重载（设置页/用户编辑 subjects.json 后调用）。</summary>
    public void ReloadRules()
    {
        lock (_reloadLock)
        {
            try
            {
                var (rules, source) = SubjectRuleFile.LoadOrSeed(_provider);
                _rules = rules;
                _source = source;
                _logger.LogInformation(
                    "学科关键词表已加载：subjects={Count}, source={Source}", rules.Count, source);
            }
            catch (Exception ex)
            {
                // 保留旧词表，不抛出（降级原则）
                _logger.LogError(ex, "学科关键词表重载失败，继续使用上一份");
            }
        }
    }

    /// <inheritdoc />
    public Task<SubjectResult?> ClassifyAsync(string text, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(text))
            {
                return Task.FromResult<SubjectResult?>(null);
            }

            var rules = _rules;
            if (rules.Count == 0)
            {
                return Task.FromResult<SubjectResult?>(null);
            }

            // 多学科命中：按命中数降序 → Priority 降序取最高；全为 0 命中则 null
            SubjectRule? best = null;
            var bestHits = 0;
            List<string>? bestHitKeywords = null;

            foreach (var rule in rules)
            {
                var hits = new List<string>();
                foreach (var kw in rule.Keywords)
                {
                    if (kw.Length > 0 && text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    {
                        hits.Add(kw);
                    }
                }

                if (hits.Count == 0)
                {
                    continue;
                }

                if (best is null ||
                    hits.Count > bestHits ||
                    (hits.Count == bestHits && rule.Priority > best.Priority))
                {
                    best = rule;
                    bestHits = hits.Count;
                    bestHitKeywords = hits;
                }
            }

            if (best is null || bestHitKeywords is null)
            {
                _logger.LogDebug("关键词级未命中学科（文本长度={Length}）", text.Length);
                return Task.FromResult<SubjectResult?>(null);
            }

            _logger.LogInformation(
                "关键词级命中学科 {Subject}：[{Keywords}]（source={Source}）",
                best.Subject, string.Join(',', bestHitKeywords), _source);
            return Task.FromResult<SubjectResult?>(new SubjectResult
            {
                Subject = best.Subject,
                Confidence = 1.0,
                Source = SubjectSource.KeywordRule,
                Reason = $"keyword_hit=[{string.Join(',', bestHitKeywords)}]"
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "关键词学科识别异常，返回 null 进入下一级");
            return Task.FromResult<SubjectResult?>(null);
        }
    }
}
