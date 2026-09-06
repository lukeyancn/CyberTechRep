using ClassIng.Plugin.Services.Stores;
using Xunit;

namespace ClassIng.Tests;

/// <summary>通知学科前缀（纯函数）单元测试。</summary>
public sealed class NoticeSubjectPrefixerTests
{
    [Fact]
    public void Apply_WithSubject_AddsPrefix()
    {
        Assert.Equal("英语：明天交作业", NoticeSubjectPrefixer.Apply("明天交作业", "英语"));
    }

    [Fact]
    public void Apply_WithoutSubject_ReturnsUnchanged()
    {
        Assert.Equal("明天交作业", NoticeSubjectPrefixer.Apply("明天交作业", null));
        Assert.Equal("明天交作业", NoticeSubjectPrefixer.Apply("明天交作业", ""));
        Assert.Equal("明天交作业", NoticeSubjectPrefixer.Apply("明天交作业", "  "));
    }

    [Fact]
    public void Apply_EmptyContent_ReturnsUnchanged()
    {
        Assert.Equal("", NoticeSubjectPrefixer.Apply("", "英语"));
    }

    [Fact]
    public void Apply_AlreadyPrefixed_IsIdempotent()
    {
        var once = NoticeSubjectPrefixer.Apply("明天交作业", "英语");
        Assert.Equal(once, NoticeSubjectPrefixer.Apply(once, "英语"));
        Assert.Equal("英语：明天交作业", NoticeSubjectPrefixer.Apply("英语：明天交作业", "英语"));
    }

    [Fact]
    public void Apply_DifferentSubjectPrefix_IsNotTreatedAsDuplicate()
    {
        // 内容已带其他学科前缀（映射变更场景）：追加当前学科前缀而非跳过
        var result = NoticeSubjectPrefixer.Apply("数学：练习一页", "英语");
        Assert.Equal("英语：数学：练习一页", result);
    }

    [Theory]
    [InlineData("英语：内容", "英语", true)]
    [InlineData("英语内容", "英语", false)]
    [InlineData("英语：内容", "数学", false)]
    [InlineData(null, "英语", false)]
    [InlineData("内容", null, false)]
    public void HasPrefix_DetectsExactSubjectPrefix(string? content, string? subject, bool expected)
    {
        Assert.Equal(expected, NoticeSubjectPrefixer.HasPrefix(content, subject));
    }
}
