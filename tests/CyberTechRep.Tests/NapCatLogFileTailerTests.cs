using CyberTechRep.Plugin.Services.MessageAccess;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 有头（Framework）形态的日志来源：QQ 进程不写 stdout，NapCat 日志只在安装目录的
/// <c>logs\*.log</c> 里——本组用例覆盖日志文件跟随（增量、半行、轮转、切换）与日志目录探测。
/// </summary>
public sealed class NapCatLogFileTailerTests : IDisposable
{
    private readonly string _dir;

    public NapCatLogFileTailerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "napcat-tail", Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void 日志跟随_首次对齐末尾_之后只读新增行()
    {
        var path = Path.Combine(_dir, "napcat.log");
        File.WriteAllText(path, "历史行1\n历史行2\n");
        using var tailer = new NapCatLogFileTailer();

        Assert.Empty(tailer.Poll(path)); // 首次打开对齐末尾：不把历史日志整篇灌进面板

        File.AppendAllText(path, "新行A\r\n新行B\n");
        var lines = tailer.Poll(path);

        Assert.Equal(new[] { "新行A", "新行B" }, lines);
    }

    [Fact]
    public void 日志跟随_半行留待下次补齐()
    {
        var path = Path.Combine(_dir, "napcat.log");
        File.WriteAllText(path, "");
        using var tailer = new NapCatLogFileTailer();
        Assert.Empty(tailer.Poll(path));

        File.AppendAllText(path, "半行内容");
        Assert.Empty(tailer.Poll(path)); // 未换行：不算完整行

        File.AppendAllText(path, "补齐了\n");
        Assert.Equal(new[] { "半行内容补齐了" }, tailer.Poll(path));
    }

    [Fact]
    public void 日志跟随_文件被截断重建_从头重新读()
    {
        var path = Path.Combine(_dir, "napcat.log");
        File.WriteAllText(path, new string('x', 200) + "\n");
        using var tailer = new NapCatLogFileTailer();
        Assert.Empty(tailer.Poll(path)); // 对齐末尾

        File.WriteAllText(path, "重建后的行\n"); // 长度回退 → 视为轮转，从头读
        var lines = tailer.Poll(path);

        Assert.Equal(new[] { "重建后的行" }, lines);
    }

    [Fact]
    public void 日志跟随_切换文件_从新文件末尾开始()
    {
        var first = Path.Combine(_dir, "a.log");
        var second = Path.Combine(_dir, "b.log");
        File.WriteAllText(first, "a-历史\n");
        File.WriteAllText(second, "b-历史\n");
        using var tailer = new NapCatLogFileTailer();

        tailer.Poll(first);
        Assert.Empty(tailer.Poll(second)); // 切换即对齐新文件末尾

        File.AppendAllText(second, "b-新增\n");
        Assert.Equal(new[] { "b-新增" }, tailer.Poll(second));
        Assert.Equal(second, tailer.CurrentPath);
    }

    [Fact]
    public void 日志跟随_路径为空或文件不存在_返回空且不抛异常()
    {
        using var tailer = new NapCatLogFileTailer();

        Assert.Empty(tailer.Poll(null));
        Assert.Empty(tailer.Poll(""));
        Assert.Empty(tailer.Poll(Path.Combine(_dir, "不存在.log")));
    }

    [Fact]
    public void 查找最新日志文件_优先较新者且覆盖两种目录布局()
    {
        var root = Path.Combine(_dir, "pkg");
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        Directory.CreateDirectory(Path.Combine(root, "napcat", "logs"));
        var older = Path.Combine(root, "logs", "old.log");
        var newer = Path.Combine(root, "napcat", "logs", "new.log");
        File.WriteAllText(older, "a");
        File.WriteAllText(newer, "b");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        Assert.Equal(newer, NapCatRunnerService.FindNewestNapCatLogFile(root));
        // 入口位于 napcat 子目录内时靠「上级目录」候选命中
        Assert.Equal(newer, NapCatRunnerService.FindNewestNapCatLogFile(Path.Combine(root, "napcat")));
    }

    [Fact]
    public void 查找最新日志文件_无日志目录_返回null()
    {
        Assert.Null(NapCatRunnerService.FindNewestNapCatLogFile(Path.Combine(_dir, "空包")));
        Assert.Null(NapCatRunnerService.FindNewestNapCatLogFile(""));
    }
}
