using ClassIng.Plugin.Services.Stores;
using ClassIng.Shared.Models;
using Xunit;

namespace ClassIng.Tests;

/// <summary>按天归档与保留期清理（纯函数）单元测试。</summary>
public sealed class RetentionPoliciesTests
{
    private static readonly DateOnly Today = new(2026, 9, 6);

    private static NoticeItem Notice(DateTimeOffset createdAt, bool isRead) => new()
    {
        MessageId = "m",
        Content = "c",
        CreatedAt = createdAt,
        IsRead = isRead
    };

    private static HomeworkItem Homework(DateTimeOffset createdAt) => new()
    {
        MessageId = "m",
        Content = "c",
        CreatedAt = createdAt
    };

    private static DateTimeOffset At(DateOnly day) =>
        new(day.ToDateTime(TimeOnly.Parse("08:30")), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(TimeOnly.Parse("08:30"))));

    // ============ BucketOf：CreatedAt → 本地日期桶 ============

    [Fact]
    public void BucketOf_UsesLocalDate()
    {
        var local = new DateTimeOffset(2026, 9, 5, 23, 59, 0, TimeSpan.FromHours(8));
        Assert.Equal(new DateOnly(2026, 9, 5), RetentionPolicies.BucketOf(local));
    }

    // ============ 通知：未读绝不动；已读仅当天（retention>0）；0=永久 ============

    [Fact]
    public void Notice_Unread_IsNeverRemoved_EvenInExpiredBuckets()
    {
        var oldUnread = Notice(At(new DateOnly(2025, 1, 1)), isRead: false);
        Assert.True(RetentionPolicies.ShouldKeepNotice(oldUnread, Today, 1));
        Assert.True(RetentionPolicies.ShouldKeepNotice(oldUnread, Today, 30));
        Assert.True(RetentionPolicies.ShouldKeepNotice(oldUnread, Today, 0));
    }

    [Fact]
    public void Notice_ReadToday_IsKept_WhenRetentionEnabled()
    {
        var readToday = Notice(At(Today), isRead: true);
        Assert.True(RetentionPolicies.ShouldKeepNotice(readToday, Today, 1));
        Assert.True(RetentionPolicies.ShouldKeepNotice(readToday, Today, 7));
    }

    [Fact]
    public void Notice_ReadYesterday_IsRemoved_WhenRetentionEnabled()
    {
        var readYesterday = Notice(At(Today.AddDays(-1)), isRead: true);
        Assert.False(RetentionPolicies.ShouldKeepNotice(readYesterday, Today, 1));
        Assert.False(RetentionPolicies.ShouldKeepNotice(readYesterday, Today, 365));
    }

    [Fact]
    public void Notice_RetentionZero_KeepsEverything()
    {
        var readOld = Notice(At(new DateOnly(2020, 1, 1)), isRead: true);
        Assert.True(RetentionPolicies.ShouldKeepNotice(readOld, Today, 0));
        Assert.True(RetentionPolicies.ShouldKeepNotice(readOld, Today, -3));
    }

    // ============ 作业：0=永久；>0 保留最近 N 天（含当天） ============

    [Fact]
    public void Homework_RetentionZero_KeepsEverything()
    {
        var ancient = Homework(At(new DateOnly(2020, 1, 1)));
        Assert.True(RetentionPolicies.ShouldKeepHomework(ancient, Today, 0));
    }

    [Theory]
    [InlineData(0, true)]   // 当天
    [InlineData(1, true)]   // 昨天
    [InlineData(6, true)]   // 6 天前（7 天窗口内）
    [InlineData(7, false)]  // 7 天前（超出 7 天窗口）
    [InlineData(30, false)]
    public void Homework_RetentionSevenDays_KeepsLastSevenDaysIncludingToday(int ageDays, bool expected)
    {
        var item = Homework(At(Today.AddDays(-ageDays)));
        Assert.Equal(expected, RetentionPolicies.ShouldKeepHomework(item, Today, 7));
    }

    [Fact]
    public void Homework_RetentionOneDay_KeepsOnlyToday()
    {
        Assert.True(RetentionPolicies.ShouldKeepHomework(Homework(At(Today)), Today, 1));
        Assert.False(RetentionPolicies.ShouldKeepHomework(Homework(At(Today.AddDays(-1))), Today, 1));
    }
}
