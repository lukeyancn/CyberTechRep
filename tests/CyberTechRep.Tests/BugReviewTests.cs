using System.Net;
using System.Text;
using CyberTechRep.Plugin.Services.Files;
using CyberTechRep.Plugin.Services.MessageAccess;
using CyberTechRep.Plugin.Services.Stores;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 全局逻辑性与缺陷审查（2.1.0-beta.1）的回归测试：只覆盖本次「确定性缺陷最小修复」的 5 处，
/// 每项修复一条对应用例（修复前会失败）：
/// <list type="number">
/// <item>文件管道 FileUpdated 订阅者异常隔离——归档成功不得被误写成下载失败；</item>
/// <item>文件管道复用失败记录时清除上一轮的「可重试」标记——不投递注定失败的重试；</item>
/// <item>通知「标记未读」保留来源展示名（换类继承用），不丢字段；</item>
/// <item><see cref="HomeworkSendService"/> 默认 HTTP 调用器跨次复用（不再每次发送新建 Handler）；</item>
/// <item><see cref="QQOfficialWsClient"/> 默认 HTTP 调用器跨次复用并随释放清除（不再每次重连泄漏连接池）。</item>
/// </list>
/// </summary>
public sealed class BugReviewTests : IDisposable
{
    private readonly string _dir;

    public BugReviewTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "bug-review", Guid.NewGuid().ToString("N"));
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

    // ============ 测试替身 ============

    /// <summary>固定返回一段文本内容的处理器（用于「下载成功」路径）。</summary>
    private sealed class StaticBodyHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(body);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>固定返回指定错误状态码的处理器（用于「瞬时失败」路径）。</summary>
    private sealed class AlwaysStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    /// <summary>不做任何网络访问的空处理器（仅用于取默认调用器身份）。</summary>
    private sealed class UnusedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private FilePipelineService CreateFileService(HttpMessageHandler handler)
    {
        var provider = new FilePipelineOptionsProvider
        {
            GetSettings = () => new FileSettings(),
            DataDirectory = _dir
        };
        // 测试不等退避：重试次数与续传行为按生产逻辑，只把退避压缩为 0
        return new FilePipelineService(provider, new HttpClient(handler)) { RetryBaseDelayMs = 0 };
    }

    // ============ ① 文件管道：订阅者异常隔离 ============

    /// <summary>
    /// 修复前：FileUpdated 订阅者抛异常会冒泡进下载重试循环的 catch-all，把已经
    /// 「归档成功」的记录回写成 Failed（files.json 与实际磁盘状态不一致、悬浮窗看不到文件），
    /// 且后面的订阅者收不到事件。修复后：逐订阅者隔离，主流程与其它订阅者不受影响。
    /// </summary>
    [Fact]
    public async Task 文件管道_订阅者抛异常_归档结果不被回写成失败()
    {
        var payload = Encoding.UTF8.GetBytes("hello-archive-content");
        var pipeline = CreateFileService(new StaticBodyHandler(payload));

        var statuses = new List<FileStatus>();
        pipeline.FileUpdated += (_, _) => throw new InvalidOperationException("订阅者炸了");
        pipeline.FileUpdated += (_, record) => statuses.Add(record.Status);

        var record = await pipeline.EnqueueAsync(
            "m-subscriber-throws", "a.txt", "https://example.com/a.txt", "mem-1", "grp-1");

        Assert.Equal(FileStatus.Archived, record.Status);
        Assert.Null(record.LastError);
        Assert.NotNull(record.ArchivedRelativePath);
        Assert.True(
            File.Exists(Path.Combine(_dir, "下载文件", record.ArchivedRelativePath!)),
            "文件应已真实归档落盘");
        Assert.Contains(FileStatus.Archived, statuses);
    }

    // ============ ② 文件管道：复用失败记录清除「可重试」标记 ============

    /// <summary>
    /// 修复前：复用既有 Failed 记录时不清理上一轮留下的 FailureRetriable=true，本次走
    /// 「永久失败」路径（如缺下载地址）后仍带 true，调用方（消息管道）据此投递注定失败的
    /// FileDownload 重试，反复失败刷屏。修复后：新一轮尝试开始即清除该标记。
    /// </summary>
    [Fact]
    public async Task 文件管道_复用失败记录_缺地址失败不残留可重试标记()
    {
        var handler = new AlwaysStatusHandler(HttpStatusCode.InternalServerError);
        var pipeline = CreateFileService(handler);

        var first = await pipeline.EnqueueAsync(
            "m-retry-flag", "b.txt", "https://example.com/b.txt", "mem-1", "grp-1");
        Assert.Equal(FileStatus.Failed, first.Status);
        Assert.True(first.FailureRetriable, "HTTP 500 属可重试类别");

        // 同一 MessageId + 文件名 → 复用该 Failed 记录；本次缺下载地址 = 永久失败
        var second = await pipeline.EnqueueAsync("m-retry-flag", "b.txt", url: null, "mem-1", "grp-1");

        Assert.Same(first, second);
        Assert.Equal(FileStatus.Failed, second.Status);
        Assert.False(second.FailureRetriable, "缺下载地址不可重试，不得残留上一轮的可重试标记");
    }

    // ============ ③ 通知存储：标记未读保留来源展示名 ============

    /// <summary>
    /// 修复前：MarkUnreadAsync 手工重建 NoticeItem 时漏拷 SenderLabel，字段被清空并落盘
    /// （改用例：通知换类到作业后「来源」显示「成员」，无法追溯发送者）。修复后：字段保留。
    /// </summary>
    [Fact]
    public async Task 通知存储_标记未读保留来源展示名()
    {
        var store = new NoticeStore(_dir);
        var item = await store.AddOrUpdateWithSourceAsync(
            "m-sender-label", "语文：明天交作文", "mem-1", "grp-1", DateTimeOffset.Now,
            senderLabel: "张老师");
        Assert.Equal("张老师", item.SenderLabel);

        await store.MarkReadAsync(item.Id);
        await store.MarkUnreadAsync(item.Id);

        var unread = Assert.Single(await store.GetUnreadAsync());
        Assert.False(unread.IsRead);
        Assert.Null(unread.ReadAt);
        Assert.Equal("张老师", unread.SenderLabel);

        // 持久化后仍保留（重启/换类继承路径都读该字段）
        var reloaded = new NoticeStore(_dir);
        Assert.Equal("张老师", Assert.Single(await reloaded.GetUnreadAsync()).SenderLabel);
    }

    // ============ ④ 发送服务：默认 HTTP 调用器跨次复用 ============

    /// <summary>
    /// 修复前：每次发送/取 AccessToken 都新建 HttpMessageInvoker + SocketsHttpHandler，
    /// 连接池与套接字随每次「整理并发送」泄漏（从不释放也不复用）。
    /// 修复后：未注入时复用同一默认调用器；注入时仍以注入者为准。
    /// </summary>
    [Fact]
    public void 发送服务_默认HTTP调用器跨次复用()
    {
        var settings = new ConnectionSettings { AppId = "app", AppSecretProtected = "secret" };
        var service = new HomeworkSendService(new HomeworkSendOptionsProvider
        {
            GetSettings = () => settings
        });

        var first = service.ResolveInvoker();
        var second = service.ResolveInvoker();
        Assert.Same(first, second);

        var injected = new HttpMessageInvoker(new UnusedHandler());
        var withInjected = new HomeworkSendService(new HomeworkSendOptionsProvider
        {
            GetSettings = () => settings,
            HttpInvoker = injected
        });
        Assert.Same(injected, withInjected.ResolveInvoker());
    }

    // ============ ⑤ 官方 WS 客户端：默认 HTTP 调用器跨次复用并随释放清除 ============

    /// <summary>
    /// 修复前：每次重连的 AccessToken / 网关两个 HTTP 调用都新建 Invoker+SocketsHttpHandler，
    /// 且 Dispose 时也不释放——反复重连（弱网/长时间运行）会持续泄漏连接池与套接字。
    /// 修复后：同一客户端内复用，DisposeAsync 释放并清空（再次使用时重建）。
    /// </summary>
    [Fact]
    public async Task 官方WS客户端_默认HTTP调用器跨次复用并随释放清除()
    {
        var client = new QQOfficialWsClient(new QQOfficialWsClientOptions
        {
            AppId = "app",
            AppSecretPlain = "secret"
        });

        var first = client.ResolveInvoker();
        Assert.Same(first, client.ResolveInvoker());

        await client.DisposeAsync();

        var afterDispose = client.ResolveInvoker();
        Assert.NotSame(first, afterDispose);
        afterDispose.Dispose();
    }
}
