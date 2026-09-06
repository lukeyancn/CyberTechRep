using System.Net;
using ClassIng.Plugin.Services.MessageAccess;
using ClassIng.Plugin.Services.Stores;
using ClassIng.Plugin.Views;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using Xunit;

namespace ClassIng.Tests;

/// <summary>需求 1+2：作业删除（HomeworkStore.DeleteAsync）、清单格式化（HomeworkDigestFormatter）、群发送结果汇总（HomeworkSendService）。</summary>
public sealed class HomeworkDigestSendTests : IDisposable
{
    private readonly string _dir;

    public HomeworkDigestSendTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "digest-send", Guid.NewGuid().ToString("N"));
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

    private static HomeworkItem Item(string subject, string content, DateTimeOffset? createdAt = null, string messageId = "") => new()
    {
        MessageId = messageId,
        Subject = subject,
        Content = content,
        CreatedAt = createdAt ?? new DateTimeOffset(2026, 9, 6, 8, 0, 0, TimeSpan.FromHours(8))
    };

    // ---------- 需求 2-1：清单格式化（纯函数） ----------

    [Fact]
    public void Format_GroupsBySubject_WithTailNote()
    {
        var nl = Environment.NewLine;
        var tail = HomeworkDigestFormatter.TailNote;
        var text = HomeworkDigestFormatter.Format(
        [
            Item("数学", "完成口算第12页", messageId: "m1"),
            Item("英语", "背单词 Unit 1", messageId: "m2"),
            Item("数学", "预习第3课", createdAt: new DateTimeOffset(2026, 9, 6, 7, 0, 0, TimeSpan.FromHours(8)), messageId: "m3")
        ]);

        // 数学组两条（组内按时间正序），组间空行，末尾固定换行追加尾注
        Assert.Equal(
            "【数学】" + nl +
            "1. 预习第3课" + nl +
            "2. 完成口算第12页" + nl +
            nl +
            "【英语】" + nl +
            "1. 背单词 Unit 1" + nl +
            tail,
            text);
        Assert.EndsWith(tail, text);
    }

    [Fact]
    public void Format_EmptySubjectOrEmptyContent_IsSkipped_NoEmptyGroup()
    {
        var nl = Environment.NewLine;
        var text = HomeworkDigestFormatter.Format(
        [
            Item("", "无学科条目", messageId: "m1"),
            Item("   ", "空白学科条目", messageId: "m2"),
            Item("数学", "   ", messageId: "m3"),
            Item("数学", "正常条目", messageId: "m4")
        ]);

        // 空学科不发：无「【】」空组，无空学科条目
        Assert.Equal(
            "【数学】" + nl +
            "1. 正常条目" + nl +
            HomeworkDigestFormatter.TailNote,
            text);
    }

    [Fact]
    public void Format_EmptyList_IsJustTailNote()
    {
        var text = HomeworkDigestFormatter.Format([]);
        Assert.Equal(HomeworkDigestFormatter.TailNote, text);
    }

    [Fact]
    public void Format_MergesSameSubjectIgnoringCaseAndWhitespace_KeepsFirstSpelling()
    {
        var nl = Environment.NewLine;
        var tail = HomeworkDigestFormatter.TailNote;
        var text = HomeworkDigestFormatter.Format(
        [
            Item("数学 ", "甲", messageId: "m1"),
            Item("math", "乙", messageId: "m2"),
            Item("Math", "丙", messageId: "m3")
        ]);

        // 学科名空白/大小写差异合并为一组（保留首次出现的写法），不产生重复组
        Assert.Equal(
            "【数学】" + nl +
            "1. 甲" + nl +
            nl +
            "【math】" + nl +
            "1. 乙" + nl +
            "2. 丙" + nl +
            tail,
            text);
    }

    // ---------- 需求 1：作业删除 ----------

    [Fact]
    public async Task Delete_RemovesOnlyTargetItem_Persists_AndRaisesChanged()
    {
        var store = new HomeworkStore(_dir);
        var a = await store.UpsertAsync(new HomeworkItem { MessageId = "mA", Subject = "数学", Content = "A" });
        var b = await store.UpsertAsync(new HomeworkItem { MessageId = "mB", Subject = "英语", Content = "B" });
        var events = new List<HomeworkItem>();
        store.Changed += (_, item) => events.Add(item);

        var deleted = await store.DeleteAsync(a.Id);

        Assert.True(deleted);
        var all = await store.GetAllAsync();
        var remaining = Assert.Single(all);
        Assert.Equal(b.Id, remaining.Id);
        // 只删这一条：不牵连按发送者映射（此处无映射数据，映射规则存储保持原样）
        Assert.Single(events);
        Assert.Equal(a.Id, events[0].Id);

        // 跨实例：删除已持久化
        var reloaded = await new HomeworkStore(_dir).GetAllAsync();
        Assert.Single(reloaded);
        Assert.DoesNotContain(reloaded, i => i.Id == a.Id);
    }

    [Fact]
    public async Task Delete_UnknownId_ReturnsFalse_AndDoesNotRaiseChanged()
    {
        var store = new HomeworkStore(_dir);
        await store.UpsertAsync(new HomeworkItem { MessageId = "mA", Subject = "数学", Content = "A" });
        var events = new List<HomeworkItem>();
        store.Changed += (_, item) => events.Add(item);

        var deleted = await store.DeleteAsync(Guid.NewGuid());

        Assert.False(deleted);
        Assert.Empty(events);
        Assert.Single(await store.GetAllAsync());
    }

    // ---------- 需求 2-2：群发送与结果汇总 ----------

    private static ConnectionSettings Settings(params string[] groups) => new()
    {
        AppId = "111222333",
        AppSecretProtected = "secret-plain",
        ApiBase = "http://test.local/api",
        TokenApiUrl = "http://test.local/token",
        GroupWhitelist = groups
    };

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body),
            System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        public required Func<HttpRequestMessage, string, Task<HttpResponseMessage>> Responder { get; init; }

        public List<(string Path, string? Auth, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri?.PathAndQuery ?? "", request.Headers.Authorization?.ToString(), body));
            return await Responder(request, body);
        }
    }

    private static HomeworkSendService CreateService(
        ConnectionSettings settings, StubHttpHandler handler, Func<bool>? enabled = null)
    {
        return new HomeworkSendService(new HomeworkSendOptionsProvider
        {
            GetSettings = () => settings,
            HttpInvoker = new HttpMessageInvoker(handler),
            GetEnabled = enabled
        });
    }

    [Fact]
    public async Task Send_AggregatesPerGroupResults_AndUsesActiveMessage()
    {
        var handler = new StubHttpHandler
        {
            Responder = (req, _) => Task.FromResult(
                req.RequestUri!.AbsolutePath.EndsWith("/token")
                    ? Json(new { access_token = "tok123", expires_in = "7200" })
                    : req.RequestUri.AbsolutePath.Contains("/groups/G1/")
                        ? Json(new { id = "msg-ok" })
                        : Json(new { code = 11253, message = "不符合发送规范" }, HttpStatusCode.Forbidden))
        };
        var service = CreateService(Settings("G1", "G2"), handler);

        var results = await service.SendTextToWhitelistedGroupsAsync("清单" + HomeworkDigestFormatter.TailNote);

        // 逐群汇总：G1 成功、G2 失败（含平台错误码，不静默）
        Assert.Equal(2, results.Count);
        Assert.Equal("G1", results[0].GroupOpenId);
        Assert.True(results[0].Success);
        Assert.Null(results[0].Error);
        Assert.Equal("G2", results[1].GroupOpenId);
        Assert.False(results[1].Success);
        Assert.Contains("11253", results[1].Error);

        // 主动消息：POST /v2/groups/{gid}/messages，msg_type=0，不带 msg_id，Bearer=QQBot token
        var send = handler.Requests.Where(r => r.Path.Contains("/v2/groups/")).ToList();
        Assert.Equal(2, send.Count);
        Assert.Contains("/api/v2/groups/G2/messages", send[1].Path);
        Assert.Equal("QQBot tok123", send[1].Auth);
        Assert.Contains("\"msg_type\":0", send[1].Body);
        Assert.DoesNotContain("msg_id", send[1].Body);
    }

    [Fact]
    public async Task Send_WithMsgId_SendsPassiveReply()
    {
        var handler = new StubHttpHandler
        {
            Responder = (req, _) => Task.FromResult(
                req.RequestUri!.AbsolutePath.EndsWith("/token")
                    ? Json(new { access_token = "tok123", expires_in = 7200 })
                    : Json(new { id = "ok" }))
        };
        var service = CreateService(Settings("G1"), handler);

        var results = await service.SendTextToWhitelistedGroupsAsync("内容", msgId: "EVENT-MSG-1");

        Assert.True(results.Single().Success);
        var send = handler.Requests.Single(r => r.Path.Contains("/v2/groups/"));
        Assert.Contains("\"msg_id\":\"EVENT-MSG-1\"", send.Body);
    }

    [Fact]
    public async Task Send_EmptyWhitelist_ReturnsSingleFailure()
    {
        var handler = new StubHttpHandler
        {
            Responder = (_, _) => Task.FromResult(Json(new { access_token = "tok123" }))
        };
        var service = CreateService(Settings(), handler);

        var results = await service.SendTextToWhitelistedGroupsAsync("内容");

        var failure = Assert.Single(results);
        Assert.False(failure.Success);
        Assert.Contains("白名单", failure.Error);
        Assert.Empty(handler.Requests); // 未发起任何 HTTP 请求
    }

    [Fact]
    public async Task Send_DisabledBySwitch_ReturnsFailureWithoutSending()
    {
        var handler = new StubHttpHandler
        {
            Responder = (_, _) => Task.FromResult(Json(new { access_token = "tok123" }))
        };
        var service = CreateService(Settings("G1"), handler, enabled: () => false);

        var results = await service.SendTextToWhitelistedGroupsAsync("内容");

        var failure = Assert.Single(results);
        Assert.False(failure.Success);
        Assert.Contains("已关闭", failure.Error);
        Assert.Empty(handler.Requests);
    }

    // ---------- 连接设置 HomeworkSendEnabled 开关接线（默认 true = 现状不变） ----------

    [Fact]
    public void HomeworkSendEnabled_DefaultTrue_AndRoundTripsThroughJson()
    {
        // 默认值：未配置（settings.json 无该字段）时保持现状开启
        Assert.True(new AppSettings().Connection.HomeworkSendEnabled);

        // 序列化往返：false 持久化后仍为 false，true 仍为 true
        var settings = new AppSettings();
        settings.Connection.HomeworkSendEnabled = false;
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.False(restored.Connection.HomeworkSendEnabled);

        settings.Connection.HomeworkSendEnabled = true;
        json = System.Text.Json.JsonSerializer.Serialize(settings);
        restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.True(restored.Connection.HomeworkSendEnabled);
    }

    [Fact]
    public void SendEntryVisibility_HidesWhenServiceMissingOrSwitchOff()
    {
        // 悬浮窗「整理并发送」入口可见性：无服务或开关关闭均隐藏，开启才显示
        Assert.False(HomeworkSuspensionWindow.IsSendEntryVisible(null));
        Assert.False(HomeworkSuspensionWindow.IsSendEntryVisible(new StubSendService(enabled: false)));
        Assert.True(HomeworkSuspensionWindow.IsSendEntryVisible(new StubSendService(enabled: true)));
    }

    private sealed class StubSendService(bool enabled) : IHomeworkSendService
    {
        public bool IsEnabled { get; } = enabled;

        public Task<IReadOnlyList<GroupSendResult>> SendTextToWhitelistedGroupsAsync(
            string content, string? msgId = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GroupSendResult>>([]);
    }
}
