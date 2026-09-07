using System.Runtime.Versioning;
using System.Text.Json;
using CyberTechRep.Plugin.Services.Maintenance;
using CyberTechRep.Plugin.Services.MessageAccess;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Shared.Models;
using CyberTechRep.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace CyberTechRep.Tests;

/// <summary>
/// 设置持久化审计回归测试（入口 → 保存点 → 加载点 全链路）：
/// 1. settings.json 损坏/空文件/版本不符：先备份为 settings.json.corrupt-&lt;时间戳&gt; 再回退默认（不再静默整档重置）；
/// 2. 悬浮窗控制器在 Current 被导入/恢复默认整体替换后重捕获活实例（拖拽回写/置顶/穿透落进新 settings.json）；
/// 3. 宿主退出兜底保存（SettingsChangeApplier.StopAsync），补设置页 DetachedFromVisualTree 自动保存的漏网场景；
/// 4. 连接设置热更新：差异判定（AppId/ApiBase/TokenApiUrl）先于快照更新；无变化不重连，参数变更触发重连。
/// </summary>
[SupportedOSPlatform("windows")]
public class SettingsPersistenceTests : IDisposable
{
    private readonly string _dir;

    public SettingsPersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "persistence", Guid.NewGuid().ToString("N"));
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
            // 临时目录清理失败不影响测试结论
        }
    }

    // ============ ① 损坏文件备份 + 回退默认 ============

    [Fact]
    public void Load_CorruptFile_BacksUpBeforeResetting()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ corrupted json !!!");

        var svc = new SettingsService(_dir);

        // 回退默认
        Assert.Equal("Info", svc.Current.Maintenance.LogLevel);
        Assert.Equal(1, svc.Current.SchemaVersion);
        // 原文件已改名备份，且仅一份备份
        Assert.False(File.Exists(path));
        var backups = Directory.GetFiles(_dir, "settings.json.corrupt-*");
        var backup = Assert.Single(backups);
        Assert.Equal("{ corrupted json !!!", File.ReadAllText(backup));
    }

    [Fact]
    public void Load_WrongSchemaVersion_BacksUpBeforeResetting()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "SchemaVersion": 99, "Maintenance": { "LogLevel": "Debug" } }""");

        var svc = new SettingsService(_dir);

        Assert.Equal("Info", svc.Current.Maintenance.LogLevel);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Load_EmptyFile_BacksUpBeforeResetting()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "");

        var svc = new SettingsService(_dir);

        Assert.Equal("Info", svc.Current.Maintenance.LogLevel);
        Assert.Single(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Load_UnknownJsonFields_AreIgnored_SettingsStillLoaded()
    {
        // schema 容错：未知字段（未来版本新增）被忽略，已认识的字段正常加载，不触发整档重置
        const string json = """
            {
              "SchemaVersion": 1,
              "Maintenance": { "LogLevel": "Debug", "SomeFutureField": 42 },
              "UnknownGroup": { "x": 1 }
            }
            """;
        File.WriteAllText(Path.Combine(_dir, "settings.json"), json);

        var svc = new SettingsService(_dir);

        Assert.Equal("Debug", svc.Current.Maintenance.LogLevel);
        Assert.Empty(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
    }

    // ============ ② Current 替换后控制器重捕获 ============

    [Fact]
    public async Task Controller_WritesReachNewCurrent_AfterResetToDefaults()
    {
        // 缺陷复现：ResetToDefaultsAsync 整体替换 Current 后，控制器若仍持旧 Overlays 实例，
        // 拖拽回写/快捷菜单开关只落到已脱离设置服务的旧对象，settings.json 不更新、重启即回退。
        var svc = new SettingsService(_dir);
        var controller = new SuspensionWindowController(_dir, settingsService: svc, windowFactory: null);

        await svc.ResetToDefaultsAsync(); // Current 整体替换 → 广播 → 控制器应重捕获

        var changed = new OverlayWindowSettings { X = 111.5, Y = 222.5, Pinned = true, ClickThrough = true };
        await controller.ApplySettingsAsync(SuspensionWindowController.NoticeKey, changed);

        // 控制器的写入必须落在新 Current（活实例）上
        Assert.Equal(111.5, svc.Current.Overlays.Notice.X);
        Assert.Equal(222.5, svc.Current.Overlays.Notice.Y);
        Assert.True(svc.Current.Overlays.Notice.Pinned);
        Assert.True(svc.Current.Overlays.Notice.ClickThrough);

        // 并最终持久化到 settings.json（ApplySettingsAsync 的落盘为后台任务，轮询等待）
        await TestWait.WaitForAsync(() =>
        {
            try
            {
                var reloaded = new SettingsService(_dir);
                return reloaded.Current.Overlays.Notice.Pinned;
            }
            catch
            {
                return false;
            }
        }, TimeSpan.FromSeconds(15), "重捕获后控制器写入应持久化到 settings.json");
    }

    [Fact]
    public async Task Controller_WritesReachNewCurrent_AfterImport()
    {
        var svc = new SettingsService(_dir);
        var controller = new SuspensionWindowController(_dir, settingsService: svc, windowFactory: null);

        var importJson = await new SettingsService(_dir).ExportAsync();
        await svc.ImportAsync(importJson); // Current 整体替换

        var changed = new OverlayWindowSettings { X = 88, Visible = true };
        await controller.ApplySettingsAsync(SuspensionWindowController.HomeworkKey, changed);

        Assert.Equal(88, svc.Current.Overlays.Homework.X);
        Assert.True(svc.Current.Overlays.Homework.Visible);
    }

    [Fact]
    public async Task Controller_OrdinarySave_DoesNotRecaptureOrLoop()
    {
        // 普通保存（非导入/重置）Current 未替换：重捕获应为 no-op，
        // 且控制器回写 → SaveAsync → 广播 → 重捕获检查 不得引发广播死循环
        var svc = new SettingsService(_dir);
        var controller = new SuspensionWindowController(_dir, settingsService: svc, windowFactory: null);
        var broadcasts = 0;
        svc.SettingsChanged += (_, _) => Interlocked.Increment(ref broadcasts);

        await controller.ApplySettingsAsync(SuspensionWindowController.NoticeKey,
            new OverlayWindowSettings { X = 55 });

        Assert.Equal(55, svc.Current.Overlays.Notice.X);

        // 一次保存恰好一次广播（无回环放大）：控制器落盘为后台任务，先等该次广播到达
        await TestWait.WaitForAsync(() => Volatile.Read(ref broadcasts) >= 1,
            TimeSpan.FromSeconds(10), "控制器回写应触发一次广播");
        var expected = Volatile.Read(ref broadcasts);
        await Task.Delay(300);
        Assert.Equal(expected, Volatile.Read(ref broadcasts));
    }

    // ============ ③ 宿主退出兜底保存 ============

    [Fact]
    public async Task StopAsync_FlushesFinalEditsToDisk()
    {
        var svc = new SettingsService(_dir);
        // 模拟设置页绑定直接改内存但未触发 DetachedFromVisualTree 自动保存
        svc.Current.Connection.AppId = "flush-test-appid";
        var applier = new SettingsChangeApplier(svc);

        await applier.StopAsync(CancellationToken.None);

        // 兜底保存为后台任务，轮询等待落盘
        await TestWait.WaitForAsync(() => File.Exists(svc.ConfigFilePath),
            TimeSpan.FromSeconds(5), "宿主退出兜底保存应落盘");
        var reloaded = new SettingsService(_dir);
        Assert.Equal("flush-test-appid", reloaded.Current.Connection.AppId);
    }

    // ============ ④ 连接设置热更新差异判定 ============

    [Fact]
    public void RequiresReconnect_OnlyConnectionParamsTrigger()
    {
        var baseline = new ConnectionSettings { AppId = "app-1", ApiBase = "https://a", TokenApiUrl = "https://t" };

        Assert.True(MessageIngestService.RequiresReconnect(null, baseline));
        Assert.False(MessageIngestService.RequiresReconnect(baseline, baseline));
        Assert.False(MessageIngestService.RequiresReconnect(baseline, new ConnectionSettings
        {
            AppId = "app-1",
            ApiBase = "https://a",
            TokenApiUrl = "https://t",
            GroupWhitelist = ["grp-changed"], // 白名单变化：管道热生效，不重连
            HomeworkSendEnabled = false,
            ReconnectInitialDelaySec = 9
        }));
        Assert.True(MessageIngestService.RequiresReconnect(baseline, new ConnectionSettings
        {
            AppId = "app-2", ApiBase = "https://a", TokenApiUrl = "https://t"
        }));
        Assert.True(MessageIngestService.RequiresReconnect(baseline, new ConnectionSettings
        {
            AppId = "app-1", ApiBase = "https://b", TokenApiUrl = "https://t"
        }));
        Assert.True(MessageIngestService.RequiresReconnect(baseline, new ConnectionSettings
        {
            AppId = "app-1", ApiBase = "https://a", TokenApiUrl = "https://t2"
        }));
    }

    [Fact]
    public void RequiresReconnect_InPlaceMutation_DetectedAgainstSnapshot()
    {
        // 关键场景：ConnectionSettings 是设置服务的活实例，可能被设置页绑定原地修改。
        // 同一实例自比较恒等（永远 false），必须与启动/上次应用时的独立快照比较才能发现变化。
        var live = new ConnectionSettings { AppId = "app-1" };
        var snapshot = new ConnectionSettings { AppId = "app-1" };

        Assert.False(MessageIngestService.RequiresReconnect(snapshot, live));
        live.AppId = "app-2"; // 原地修改（等价于设置页绑定写 Current.Connection.AppId）

        // 与快照比较 → 检出；若快照也被原地污染（旧缺陷把 _lastApplied 指向活实例）则永远检不出
        Assert.True(MessageIngestService.RequiresReconnect(snapshot, live));
        Assert.False(MessageIngestService.RequiresReconnect(live, live));
    }

    // ============ ④b 连接设置热更新端到端（本地假网关） ============

    [Fact]
    public async Task ApplySettings_NoConnectionChange_DoesNotReconnect()
    {
        await using var server = await StartGatewayAsync();
        var service = CreateService(server);
        try
        {
            await service.StartAsync();
            await TestWait.WaitForAsync(() => service.Status == ConnectionStatus.Connected,
                TimeSpan.FromSeconds(10), "初始连接应建立");

            // 与当前连接参数完全相同的新实例（等价于其他组设置保存触发的广播）：
            // 不允许重连（否则悬浮窗拖拽等任何保存都会打断连接）
            service.ApplySettings(new ConnectionSettings
            {
                AppId = "app-test",
                AppSecretProtected = "secret-plain",
                ApiBase = "http://fake.api",
                TokenApiUrl = "http://fake.api/token",
                ReconnectInitialDelaySec = 1,
                ReconnectBackoffFactor = 2.0,
                ReconnectMaxDelaySec = 5
            });

            await Task.Delay(1200);
            Assert.Equal(1, server.AcceptedConnections);
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task ApplySettings_ConnectionParamsChanged_Reconnects()
    {
        await using var server = await StartGatewayAsync();
        var service = CreateService(server);
        try
        {
            await service.StartAsync();
            await TestWait.WaitForAsync(() => service.Status == ConnectionStatus.Connected,
                TimeSpan.FromSeconds(10), "初始连接应建立");

            var changed = new ConnectionSettings
            {
                AppId = "app-changed",
                AppSecretProtected = "secret-plain",
                ApiBase = "http://fake.api",
                TokenApiUrl = "http://fake.api/token",
                ReconnectInitialDelaySec = 1,
                ReconnectBackoffFactor = 2.0,
                ReconnectMaxDelaySec = 5
            };
            service.ApplySettings(changed);

            // AppId 变更（原缺陷：差异判定死代码从不重连）→ 应重连（新 TCP 连接被接受）
            await TestWait.WaitForAsync(() => server.AcceptedConnections >= 2,
                TimeSpan.FromSeconds(15), "连接参数变更应触发重连");
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    private static async Task<TestGatewayServer> StartGatewayAsync()
    {
        var server = new TestGatewayServer
        {
            // 完成握手后保持连接（不响应心跳也不主动断开，与既有心跳测试同构）
            OnConnection = async (conn, ct) =>
            {
                await MessageIngestServiceTests.HandshakeAsync(conn, "sess-hold", 30_000, ct);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        };
        await server.StartAsync();
        return server;
    }

    private static MessageIngestService CreateService(TestGatewayServer server)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "classing-tests", "persistence-ingest",
            Guid.NewGuid().ToString("N"));
        return new MessageIngestService(
            new IngestOptionsProvider
            {
                GetSettings = () => new ConnectionSettings
                {
                    AppId = "app-test",
                    AppSecretProtected = "secret-plain",
                    ApiBase = "http://fake.api",
                    TokenApiUrl = "http://fake.api/token",
                    ReconnectInitialDelaySec = 1,
                    ReconnectBackoffFactor = 2.0,
                    ReconnectMaxDelaySec = 5
                },
                DataDirectory = dataDir
            },
            null,
            options =>
            {
                options.HttpInvoker = new HttpMessageInvoker(new FakePlatformHttpHandler(server.Port));
                options.SocketFactory = async (uri, ct) =>
                {
                    var ws = new System.Net.WebSockets.ClientWebSocket();
                    await ws.ConnectAsync(uri, ct);
                    return ws;
                };
            });
    }
}
