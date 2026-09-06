using ClassIng.Plugin.Services.Overlays;
using Xunit;

namespace ClassIng.Tests;

/// <summary>
/// 需求 4：作业悬浮窗学科分组顺序纯逻辑测试。
/// 配置顺序优先，未配置的按名称序追加；空配置 = 现状（全名称序）；未知配置项安全忽略。
/// </summary>
public sealed class HomeworkGroupOrderTests
{
    [Fact]
    public void EmptyConfiguredOrder_KeepsAlphabeticalOrder()
    {
        var ordered = HomeworkGroupOrdering.Sort([], ["B", "A", "C"]);

        Assert.Equal(["A", "B", "C"], ordered);
    }

    [Fact]
    public void NullConfiguredOrder_KeepsAlphabeticalOrder()
    {
        var ordered = HomeworkGroupOrdering.Sort(null, ["英语", "数学"]);

        Assert.Equal(["数学", "英语"], ordered);
    }

    [Fact]
    public void ConfiguredOrder_TakesPriorityAndAppendsRestAlphabetically()
    {
        var ordered = HomeworkGroupOrdering.Sort(["英语", "语文"], ["数学", "语文", "英语"]);

        Assert.Equal(["英语", "语文", "数学"], ordered);
    }

    [Fact]
    public void UnknownEntries_AreSafelyIgnored()
    {
        var ordered = HomeworkGroupOrdering.Sort(["不存在的学科", "数学"], ["数学", "语文"]);

        Assert.Equal(["数学", "语文"], ordered);
    }

    [Fact]
    public void DuplicateEntries_AreDeduplicated()
    {
        var ordered = HomeworkGroupOrdering.Sort(["数学", "数学"], ["数学", "语文"]);

        Assert.Equal(["数学", "语文"], ordered);
    }

    [Fact]
    public void CaseInsensitiveMatch_KeepsPresentSpelling()
    {
        var ordered = HomeworkGroupOrdering.Sort(["MATH"], ["math"]);

        Assert.Equal(["math"], ordered);
    }

    [Fact]
    public void BlankAndNullEntries_AreSkipped()
    {
        var ordered = HomeworkGroupOrdering.Sort(["", "  ", null!, "语文"], ["数学", "语文"]);

        Assert.Equal(["语文", "数学"], ordered);
    }

    [Fact]
    public void NullAndWhitespaceSubjects_AreDropped()
    {
        var ordered = HomeworkGroupOrdering.Sort(["语文"], ["语文", "", "  ", null!]);

        Assert.Equal(["语文"], ordered);
    }

    [Fact]
    public void EmptyPresentList_ReturnsEmpty()
    {
        Assert.Empty(HomeworkGroupOrdering.Sort(["语文"], []));
    }

    [Fact]
    public void WhitespaceAroundNames_IsTrimmed()
    {
        var ordered = HomeworkGroupOrdering.Sort([" 语文 "], [" 数学", "语文"]);

        Assert.Equal(["语文", "数学"], ordered);
    }
}
