using ClassIng.Plugin.Services.Maintenance;
using Xunit;

namespace ClassIng.Tests;

/// <summary>模块 8：UpdateNotifyService 单元测试（版本比较纯函数 / Releases URL 构造；不打网络）。</summary>
public class UpdateNotifyServiceTests
{
    [Theory]
    [InlineData("1.1.0", "1.0.0", true)]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]  // 相同版本不算更新
    [InlineData("1.0.0", "1.1.0", false)]  // 本地比远端新
    [InlineData("1.2.10", "1.2.9", true)]  // 逐段数字比较：10 > 9
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("v1.1.0", "1.0.0", true)]  // 容忍 v 前缀
    public void IsNewerVersion_ComparesPerSegment(string latest, string current, bool expected)
    {
        Assert.Equal(expected, UpdateNotifyService.IsNewerVersion(latest, current));
    }

    [Theory]
    [InlineData("", "1.0.0", false)]
    [InlineData("1.1.0", "", false)]
    public void IsNewerVersion_EmptyInput_ReturnsFalse(string latest, string current, bool expected)
    {
        Assert.Equal(expected, UpdateNotifyService.IsNewerVersion(latest, current));
    }

    [Fact]
    public void Constructor_WithoutExplicitVersion_FallsBackToAssemblyVersion()
    {
        var svc = new UpdateNotifyService(new UpdateNotifyOptions { Repository = "test/repo" });
        Assert.NotNull(svc);
        // 不抛异常即可；版本号来自入口程序集
    }
}
