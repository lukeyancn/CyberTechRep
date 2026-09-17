using System.Text;

namespace CyberTechRep.Plugin.Services.MessageAccess;

/// <summary>
/// NapCat 日志文件跟随器（有头 / Framework 形态专用）。
/// <para>
/// 有头形态下插件启动的是官方 QQ 客户端，进程没有 stdout 可读（界面程序不写控制台），
/// NapCat 的日志只落在安装目录的 <c>logs\*.log</c> 里；本类把新增内容按行取出，
/// 交给排错面板的日志环形缓冲，保持与无头形态一致的排错体验。
/// </para>
/// <para>
/// 行为约定：
/// <list type="bullet">
/// <item>首次打开对齐到文件末尾——只显示本次启动之后的新日志，不把历史日志整篇灌进面板；</item>
/// <item>日志轮转/被清空（文件长度回退）时自动从头重新跟随；</item>
/// <item>未写完的半行留在内部缓冲，下次轮询补齐（不把半行当完整行推给面板）；
/// 单行超长时按 <see cref="MaxLineLength"/> 断开，保证内存有界；</item>
/// <item>文件被占用/删除/切换文件名一律不抛异常，下次轮询重新打开（不影响 NapCat 运行）。</item>
/// </list>
/// </para>
/// </summary>
internal sealed class NapCatLogFileTailer : IDisposable
{
    /// <summary>单次轮询最多读取的字节数（避免一次灌爆日志缓冲，剩余下次继续）。</summary>
    internal const int MaxBytesPerPoll = 256 * 1024;

    /// <summary>未换行的单行上限：超出即按行推出，避免异常文件把内存撑爆。</summary>
    internal const int MaxLineLength = 8 * 1024;

    private readonly StringBuilder _pending = new();
    private readonly byte[] _buffer = new byte[8192];
    private FileStream? _stream;
    private long _offset;

    /// <summary>当前跟随的文件路径（null = 尚未跟随任何文件）。</summary>
    public string? CurrentPath { get; private set; }

    /// <summary>
    /// 跟随指定文件并返回本次新增的完整行（不含以换行结束的判定之外的内容）。
    /// <paramref name="path"/> 为空或文件不可访问时返回空列表，不抛出。
    /// </summary>
    public IReadOnlyList<string> Poll(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Reset();
            return [];
        }

        try
        {
            if (_stream is null || !string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                Open(path);
            }

            if (_stream is null)
            {
                return [];
            }

            // 日志轮转/被清空：长度回退即从头跟随（旧内容已不在）
            if (_stream.Length < _offset)
            {
                _offset = 0;
                _pending.Clear();
            }

            var lines = new List<string>();
            var budget = MaxBytesPerPoll;
            _stream.Seek(_offset, SeekOrigin.Begin);
            while (budget > 0)
            {
                var want = Math.Min(_buffer.Length, budget);
                var read = _stream.Read(_buffer, 0, want);
                if (read <= 0)
                {
                    break;
                }

                _offset += read;
                budget -= read;
                AppendLines(Encoding.UTF8.GetString(_buffer, 0, read), lines);
            }

            return lines;
        }
        catch (IOException)
        {
            Reset(); // 文件被占用/删除/共享冲突：下次轮询重新打开
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            Reset();
            return [];
        }
    }

    /// <summary>关闭当前跟随的文件（路径变化或轮询失败时调用），下次 Poll 会重新打开。</summary>
    public void Reset()
    {
        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // 释放失败不影响后续重新打开
        }

        _stream = null;
        _offset = 0;
        CurrentPath = null;
        _pending.Clear();
    }

    public void Dispose() => Reset();

    private void Open(string path)
    {
        Reset();
        var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            8192, FileOptions.SequentialScan);
        _stream = stream;
        // 首次对齐到末尾：只跟随「本次启动之后」写入的日志
        _offset = stream.Length;
        CurrentPath = path;
    }

    /// <summary>
    /// 把新读到的文本按行切分：完整行进入 <paramref name="lines"/>，末尾残行留在内部缓冲等下次补齐；
    /// 单行超过 <see cref="MaxLineLength"/> 时按上限断开推出（防内存无界）。
    /// </summary>
    private void AppendLines(string chunk, List<string> lines)
    {
        _pending.Append(chunk);
        while (true)
        {
            var text = _pending.ToString();
            var index = text.IndexOf('\n');
            if (index < 0)
            {
                if (text.Length > MaxLineLength)
                {
                    lines.Add(TrimCarriageReturn(text[..MaxLineLength]));
                    _pending.Remove(0, MaxLineLength);
                    continue;
                }

                return;
            }

            lines.Add(TrimCarriageReturn(text[..index]));
            _pending.Remove(0, index + 1);
        }
    }

    private static string TrimCarriageReturn(string line) =>
        line.EndsWith('\r') ? line[..^1] : line;
}
