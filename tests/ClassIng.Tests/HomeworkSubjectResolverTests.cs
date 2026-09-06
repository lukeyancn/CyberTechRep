using ClassIng.Plugin.Services.Stores;
using ClassIng.Shared.Models;
using Xunit;

namespace ClassIng.Tests;

/// <summary>学科判定优先级矩阵（纯函数）单元测试。</summary>
public sealed class HomeworkSubjectResolverTests
{
    [Theory]
    [InlineData("数学", false)]  // 真实学科 ≠ 未分类
    [InlineData("未分类", true)]
    [InlineData("", true)]
    [InlineData(" ", true)]
    [InlineData(null, true)]
    [InlineData("物理", false)]
    public void IsUnclassified_ClassifiesPlaceholderAndEmpty(string? subject, bool expected)
    {
        Assert.Equal(expected, HomeworkSubjectResolver.IsUnclassified(subject));
    }

    [Fact]
    public void Resolve_ChainHit_IsNeverOverriddenByUserRule()
    {
        // 识别链正常命中（数学）+ 发送者映射为英语 → 采用识别链结果（映射不覆盖）
        var r = HomeworkSubjectResolver.Resolve("数学", 0.95, SubjectSource.KeywordRule, "英语");

        Assert.False(r.RuleApplied);
        Assert.Equal("数学", r.Subject);
        Assert.Equal(0.95, r.Confidence);
        Assert.Equal(SubjectSource.KeywordRule, r.Source);
    }

    [Theory]
    [InlineData(SubjectSource.KeywordRule)]
    [InlineData(SubjectSource.LocalModel)]
    [InlineData(SubjectSource.CloudLlm)]
    public void Resolve_ChainHit_AllChainSourcesWinOverRule(SubjectSource source)
    {
        var r = HomeworkSubjectResolver.Resolve("化学", 0.8, source, "英语");

        Assert.False(r.RuleApplied);
        Assert.Equal("化学", r.Subject);
        Assert.Equal(source, r.Source);
    }

    [Fact]
    public void Resolve_ChainUnclassified_WithRule_AppliesRuleAsManual()
    {
        var r = HomeworkSubjectResolver.Resolve("未分类", 0.2, SubjectSource.Manual, "英语");

        Assert.True(r.RuleApplied);
        Assert.Equal("英语", r.Subject);
        Assert.Equal(1.0, r.Confidence);
        Assert.Equal(SubjectSource.Manual, r.Source);
    }

    [Fact]
    public void Resolve_ChainUnclassified_WithoutRule_KeepsChainResult()
    {
        var r = HomeworkSubjectResolver.Resolve("未分类", 0.3, SubjectSource.LocalModel, null);

        Assert.False(r.RuleApplied);
        Assert.Equal("未分类", r.Subject);
        Assert.Equal(0.3, r.Confidence);
        Assert.Equal(SubjectSource.LocalModel, r.Source);
    }

    [Fact]
    public void Resolve_RuleItselfUnclassified_IsTreatedAsNoRule()
    {
        // 映射值为「未分类」（用户曾把作业修回未分类）：视为无映射，不空转
        var r = HomeworkSubjectResolver.Resolve("未分类", 0.2, SubjectSource.Manual, "未分类");

        Assert.False(r.RuleApplied);
        Assert.Equal("未分类", r.Subject);
    }
}
