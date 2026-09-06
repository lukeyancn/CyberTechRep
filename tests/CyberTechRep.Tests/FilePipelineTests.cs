using System.Net;
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

    // ---------- 构造辅助 ----------

    private FilePipelineService CreateService(FileSettings? settings = null, FakeHandler? handler = null)
    {
        settings ??= new FileSettings();
        var provider = new FilePipelineOptionsProvider
        {
            GetSettings = () => settings,
            DataDirectory = _dataDir
        };
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        return new FilePipelineService(provider, http);
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
    public async Task EnqueueAsync_DownloadInterrupted_NoPartialFileArchived()
    {
        var handler = new FakeHandler();
        handler.Map("https://example.com/attach/broken",
            new StreamContent(new PartialThenThrowStream([1, 2, 3, 4, 5])));
        var svc = CreateService(handler: handler);

        var record = await svc.EnqueueAsync("m1", "中断.txt", "https://example.com/attach/broken");

        Assert.Equal(FileStatus.Failed, record.Status);
        Assert.NotNull(record.LastError);
        Assert.Equal(1, record.AttemptCount);
        Assert.Null(record.ArchivedRelativePath);
        Assert.Empty(FilesUnderRoot()); // 无半成品归档，也无残留临时文件
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
