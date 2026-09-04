using System.Runtime.Versioning;
using ClassIng.Plugin.Services.FirstRun;
using ClassIng.Plugin.Services.Maintenance;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClassIng.Tests;

/// <summary>模块 9：首次启动引导测试（首启检测 / 完成标记持久化 / 失败不崩溃 / 连接配置写入 / 重新打开入口）。</summary>
[SupportedOSPlatform("windows")]
public class FirstRunServiceTests : IDisposable
{
    private readonly string _dir;

    public FirstRunServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "firstrun", Guid.NewGuid().ToString("N"));
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
            // 清理失败不影响测试结论
        }
    }

    /// <summary>引导窗口假实现（记录 Show/Activate/Closed 调用，避免测试依赖 Avalonia 平台）。</summary>
    private sealed class FakeWizardWindow : IFirstRunWizardWindow
    {
        public int ShowCount;
        public int ActivateCount;
        public event EventHandler? Closed;

        public void Show() => ShowCount++;

        public void Activate() => ActivateCount++;

        public void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);
    }

    private static FirstRunService CreateService(string dir, Func<IFirstRunWizardWindow?>? factory = null)
    {
        var service = new FirstRunService(new SettingsService(dir), NullLogger.Instance, factory);
        // 测试环境无 Avalonia 平台：注入同步执行器替代 UI 线程调度
        service.UiInvoker = action =>
        {
            action();
            return Task.CompletedTask;
        };
        return service;
    }

    // ---- 首启检测 ----

    [Fact]
    public void FreshSettingsWithoutFlag_IsDetectedAsFirstRun()
    {
        var service = CreateService(_dir);

        Assert.True(service.IsFirstRun);
        Assert.False(service.IsWizardOpen);
    }

    [Fact]
    public void CorruptedSettingsFile_FallsBackToDefaults_TreatedAsFirstRun()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ corrupted json !!!");

        var service = CreateService(_dir);

        Assert.True(service.IsFirstRun);
    }

    // ---- 引导完成标记持久化 ----

    [Fact]
    public async Task MarkCompleted_PersistsAcrossInstances()
    {
        var service = CreateService(_dir);

        await service.MarkCompletedAsync();

        Assert.False(service.IsFirstRun);
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json")));

        var reloaded = CreateService(_dir);
        Assert.False(reloaded.IsFirstRun);
    }

    [Fact]
    public async Task MarkCompleted_RaisesSettingsChanged()
    {
        var service = CreateService(_dir);
        var raised = 0;
        service.SettingsService.SettingsChanged += (_, _) => raised++;

        await service.MarkCompletedAsync();

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task SkipPath_MarksCompletedWithoutConnectionConfig()
    {
        var service = CreateService(_dir);

        // 跳过 = 只标记完成，不写连接配置
        await service.MarkCompletedAsync();

        var reloaded = CreateService(_dir);
        Assert.False(reloaded.IsFirstRun);
        Assert.Equal("", reloaded.SettingsService.Current.Connection.AppId);
    }

    // ---- 连接配置写入 ISettingsService（模拟引导窗口完成步骤）----

    [Fact]
    public async Task WizardConnectionConfig_PersistsWithCompletionFlag()
    {
        var service = CreateService(_dir);
        var settings = service.SettingsService;

        // 模拟引导窗口「完成」：写连接配置 → 标记完成
        settings.Current.Connection.AppId = "app-9527";
        settings.Current.Connection.AppSecretProtected = settings.Protect("wizard-secret");
        await service.MarkCompletedAsync();

        var reloaded = CreateService(_dir);
        Assert.False(reloaded.IsFirstRun);
        var connection = reloaded.SettingsService.Current.Connection;
        Assert.Equal("app-9527", connection.AppId);
        Assert.Equal("wizard-secret", reloaded.SettingsService.Unprotect(connection.AppSecretProtected));
    }

    // ---- 引导失败不崩溃 ----

    [Fact]
    public async Task ShowWizard_FactoryNotInjected_ReturnsFalseAndDoesNotThrow()
    {
        var service = CreateService(_dir, factory: null);

        var shown = await service.ShowWizardAsync();

        Assert.False(shown);
        Assert.False(service.IsWizardOpen);
    }

    [Fact]
    public async Task ShowWizard_FactoryThrows_ReturnsFalseAndDoesNotThrow()
    {
        var service = CreateService(_dir, () => throw new InvalidOperationException("boom"));

        var shown = await service.ShowWizardAsync();

        Assert.False(shown);
        Assert.False(service.IsWizardOpen);
    }

    [Fact]
    public async Task ShowWizard_FactoryReturnsNull_ReturnsFalse()
    {
        var service = CreateService(_dir, () => null);

        var shown = await service.ShowWizardAsync();

        Assert.False(shown);
    }

    // ---- 引导窗口显示/复用/关闭 ----

    [Fact]
    public async Task ShowWizard_WithFactory_ShowsWindowAndReportsOpen()
    {
        var window = new FakeWizardWindow();
        var service = CreateService(_dir, () => window);

        var shown = await service.ShowWizardAsync();

        Assert.True(shown);
        Assert.Equal(1, window.ShowCount);
        Assert.True(service.IsWizardOpen);
    }

    [Fact]
    public async Task ShowWizard_SecondCallActivatesExistingWindow()
    {
        var window = new FakeWizardWindow();
        var service = CreateService(_dir, () => window);
        await service.ShowWizardAsync();

        var shownAgain = await service.ShowWizardAsync();

        Assert.True(shownAgain);
        Assert.Equal(1, window.ShowCount);
        Assert.Equal(1, window.ActivateCount);
    }

    [Fact]
    public async Task WizardClosed_ClearsOpenState_NextShowCreatesNewWindow()
    {
        var first = new FakeWizardWindow();
        var second = new FakeWizardWindow();
        var queue = new Queue<FakeWizardWindow>([first, second]);
        var service = CreateService(_dir, () => queue.Dequeue());
        await service.ShowWizardAsync();

        first.RaiseClosed();

        Assert.False(service.IsWizardOpen);

        var shown = await service.ShowWizardAsync();

        Assert.True(shown);
        Assert.Equal(1, second.ShowCount);
    }

    // ---- 重新打开引导入口 ----

    [Fact]
    public async Task ReopenAfterCompleted_StillShowsWizard()
    {
        var window = new FakeWizardWindow();
        var service = CreateService(_dir, () => window);
        await service.MarkCompletedAsync();
        Assert.False(service.IsFirstRun);

        // 重新打开引导入口不检查 IsFirstRun，完成后仍可唤出
        var shown = await service.ShowWizardAsync();

        Assert.True(shown);
        Assert.Equal(1, window.ShowCount);
    }
}

/// <summary>模块 9：启动钩子测试（首启自动弹出 / 已完成不弹出 / 弹出失败不崩溃）。</summary>
[SupportedOSPlatform("windows")]
public class FirstRunStartupServiceTests : IDisposable
{
    private readonly string _dir;

    public FirstRunStartupServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "firstrun-startup", Guid.NewGuid().ToString("N"));
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
            // 清理失败不影响测试结论
        }
    }

    private sealed class FakeWizardWindow : IFirstRunWizardWindow
    {
        public int ShowCount;
        public event EventHandler? Closed;

        public void Show() => ShowCount++;

        public void Activate()
        {
        }

        /// <summary>引发关闭事件（避免 CS0067，并保持与首个假窗口一致的可驱动性）。</summary>
        public void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>构造（服务， 假窗口）；UiInvoker 注入同步执行，StartAsync 后状态确定。</summary>
    private static (FirstRunService Service, FakeWizardWindow Window) CreatePair(string dir)
    {
        var window = new FakeWizardWindow();
        var service = new FirstRunService(new SettingsService(dir), NullLogger.Instance, () => window);
        service.UiInvoker = action =>
        {
            action();
            return Task.CompletedTask;
        };
        return (service, window);
    }

    [Fact]
    public void StartAsync_OnFirstRun_ShowsWizard()
    {
        var (service, window) = CreatePair(_dir);
        var startup = new FirstRunStartupService(service, NullLogger<FirstRunStartupService>.Instance);

        startup.StartAsync(CancellationToken.None);

        Assert.Equal(1, window.ShowCount);
    }

    [Fact]
    public void StartAsync_AlreadyCompleted_DoesNotShow()
    {
        var (service, window) = CreatePair(_dir);
        service.SettingsService.Current.FirstRunCompleted = true;
        var startup = new FirstRunStartupService(service, NullLogger<FirstRunStartupService>.Instance);

        startup.StartAsync(CancellationToken.None);

        Assert.Equal(0, window.ShowCount);
    }

    [Fact]
    public void StartAsync_ShowFailure_DoesNotThrow()
    {
        // 工厂抛异常 + 无 UI 平台调度兜底：StartAsync 本身绝不外抛
        var service = new FirstRunService(new SettingsService(_dir), NullLogger.Instance,
            () => throw new InvalidOperationException("boom"));
        service.UiInvoker = action =>
        {
            action();
            return Task.CompletedTask;
        };
        var startup = new FirstRunStartupService(service, NullLogger<FirstRunStartupService>.Instance);

        startup.StartAsync(CancellationToken.None);

        Assert.False(service.IsWizardOpen);
    }
}
