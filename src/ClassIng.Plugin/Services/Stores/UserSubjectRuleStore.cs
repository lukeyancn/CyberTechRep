using System.Text.Json.Serialization;
using ClassIng.Plugin.Services.SubjectChain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Services.Stores;

/// <summary>user-subjects.json 顶层 DTO。</summary>
internal sealed class UserSubjectRuleFileDto
{
    [JsonPropertyName("rules")]
    public List<UserSubjectRuleDto> Rules { get; set; } = [];
}

/// <summary>单条「按发送者记住学科」规则。</summary>
internal sealed class UserSubjectRuleDto
{
    [JsonPropertyName("memberOpenId")]
    public string MemberOpenId { get; set; } = "";

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = "";

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 按发送者记住学科（user-subjects.json，JSON 原子写入 + .bak 损坏恢复，进程内锁串行化）：
/// 某成员发送的「未分类」作业被人工指定学科后，记录 成员OpenID → 学科 的永久规则；
/// 该成员之后的所有作业（重启后同样生效）自动套用该学科，再次人工修正时覆盖。
/// </summary>
public sealed class UserSubjectRuleStore
{
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private Dictionary<string, string>? _rules;

    public UserSubjectRuleStore(string dataDirectory, ILogger? logger = null)
    {
        _filePath = Path.Combine(dataDirectory, "user-subjects.json");
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>取成员的永久学科规则；无规则返回 null。</summary>
    public string? Get(string memberOpenId)
    {
        if (string.IsNullOrWhiteSpace(memberOpenId))
        {
            return null;
        }

        lock (_lock)
        {
            LoadIfNeeded();
            return _rules!.TryGetValue(memberOpenId, out var subject) && subject.Length > 0 ? subject : null;
        }
    }

    /// <summary>写入/覆盖成员的永久学科规则并立即持久化。</summary>
    public void Set(string memberOpenId, string subject)
    {
        if (string.IsNullOrWhiteSpace(memberOpenId) || string.IsNullOrWhiteSpace(subject))
        {
            return;
        }

        lock (_lock)
        {
            LoadIfNeeded();
            _rules![memberOpenId] = subject;
            Save();
        }

        _logger.LogInformation(
            "已记录按发送者的学科规则（永久生效）Member={Member}, Subject={Subject}", memberOpenId, subject);
    }

    private void LoadIfNeeded()
    {
        if (_rules is not null)
        {
            return;
        }

        var dto = JsonStoreFile.LoadOrRestore<UserSubjectRuleFileDto>(_filePath, _logger);
        _rules = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in dto?.Rules ?? [])
        {
            if (!string.IsNullOrWhiteSpace(rule.MemberOpenId) && !string.IsNullOrWhiteSpace(rule.Subject))
            {
                _rules[rule.MemberOpenId] = rule.Subject;
            }
        }
    }

    private void Save()
    {
        try
        {
            var dto = new UserSubjectRuleFileDto
            {
                Rules = [.. _rules!.Select(kv => new UserSubjectRuleDto
                {
                    MemberOpenId = kv.Key,
                    Subject = kv.Value,
                    UpdatedAt = DateTimeOffset.Now
                })]
            };
            JsonStoreFile.SaveWithBackup(_filePath, JsonStoreFile.Serialize(dto));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "按发送者学科规则持久化失败（内存中状态保留）");
        }
    }
}
