using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClassIng.Tests.Support;

/// <summary>
/// 模拟 QQ 官方 WebSocket 网关的测试服务器（RFC6455 最小实现，基于 TcpListener，无外部依赖）。
/// 每个测试脚本通过 <see cref="OnConnection"/> 自定义行为。
/// </summary>
public sealed class TestGatewayServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private TcpListener _listener = null!;

    /// <summary>收到的帧（当前全部连接，按顺序）：(op, 原始 JSON)。</summary>
    public ConcurrentQueue<(int Op, string Json)> ReceivedFrames { get; } = new();

    /// <summary>已接受的连接数（含重连）。</summary>
    public int AcceptedConnections;

    /// <summary>自定义每条连接的处理脚本。</summary>
    public required Func<GatewayConnection, CancellationToken, Task> OnConnection { get; set; }

    public int Port { get; private set; }

    public Task StartAsync()
    {
        _listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break; // 监听器已停止
            }

            Interlocked.Increment(ref AcceptedConnections);
            _ = Task.Run(() => HandleConnectionAsync(tcp, ct), ct);
        }
    }

    private async Task HandleConnectionAsync(TcpClient tcp, CancellationToken ct)
    {
        using var _ = tcp;
        var stream = tcp.GetStream();
        var conn = new GatewayConnection(stream);

        try
        {
            await HandshakeAsync(stream, ct).ConfigureAwait(false);
            await OnConnection(conn, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // 客户端断开：正常（重连测试会主动断开）
        }
        catch (SocketException)
        {
        }
        finally
        {
            conn.Dispose();
        }
    }

    /// <summary>读取 HTTP 升级请求并回应 101（ClientWebSocket 校验 Sec-WebSocket-Accept）。</summary>
    private static async Task HandshakeAsync(NetworkStream stream, CancellationToken ct)
    {
        var headerBytes = new byte[8192];
        var total = 0;
        while (true)
        {
            var n = await stream.ReadAsync(headerBytes.AsMemory(total, headerBytes.Length - total), ct)
                .ConfigureAwait(false);
            if (n == 0)
            {
                throw new IOException("握手阶段连接关闭");
            }

            total += n;
            if (Encoding.ASCII.GetString(headerBytes, 0, total).Contains("\r\n\r\n"))
            {
                break;
            }
        }

        var request = Encoding.ASCII.GetString(headerBytes, 0, total);
        var key = request.Split("\r\n")
            .FirstOrDefault(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            ?.Split(':')[1].Trim() ?? throw new IOException("缺少 Sec-WebSocket-Key");

        const string magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + magic)));
        var response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        var respBytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(respBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            _listener.Stop();
        }
        catch
        {
        }

        try
        {
            if (_acceptLoop is not null)
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
        }
        catch
        {
        }
    }
}

/// <summary>测试网关的单条连接（服务端视角的 WebSocket 读写）。</summary>
public sealed class GatewayConnection : IDisposable
{
    private readonly NetworkStream _stream;

    internal GatewayConnection(NetworkStream stream) => _stream = stream;

    /// <summary>发送一条 JSON 文本帧（服务端帧不掩码）。</summary>
    public Task SendJsonAsync(object payload, CancellationToken ct = default)
        => SendTextAsync(JsonSerializer.Serialize(payload), ct);

    public async Task SendTextAsync(string text, CancellationToken ct = default)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var header = new MemoryStream();
        header.WriteByte(0x81); // FIN + text
        if (payload.Length < 126)
        {
            header.WriteByte((byte)payload.Length);
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            header.WriteByte(126);
            header.WriteByte((byte)(payload.Length >> 8));
            header.WriteByte((byte)payload.Length);
        }
        else
        {
            header.WriteByte(127);
            var len = payload.Length;
            for (var i = 7; i >= 0; i--)
            {
                header.WriteByte((byte)(len >> (8 * i)));
            }
        }

        var head = header.ToArray();
        await _stream.WriteAsync(head, ct).ConfigureAwait(false);
        await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>读取一条完整文本帧（解析客户端掩码帧），返回 UTF8 JSON 文本。连接关闭返回 null。</summary>
    public async Task<string?> ReadTextAsync(CancellationToken ct = default)
    {
        var first = new byte[1];
        var firstCount = await _stream.ReadAsync(first, ct).ConfigureAwait(false);
        if (firstCount == 0)
        {
            return null;
        }

        var opcode = first[0] & 0x0F;
        if ((first[0] & 0x0F) == 0x8)
        {
            return null; // close
        }

        var second = new byte[1];
        if (await _stream.ReadAsync(second, ct).ConfigureAwait(false) == 0)
        {
            return null;
        }

        var masked = (second[0] & 0x80) != 0;
        var len = second[0] & 0x7F;
        if (len == 126)
        {
            var ext = new byte[2];
            await ReadFullAsync(ext, ct).ConfigureAwait(false);
            len = (ext[0] << 8) | ext[1];
        }
        else if (len == 127)
        {
            var ext = new byte[8];
            await ReadFullAsync(ext, ct).ConfigureAwait(false);
            len = 0;
            for (var i = 0; i < 8; i++)
            {
                len = (len << 8) | ext[i];
            }
        }

        var mask = new byte[4];
        if (masked)
        {
            await ReadFullAsync(mask, ct).ConfigureAwait(false);
        }

        var payload = new byte[len];
        var read = 0;
        while (read < len)
        {
            var got = await _stream.ReadAsync(payload.AsMemory(read, len - read), ct).ConfigureAwait(false);
            if (got == 0)
            {
                return null;
            }

            read += got;
        }

        if (masked)
        {
            for (var i = 0; i < len; i++)
            {
                payload[i] ^= mask[i % 4];
            }
        }

        // 忽略控制帧语义（心跳是文本帧，ping/pong 未被客户端主动使用）
        _ = opcode;
        return Encoding.UTF8.GetString(payload);
    }

    private async Task ReadFullAsync(byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new IOException("帧解析时连接关闭");
            }

            read += n;
        }
    }

    public void Dispose()
    {
        try
        {
            _stream.Dispose();
        }
        catch
        {
        }
    }
}

/// <summary>测试辅助：轮询等待条件成立。</summary>
public static class TestWait
{
    public static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string description)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException($"等待超时：{description}");
    }
}
