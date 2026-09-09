using CyberTechRep.Plugin.Services.Stores;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 需求 1 去重合并算法（<see cref="HomeworkDocumentMerger"/>）单元测试：
/// 归一化、完全重复跳过、行级并集增量合并、同发送者限定、手工编辑增量行输出。
/// </summary>
public sealed class HomeworkDocumentMergerTests
{
    private static HomeworkDocumentEntry Entry(string text, string member = "teacher") =>
        new() { Text = text, MemberOpenId = member, SourceMessageIds = ["m1"] };

    [Fact]
    public void Normalize_StripsWhitespaceAndUnifiesFullWidthPunctuation()
    {
        Assert.Equal("1.第1页,第2页", HomeworkDocumentMerger.Normalize("１.第1页，第2页"));
        Assert.Equal("abc", HomeworkDocumentMerger.Normalize("  A B  C "));
        Assert.Equal("", HomeworkDocumentMerger.Normalize("   \r\n  "));
    }

    [Fact]
    public void Plan_IdenticalContent_IsDuplicate()
    {
        var entries = new List<HomeworkDocumentEntry> { Entry("第 1 页，抄写生字。") };

        // 全角/半角、空白、大小写差异都应判为完全重复
        var plan = HomeworkDocumentMerger.Plan(entries, "第1页, 抄写生字.", "teacher");

        Assert.Equal(DocumentMergeDecision.Duplicate, plan.Decision);
        Assert.Equal(0, plan.ExistingIndex);
        Assert.Empty(plan.NewLines);
    }

    [Fact]
    public void Plan_SupersetResend_MergesUnionAndReportsNewLines()
    {
        var entries = new List<HomeworkDocumentEntry> { Entry("1. 第1页\r\n2. 第2页") };

        var plan = HomeworkDocumentMerger.Plan(entries, "第1页\n第2页\n第3页", "teacher");

        Assert.Equal(DocumentMergeDecision.MergeIntoExisting, plan.Decision);
        Assert.Equal(0, plan.ExistingIndex);
        Assert.Equal(["第3页"], plan.NewLines);
        Assert.Contains("第3页", plan.MergedText);
        // 保留原有行顺序（旧行在前）
        Assert.True(plan.MergedText.IndexOf("第1页", StringComparison.Ordinal)
                    < plan.MergedText.IndexOf("第3页", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_SubsetResend_IsDuplicateOrMergeWithoutNewLines()
    {
        var entries = new List<HomeworkDocumentEntry> { Entry("第1页\n第2页") };

        var plan = HomeworkDocumentMerger.Plan(entries, "第2页", "teacher");

        // 行级 Jaccard = 1/2 ≥ 阈值 → 并入，但没有新增行
        Assert.Equal(DocumentMergeDecision.MergeIntoExisting, plan.Decision);
        Assert.Empty(plan.NewLines);
        Assert.Contains("第1页", plan.MergedText);
    }

    [Fact]
    public void Plan_UnrelatedContent_AppendsNewEntry()
    {
        var entries = new List<HomeworkDocumentEntry> { Entry("数学练习册 P12") };

        var plan = HomeworkDocumentMerger.Plan(entries, "背诵古诗三首", "teacher");

        Assert.Equal(DocumentMergeDecision.Append, plan.Decision);
        Assert.Equal(["背诵古诗三首"], plan.NewLines);
    }

    [Fact]
    public void Plan_DifferentSender_NeverMergesEvenIfIdentical()
    {
        var entries = new List<HomeworkDocumentEntry> { Entry("第1页", member: "teacherA") };

        var plan = HomeworkDocumentMerger.Plan(entries, "第1页", "teacherB");

        Assert.Equal(DocumentMergeDecision.Append, plan.Decision);
    }

    [Fact]
    public void Plan_LegacyEntryWithoutMember_StillMerges()
    {
        // 旧数据 MemberOpenId 为空：视为可合并，避免历史文档被重复堆叠
        var entries = new List<HomeworkDocumentEntry> { Entry("第1页", member: "") };

        var plan = HomeworkDocumentMerger.Plan(entries, "第1页", "teacherB");

        Assert.Equal(DocumentMergeDecision.Duplicate, plan.Decision);
    }

    [Fact]
    public void Plan_EmptyText_IsDuplicateNoop()
    {
        var entries = new List<HomeworkDocumentEntry> { Entry("第1页") };

        var plan = HomeworkDocumentMerger.Plan(entries, "   \n  ", "teacher");

        Assert.Equal(DocumentMergeDecision.Duplicate, plan.Decision);
        Assert.Empty(plan.NewLines);
    }

    [Fact]
    public void SplitLines_StripsLeadingMarkersAndBlankLines()
    {
        Assert.Equal(["第1页", "第2页", "第3页"],
            HomeworkDocumentMerger.SplitLines("1. 第1页\r\n- 第2页\n\n(3) 第3页"));
    }

    [Fact]
    public void Overlap_IsJaccardOnNormalizedLines()
    {
        var left = new[] { "第1页", "第2页" };
        var right = new[] { "第2页", "第3页" };

        var score = HomeworkDocumentMerger.Overlap(left, right);

        Assert.Equal(1.0 / 3, score, 6);
    }

    [Fact]
    public void Union_DeduplicatesNormalizedDuplicates_AndReportsAdded()
    {
        var (merged, added) = HomeworkDocumentMerger.Union(
            ["第 1 页"], ["第1页", "第2页"]);

        Assert.Equal(["第 1 页", "第2页"], merged);
        Assert.Equal(["第2页"], added);
    }
}
