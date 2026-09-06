using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Services.Stores;

/// <summary>member-subject-bindings.json 顶层 DTO。</summary>
internal sealed class MemberSubjectBindingFileDto
{
    [JsonPropertyName("bindings")]
    public List<MemberSubjectBindingDto> Bindings { get; set; } = [];
}

/// <summary>单条「成员 OpenID → 学科」显式绑定。</summary>
internal sealed class MemberSubjectBindingDto
{
    [JsonPropertyName("memberOpenId")]
    public string MemberOpenId { get; set; } = "";

    /// <summary>群 OpenID 作用域；空 = 全局（跨群生效）。</summary>
    [JsonPropertyName("groupOpenId")]
    public string GroupOpenId { get; set; } = "";

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = "";

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>成员学科绑定条目（查询/管理 UI 用公开视图）。</summary>
public sealed record MemberSubjectBinding(
    string MemberOpenId,
    string GroupOpenId,
    string Subject,
    DateTimeOffset UpdatedAt);

/// <summary>
/// 成员学科显式绑定存储（member-subject-bindings.json，JSON 原子写入 + .bak 损坏恢复，进程内锁串行化）。
/// <para>
/// 与 <see cref="UserSubjectRuleStore"/>（user-subjects.json，人工修正后「学习」出来的映射）不同：
/// 本存储保存的是<b>显式绑定</b>——由设置页管理界面或「未绑定学科选择悬浮窗」直接写入，
/// 支持<strong>增删查</strong>与群作用域（群内绑定优先于全局绑定）。
/// 查询接口 <see cref="TryGetSubject"/>：群内绑定 → 全局绑定 → 无（null）。
/// </para>
/// <para>
/// 本存储只在「学科识别模式 = MemberSelection」时被消息管道消费（Keyword 模式完全不读取，
/// 保证纯关键词行为与现状完全一致）；增删改立即持久化，进程内缓存即时生效。
/// </para>
/// </summary>
public sealed class MemberSubjectBindingStore
{
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private Dictionary<string, string>? _global;
    private Dictionary<string, Dictionary<string, string>>? _byGroup;

    public MemberSubjectBindingStore(string dataDirectory, ILogger? logger = null)
    {
        _filePath = Path.Combine(dataDirectory, "member-subject-bindings.json");
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// 查询成员绑定学科：先查群作用域（<paramref name="groupOpenId"/> 非空时），
    /// 未命中再查全局作用域；均未绑定返回 false。
    /// </summary>
    public bool TryGetSubject(string memberOpenId, string? groupOpenId, out string subject)
    {
        subject = "";
        if (string.IsNullOrWhiteSpace(memberOpenId))
        {
            return false;
        }

        lock (_lock)
        {
            LoadIfNeeded();
            if (!string.IsNullOrWhiteSpace(groupOpenId)
                && _byGroup!.TryGetValue(groupOpenId, out var groupMap)
                && groupMap.TryGetValue(memberOpenId, out var groupSubject)
                && groupSubject.Length > 0)
            {
                subject = groupSubject;
                return true;
            }

            if (_global!.TryGetValue(memberOpenId, out var globalSubject) && globalSubject.Length > 0)
            {
                subject = globalSubject;
                return true;
            }

            return false;
        }
    }

    /// <summary>查询成员绑定学科（<see cref="TryGetSubject"/> 的可空返回形式）。</summary>
    public string? GetSubject(string memberOpenId, string? groupOpenId = null) =>
        TryGetSubject(memberOpenId, groupOpenId, out var subject) ? subject : null;

    /// <summary>
    /// 写入/覆盖成员的显式学科绑定并立即持久化。
    /// <paramref name="groupOpenId"/> 为空 = 全局绑定（跨群生效）；非空 = 仅该群生效。
    /// </summary>
    public void Set(string memberOpenId, string subject, string? groupOpenId = null)
    {
        if (string.IsNullOrWhiteSpace(memberOpenId) || string.IsNullOrWhiteSpace(subject))
        {
            return;
        }

        var group = groupOpenId?.Trim() ?? "";
        lock (_lock)
        {
            LoadIfNeeded();
            if (group.Length == 0)
            {
                _global![memberOpenId] = subject;
            }
            else
            {
                if (!_byGroup!.TryGetValue(group, out var map))
                {
                    map = new Dictionary<string, string>(StringComparer.Ordinal);
                    _byGroup[group] = map;
                }

                map[memberOpenId] = subject;
            }

            _updatedAt[Key(memberOpenId, group)] = DateTimeOffset.Now;
            Save();
        }

        _logger.LogInformation(
            "已写入成员学科绑定 Member={Member}, Group={Group}, Subject={Subject}",
            memberOpenId, group.Length == 0 ? "(全局)" : group, subject);
    }

    /// <summary>
    /// 删除成员绑定（<paramref name="groupOpenId"/> 为空删全局绑定；传 " * " 语义不存在，
    /// 需删全部作用域请用 <see cref="RemoveAll"/>）。返回是否实际删除。
    /// </summary>
    public bool Remove(string memberOpenId, string? groupOpenId = null)
    {
        if (string.IsNullOrWhiteSpace(memberOpenId))
        {
            return false;
        }

        var group = groupOpenId?.Trim() ?? "";
        bool removed;
        lock (_lock)
        {
            LoadIfNeeded();
            removed = group.Length == 0
                ? _global!.Remove(memberOpenId)
                : _byGroup!.TryGetValue(group, out var map) && map.Remove(memberOpenId);
            if (removed)
            {
                Save();
            }
        }

        if (removed)
        {
            _logger.LogInformation(
                "已删除成员学科绑定 Member={Member}, Group={Group}", memberOpenId, group.Length == 0 ? "(全局)" : group);
        }

        return removed;
    }

    /// <summary>删除成员在全部作用域（全局 + 所有群）的绑定，返回是否实际删除。</summary>
    public bool RemoveAll(string memberOpenId)
    {
        if (string.IsNullOrWhiteSpace(memberOpenId))
        {
            return false;
        }

        bool removed;
        lock (_lock)
        {
            LoadIfNeeded();
            removed = _global!.Remove(memberOpenId);
            var groupKeys = _byGroup!.Keys.ToList();
            foreach (var group in groupKeys)
            {
                removed |= _byGroup[group].Remove(memberOpenId);
            }

            if (removed)
            {
                Save();
            }
        }

        if (removed)
        {
            _logger.LogInformation("已删除成员全部学科绑定 Member={Member}", memberOpenId);
        }

        return removed;
    }

    /// <summary>全部绑定（全局 + 群作用域，按更新时间倒序；管理 UI 数据源）。</summary>
    public IReadOnlyList<MemberSubjectBinding> GetAll()
    {
        lock (_lock)
        {
            LoadIfNeeded();
            var list = new List<MemberSubjectBinding>();
            foreach (var (member, subject) in _global!)
            {
                list.Add(new MemberSubjectBinding(member, "", subject, LookupUpdatedAt(member, "")));
            }

            foreach (var (group, map) in _byGroup!)
            {
                foreach (var (member, subject) in map)
                {
                    list.Add(new MemberSubjectBinding(member, group, subject, LookupUpdatedAt(member, group)));
                }
            }

            return list.OrderByDescending(b => b.UpdatedAt).ToList();
        }
    }

    private DateTimeOffset LookupUpdatedAt(string memberOpenId, string groupOpenId)
    {
        // UpdatedAt 仅做展示排序：命中不到（理论上不会）回退 Epoch
        return _updatedAt.TryGetValue(Key(memberOpenId, groupOpenId), out var at) ? at : DateTimeOffset.MinValue;
    }

    private readonly Dictionary<string, DateTimeOffset> _updatedAt = new(StringComparer.Ordinal);

    private static string Key(string memberOpenId, string groupOpenId) => $"{groupOpenId}\n{memberOpenId}";

    private void LoadIfNeeded()
    {
        if (_global is not null && _byGroup is not null)
        {
            return;
        }

        _global = new Dictionary<string, string>(StringComparer.Ordinal);
        _byGroup = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        _updatedAt.Clear();

        var dto = JsonStoreFile.LoadOrRestore<MemberSubjectBindingFileDto>(_filePath, _logger);
        foreach (var binding in dto?.Bindings ?? [])
        {
            if (string.IsNullOrWhiteSpace(binding.MemberOpenId) || string.IsNullOrWhiteSpace(binding.Subject))
            {
                continue;
            }

            var group = binding.GroupOpenId?.Trim() ?? "";
            if (group.Length == 0)
            {
                _global[binding.MemberOpenId] = binding.Subject;
            }
            else
            {
                if (!_byGroup.TryGetValue(group, out var map))
                {
                    map = new Dictionary<string, string>(StringComparer.Ordinal);
                    _byGroup[group] = map;
                }

                map[binding.MemberOpenId] = binding.Subject;
            }

            _updatedAt[Key(binding.MemberOpenId, group)] = binding.UpdatedAt;
        }
    }

    private void Save()
    {
        try
        {
            var dto = new MemberSubjectBindingFileDto();
            foreach (var (member, subject) in _global!)
            {
                dto.Bindings.Add(new MemberSubjectBindingDto
                {
                    MemberOpenId = member,
                    GroupOpenId = "",
                    Subject = subject,
                    UpdatedAt = _updatedAt.TryGetValue(Key(member, ""), out var at) ? at : DateTimeOffset.Now
                });
            }

            foreach (var (group, map) in _byGroup!)
            {
                foreach (var (member, subject) in map)
                {
                    dto.Bindings.Add(new MemberSubjectBindingDto
                    {
                        MemberOpenId = member,
                        GroupOpenId = group,
                        Subject = subject,
                        UpdatedAt = _updatedAt.TryGetValue(Key(member, group), out var at) ? at : DateTimeOffset.Now
                    });
                }
            }

            JsonStoreFile.SaveWithBackup(_filePath, JsonStoreFile.Serialize(dto));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "成员学科绑定持久化失败（内存中状态保留）");
        }
    }
}
