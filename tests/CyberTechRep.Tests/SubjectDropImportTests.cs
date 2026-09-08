using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using CyberTechRep.Plugin.Services.Files;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Shared.Models;
using Xunit;
using Xunit.Abstractions;

namespace CyberTechRep.Tests;

/// <summary>
/// 拖放导入单元测试：
/// ① 负载路径提取（DataFormats.Files 原生格式 + FileNames 文本回退 + 非文件负载）；
/// ② 同名冲突后缀 name (2).ext 顺延策略；
/// ③ Copy 语义回归——源文件仍在、目标存在且字节数一致（真实 FilePipelineService 落盘验证）。
/// </summary>
public class SubjectDropImportTests : IDisposable
{
    private readonly string _dataDir;
    private readonly string _root;
    private readonly string _sourceDir;
    private readonly ITestOutputHelper _output;

    public SubjectDropImportTests(ITestOutputHelper output)
    {
        _output = output;
        _dataDir = Path.Combine(Path.GetTempPath(), "classing-tests", "drop-import", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_dataDir, "下载文件");
        _sourceDir = Path.Combine(_dataDir, "src");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_sourceDir);
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

    // ---------- 构造辅助 ----------

    private FilePipelineService CreateService(FileSettings? settings = null)
    {
        settings ??= new FileSettings();
        var provider = new FilePipelineOptionsProvider
        {
            GetSettings = () => settings,
            DataDirectory = _dataDir
        };
        return new FilePipelineService(provider, new HttpClient());
    }

    private string WriteSource(string fileName, byte[] bytes)
    {
        var path = Path.Combine(_sourceDir, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Payload(string text) => Encoding.UTF8.GetBytes(text);

    // ---------- ① 负载路径提取 ----------

    /// <summary>
    /// IStorageItem 运行时代理（该接口被 Avalonia 标记 [NotClientImplementable]，禁止编译期实现）：
    /// 只需携带本地 file:// 路径即可模拟资源管理器原生拖放的 Files 格式条目。
    /// </summary>
    private class StorageItemProxy : DispatchProxy
    {
        private Uri _path = null!;

        public static IStorageItem Create(Uri path)
        {
            var proxy = Create<IStorageItem, StorageItemProxy>()!;
            ((StorageItemProxy)(object)proxy)._path = path;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_Path": return _path;
                case "get_Name": return System.IO.Path.GetFileName(_path.LocalPath);
                case "get_CanBookmark": return false;
                case "GetBasicPropertiesAsync": return Task.FromResult(new StorageItemProperties());
                case "SaveBookmarkAsync": return Task.FromResult<string?>(null);
                case "GetParentAsync": return Task.FromResult<IStorageFolder?>(null);
                case "DeleteAsync": return Task.CompletedTask;
                case "MoveAsync": return Task.FromResult<IStorageItem?>(null);
                default: return null;
            }
        }
    }

    [Fact]
    public Task Extract_NativeFilesFormat_ReturnsAllPaths()
    {
        // 资源管理器原生拖放：DataFormats.Files（IStorageItem，取本地路径）
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var data = new DataObject();
            data.Set(DataFormats.Files, new List<IStorageItem>
            {
                StorageItemProxy.Create(new Uri(@"file:///C:/a.txt")),
                StorageItemProxy.Create(new Uri(@"file:///D:/报告.docx"))
            });

            var paths = SubjectDropImport.ExtractFilePaths(data);

            Assert.Equal(2, paths.Count);
            Assert.Contains(@"C:\a.txt", paths);
            Assert.Contains(@"D:\报告.docx", paths);
            Assert.True(SubjectDropImport.HasFiles(data));
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public Task Extract_FileNamesListFallback_ReturnsPaths()
    {
        // 无原生 Files 格式时的 FileNames 列表负载（浏览器/压缩包工具常见降级）
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var data = new DataObject();
            data.Set(DataFormats.FileNames, new List<string> { @"C:\x.zip", @"C:\y.pdf" });

            Assert.False(data.Contains(DataFormats.Files));
            var paths = SubjectDropImport.ExtractFilePaths(data);

            Assert.Equal(2, paths.Count);
            Assert.Contains(@"C:\x.zip", paths);
            Assert.Contains(@"C:\y.pdf", paths);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public Task Extract_FileNamesTextFallback_ReturnsPaths()
    {
        // FileNames 以多行文本形式携带路径的变体
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var data = new DataObject();
            data.Set(DataFormats.FileNames, "C:\\x.zip\r\nC:\\y.pdf");

            var paths = SubjectDropImport.ExtractFilePaths(data);

            Assert.Equal(2, paths.Count);
            Assert.Contains("C:\\x.zip", paths);
            Assert.Contains("C:\\y.pdf", paths);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public Task Extract_NonFilePayload_ReturnsEmptyWithoutThrowing()
    {
        // 纯文本/位图等非文件负载：返回空列表（不抛异常），调用方据此展示非致命提示
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var text = new DataObject();
            text.Set(DataFormats.Text, "hello");

            Assert.Empty(SubjectDropImport.ExtractFilePaths(text));
            Assert.Empty(SubjectDropImport.ExtractFilePaths(null));
            Assert.False(SubjectDropImport.HasFiles(text));
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    // ---------- ② 同名冲突后缀策略 ----------

    [Theory]
    [InlineData("报告.txt", 2, "报告 (2).txt")]
    [InlineData("报告.txt", 3, "报告 (3).txt")]
    [InlineData("档案", 2, "档案 (2)")]
    [InlineData("a.b.c", 2, "a.b (2).c")]
    public void WithConflictSuffix_AppendsSpaceAndNumber(string name, int n, string expected)
    {
        Assert.Equal(expected, SubjectDropImport.WithConflictSuffix(name, n));
    }

    [Fact]
    public void ResolveAvailableName_FreeNameReturnedAsIs()
    {
        var exists = new Func<string, bool>(_ => false);
        Assert.Equal("作业.txt", SubjectDropImport.ResolveAvailableName(@"X:\dir", "作业.txt", exists));
    }

    [Fact]
    public void ResolveAvailableName_ConflictAppendsSuffix_SequentialWhenSuffixTaken()
    {
        // name (2).ext 已存在时继续顺延到 name (3).ext
        var taken = new HashSet<string> { @"X:\dir\作业.txt", @"X:\dir\作业 (2).txt" };
        Assert.Equal("作业 (3).txt",
            SubjectDropImport.ResolveAvailableName(@"X:\dir", "作业.txt", taken.Contains));
    }

    [Fact]
    public void ResolveAvailableName_NoExtensionFile_TakesSuffixWithoutDot()
    {
        var taken = new HashSet<string> { @"X:\dir\Makefile" };
        Assert.Equal("Makefile (2)",
            SubjectDropImport.ResolveAvailableName(@"X:\dir", "Makefile", taken.Contains));
    }

    // ---------- ③ Copy 语义（真实落盘）----------

    [Fact]
    public async Task ImportAsync_SourceRemains_TargetHasSameBytes()
    {
        var content = Payload("拖放导入的作业内容");
        var source = WriteSource("作业说明.txt", content);
        var svc = CreateService();

        var result = await svc.ImportAsync(source, "数学");

        Assert.Equal(SubjectDropImportOutcome.Imported, result.Outcome);
        Assert.NotNull(result.Record);
        Assert.Equal(FileStatus.Archived, result.Record!.Status);

        // Copy 语义断言：源文件仍在原位、内容不变
        Assert.True(File.Exists(source), "源文件必须保留（Copy 而非 Move）");
        Assert.Equal(content, await File.ReadAllBytesAsync(source));

        // 目标按既有归档布局落位：下载文件/<学科>/<yyyy-MM-dd>/<文件名>，字节数一致
        var target = Path.Combine(_root, "数学", DateTime.Now.ToString("yyyy-MM-dd"), "作业说明.txt");
        Assert.True(File.Exists(target), $"目标副本应存在：{target}");
        Assert.Equal(content, await File.ReadAllBytesAsync(target));
        Assert.Equal(Path.GetRelativePath(_root, target), result.Record.ArchivedRelativePath);
        Assert.Equal(content.Length, result.Record.Size);
    }

    [Fact]
    public async Task ImportAsync_SameNameTwice_SecondGetsConflictSuffix_BothSourcesRemain()
    {
        var first = WriteSource("作业.txt", Payload("版本一"));
        var second = WriteSource("second.txt", Payload("版本二"));
        var svc = CreateService();

        var r1 = await svc.ImportAsync(first, "语文");
        // 同名内容不同：FileSettings.Md5DedupEnabled 默认 true，内容不同不会被去重拦截
        File.Copy(second, Path.Combine(_sourceDir, "作业.txt"), overwrite: true);

        var r2 = await svc.ImportAsync(Path.Combine(_sourceDir, "作业.txt"), "语文");

        Assert.Equal(SubjectDropImportOutcome.Imported, r1.Outcome);
        Assert.Equal(SubjectDropImportOutcome.Imported, r2.Outcome);
        Assert.Equal("作业 (2).txt", r2.Record!.FileName);

        var day = DateTime.Now.ToString("yyyy-MM-dd");
        Assert.True(File.Exists(Path.Combine(_root, "语文", day, "作业.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "语文", day, "作业 (2).txt")), "同名文件应顺延改名而非覆盖");

        // 两个源文件都在（Copy 语义；second.txt 是源头副本）
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task ImportAsync_IdenticalContentTwice_SecondIsDuplicateByMd5()
    {
        var content = Payload("same");
        var source = WriteSource("same.txt", content);
        var svc = CreateService();

        var r1 = await svc.ImportAsync(source, "英语");
        var r2 = await svc.ImportAsync(source, "英语");

        Assert.Equal(SubjectDropImportOutcome.Imported, r1.Outcome);
        Assert.Equal(SubjectDropImportOutcome.Duplicate, r2.Outcome);
        Assert.Single(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
        Assert.True(File.Exists(source), "去重跳过也必须保留源文件");
    }

    [Fact]
    public async Task ImportAsync_ZeroByteOrMissingSource_SkippedWithoutSideEffects()
    {
        var empty = WriteSource("empty.txt", []);
        var svc = CreateService();

        var zero = await svc.ImportAsync(empty, "数学");
        var missing = await svc.ImportAsync(Path.Combine(_sourceDir, "不存在.txt"), "数学");

        Assert.Equal(SubjectDropImportOutcome.Skipped, zero.Outcome);
        Assert.Equal(SubjectDropImportOutcome.Skipped, missing.Outcome);
        Assert.Empty(Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            : Enumerable.Empty<string>()); // 跳过场景不得在归档库产生任何文件
        Assert.True(File.Exists(empty));
    }

    [Fact]
    public async Task ImportAsync_SourceInsideArchiveRoot_SkippedToAvoidSelfCopy()
    {
        var svc = CreateService();
        Directory.CreateDirectory(Path.Combine(_root, "数学", DateTime.Now.ToString("yyyy-MM-dd")));
        var inside = Path.Combine(_root, "数学", DateTime.Now.ToString("yyyy-MM-dd"), "already.txt");
        File.WriteAllBytes(inside, Payload("x"));

        var result = await svc.ImportAsync(inside, "语文");

        Assert.Equal(SubjectDropImportOutcome.Skipped, result.Outcome);
        Assert.Equal(1, Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task ImportAsync_RecordVisibleViaGetRecords_AndSubjectSegmentResolved()
    {
        var source = WriteSource("历史作业.txt", Payload("历史"));
        var svc = CreateService();

        var result = await svc.ImportAsync(source, "历史");

        var records = await svc.GetRecordsAsync();
        var record = records.Single(r => r.Id == result.Record!.Id);
        Assert.Equal(FileStatus.Archived, record.Status);
        // SubjectFilesQuery.ExtractSubject 以相对路径首段归属学科 → 悬浮窗/圆圈栏可见
        Assert.Equal("历史", SubjectFilesQuery.ExtractSubject(record.ArchivedRelativePath));
    }
}
