using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CyberTechRep.Plugin.Services.SubjectChain;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using ClassIsland.Core.Attributes;

namespace CyberTechRep.Plugin.Controls.SettingsPages;

/// <summary>
/// 常态化作业设置行（学科 / 内容 / 启用）：包装存档里的 <see cref="StandingHomeworkItem"/> 实例，
/// 编辑直接写回该实例（存档为单一事实源），页面负责落盘。
/// </summary>
public sealed class StandingHomeworkRow : INotifyPropertyChanged
{
    public required StandingHomeworkItem Item { get; init; }

    /// <summary>可选学科（学科词表 + 固定七学科 + 未分类，去重；当前值恒在列表内）。</summary>
    public required IReadOnlyList<string> SubjectCandidates { get; init; }

    public string Subject
    {
        get => Item.Subject;
        set
        {
            if (string.Equals(Item.Subject, value, StringComparison.Ordinal))
            {
                return;
            }

            Item.Subject = value ?? "";
            Raise(nameof(Subject));
        }
    }

    public string Content
    {
        get => Item.Content;
        set
        {
            if (string.Equals(Item.Content, value, StringComparison.Ordinal))
            {
                return;
            }

            Item.Content = value ?? "";
            Raise(nameof(Content));
        }
    }

    public bool Enabled
    {
        get => Item.Enabled;
        set
        {
            if (Item.Enabled == value)
            {
                return;
            }

            Item.Enabled = value;
            Raise(nameof(Enabled));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 常态化作业设置页（需求 3）：按学科维护固定作业项，支持增 / 删 / 改（学科、内容、启用状态）。
/// <para>
/// <b>持久化</b>：编辑直接写回 <see cref="ISettingsService.Current"/> 的条目实例，结构性变更
/// （增/删）与编辑停顿 500ms 后统一同步列表引用并落盘 settings.json（沿用既有设置持久化机制，
/// 不引入新的存档文件，重启不丢）。设置被导入/恢复默认（Current 整体替换）后自动重建列表。
/// </para>
/// </summary>
[SettingsPageInfo("cybertechrep.settings.standing-homework", "CyberTechRep 常态化作业")]
[Group("cybertechrep.settings")]
[SupportedOSPlatform("windows")]
public partial class StandingHomeworkSettingsPage : CyberTechRepSettingsPageBase
{
    /// <summary>编辑落盘防抖（连续输入合并为一次保存）。</summary>
    internal static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(500);

    private readonly SubjectChainOptionsProvider? _subjectOptions;
    private readonly DispatcherTimer _saveTimer;
    private IReadOnlyList<StandingHomeworkItem> _boundList = [];
    private string _feedback = "";

    public StandingHomeworkSettingsPage(
        ISettingsService settingsService,
        SubjectChainOptionsProvider? subjectOptions = null)
        : base(settingsService, PluginRuntime.DataDirectory)
    {
        _subjectOptions = subjectOptions;
        InitializeComponent();
        _saveTimer = new DispatcherTimer { Interval = SaveDebounce };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };
        SettingsService.SettingsChanged += (_, _) => RebuildFromSettingsIfReplaced();
        RebuildFromSettingsIfReplaced();
    }

    /// <summary>常态化作业行集合（绑定源）。</summary>
    public ObservableCollection<StandingHomeworkRow> Items { get; } = [];

    /// <summary>操作反馈文本。</summary>
    public string Feedback
    {
        get => _feedback;
        private set
        {
            _feedback = value;
            RaisePropertyChanged(nameof(Feedback));
        }
    }

    /// <summary>学科候选（学科词表 → 固定七学科 → 未分类，去重保序）。</summary>
    private IReadOnlyList<string> BuildSubjectCandidates()
    {
        var candidates = new List<string>();
        try
        {
            if (_subjectOptions is not null)
            {
                candidates.AddRange(SubjectRuleFile.LoadOrSeed(_subjectOptions).Rules
                    .Select(r => r.Subject?.Trim() ?? "")
                    .Where(s => s.Length > 0));
            }
        }
        catch
        {
            // 词表读取失败回落固定七学科（不阻断设置页）
        }

        foreach (var subject in CyberTechRep.Plugin.Views.HomeworkSuspensionWindow.BaseSubjects)
        {
            candidates.Add(subject);
        }

        candidates.Add("未分类");
        return candidates
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>设置中的条目列表被整体替换（导入/恢复默认）时重建行集合；同一次 Current 不动。</summary>
    private void RebuildFromSettingsIfReplaced()
    {
        var list = Settings.StandingHomework.Items;
        if (ReferenceEquals(list, _boundList))
        {
            return;
        }

        _boundList = list;
        var candidates = BuildSubjectCandidates();
        Items.Clear();
        foreach (var item in list)
        {
            AddRow(item, candidates);
        }

        EmptyHint.IsVisible = Items.Count == 0;
    }

    private void AddRow(StandingHomeworkItem item, IReadOnlyList<string> candidates)
    {
        var subject = item.Subject?.Trim() ?? "";
        var rowCandidates = subject.Length > 0 && !candidates.Contains(subject, StringComparer.Ordinal)
            ? candidates.Prepend(subject).ToList()
            : candidates;
        var row = new StandingHomeworkRow { Item = item, SubjectCandidates = rowCandidates };
        row.PropertyChanged += (_, _) =>
        {
            // 编辑停顿后落盘（不逐字符写盘）
            _saveTimer.Stop();
            _saveTimer.Start();
        };
        Items.Add(row);
    }

    /// <summary>把行集合写回设置列表引用（结构性变更后必须同步，否则新增/删除不落盘）。</summary>
    private void SyncListToSettings()
    {
        var list = Items.Select(r => r.Item).ToList();
        Settings.StandingHomework.Items = list;
        _boundList = list;
    }

    /// <summary>保存前同步列表引用（页面关闭自动保存、显式保存、防抖保存三条路径统一走这里）。</summary>
    protected override async Task SaveCoreAsync(Button? saveButton)
    {
        SyncListToSettings();
        await base.SaveCoreAsync(saveButton).ConfigureAwait(true);
    }

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        var candidates = BuildSubjectCandidates();
        var item = new StandingHomeworkItem
        {
            Subject = candidates.FirstOrDefault(s => s != "未分类") ?? "其他",
            Content = "",
            Enabled = true
        };
        AddRow(item, candidates);
        SyncListToSettings();
        EmptyHint.IsVisible = false;
        Feedback = "已添加一条，填写内容后点「保存并应用」（页面关闭也会自动保存）。";
        SaveNow();
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: StandingHomeworkRow row })
        {
            return;
        }

        Items.Remove(row);
        SyncListToSettings();
        EmptyHint.IsVisible = Items.Count == 0;
        Feedback = "已删除该条常态化作业。";
        SaveNow();
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        Feedback = "正在保存…";
        SaveNow(sender);
    }
}
