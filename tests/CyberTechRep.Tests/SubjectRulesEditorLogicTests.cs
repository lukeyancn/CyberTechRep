using CyberTechRep.Plugin.Services.SubjectChain;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 需求 3：学科词表可视化编辑器校验纯逻辑测试。
/// 防呆项：空学科名 / 非法字符（与文件管道文件名安全校验同口径）/ 重复学科 / 空关键词。
/// </summary>
public sealed class SubjectRulesEditorLogicTests
{
    private static SubjectRule Rule(string subject, string[] keywords, int priority = 10) => new()
    {
        Subject = subject,
        Keywords = keywords,
        Priority = priority
    };

    [Fact]
    public void ValidRules_ProduceNoErrors()
    {
        var errors = SubjectRulesEditorLogic.Validate(
        [
            Rule("数学", ["数学", "代数"]),
            Rule("语文", ["语文"])
        ]);

        Assert.Empty(errors);
    }

    [Fact]
    public void EmptySubjectName_IsRejected()
    {
        var errors = SubjectRulesEditorLogic.Validate([Rule("", ["关键词"])]);

        Assert.Single(errors);
        Assert.Contains("学科名不能为空", errors[0]);
    }

    [Fact]
    public void WhitespaceOnlySubjectName_IsRejected()
    {
        var errors = SubjectRulesEditorLogic.Validate([Rule("   ", ["关键词"])]);

        Assert.Single(errors);
    }

    [Fact]
    public void PathSeparators_AreRejected()
    {
        Assert.NotEmpty(SubjectRulesEditorLogic.Validate([Rule("数/学", ["关键词"])]));
        Assert.NotEmpty(SubjectRulesEditorLogic.Validate([Rule("数\\学", ["关键词"])]));
    }

    [Fact]
    public void PathTraversalFragment_IsRejected()
    {
        Assert.NotEmpty(SubjectRulesEditorLogic.Validate([Rule("..", ["关键词"])]));
    }

    [Fact]
    public void InvalidFileNameCharacters_AreRejected()
    {
        Assert.NotEmpty(SubjectRulesEditorLogic.Validate([Rule("数学:", ["关键词"])]));
        Assert.NotEmpty(SubjectRulesEditorLogic.Validate([Rule("数学?", ["关键词"])]));
        Assert.NotEmpty(SubjectRulesEditorLogic.Validate([Rule("数学|", ["关键词"])]));
    }

    [Fact]
    public void DuplicateSubjects_IgnoringCase_AreRejected()
    {
        var errors = SubjectRulesEditorLogic.Validate(
        [
            Rule("数学", ["关键词"]),
            Rule("数学", ["另一关键词"]),
            Rule("数学", ["第三个关键词"])
        ]);

        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Contains("重复", e));
    }

    [Fact]
    public void SubjectWithoutKeywords_IsRejected()
    {
        var errors = SubjectRulesEditorLogic.Validate([Rule("数学", [])]);

        Assert.Single(errors);
        Assert.Contains("没有任何关键词", errors[0]);
    }

    [Fact]
    public void SubjectWithOnlyWhitespaceKeywords_IsRejected()
    {
        var errors = SubjectRulesEditorLogic.Validate([Rule("数学", ["  ", ""])]);

        Assert.Single(errors);
        Assert.Contains("没有任何关键词", errors[0]);
    }

    [Fact]
    public void NullRules_ProduceNoErrors()
    {
        Assert.Empty(SubjectRulesEditorLogic.Validate(null));
        Assert.Empty(SubjectRulesEditorLogic.Normalize(null));
    }

    [Fact]
    public void Normalize_TrimsAndDeduplicatesKeywords()
    {
        var normalized = SubjectRulesEditorLogic.Normalize(
        [
            Rule(" 数学 ", [" 数学", "代数 ", "数学", "", "  "])
        ]);

        var rule = Assert.Single(normalized);
        Assert.Equal("数学", rule.Subject);
        Assert.Equal(["数学", "代数"], rule.Keywords);
    }

    [Fact]
    public void Normalize_DropsInvalidEntriesAndPreservesOrder()
    {
        var normalized = SubjectRulesEditorLogic.Normalize(
        [
            Rule("", ["关键词"]),
            Rule("语文", ["语文"]),
            null!,
            Rule("数学", ["数学"])
        ]);

        Assert.Equal(2, normalized.Count);
        Assert.Equal("语文", normalized[0].Subject);
        Assert.Equal("数学", normalized[1].Subject);
    }

    [Fact]
    public void UnsafeNameHelper_MatchesFilePipelineValidationSemantics()
    {
        Assert.True(SubjectRulesEditorLogic.IsUnsafeSubjectName("a/b"));
        Assert.True(SubjectRulesEditorLogic.IsUnsafeSubjectName("a\\b"));
        Assert.True(SubjectRulesEditorLogic.IsUnsafeSubjectName("a..b"));
        Assert.True(SubjectRulesEditorLogic.IsUnsafeSubjectName("a<b"));
        Assert.False(SubjectRulesEditorLogic.IsUnsafeSubjectName("数学"));
    }
}
