using CyberTechRep.Plugin.Services.MessageAccess;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// NapCat 后台日志环形缓冲测试：容量钳制/回退、换行拆分、序号单调、快照隔离、
/// 清空重置、超容量丢弃计数与订阅者异常隔离（读取线程安全边界）。
/// </summary>
public class NapCatLogBufferTests
{
    // ================= 换行拆分 =================

    [Fact]
    public void 追加_按CRLF_LF_CR拆分_跳过空行()
    {
        var buffer = new NapCatLogBuffer();

        buffer.Append("a\r\nb\nc\rd\r\n\r\n\n  \r\n", NapCatLogStream.StdOut);

        var lines = buffer.Snapshot();
        Assert.Equal(["a", "b", "c", "d"], lines.Select(l => l.Text));
        Assert.All(lines, l => Assert.Equal(NapCatLogStream.StdOut, l.Stream));
    }

    [Fact]
    public void 追加_逐行去除首尾空白()
    {
        var buffer = new NapCatLogBuffer();

        buffer.Append("  前后有空白  \r\n\t制表符\t", NapCatLogStream.StdErr);

        var lines = buffer.Snapshot();
        Assert.Equal(["前后有空白", "制表符"], lines.Select(l => l.Text));
        Assert.All(lines, l => Assert.Equal(NapCatLogStream.StdErr, l.Stream));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\n\r")]
    public void 追加_空内容_不产生行也不抛异常(string? text)
    {
        var buffer = new NapCatLogBuffer();

        var ex = Record.Exception(() => buffer.Append(text, NapCatLogStream.System));

        Assert.Null(ex);
        Assert.Empty(buffer.Snapshot());
        Assert.Equal(0, buffer.LastSequence);
    }

    // ================= 序号单调 =================

    [Fact]
    public void 序号_单调递增且空行不占号()
    {
        var buffer = new NapCatLogBuffer();

        buffer.Append("a\r\n\r\nb", NapCatLogStream.StdOut);
        buffer.Append("c", NapCatLogStream.StdOut);

        var lines = buffer.Snapshot();
        Assert.Equal([1L, 2L, 3L], lines.Select(l => l.Sequence));
        Assert.Equal(3, buffer.LastSequence);
    }

    [Fact]
    public void 清空后追加_序号继续递增不回退()
    {
        var buffer = new NapCatLogBuffer();
        buffer.Append("a\r\nb", NapCatLogStream.StdOut);
        Assert.Equal(2, buffer.LastSequence);

        buffer.Clear();
        buffer.Append("c", NapCatLogStream.StdOut);

        // 序号不回退：UI 侧「最新序号变化才刷新」的判断不会因清空而失效
        var line = Assert.Single(buffer.Snapshot());
        Assert.Equal(3, line.Sequence);
    }

    // ================= 环形边界与丢弃计数 =================

    [Fact]
    public void 超容量_丢弃最旧行并累计丢弃数()
    {
        var buffer = new NapCatLogBuffer(() => 100);

        for (var i = 1; i <= 150; i++)
        {
            buffer.Append($"line {i}", NapCatLogStream.StdOut);
        }

        var lines = buffer.Snapshot();
        Assert.Equal(100, lines.Count);
        Assert.Equal("line 51", lines[0].Text);
        Assert.Equal("line 150", lines[^1].Text);
        Assert.Equal(50, buffer.DroppedCount);
    }

    [Fact]
    public void 快照_最旧到最新_且不受后续追加影响()
    {
        var buffer = new NapCatLogBuffer();
        buffer.Append("first\r\nsecond", NapCatLogStream.StdOut);

        var snapshot = buffer.Snapshot();
        buffer.Append("third", NapCatLogStream.StdOut);

        Assert.Equal(["first", "second"], snapshot.Select(l => l.Text));
        Assert.Equal(["first", "second", "third"], buffer.Snapshot().Select(l => l.Text));
    }

    [Fact]
    public void 清空_重置行与丢弃计数()
    {
        var buffer = new NapCatLogBuffer(() => 100);
        for (var i = 0; i < 120; i++)
        {
            buffer.Append($"line {i}", NapCatLogStream.StdOut);
        }

        Assert.Equal(20, buffer.DroppedCount);

        buffer.Clear();

        Assert.Empty(buffer.Snapshot());
        Assert.Equal(0, buffer.DroppedCount);
    }

    // ================= 容量钳制 / 回退 / 热生效 =================

    [Theory]
    [InlineData(5, 100)]
    [InlineData(99, 100)]
    [InlineData(100, 100)]
    [InlineData(500, 500)]
    [InlineData(20000, 20000)]
    [InlineData(20001, 20000)]
    [InlineData(1000000, 20000)]
    [InlineData(0, 2000)]
    [InlineData(-3, 2000)]
    public void 容量_按上下限钳制_非正回退(int requested, int expected)
    {
        Assert.Equal(expected, new NapCatLogBuffer(() => requested).Capacity);
    }

    [Fact]
    public void 容量_提供者异常_回退2000()
    {
        var buffer = new NapCatLogBuffer(() => throw new InvalidOperationException("设置读取失败"));

        Assert.Equal(2000, buffer.Capacity);
        Assert.Null(Record.Exception(() => buffer.Append("仍然可用", NapCatLogStream.System)));
        Assert.Single(buffer.Snapshot());
    }

    [Fact]
    public void 容量_未提供提供者_使用回退容量()
    {
        Assert.Equal(2000, new NapCatLogBuffer().Capacity);
    }

    [Fact]
    public void 容量_设置热生效_缩小时裁剪最旧行()
    {
        var capacity = 500;
        var buffer = new NapCatLogBuffer(() => capacity);
        for (var i = 0; i < 600; i++)
        {
            buffer.Append($"line {i}", NapCatLogStream.StdOut);
        }

        Assert.Equal(500, buffer.Snapshot().Count);
        Assert.Equal(100, buffer.DroppedCount);

        capacity = 200;
        buffer.Append("new", NapCatLogStream.StdOut);

        var lines = buffer.Snapshot();
        Assert.Equal(200, lines.Count);
        Assert.Equal("new", lines[^1].Text);
        Assert.Equal(401, buffer.DroppedCount);
        Assert.Equal(200, buffer.Capacity);
    }

    [Fact]
    public void 容量_扩容后保留原有顺序()
    {
        var capacity = 100;
        var buffer = new NapCatLogBuffer(() => capacity);
        for (var i = 1; i <= 150; i++)
        {
            buffer.Append($"line {i}", NapCatLogStream.StdOut);
        }

        capacity = 200;
        buffer.Append("line 151", NapCatLogStream.StdOut);

        var texts = buffer.Snapshot().Select(l => l.Text).ToList();
        Assert.Equal(101, texts.Count);
        Assert.Equal("line 51", texts[0]);
        Assert.Equal("line 151", texts[^1]);
        Assert.Equal(50, buffer.DroppedCount);
        Assert.Equal(200, buffer.Capacity);
    }

    // ================= 订阅者异常隔离 =================

    [Fact]
    public void 追加_订阅者抛异常_被隔离且其他订阅者仍收到()
    {
        var buffer = new NapCatLogBuffer();
        var received = new List<string>();
        buffer.Appended += (_, _) => throw new InvalidOperationException("订阅者异常");
        buffer.Appended += (_, line) => received.Add(line.Text);

        var ex = Record.Exception(() => buffer.Append("hello\r\nworld", NapCatLogStream.StdOut));

        Assert.Null(ex);
        Assert.Equal(["hello", "world"], received);
    }

    [Fact]
    public void 追加_订阅者收到完整行信息()
    {
        var buffer = new NapCatLogBuffer();
        NapCatLogLine? received = null;
        buffer.Appended += (_, line) => received = line;

        buffer.Append("payload", NapCatLogStream.StdErr);

        Assert.NotNull(received);
        Assert.Equal(1, received!.Sequence);
        Assert.Equal(NapCatLogStream.StdErr, received.Stream);
        Assert.Equal("payload", received.Text);
    }
}
