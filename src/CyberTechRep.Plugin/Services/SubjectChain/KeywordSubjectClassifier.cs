using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.SubjectChain;

/// <summary>
/// 模块 3 第①级：学科关键词规则识别器（subjects.json 外置词表）。
/// <para>
/// 计数仲裁：每条规则得分 = 其全部关键词的出现次数总和（同关键词多次出现计多次，大小写不敏感）；
/// 得分最高者胜，平局按 Priority 降序，Priority 也相同时按学科名 Ordinal 升序（确定性）。
/// 命中返回 Confidence=1.0（与模块 2 关键词分类口径一致）；未命中返回 null（链进下一级）。
/// </para>
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

            // 计数仲裁（文档化的唯一口径）：
            // 每条规则的得分 = 其全部关键词在文本中的出现次数总和
            //（同一关键词出现多次计多次；大小写不敏感；非重叠匹配）。
            // 胜者 = 得分最高者；得分相同 → Priority 高者胜；
            // Priority 也相同 → 学科名（Subject）按 StringComparer.Ordinal 升序，
            // 保证与 subjects.json 中的书写顺序无关的确定性结果。
            SubjectRule? best = null;
            var bestScore = 0;
            var bestPriority = 0;
            List<string>? bestHitKeywords = null;

            foreach (var rule in rules)
            {
                var hits = new List<string>();
                var score = 0;
                foreach (var kw in rule.Keywords)
                {
                    if (kw.Length == 0)
                    {
                        continue;
                    }

                    var occurrences = CountOccurrences(text, kw);
                    if (occurrences > 0)
                    {
                        hits.Add(kw);
                        score += occurrences;
                    }
                }

                if (score == 0)
                {
                    continue;
                }

                var better = best is null ||
                             score > bestScore ||
                             (score == bestScore && rule.Priority > bestPriority) ||
                             (score == bestScore && rule.Priority == bestPriority &&
                                 string.CompareOrdinal(rule.Subject, best.Subject) < 0);
                if (better)
                {
                    best = rule;
                    bestScore = score;
                    bestPriority = rule.Priority;
                    bestHitKeywords = hits;
                }
            }

            if (best is null || bestHitKeywords is null)
            {
                _logger.LogDebug("关键词级未命中学科（文本长度={Length}）", text.Length);
                return Task.FromResult<SubjectResult?>(null);
            }

            _logger.LogInformation(
                "关键词级命中学科 {Subject}：[{Keywords}]（score={Score}, source={Source}）",
                best.Subject, string.Join(',', bestHitKeywords), bestScore, _source);
            return Task.FromResult<SubjectResult?>(new SubjectResult
            {
                Subject = best.Subject,
                Confidence = 1.0,
                Source = SubjectSource.KeywordRule,
                Reason = $"keyword_hit=[{string.Join(',', bestHitKeywords)}]; score={bestScore}"
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

    /// <summary>
    /// 统计单个关键词在文本中的出现次数：大小写不敏感（OrdinalIgnoreCase）、
    /// 非重叠匹配（命中后从关键词长度处继续向后扫描）。
    /// 与 <see cref="Services.Classification.KeywordMessageClassifier"/> 的计数口径一致。
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
}
