using ClassIng.Plugin.Services.Maintenance;
using ClassIng.Shared.Models;
using Xunit;

namespace ClassIng.Tests;

/// <summary>模块 8：RetryQueueService 单元测试（指数退避/上限 GivenUp/Replay 重置计数/持久化跨实例）。</summary>
public class RetryQueueServiceTests : IDisposable
{
    private readonly string _dir;

    public RetryQueueServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "retry", Guid.NewGuid().ToString("N"));
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
            // 清理失败不影响测试
        }
    }

    private RetryQueueOptions CreateOptions(bool autoStart = false, string? dir = null) => new()
    {
        GetSettings = () => new MaintenanceSettings { RetryQueueEnabled = true, MaxRetryAttempts = 3 },
        DataDirectory = dir ?? _dir,
        InitialDelaySec = 5,
        BackoffFactor = 2.0,
        MaxDelaySec = 600,
        AutoStartScheduler = autoStart
    };

    [Fact]
    public void ComputeNextDelaySec_ExponentialBackoff()
    {
        using var svc = new RetryQueueService(CreateOptions());

        Assert.Equal(5, svc.ComputeNextDelaySec(0));
        Assert.Equal(10, svc.ComputeNextDelaySec(1));
        Assert.Equal(20, svc.ComputeNextDelaySec(2));
        Assert.Equal(40, svc.ComputeNextDelaySec(3));
        Assert.Equal(80, svc.ComputeNextDelaySec(4));
    }

    [Fact]
    public void ComputeNextDelaySec_CappedAtMaxDelay()
    {
        using var svc = new RetryQueueService(CreateOptions());

        // 5 × 2^n 无限倍增，但不超过 MaxDelaySec=600
        Assert.Equal(600, svc.ComputeNextDelaySec(8));
        Assert.Equal(600, svc.ComputeNextDelaySec(16));
    }

    [Fact]
    public async Task Enqueue_PersistsAcrossInstances()
    {
        using (var first = new RetryQueueService(CreateOptions()))
        {
            await first.EnqueueAsync(RetryOperationType.StoreWrite, new { Target = "notices.json" });
        }

        // 新实例（模拟插件重启）应能读回队列
        using var second = new RetryQueueService(CreateOptions());
        var items = await second.GetAllAsync();

        Assert.Single(items);
        Assert.Equal(RetryOperationType.StoreWrite, items[0].OperationType);
        Assert.Equal(RetryItemStatus.Waiting, items[0].Status);
        Assert.Contains("notices.json", items[0].PayloadJson);
    }

    [Fact]
    public async Task Tick_ExhaustedAttempts_BecomesGivenUp()
    {
        using var svc = new RetryQueueService(CreateOptions());
        await svc.EnqueueAsync(RetryOperationType.Custom, "op-1");

        // MaxRetryAttempts=3：三次失败后 GivenUp
        await svc.TickAsync(true);
        await svc.TickAsync(true);
        await svc.TickAsync(true);
        var items = await svc.GetAllAsync();

        var item = Assert.Single(items);
        Assert.Equal(RetryItemStatus.GivenUp, item.Status);
        Assert.Equal(3, item.AttemptCount);
        Assert.NotNull(item.LastError);
    }

    [Fact]
    public async Task Tick_FailingExecutor_SchedulesNextAttempt()
    {
        using var svc = new RetryQueueService(CreateOptions());
        await svc.EnqueueAsync(RetryOperationType.StoreWrite, "write-1");

        await svc.TickAsync(true);
        var item = Assert.Single(await svc.GetAllAsync());

        Assert.Equal(1, item.AttemptCount);
        Assert.Equal(RetryItemStatus.Waiting, item.Status);
        // NextAttemptAt 在未来（指数退避）
        Assert.True(item.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(4));
    }

    [Fact]
    public async Task ReplayAsync_ResetsAttemptCount_AndExecutesImmediately()
    {
        using var svc = new RetryQueueService(CreateOptions());
        await svc.EnqueueAsync(RetryOperationType.Custom, "op-1");
        var calls = 0;
        svc.CustomCallback = (_, _) =>
        {
            calls++;
            return Task.FromResult(true);
        };

        await svc.TickAsync(true); // CustomCallback 已注入 → 成功
        var afterFirst = Assert.Single(await svc.GetAllAsync());
        Assert.Equal(RetryItemStatus.Succeeded, afterFirst.Status);

        // 构造一个 GivenUp 条目再重放（独立持久化目录，避免读回 svc 已持久化的条目）
        var svc2Dir = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(svc2Dir);
        var svc2 = new RetryQueueService(CreateOptions(dir: svc2Dir));
        await svc2.EnqueueAsync(RetryOperationType.Custom, "op-2");
        for (var i = 0; i < 3; i++)
        {
            await svc2.TickAsync(true);
        }

        var givenUp = Assert.Single(await svc2.GetAllAsync());
        Assert.Equal(RetryItemStatus.GivenUp, givenUp.Status);

        svc2.CustomCallback = (_, _) => Task.FromResult(true);
        await svc2.ReplayAsync(givenUp.Id);

        var replayed = Assert.Single(await svc2.GetAllAsync());
        Assert.Equal(RetryItemStatus.Succeeded, replayed.Status);
        Assert.Equal(1, replayed.AttemptCount); // 重放重置计数后执行成功
        Assert.Equal(1, calls); // svc 的回调计数不受影响
    }

    [Fact]
    public async Task Tick_SuccessfulExecutor_MarksSucceeded()
    {
        using var svc = new RetryQueueService(CreateOptions());
        await svc.EnqueueAsync(RetryOperationType.Custom, "op-ok");
        svc.CustomCallback = (_, _) => Task.FromResult(true);

        await svc.TickAsync(true);
        var item = Assert.Single(await svc.GetAllAsync());

        Assert.Equal(RetryItemStatus.Succeeded, item.Status);
        Assert.Equal(1, item.AttemptCount);
    }

    [Fact]
    public async Task PurgeSucceeded_RemovesOnlySucceededItems()
    {
        using var svc = new RetryQueueService(CreateOptions());
        await svc.EnqueueAsync(RetryOperationType.Custom, "ok");
        svc.CustomCallback = (_, _) => Task.FromResult(true);
        await svc.TickAsync(true); // "ok" 成功

        svc.CustomCallback = null;
        await svc.EnqueueAsync(RetryOperationType.Custom, "pending"); // 保持 Waiting

        await svc.PurgeSucceededAsync();

        var remaining = await svc.GetAllAsync();
        Assert.Single(remaining);
        Assert.Contains("pending", remaining[0].PayloadJson);
        Assert.Equal(RetryItemStatus.Waiting, remaining[0].Status);
    }

    [Fact]
    public async Task Tick_DisabledQueue_DoesNothing()
    {
        var options = new RetryQueueOptions
        {
            GetSettings = () => new MaintenanceSettings { RetryQueueEnabled = false, MaxRetryAttempts = 3 },
            DataDirectory = _dir,
            AutoStartScheduler = false
        };
        using var svc = new RetryQueueService(options);
        await svc.EnqueueAsync(RetryOperationType.Custom, "op-1");

        await svc.TickAsync(true);
        var item = Assert.Single(await svc.GetAllAsync());

        Assert.Equal(0, item.AttemptCount);
        Assert.Equal(RetryItemStatus.Waiting, item.Status);
    }

    [Fact]
    public async Task UnregisteredExecutorType_CountsAsFailure_ItemKept()
    {
        using var svc = new RetryQueueService(CreateOptions());
        await svc.EnqueueAsync(RetryOperationType.FileDownload, "https://example.com/file.pdf");

        await svc.TickAsync(true);
        var item = Assert.Single(await svc.GetAllAsync());

        Assert.Equal(1, item.AttemptCount);
        Assert.Equal(RetryItemStatus.Waiting, item.Status);
        Assert.Contains("executor_not_registered", item.LastError);
    }
}
