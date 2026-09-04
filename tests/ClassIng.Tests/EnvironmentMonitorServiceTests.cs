using ClassIng.Plugin.Services.Maintenance;
using ClassIng.Shared.Models;
using Xunit;

namespace ClassIng.Tests;

/// <summary>模块 8：EnvironmentMonitorService 单元测试（磁盘阈值纯函数 / 离线时长 / 告警去抖）。</summary>
public class EnvironmentMonitorServiceTests
{
    [Theory]
    [InlineData(1024, 512, false)]  // 剩余 1KB？——此处以字节计：free=1024MB×1MB, threshold=512MB
    [InlineData(511, 512, true)]
    [InlineData(512, 512, false)]   // 等于阈值不算低（严格小于）
    [InlineData(0, 1, true)]
    public void IsDiskSpaceLowCore_ThresholdJudgement(long freeMb, long thresholdMb, bool expected)
    {
        var actual = EnvironmentMonitorService.IsDiskSpaceLowCore(
            freeMb * 1024L * 1024L, thresholdMb * 1024L * 1024L);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsDiskSpaceLow_UsesInjectedProvider_AndThreshold()
    {
        var svc = CreateMonitor(freeBytes: 100L * 1024 * 1024, thresholdMb: 512);
        Assert.True(svc.IsDiskSpaceLow());

        var svc2 = CreateMonitor(freeBytes: 600L * 1024 * 1024, thresholdMb: 512);
        Assert.False(svc2.IsDiskSpaceLow());
    }

    [Fact]
    public void IsDiskSpaceLow_ProviderThrows_DoesNotReportLow()
    {
        var svc = new EnvironmentMonitorService(new EnvironmentMonitorOptions
        {
            MonitoredDirectory = Path.GetTempPath(),
            FreeSpaceBytesProvider = () => throw new IOException("disk gone"),
            CheckIntervalSeconds = 0
        });

        Assert.False(svc.IsDiskSpaceLow());
    }

    [Fact]
    public void OfflineDuration_TracksStatusTransitions()
    {
        var svc = CreateMonitor(freeBytes: long.MaxValue / 2);
        Assert.Null(svc.OfflineDuration); // 初始在线

        svc.MarkOffline();
        var duration = svc.OfflineDuration;
        Assert.NotNull(duration);
        Assert.True(duration >= TimeSpan.Zero);

        svc.MarkOnline();
        Assert.Null(svc.OfflineDuration);
    }

    [Fact]
    public void CheckNow_DiskLow_RaisesWarningOnce_UntilResolved()
    {
        long freeBytes = 10L * 1024 * 1024; // 10MB < 512MB
        var svc = new EnvironmentMonitorService(new EnvironmentMonitorOptions
        {
            MonitoredDirectory = Path.GetTempPath(),
            DiskFreeThresholdMb = 512,
            CheckIntervalSeconds = 0,
            FreeSpaceBytesProvider = () => freeBytes
        });

        var warnings = new List<string>();
        svc.WarningRaised += (_, w) => warnings.Add(w);

        svc.CheckNow();
        svc.CheckNow(); // 去抖：不重复告警
        Assert.Single(warnings);

        // 恢复到阈值以上（去抖解除）→ 再次下降允许再次告警
        freeBytes = 1024L * 1024 * 1024;
        svc.CheckNow();
        freeBytes = 5L * 1024 * 1024;
        svc.CheckNow();
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void CheckNow_OfflineBeyondThreshold_RaisesWarning()
    {
        var svc = new EnvironmentMonitorService(new EnvironmentMonitorOptions
        {
            MonitoredDirectory = Path.GetTempPath(),
            DiskFreeThresholdMb = 0,
            OfflineWarningMinutes = 10,
            CheckIntervalSeconds = 0,
            FreeSpaceBytesProvider = () => long.MaxValue / 2
        });
        svc.MarkOffline();

        var warnings = new List<string>();
        svc.WarningRaised += (_, w) => warnings.Add(w);

        // 离线时长为刚发生，不触发
        svc.CheckNow();
        Assert.Empty(warnings);

        // 直接操纵内部状态模拟离线超阈值
        typeof(EnvironmentMonitorService)
            .GetField("_offlineSince", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(svc, DateTimeOffset.UtcNow.AddMinutes(-11));
        svc.CheckNow();
        Assert.Single(warnings);
        Assert.Contains("离线", warnings[0]);
    }

    private static EnvironmentMonitorService CreateMonitor(long freeBytes, long thresholdMb = 512)
        => new(new EnvironmentMonitorOptions
        {
            MonitoredDirectory = Path.GetTempPath(),
            DiskFreeThresholdMb = thresholdMb,
            CheckIntervalSeconds = 0,
            FreeSpaceBytesProvider = () => freeBytes
        });
}
