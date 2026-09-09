namespace CyberTechRep.Plugin.Services.MessageAccess;

/// <summary>NapCat 日志流来源（排错面板按来源区分显示）。</summary>
public enum NapCatLogStream
{
    /// <summary>标准输出（NapCat / Node 常规运行日志）。</summary>
    StdOut,

    /// <summary>标准错误（异常与崩溃信息）。</summary>
    StdErr,

    /// <summary>插件自身追加的系统行（启动命令、WebUI 发现、进程退出、读取异常等）。</summary>
    System
}

/// <summary>
/// NapCat 单行日志（不可变快照元素）。
/// <see cref="Sequence"/> 全局单调递增（<see cref="NapCatLogBuffer.Clear"/> 后继续递增），
/// 排错面板据此判断是否需要重新取快照。
/// </summary>
public sealed record NapCatLogLine(long Sequence, DateTimeOffset Timestamp, NapCatLogStream Stream, string Text);

/// <summary>
/// NapCat 进程 stdout/stderr 的线程安全有界环形缓冲。
/// <para>
/// 写入方为 stdout/stderr 异步读取循环（后台线程），读取方为排错面板 300ms 定时轮询
/// <see cref="Snapshot"/>（UI 线程）；两侧并发访问。内部为定长数组 + 头指针的环形结构，
/// 追加为 O(1)，容量满时覆盖最旧行并累计 <see cref="DroppedCount"/>，长时间运行内存占用恒定。
/// </para>
/// <para>
/// 容量由构造函数传入的 <c>Func&lt;int&gt;</c> 提供（读取
/// <c>ConnectionSettings.NapCatLogBufferLines</c>），每次追加时重新求值以支持设置热生效；
/// 求值失败或返回值 &lt;= 0 时回退 <see cref="FallbackCapacity"/>，随后钳制到
/// [<see cref="MinCapacity"/>, <see cref="MaxCapacity"/>]。<see cref="Append"/> 不抛异常、不做 IO，
/// 订阅方异常被逐个吞掉，绝不向读取线程逃逸。
/// </para>
/// </summary>
public sealed class NapCatLogBuffer
{
    /// <summary>容量下限（行）：设置值过小时钳制到此值，避免日志刚启动就被丢弃。</summary>
    public const int MinCapacity = 100;

    /// <summary>容量上限（行）：设置值过大时钳制到此值，保证内存占用有界。</summary>
    public const int MaxCapacity = 20000;

    /// <summary>容量回退值（行）：设置读取失败或返回值非正时使用。</summary>
    public const int FallbackCapacity = 2000;

    /// <summary>换行分隔符：先匹配 CRLF 再匹配单字符，避免 CRLF 被拆成两行。</summary>
    private static readonly string[] LineSeparators = ["\r\n", "\n", "\r"];

    private readonly object _lock = new();

    /// <summary>容量提供者（读设置）；null = 恒用回退容量。</summary>
    private readonly Func<int>? _capacityProvider;

    /// <summary>环形存储（长度 = 当前容量；未满时下标 (_head + i) % 长度为第 i 旧行）。</summary>
    private NapCatLogLine[] _lines;

    /// <summary>最旧行下标（仅由 <see cref="_lock"/> 保护下的代码读写）。</summary>
    private int _head;

    /// <summary>当前行数（&lt;= 容量）。</summary>
    private int _count;

    /// <summary>已分配的行序号（单调递增，Clear 不重置，保证 UI 增量判断不回退）。</summary>
    private long _sequence;

    /// <summary>因超容量被覆盖丢弃的行数（Clear 归零）。</summary>
    private long _droppedCount;

    /// <summary>
    /// 创建缓冲。<paramref name="capacityProvider"/> 返回期望行数上限（null = 使用回退容量）。
    /// </summary>
    public NapCatLogBuffer(Func<int>? capacityProvider = null)
    {
        _capacityProvider = capacityProvider;
        _lines = new NapCatLogLine[ResolveCapacity(capacityProvider)];
    }

    /// <summary>新行追加通知（在锁外逐行触发；订阅方异常被吞掉，不影响读取线程）。</summary>
    public event EventHandler<NapCatLogLine>? Appended;

    /// <summary>当前容量（行）；随设置热生效（每次追加时重新读取）。</summary>
    public int Capacity
    {
        get
        {
            lock (_lock)
            {
                return _lines.Length;
            }
        }
    }

    /// <summary>已因超容量丢弃的最旧行数（<see cref="Clear"/> 后归零）。</summary>
    public long DroppedCount
    {
        get
        {
            lock (_lock)
            {
                return _droppedCount;
            }
        }
    }

    /// <summary>最新已分配的行序号（缓冲为空时为 0）；UI 据此判断是否需要重新取快照。</summary>
    public long LastSequence
    {
        get
        {
            lock (_lock)
            {
                return _sequence;
            }
        }
    }

    /// <summary>
    /// 追加一段进程输出：按 CRLF/LF/CR 拆分为多行，逐行去除首尾空白并跳过空行。
    /// 可被任意线程并发调用；不抛异常、不阻塞（订阅方异常被吞掉）。
    /// </summary>
    public void Append(string? text, NapCatLogStream stream)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var parts = text.Split(LineSeparators, StringSplitOptions.None);
        List<NapCatLogLine>? appended = null;
        lock (_lock)
        {
            RefreshCapacityLocked();
            var timestamp = DateTimeOffset.Now;
            foreach (var part in parts)
            {
                var line = part.Trim();
                if (line.Length == 0)
                {
                    continue; // 空行（含仅含空白）不占用行号与容量
                }

                var record = new NapCatLogLine(++_sequence, timestamp, stream, line);
                if (_count == _lines.Length)
                {
                    // 容量已满：覆盖最旧行（O(1)）
                    _lines[_head] = record;
                    _head = (_head + 1) % _lines.Length;
                    _droppedCount++;
                }
                else
                {
                    _lines[(_head + _count) % _lines.Length] = record;
                    _count++;
                }

                (appended ??= []).Add(record);
            }
        }

        if (appended is null || Appended is not { } handler)
        {
            return;
        }

        // 锁外通知：逐个订阅者独立 try/catch，任一订阅方异常都不影响其他订阅者与读取线程
        foreach (var record in appended)
        {
            foreach (EventHandler<NapCatLogLine> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(this, record);
                }
                catch
                {
                    // 订阅方异常隔离
                }
            }
        }
    }

    /// <summary>取一致快照（最旧 → 最新）；返回独立副本，后续追加不影响已取快照。</summary>
    public IReadOnlyList<NapCatLogLine> Snapshot()
    {
        lock (_lock)
        {
            var result = new NapCatLogLine[_count];
            for (var i = 0; i < _count; i++)
            {
                result[i] = _lines[(_head + i) % _lines.Length];
            }

            return result;
        }
    }

    /// <summary>清空全部行并重置 <see cref="DroppedCount"/>；行序号保持单调递增不回退。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_lines);
            _head = 0;
            _count = 0;
            _droppedCount = 0;
        }
    }

    /// <summary>解析并钳制容量：异常/非正 → 回退容量；随后钳制到 [下限, 上限]。</summary>
    private static int ResolveCapacity(Func<int>? provider)
    {
        if (provider is null)
        {
            return FallbackCapacity;
        }

        try
        {
            var requested = provider();
            return requested > 0 ? Math.Clamp(requested, MinCapacity, MaxCapacity) : FallbackCapacity;
        }
        catch
        {
            return FallbackCapacity;
        }
    }

    /// <summary>追加前刷新容量（设置热生效）；容量变化时按「保留最新行」重建环形数组。</summary>
    private void RefreshCapacityLocked()
    {
        var capacity = ResolveCapacity(_capacityProvider);
        if (capacity == _lines.Length)
        {
            return;
        }

        // 缩容时丢弃最旧的超出部分并计入丢弃数；扩容时原样保留
        var keep = Math.Min(_count, capacity);
        var drop = _count - keep;
        if (drop > 0)
        {
            _droppedCount += drop;
        }

        var resized = new NapCatLogLine[capacity];
        for (var i = 0; i < keep; i++)
        {
            resized[i] = _lines[(_head + drop + i) % _lines.Length];
        }

        _lines = resized;
        _head = 0;
        _count = keep;
    }
}
