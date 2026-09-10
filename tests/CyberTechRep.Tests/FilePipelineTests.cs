using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using CyberTechRep.Plugin.Services.Files;
using CyberTechRep.Shared.Models;
using Xunit;
using Xunit.Abstractions;

namespace CyberTechRep.Tests;

/// <summary>模块 4 单元测试：文件处理管道（原子下载 / MD5 去重 / 路径安全 / 大小与磁盘上限 / 学科二次归档）。</summary>
public class FilePipelineTests : IDisposable
{
    private readonly string _dataDir;
    private readonly string _root;
    private readonly ITestOutputHelper _output;

    public FilePipelineTests(ITestOutputHelper output)
    {
        _output = output;
        _dataDir = Path.Combine(Path.GetTempPath(), "classing-tests", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_dataDir, "下载文件");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataDir))
            {
                Directory.Delete(_dataDir, recursive: true);
            }
        }
        catch
        {
            // 测试清理失败忽略
        }
    }

    // ---------- 测试替身 ----------

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpContent> _responses = new(StringComparer.Ordinal);

        public void Map(string url, HttpContent content) => _responses[url] = content;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? "";
            if (!_responses.TryGetValue(url, out var content))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>先输出部分字节再抛 IO 异常的流（模拟下载中断）。</summary>
    private sealed class PartialThenThrowStream(byte[] prefix) : Stream
    {
        private bool _prefixRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => prefix.Length;
        public override long Position { get => 0; set { } }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_prefixRead)
            {
                _prefixRead = true;
                var n = Math.Min(count, prefix.Length);
                Array.Copy(prefix, 0, buffer, offset, n);
                return n;
            }

            throw new IOException("模拟网络中断");
        }

        public override void Flush() { }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer, offset, count));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>每次请求都先吐 <c>prefix</c> 字节再断流的处理器（模拟持续中断的下载源）。</summary>
    private sealed class AlwaysInterruptedHandler(byte[] prefix) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var content = new StreamContent(new PartialThenThrowStream(prefix));
            content.Headers.ContentLength = prefix.Length * 2; // 声明得比实际能给的更多 → 中断即不完整
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>首次请求中断、后续请求按 Range 返回剩余字节的处理器（模拟支持续传的服务器）。</summary>
    private sealed class InterruptThenRangeHandler(byte[] payload, int breakAfter) : HttpMessageHandler
    {
        public List<long?> RequestRanges { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
            RequestRanges.Add(from);

            if (from is null)
            {
                var first = new StreamContent(new PartialThenThrowStream(payload[..breakAfter]));
                first.Headers.ContentLength = payload.Length; // 声明完整长度、实际只给一半 → 中断
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = first });
            }

            var start = (int)from.Value;
            var rest = new ByteArrayContent(payload[start..]);
            rest.Headers.ContentRange = new ContentRangeHeaderValue(start, payload.Length - 1, payload.Length);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = rest });
        }
    }

    /// <summary>首次请求中断、后续请求忽略 Range 回完整内容的处理器（模拟不支持续传的服务器）。</summary>
    private sealed class InterruptThenFullHandler(byte[] payload, int breakAfter) : HttpMessageHandler
    {
        public List<long?> RequestRanges { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
            RequestRanges.Add(from);

            if (from is null)
            {
                var first = new StreamContent(new PartialThenThrowStream(payload[..breakAfter]));
                first.Headers.ContentLength = payload.Length;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = first });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
        }
    }

    /// <summary>固定返回某个 HTTP 状态码的处理器（模拟 403 直链过期等永久失败）。</summary>
    private sealed class StatusCodeHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("forbidden")
            });
        }
    }

    // ---------- 构造辅助 ----------
    private FilePipelineService CreateService(FileSettings? settings = null, HttpMessageHandler? handler = null)
    {
        settings ??= new FileSettings();
        var provider = new FilePipelineOptionsProvider
        {
            GetSettings = () => settings,
            DataDirectory = _dataDir
        };
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        // 测试不等退避：重试次数与续传行为按生产逻辑，只把退避压缩为 0
        return new FilePipelineService(provider, http) { RetryBaseDelayMs = 0 };
    }

    private string[] FilesUnderRoot() =>
        Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).ToArray()
            : [];

    private static byte[] Payload(string text) => Encoding.UTF8.GetBytes(text);

    private static string Md5Hex(byte[] bytes) => Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();

    // ---------- 用例 ----------

    [Fact]
    public async Task EnqueueAsync_DownloadsAndArchivesToSubjectDateFolder()
    {
        var handler = new FakeHandler();
        var content = Payload("hello classing");
        handler.Map("https://example.com/attach/1", new ByteArrayContent(content));
        var svc = CreateService(handler: handler);

        var record = await svc.EnqueueAsync("m1", "作业说明.txt", "https://example.com/attach/1");

        Assert.Equal(FileStatus.Archived, record.Status);
        Assert.Null(record.LastError);
        Assert.Equal(content.Length, record.Size);
        Assert.Equal(Md5Hex(content), record.Md5);
        Assert.NotNull(record.CompletedAt);

        var expected = Path.Combine(_root, "未分类", DateTime.Now.ToString("yyyy-MM-dd"), "作业说明.txt");
        Assert.True(File.Exists(expected), $"归档文件应存在：{expected}");
        Assert.Equal(content, await File.ReadAllBytesAsync(expected));
        Assert.Equal(Path.GetRelativePath(_root, expected), record.ArchivedRelativePath);

        // 无半成品：不残留 .tmp 临时文件
        Assert.Empty(FilesUnderRoot().Where(f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task EnqueueAsync_Md5Dedup_DuplicateStatusAndSingleFileOnDisk()
    {
        var handler = new FakeHandler();
        var content = Payload("same content");
        handler.Map("https://example.com/attach/a", new ByteArrayContent(content));
        handler.Map("https://example.com/attach/b", new ByteArrayContent(content));
        var svc = CreateService(handler: handler);

        var first = await svc.EnqueueAsync("m1", "a.txt", "https://example.com/attach/a");
        var second = await svc.EnqueueAsync("m2", "b.txt", "https://example.com/attach/b");

        Assert.Equal(FileStatus.Archived, first.Status);
        Assert.Equal(FileStatus.Duplicate, second.Status);
        Assert.Equal(Md5Hex(content), second.Md5);
        Assert.Null(second.ArchivedRelativePath); // 重复文件未归档
        Assert.Single(FilesUnderRoot()); // 磁盘仅保留一份物理文件
    }

    [Theory]
    [InlineData(@"..\..\evil.txt")]
    [InlineData("a/b.txt")]
    [InlineData("..")]
    [InlineData("CON")]
    [InlineData("aux.txt")]
    [InlineData("com1.log")]
    [InlineData("名字?非法.txt")]
    public async Task EnqueueAsync_UnsafeFileName_RejectedWithoutAnyFile(string unsafeName)
    {
        var handler = new FakeHandler();
        handler.Map("https://example.com/attach/x", new ByteArrayContent(Payload("data")));
        var svc = CreateService(handler: handler);

        var record = await svc.EnqueueAsync("m1", unsafeName, "https://example.com/attach/x");

        Assert.Equal(FileStatus.Failed, record.Status);
        Assert.False(string.IsNullOrWhiteSpace(record.LastError));
        Assert.Empty(FilesUnderRoot()); // 未产生任何文件（含临时文件）
    }

    [Fact]
    public async Task EnqueueAsync_TooLongFileName_Rejected()
    {
        var handler = new FakeHandler();
        handler.Map("https://example.com/attach/x", new ByteArrayContent(Payload("data")));
        var settings = new FileSettings { MaxFileNameLength = 200 };
        var svc = CreateService(settings, handler);

        var longName = new string('长', 201) + ".txt";
        var record = await svc.EnqueueAsync("m1", longName, "https://example.com/attach/x");

        Assert.Equal(FileStatus.Failed, record.Status);
        Assert.Contains("上限", record.LastError);
        Assert.Empty(FilesUnderRoot());
    }

    [Fact]
    public async Task EnqueueAsync_DownloadInterrupted_RetriesThenFailsWithoutLeftovers()
    {
        // 持续中断的下载源：三次尝试（DownloadMaxAttempts）都用尽后落 Failed，
        // 永久失败时清理半成品（不留残渣）；每次尝试计一次 AttemptCount。
        var handler = new AlwaysInterruptedHandler([1, 2, 3, 4, 5]);
        var svc = CreateService(handler: handler);

        var record = await svc.EnqueueAsync("m1", "中断.txt", "https://example.com/attach/broken");

        Assert.Equal(FileStatus.Failed, record.Status);
        Assert.NotNull(record.LastError);
        Assert.Equal(3, record.AttemptCount);
        Assert.Equal(3, handler.RequestCount);
        Assert.True(record.FailureRetriable); // 瞬时类失败：调用方据此投递重试队列
        Assert.Null(record.ArchivedRelativePath);
        Assert.Empty(FilesUnderRoot()); // 无半成品归档，也无残留临时文件
    }

    [Fact]
    public async Task EnqueueAsync_HttpForbidden_IsPermanentAndNotRetried()
    {
        // 直链过期/被拒（403）：重试没有意义——不重试（一次尝试即落 Failed），且标记为不可重试
        var handler = new StatusCodeHandler(HttpStatusCode.Forbidden);
        var svc = CreateService(handler: handler);

        var record = await svc.EnqueueAsync("m1", "过期.txt", "https://example.com/attach/expired");

        Assert.Equal(FileStatus.Failed, record.Status);
        Assert.Equal(1, record.AttemptCount);
        Assert.Equal(1, handler.RequestCount);
        Assert.False(record.FailureRetriable);
        Assert.Contains("过期", record.LastError);
    }

    [Fact]
    public async Task EnqueueAsync_DownloadInterrupted_RetriesWithRangeAndArchivesCompleteFile()
    {
        // 断点续传：首次中断后重试用 Range 从断点继续，最终归档内容与 MD5 必须是完整文件的
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("班级作业清单内容-片段", 64)));
        var handler = new InterruptThenRangeHandler(payload, breakAfter: 200);
        var svc = CreateService(handler: handler);

        var record = await svc.EnqueueAsync("m1", "续传.txt", "https://example.com/attach/resume");

        Assert.Equal(FileStatus.Archived, record.Status);
        Assert.Equal(Md5Hex(payload), record.Md5);
        Assert.Equal(payload.Length, record.Size);
        Assert.Equal(1, record.AttemptCount); // 第二次尝试即成功
        Assert.False(record.FailureRetriable); // 成功即清除可重试标记
        Assert.Equal(2, handler.RequestRanges.Count);
        Assert.Null(handler.RequestRanges[0]);       // 首次：无 Range
        Assert.Equal(200, handler.RequestRanges[1]); // 重试：从断点 200 字节处续传

        var archived = Path.Combine(_root, "未分类", DateTime.Now.ToString("yyyy-MM-dd"), "续传.txt");
        Assert.Equal(payload, await File.ReadAllBytesAsync(archived));
        Assert.Empty(FilesUnderRoot().Where(f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task EnqueueAsync_ServerIgnoresRange_RestartsFromScratchWithoutCorruption()
    {
        // 服务器不支持续传（对 Range 回 200 完整内容）：必须从头下（清空半成品），
        // 归档内容不得出现「半成品 + 完整内容」拼接造成的损坏
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("不支持续传的服务端-内容", 48)));
        var handler = new InterruptThenFullHandler(payload, breakAfter: 128);
        var svc = CreateService(handler: handler);

        var record = await svc.EnqueueAsync("m1", "重下.txt", "https://example.com/attach/no-range");

        Assert.Equal(FileStatus.Archived, record.Status);
        Assert.Equal(Md5Hex(payload), record.Md5);
        Assert.Equal(payload.Length, record.Size);
        Assert.Equal(2, handler.RequestRanges.Count);
        Assert.NotNull(handler.RequestRanges[1]); // 我们请求了续传，服务端却回了完整内容

        var archived = Path.Combine(_root, "未分类", DateTime.Now.ToString("yyyy-MM-dd"), "重下.txt");
        Assert.Equal(payload, await File.ReadAllBytesAsync(archived));
    }

    [Fact]
    public async Task EnqueueAsync_FileSizeExceeded_RejectedBeforeDownload()
    {
        var handler = new FakeHandler();
        handler.Map("https://example.com/attach/big", new ByteArrayContent(new byte[1024 * 1024 + 1]));
        var settings = new FileSettings { MaxFileSizeMb = 1 };
        var svc = CreateService(settings, handler);

        var record = await svc.EnqueueAsync("m1", "大文件.bin", "https://example.com/attach/big");

        Assert.Equal(FileStatus.Failed, record.Status);
        Assert.NotNull(record.LastError);
        Assert.Null(record.ArchivedRelativePath);
        Assert.Empty(FilesUnderRoot());
    }

    [Fact]
    public async Task ReassignSubject_MovesFromUnfiledToSubject_AndIsIdempotent()
    {
        var handler = new FakeHandler();
        var content = Payload("math homework");
        handler.Map("https://example.com/attach/1", new ByteArrayContent(content));
        var svc = CreateService(handler: handler);

        var record = await svc.EnqueueAsync("m1", "作业.txt", "https://example.com/attach/1");
        Assert.Equal(FileStatus.Archived, record.Status);
        var unfiledPath = Path.Combine(_root, record.ArchivedRelativePath!);

        await svc.ReassignSubjectAsync(record.Id, "数学");

        var moved = (await svc.GetRecordsAsync()).Single(r => r.Id == record.Id);
        var target = Path.Combine(_root, "数学", DateTime.Now.ToString("yyyy-MM-dd"), "作业.txt");
        Assert.True(File.Exists(target), $"学科目录文件应存在：{target}");
        Assert.False(File.Exists(unfiledPath), "未分类目录原文件应已移动");
        Assert.Equal(Path.GetRelativePath(_root, target), moved.ArchivedRelativePath);

        // 幂等：重复执行无变化（不移动、不产生副本）
        await svc.ReassignSubjectAsync(record.Id, "数学");
        var again = (await svc.GetRecordsAsync()).Single(r => r.Id == record.Id);
        Assert.Equal(moved.ArchivedRelativePath, again.ArchivedRelativePath);
        Assert.Single(FilesUnderRoot());
        Assert.Equal(content, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task DiskLimit_PolicyStop_RejectsNewFileAndKeepsExisting()
    {
        Directory.CreateDirectory(_root);
        var oldFile = Path.Combine(_root, "old.bin");
        File.WriteAllBytes(oldFile, new byte[1024 * 1024]); // 恰好 1 MB
        var settings = new FileSettings { MaxDiskUsageMb = 1, CleanupPolicy = 0 };
        var handler = new FakeHandler();
        handler.Map("https://example.com/attach/new", new ByteArrayContent(Payload("new")));
        var svc = CreateService(settings, handler);

        var record = await svc.EnqueueAsync("m1", "new.txt", "https://example.com/attach/new");

        Assert.Equal(FileStatus.Failed, record.Status);
        Assert.Contains("磁盘", record.LastError);
        Assert.True(File.Exists(oldFile), "策略 0 不应删除既有文件");
        Assert.Null(record.ArchivedRelativePath);
        Assert.Single(FilesUnderRoot());
    }

    [Fact]
    public async Task DiskLimit_PolicyCleanupOldest_DeletesOldestAndArchivesNewFile()
    {
        Directory.CreateDirectory(_root);
        var oldFile = Path.Combine(_root, "old.bin");
        File.WriteAllBytes(oldFile, new byte[1024 * 1024]); // 恰好 1 MB
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow - TimeSpan.FromDays(1));
        var settings = new FileSettings { MaxDiskUsageMb = 1, CleanupPolicy = 1 };
        var handler = new FakeHandler();
        var content = Payload("new");
        handler.Map("https://example.com/attach/new", new ByteArrayContent(content));
        var svc = CreateService(settings, handler);

        var record = await svc.EnqueueAsync("m1", "new.txt", "https://example.com/attach/new");

        Assert.Equal(FileStatus.Archived, record.Status);
        Assert.False(File.Exists(oldFile), "最旧文件应被清理策略删除");
        var target = Path.Combine(_root, record.ArchivedRelativePath!);
        Assert.True(File.Exists(target));
        Assert.Equal(content, await File.ReadAllBytesAsync(target));
    }
}
