using CyberTechRep.Plugin.Services.Stores;
using CyberTechRep.Plugin.Services.SubjectChain;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 学科识别「完全重置」：
/// ① 重置后绑定存储为空、学习映射为空、识别模式回默认（MemberSelection + 选择窗开）；
/// ② 不动同目录其他数据（作业/归档库等文件原样保留）；
/// ③ 写穿持久化（新实例读到空）；④ 两段式确认门：超时/未确认不执行，窗口内才放行。
/// </summary>
public sealed class SubjectRecognitionResetTests : IDisposable
{
    private readonly string _dir;

    public SubjectRecognitionResetTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "subject-reset", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private MemberSubjectBindingStore NewBindingStore() => new(_dir);

    private UserSubjectRuleStore NewRuleStore() => new(_dir);

    [Fact]
    public void Execute_ClearsStores_AndResetsSettingsToDefaults()
    {
        var bindings = NewBindingStore();
        bindings.Set("member-1", "数学");                    // 全局
        bindings.Set("member-1", "物理", "group-a");         // 群作用域
        var rules = NewRuleStore();
        rules.Set("sender-1", "语文");
        var settings = new SubjectRecognitionSettings
        {
            Mode = SubjectRecognitionMode.Keyword,
            SelectionWindowEnabled = false
        };

        var summary = SubjectRecognitionResetLogic.Execute(bindings, rules, settings);

        Assert.Equal(2, summary.RemovedBindings);
        Assert.Equal(1, summary.RemovedRules);
        Assert.Equal(SubjectRecognitionMode.Keyword, summary.ModeBefore);
        Assert.False(summary.SelectionWindowEnabledBefore);

        // 重置后：绑定存储为空（含群作用域）、学习映射为空、模式回默认
        Assert.Empty(bindings.GetAll());
        Assert.False(bindings.TryGetSubject("member-1", "group-a", out _));
        Assert.False(bindings.TryGetSubject("member-1", null, out _));
        Assert.Null(rules.Get("sender-1"));
        Assert.Equal(SubjectRecognitionMode.MemberSelection, settings.Mode);
        Assert.True(settings.SelectionWindowEnabled);
    }

    [Fact]
    public void Execute_PersistsClearing_ForNewStoreInstances()
    {
        var bindings = NewBindingStore();
        bindings.Set("member-1", "数学");
        var rules = NewRuleStore();
        rules.Set("sender-1", "语文");

        SubjectRecognitionResetLogic.Execute(bindings, rules, new SubjectRecognitionSettings());

        // 新实例（模拟重启/其他消费者）从盘上读到的也是空
        Assert.Empty(NewBindingStore().GetAll());
        Assert.Null(NewRuleStore().Get("sender-1"));
    }

    [Fact]
    public void Execute_DoesNotTouchOtherData_InSameDirectory()
    {
        // 同目录预先放置「其他数据」：归档库 files.json、作业通知类文件、学科词表
        var filesJson = Path.Combine(_dir, "files.json");
        var homeworkJson = Path.Combine(_dir, "homework.json");
        var subjectsJson = Path.Combine(_dir, "subjects.json");
        const string filesContent = """{"archived":[{"id":"a1","subject":"数学"}]}""";
        const string homeworkContent = """{"items":[{"id":"h1","content":"作业"}]}""";
        const string subjectsContent = """{"rules":[{"subject":"数学","keywords":["数学"]}]}""";
        File.WriteAllText(filesJson, filesContent);
        File.WriteAllText(homeworkJson, homeworkContent);
        File.WriteAllText(subjectsJson, subjectsContent);

        var bindings = NewBindingStore();
        bindings.Set("member-1", "数学");
        var rules = NewRuleStore();
        rules.Set("sender-1", "语文");

        SubjectRecognitionResetLogic.Execute(bindings, rules, new SubjectRecognitionSettings());

        Assert.Equal(filesContent, File.ReadAllText(filesJson));
        Assert.Equal(homeworkContent, File.ReadAllText(homeworkJson));
        Assert.Equal(subjectsContent, File.ReadAllText(subjectsJson));
    }

    [Fact]
    public void Execute_ToleratesNullStores()
    {
        var settings = new SubjectRecognitionSettings { Mode = SubjectRecognitionMode.Keyword };

        var summary = SubjectRecognitionResetLogic.Execute(null, null, settings);

        Assert.Equal(0, summary.RemovedBindings);
        Assert.Equal(0, summary.RemovedRules);
        Assert.Equal(SubjectRecognitionMode.MemberSelection, settings.Mode);
        Assert.True(settings.SelectionWindowEnabled);
    }

    [Fact]
    public void Collect_ReturnsCurrentCounts()
    {
        var bindings = NewBindingStore();
        bindings.Set("m1", "数学");
        bindings.Set("m2", "物理", "g");
        var rules = NewRuleStore();
        rules.Set("s1", "语文");

        var (bindingCount, ruleCount) = SubjectRecognitionResetLogic.Collect(bindings, rules);

        Assert.Equal(2, bindingCount);
        Assert.Equal(1, ruleCount);

        SubjectRecognitionResetLogic.Execute(bindings, rules, new SubjectRecognitionSettings());
        var (afterBindings, afterRules) = SubjectRecognitionResetLogic.Collect(bindings, rules);
        Assert.Equal(0, afterBindings);
        Assert.Equal(0, afterRules);
    }

    [Fact]
    public void ConfirmGate_ConfirmsOnlyWithinWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var gate = new TwoStageConfirmGate(() => now);

        // 未确认（第一次点击前）不放行
        Assert.False(gate.IsPending);
        Assert.False(gate.TryConfirm());

        // 第一次点击 → 待确认态；窗口内第二次点击 → 放行
        gate.Begin();
        Assert.True(gate.IsPending);
        now += TimeSpan.FromSeconds(2);
        Assert.True(gate.IsPending);
        Assert.True(gate.TryConfirm());

        // 放行后退出待确认态，不会二次放行
        Assert.False(gate.IsPending);
        Assert.False(gate.TryConfirm());
    }

    [Fact]
    public void ConfirmGate_TimesOut_AndDoesNotExecuteAcrossWindows()
    {
        var now = DateTimeOffset.UtcNow;
        var gate = new TwoStageConfirmGate(() => now);

        gate.Begin();
        now += TimeSpan.FromSeconds(4); // 超过 3 秒窗口
        Assert.False(gate.IsPending);
        Assert.False(gate.TryConfirm()); // 超时点击不放行且复位

        // 复位后紧接着点击仍不放行（无跨窗口残留）
        Assert.False(gate.TryConfirm());
    }
}
