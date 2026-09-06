using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassIng.Plugin.Services.Classification;
using ClassIng.Plugin.Services.Stores;
using ClassIng.Plugin.Services.SubjectChain;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using ClassIsland.Core.Attributes;

namespace ClassIng.Plugin.Controls.SettingsPages;

/// <summary>
/// 分类词表可视化编辑页（需求 3）：
/// ① 学科关键词规则（subjects.json）增删改、每学科关键词列表编辑与优先级调整；
/// ② 消息分类关键词（classification-keywords.json）通知/作业关键词编辑；
/// 均支持导出 JSON / 导入 JSON（剪贴板通道，与维护页一致）/ 恢复默认（Assets 模板为默认源）。
/// 保存走原子写路径（<see cref="SubjectRuleFile.WriteAtomic"/> / <see cref="KeywordRulesFile.Seed"/>），
/// 随后经 SettingsService.SaveAsync → SettingsChanged → 各分类器 ReloadRules 热生效。
/// 防呆校验（空学科名/非法字符/重复学科/空关键词）由 <see cref="SubjectRulesEditorLogic"/> 提供。
/// </summary>
[SettingsPageInfo("classing.settings.subject-rules", "CyberTechRep 词表编辑")]
[Group("classing.settings")]
public partial class SubjectRulesEditorPage : ClassIngSettingsPageBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>学科词表数据文件名（相对插件数据目录；与 SubjectChainOptionsProvider 默认一致）。</summary>
    private const string SubjectsFileName = "subjects.json";

    /// <summary>分类关键词数据文件名（相对插件数据目录；与 ClassifierOptionsProvider 默认一致）。</summary>
    private const string KeywordsFileName = "classification-keywords.json";

    private readonly string _subjectsDataPath;
    private readonly string _keywordsDataPath;
    private readonly string _subjectsAssetPath;
    private readonly string _keywordsAssetPath;
    private readonly MemberSubjectBindingStore? _memberBindings;

    private string _validationMessage = "";
    private string _transferFeedback = "";

    public SubjectRulesEditorPage(ISettingsService settingsService,
        MemberSubjectBindingStore? memberBindings = null)
        : base(settingsService, PluginRuntime.DataDirectory)
    {
        _subjectsDataPath = Path.Combine(DataDirectory, SubjectsFileName);
        _keywordsDataPath = Path.Combine(DataDirectory, KeywordsFileName);
        _subjectsAssetPath = Path.Combine(AppContext.BaseDirectory, "Assets", SubjectsFileName);
        _keywordsAssetPath = Path.Combine(AppContext.BaseDirectory, "Assets", KeywordsFileName);
        _memberBindings = memberBindings ?? new MemberSubjectBindingStore(DataDirectory);

        Rules = new ObservableCollection<SubjectRuleRow>(
            LoadCurrentRules().Select(SubjectRuleRow.FromRule));
        NoticeKeywordsText = LinesToText(LoadCurrentKeywords().NoticeKeywords);
        HomeworkKeywordsText = LinesToText(LoadCurrentKeywords().HomeworkKeywords);
        Bindings = new ObservableCollection<MemberBindingRow>(
            _memberBindings.GetAll().Select(b => MemberBindingRow.FromBinding(b, LoadSubjectOptions())));
        InitializeComponent();
        // 组合框初始选中项按当前设置回填（需在 InitializeComponent 之后）
        ModeBox.SelectedIndex = Settings.SubjectRecognition.Mode == SubjectRecognitionMode.Keyword ? 1 : 0;
    }

    /// <summary>学科规则编辑行集合（UI 绑定源）。</summary>
    public ObservableCollection<SubjectRuleRow> Rules { get; }

    /// <summary>成员学科绑定编辑行集合（UI 绑定源，需求 2 管理入口）。</summary>
    public ObservableCollection<MemberBindingRow> Bindings { get; }

    /// <summary>通知关键词 ↔ 多行文本（classification-keywords.json 编辑区）。</summary>
    public string NoticeKeywordsText { get; set; } = "";

    /// <summary>作业关键词 ↔ 多行文本（classification-keywords.json 编辑区）。</summary>
    public string HomeworkKeywordsText { get; set; } = "";

    /// <summary>校验错误提示（保存前防呆；空 = 无错误）。</summary>
    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            _validationMessage = value;
            RaisePropertyChanged(nameof(ValidationMessage));
        }
    }

    /// <summary>导入/导出/恢复默认操作反馈。</summary>
    public string TransferFeedback
    {
        get => _transferFeedback;
        private set
        {
            _transferFeedback = value;
            RaisePropertyChanged(nameof(TransferFeedback));
        }
    }

    // ============ 保存（校验 → 原子写文件 → 设置保存广播 → 分类器热重载） ============

    protected override async Task SaveCoreAsync(Button? saveButton)
    {
        var rules = SubjectRulesEditorLogic.Normalize(Rules.Select(r => r.ToRule()));
        var errors = new List<string>(SubjectRulesEditorLogic.Validate(rules));
        errors.AddRange(ValidateBindings());
        if (errors.Count > 0)
        {
            ValidationMessage = string.Join("\n", errors);
            SaveButtonFeedback.ShowFailure(saveButton, errors[0]);
            return;
        }

        try
        {
            // 学科词表：原子写 subjects.json（先落盘，随后 SettingsChanged 触发分类器重载即读到新表）
            var dto = new SubjectRulesDto { Rules = [.. rules] };
            SubjectRuleFile.WriteAtomic(_subjectsDataPath, JsonSerializer.Serialize(dto, JsonOptions));

            // 成员学科绑定（需求 2）：与存储做差量同步（删掉的行 Remove、新增/修改的行 Set），即时持久化
            SaveBindings();

            // 消息分类关键词：原子写 classification-keywords.json，并与设置值保持同步
            var noticeKeywords = TextToLines(NoticeKeywordsText);
            var homeworkKeywords = TextToLines(HomeworkKeywordsText);
            KeywordRulesFile.Seed(_keywordsDataPath, new KeywordRulesSnapshot(noticeKeywords, homeworkKeywords, "editor"));
            Settings.Classification.NoticeKeywords = noticeKeywords;
            Settings.Classification.HomeworkKeywords = homeworkKeywords;

            ValidationMessage = "";
            // settings.json 保存并广播 SettingsChanged → SettingsChangeApplier →
            // KeywordMessageClassifier.ReloadRules + KeywordSubjectClassifier.ReloadRules（热生效）
            // 学科识别模式（SubjectRecognition.Mode/SelectionWindowEnabled）随本次保存一并持久化并热生效
            await base.SaveCoreAsync(saveButton).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SaveButtonFeedback.ShowFailure(saveButton, ex.Message);
            ValidationMessage = $"保存失败：{ex.Message}";
        }
    }

    // ============ 学科识别模式与成员绑定管理（需求 2/4） ============

    private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ModeBox.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<SubjectRecognitionMode>(tag, out var mode))
        {
            Settings.SubjectRecognition.Mode = mode;
        }
    }

    /// <summary>学科下拉选项（subjects.json 当前学科名列表；与归档目录一致）。</summary>
    private IReadOnlyList<string> LoadSubjectOptions() =>
        LoadCurrentRules().Select(r => r.Subject).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

    private void OnAddBindingClicked(object? sender, RoutedEventArgs e)
    {
        var row = new MemberBindingRow { SubjectOptions = LoadSubjectOptions() };
        Bindings.Add(row);
        BindingsList.ScrollIntoView(row);
    }

    private void OnDeleteBindingClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MemberBindingRow row })
        {
            Bindings.Remove(row);
        }
    }

    /// <summary>绑定行防呆校验：成员 OpenID 必填、学科必选、同一（群, 成员）不可重复。空行（全空）忽略。</summary>
    private List<string> ValidateBindings()
    {
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Bindings)
        {
            if (string.IsNullOrWhiteSpace(row.MemberOpenId)
                && string.IsNullOrWhiteSpace(row.GroupOpenId)
                && string.IsNullOrWhiteSpace(row.Subject))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.MemberOpenId))
            {
                errors.Add("成员学科绑定：成员 OpenID 不能为空。");
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.Subject))
            {
                errors.Add($"成员学科绑定：{row.MemberOpenId} 未选择学科。");
                continue;
            }

            if (!seen.Add($"{row.GroupOpenId?.Trim() ?? ""}\n{row.MemberOpenId.Trim()}"))
            {
                errors.Add($"成员学科绑定：成员 {row.MemberOpenId}（群 {row.GroupOpenId}）重复条目。");
            }
        }

        return errors;
    }

    /// <summary>绑定编辑行 → 存储差量同步（先删后写，即时持久化；空行视为删除）。</summary>
    private void SaveBindings()
    {
        if (_memberBindings is null)
        {
            return;
        }

        var valid = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Bindings)
        {
            if (string.IsNullOrWhiteSpace(row.MemberOpenId) || string.IsNullOrWhiteSpace(row.Subject))
            {
                continue;
            }

            var group = row.GroupOpenId?.Trim() ?? "";
            var key = $"{group}\n{row.MemberOpenId.Trim()}";
            valid.Add(key);
            _memberBindings.Set(row.MemberOpenId.Trim(), row.Subject.Trim(), group.Length == 0 ? null : group);
        }

        foreach (var existing in _memberBindings.GetAll())
        {
            var key = $"{existing.GroupOpenId}\n{existing.MemberOpenId}";
            if (!valid.Contains(key))
            {
                _memberBindings.Remove(existing.MemberOpenId,
                    existing.GroupOpenId.Length == 0 ? null : existing.GroupOpenId);
            }
        }
    }

    // ============ 学科规则编辑 ============

    private void OnAddSubjectClicked(object? sender, RoutedEventArgs e)
    {
        Rules.Add(new SubjectRuleRow { Subject = "", KeywordsText = "", Priority = 10 });
        RulesList.ScrollIntoView(Rules[^1]);
    }

    private void OnDeleteSubjectClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SubjectRuleRow row })
        {
            Rules.Remove(row);
        }
    }

    private async void OnExportSubjectsClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dto = new SubjectRulesDto { Rules = [.. Rules.Select(r => r.ToRule())] };
            await CopyToClipboardAsync(JsonSerializer.Serialize(dto, JsonOptions)).ConfigureAwait(true);
            TransferFeedback = "已导出学科规则 JSON 到剪贴板。";
        }
        catch (Exception ex)
        {
            TransferFeedback = $"导出失败：{ex.Message}";
        }
    }

    private async void OnImportSubjectsClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var json = await ReadClipboardTextAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(json))
            {
                TransferFeedback = "剪贴板没有可导入的学科规则 JSON。";
                return;
            }

            var imported = JsonSerializer.Deserialize<SubjectRulesDto>(json, JsonOptions)?.Rules;
            if (imported is null || imported.Count == 0)
            {
                TransferFeedback = "导入内容没有可用的学科规则（需要 { \"rules\": [ ... ] } 结构）。";
                return;
            }

            ReplaceRules(SubjectRulesEditorLogic.Normalize(imported));
            // 导入内容可能自带防呆问题（空学科名等）：立即提示，保存时仍会拦截
            var errors = SubjectRulesEditorLogic.Validate(Rules.Select(r => r.ToRule()));
            ValidationMessage = errors.Count > 0 ? string.Join("\n", errors) : "";
            TransferFeedback = $"已导入 {Rules.Count} 条学科规则，请检查后点击「保存并应用」。";
        }
        catch (JsonException ex)
        {
            TransferFeedback = $"导入被拒绝：不是合法的学科规则 JSON（{ex.Message}）";
        }
        catch (Exception ex)
        {
            TransferFeedback = $"导入失败：{ex.Message}";
        }
    }

    private void OnRestoreSubjectsClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(_subjectsAssetPath))
            {
                TransferFeedback = $"Assets 模板缺失（{_subjectsAssetPath}），已改用内置默认规则。";
                ReplaceRules(SubjectRuleFile.BuiltInDefaults);
                return;
            }

            var defaults = ParseRules(File.ReadAllText(_subjectsAssetPath));
            ReplaceRules(defaults);
            TransferFeedback = "已载入 Assets 默认学科模板，点击「保存并应用」写入并热生效。";
        }
        catch (Exception ex)
        {
            TransferFeedback = $"恢复默认失败：{ex.Message}";
        }
    }

    // ============ 消息分类关键词编辑 ============

    private async void OnExportKeywordsClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dto = new KeywordRulesDto
            {
                NoticeKeywords = [.. TextToLines(NoticeKeywordsText)],
                HomeworkKeywords = [.. TextToLines(HomeworkKeywordsText)]
            };
            await CopyToClipboardAsync(JsonSerializer.Serialize(dto, JsonOptions)).ConfigureAwait(true);
            TransferFeedback = "已导出分类关键词 JSON 到剪贴板。";
        }
        catch (Exception ex)
        {
            TransferFeedback = $"导出失败：{ex.Message}";
        }
    }

    private async void OnImportKeywordsClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var json = await ReadClipboardTextAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(json))
            {
                TransferFeedback = "剪贴板没有可导入的分类关键词 JSON。";
                return;
            }

            var dto = JsonSerializer.Deserialize<KeywordRulesDto>(json, JsonOptions);
            if (dto is null)
            {
                TransferFeedback = "导入内容为空（需要 noticeKeywords / homeworkKeywords 结构）。";
                return;
            }

            NoticeKeywordsText = LinesToText([.. dto.NoticeKeywords]);
            HomeworkKeywordsText = LinesToText([.. dto.HomeworkKeywords]);
            RaisePropertyChanged(nameof(NoticeKeywordsText));
            RaisePropertyChanged(nameof(HomeworkKeywordsText));
            TransferFeedback = "已导入分类关键词，点击「保存并应用」写入并热生效。";
        }
        catch (JsonException ex)
        {
            TransferFeedback = $"导入被拒绝：不是合法的分类关键词 JSON（{ex.Message}）";
        }
        catch (Exception ex)
        {
            TransferFeedback = $"导入失败：{ex.Message}";
        }
    }

    private void OnRestoreKeywordsClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var snapshot = File.Exists(_keywordsAssetPath)
                ? ParseKeywords(File.ReadAllText(_keywordsAssetPath))
                : KeywordRulesFile.FromSettings(new ClassificationSettings());
            NoticeKeywordsText = LinesToText([.. snapshot.NoticeKeywords]);
            HomeworkKeywordsText = LinesToText([.. snapshot.HomeworkKeywords]);
            RaisePropertyChanged(nameof(NoticeKeywordsText));
            RaisePropertyChanged(nameof(HomeworkKeywordsText));
            TransferFeedback = "已载入 Assets 默认关键词模板，点击「保存并应用」写入并热生效。";
        }
        catch (Exception ex)
        {
            TransferFeedback = $"恢复默认失败：{ex.Message}";
        }
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e) => SaveNow(sender);

    // ============ 数据读写辅助 ============

    private void ReplaceRules(IEnumerable<SubjectRule> rules)
    {
        Rules.Clear();
        foreach (var row in rules.Select(SubjectRuleRow.FromRule))
        {
            Rules.Add(row);
        }
    }

    private IReadOnlyList<SubjectRule> LoadCurrentRules()
    {
        try
        {
            if (File.Exists(_subjectsDataPath))
            {
                return ParseRules(File.ReadAllText(_subjectsDataPath));
            }

            if (File.Exists(_subjectsAssetPath))
            {
                return ParseRules(File.ReadAllText(_subjectsAssetPath));
            }
        }
        catch
        {
            // 读取失败回退内置默认（与 SubjectRuleFile 降级口径一致）
        }

        return SubjectRuleFile.BuiltInDefaults;
    }

    private KeywordRulesSnapshot LoadCurrentKeywords()
    {
        try
        {
            if (File.Exists(_keywordsDataPath))
            {
                return ParseKeywords(File.ReadAllText(_keywordsDataPath));
            }
        }
        catch
        {
            // 读取失败回退设置值
        }

        return KeywordRulesFile.FromSettings(Settings.Classification);
    }

    private static IReadOnlyList<SubjectRule> ParseRules(string json) =>
        JsonSerializer.Deserialize<SubjectRulesDto>(json, JsonOptions)?.Rules ?? [];

    private static KeywordRulesSnapshot ParseKeywords(string json)
    {
        var dto = JsonSerializer.Deserialize<KeywordRulesDto>(json, JsonOptions)
                  ?? throw new InvalidDataException("关键词 JSON 反序列化为 null");
        return new KeywordRulesSnapshot(
            [.. dto.NoticeKeywords], [.. dto.HomeworkKeywords], "imported");
    }

    private async Task CopyToClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            throw new InvalidOperationException("剪贴板不可用。");
        }

        await clipboard.SetTextAsync(text).ConfigureAwait(true);
    }

    private async Task<string?> ReadClipboardTextAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        return clipboard is null ? null : await clipboard.GetTextAsync().ConfigureAwait(true);
    }

    /// <summary>学科规则编辑行（每学科一行：名称 / 关键词多行文本 / 优先级）。</summary>
    public sealed class SubjectRuleRow : INotifyPropertyChanged
    {
        private string _subject = "";
        private string _keywordsText = "";
        private int _priority;

        public string Subject
        {
            get => _subject;
            set
            {
                _subject = value;
                RaisePropertyChanged(nameof(Subject));
            }
        }

        /// <summary>关键词编辑文本（一行一个；保存时剔除空白行并去重）。</summary>
        public string KeywordsText
        {
            get => _keywordsText;
            set
            {
                _keywordsText = value;
                RaisePropertyChanged(nameof(KeywordsText));
            }
        }

        public int Priority
        {
            get => _priority;
            set
            {
                _priority = value;
                RaisePropertyChanged(nameof(Priority));
            }
        }

        public static SubjectRuleRow FromRule(SubjectRule rule) => new()
        {
            Subject = rule.Subject,
            KeywordsText = string.Join(Environment.NewLine, rule.Keywords ?? []),
            Priority = rule.Priority
        };

        /// <summary>转为规则对象（关键词按行拆分；空白行在规范化阶段剔除）。</summary>
        public SubjectRule ToRule() => new()
        {
            Subject = Subject,
            Keywords = (KeywordsText ?? "")
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.Length > 0)
                .ToArray(),
            Priority = Priority
        };

        private void RaisePropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// 成员学科绑定编辑行（需求 2 管理入口）：成员 OpenID / 群 OpenID（留空=全局）/ 学科下拉。
    /// </summary>
    public sealed class MemberBindingRow : INotifyPropertyChanged
    {
        private string _memberOpenId = "";
        private string _groupOpenId = "";
        private string _subject = "";

        public IReadOnlyList<string> SubjectOptions { get; init; } = [];

        public string MemberOpenId
        {
            get => _memberOpenId;
            set
            {
                _memberOpenId = value;
                RaisePropertyChanged(nameof(MemberOpenId));
            }
        }

        public string GroupOpenId
        {
            get => _groupOpenId;
            set
            {
                _groupOpenId = value;
                RaisePropertyChanged(nameof(GroupOpenId));
            }
        }

        public string Subject
        {
            get => _subject;
            set
            {
                _subject = value;
                RaisePropertyChanged(nameof(Subject));
            }
        }

        public static MemberBindingRow FromBinding(MemberSubjectBinding binding, IReadOnlyList<string> subjectOptions) =>
            new()
            {
                MemberOpenId = binding.MemberOpenId,
                GroupOpenId = binding.GroupOpenId,
                Subject = binding.Subject,
                SubjectOptions = subjectOptions
            };

        private void RaisePropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
