using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Link.Relay;

/// <summary>
///     自研隧道客户端（PTP v1）。完全内嵌在启动器里，无需外部二进制。
///
///     协议（PTP v1）：
///     - 控制连接（持久）：AUTH(token) → REGISTER(port) → 等 NEW_CONN 信号
///     - 数据连接（按玩家）：BIND(conn_id) → 原始字节管道
///
///     防篡改：协议私有，通用隧道工具 / 任意 HTTP 代理无法握手。
///     防滥用：服务端做首包检测 + 限速 + 配额，客户端只负责搬运字节。
///
///     资源安全（review 时重点核对）：
///     - 心跳循环每 25 秒发 PING，防止服务端读超时踢线；
///     - 数据连接任务用 ConcurrentDictionary 跟踪并在结束时移除，不无限增长；
///     - 每条数据连接独立 CTS，任一方向结束即整条链路拆除；
///     - SemaphoreSlim 限制并发数据连接，超出直接拒绝。
/// </summary>
public sealed class TunnelClient : IDisposable
{
    // 帧类型（与 relay/protocol.go 一致）
    private const byte FrameAuth = 0x01;
    private const byte FrameOk = 0x02;
    private const byte FrameErr = 0x03;
    private const byte FrameRegister = 0x04;
    private const byte FrameNewConn = 0x05;
    private const byte FrameBind = 0x06;
    private const byte FramePing = 0x07;
    private const byte FramePong = 0x08;

    private const int MaxFrameSize = 1 << 20; // 1 MB
    private const int MaxDataConnections = 128; // 并发数据连接上限
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan AuthRetryDelay = TimeSpan.FromSeconds(2);

    private readonly RelayTunnel _tunnel;
    private readonly int _localPort;
    private readonly string _label;

    private TcpClient? _control;
    private NetworkStream? _controlStream;
    private CancellationTokenSource? _cts;

    // 数据连接任务表：只增不删会无限涨内存——完成后必须移除。
    private readonly ConcurrentDictionary<Task, byte> _dataTasks = new();
    private readonly SemaphoreSlim _dataGate = new(MaxDataConnections, MaxDataConnections);

    private volatile bool _running;
    private int _disposed;

    public TunnelClient(RelayTunnel tunnel, int localPort, string label)
    {
        _tunnel = tunnel;
        _localPort = localPort;
        _label = string.IsNullOrWhiteSpace(label) ? "pcl-relay" : label;
    }

    public bool IsRunning => _running && _control is { Connected: true };

    /// <summary>
    ///     启动隧道。成功返回 null，失败返回原因。
    ///     云端分配的令牌要等节点下一次轮询 state（默认 5 秒）才会进入绑定表，
    ///     所以 AUTH 被拒（无效令牌）并不一定是真失败：在超时预算内间隔重试，等绑定表收敛。
    /// </summary>
    public async Task<string?> StartAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        var deadline = DateTime.UtcNow + timeout;
        var lastError = "连接超时";

        while (true)
        {
            try
            {
                // 1. 连接控制端口
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    return lastError;
                _control = new TcpClient { NoDelay = true };
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                connectCts.CancelAfter(remaining);
                await _control.ConnectAsync(_tunnel.ServerAddr, _tunnel.ServerPort, connectCts.Token).ConfigureAwait(false);
                _controlStream = _control.GetStream();

                // 2. AUTH
                await WriteFrame(_controlStream, FrameAuth, System.Text.Encoding.UTF8.GetBytes(_tunnel.Token), token).ConfigureAwait(false);
                var (typ, payload) = await ReadFrame(_controlStream, token).ConfigureAwait(false);
                if (typ == FrameErr)
                {
                    lastError = "鉴权失败：" + System.Text.Encoding.UTF8.GetString(payload);
                    // 令牌未同步到节点的绑定表是常态（分配与轮询之间的竞态），释放本连接后在预算内重试。
                    _CloseControl();
                    if (DateTime.UtcNow + AuthRetryDelay >= deadline)
                        return lastError;
                    await Task.Delay(AuthRetryDelay, token).ConfigureAwait(false);
                    continue;
                }
                if (typ != FrameOk)
                    return $"意外的响应帧 0x{typ:X2}";

                // 3. REGISTER（端口与令牌强绑定，失败是确定性错误，不重试）
                var portBuf = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(portBuf, (ushort)_tunnel.RemotePort);
                await WriteFrame(_controlStream, FrameRegister, portBuf, token).ConfigureAwait(false);
                (typ, payload) = await ReadFrame(_controlStream, token).ConfigureAwait(false);
                if (typ == FrameErr)
                    return "注册失败：" + System.Text.Encoding.UTF8.GetString(payload);
                if (typ != FrameOk)
                    return $"意外的响应帧 0x{typ:X2}";

                _running = true;

                // 4. 后台监听 NEW_CONN / PING
                _ = Task.Run(() => ControlLoop(token), token);
                // 5. 心跳：服务端 90 秒收不到帧就踢线，必须主动 PING
                _ = Task.Run(() => PingLoop(token), token);

                return null; // 成功
            }
            catch (OperationCanceledException)
            {
                _CloseControl();
                return lastError;
            }
            catch (Exception ex)
            {
                _CloseControl();
                return "隧道连接失败：" + ex.Message;
            }
        }
    }

    /// <summary>关闭并释放控制连接（重试与失败路径共用）。</summary>
    private void _CloseControl()
    {
        _controlStream = null;
        _control?.Dispose();
        _control = null;
    }

    /// <summary>
    ///     心跳循环：定期发 PING 保活。
    /// </summary>
    private async Task PingLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _controlStream is { } stream)
            {
                await Task.Delay(PingInterval, ct).ConfigureAwait(false);
                if (!ct.IsCancellationRequested && _running)
                    await WriteFrame(stream, FramePing, BitConverter.GetBytes(Environment.TickCount64), ct).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 连接断开，ControlLoop 会收口
        }
    }

    /// <summary>
    ///     控制连接主循环：接收 NEW_CONN 信号、响应 PING。
    /// </summary>
    private async Task ControlLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _controlStream is { } stream)
            {
                var (typ, payload) = await ReadFrame(stream, ct).ConfigureAwait(false);
                switch (typ)
                {
                    case FrameNewConn:
                        if (payload.Length < 4) break;
                        var connId = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4));
                        StartDataConnection(connId, ct);
                        break;

                    case FramePing:
                        // 回 PONG
                        await WriteFrame(stream, FramePong, payload, ct).ConfigureAwait(false);
                        break;

                    case FramePong:
                        // 心跳响应，忽略
                        break;

                    case FrameErr:
                        // 服务端踢了我们
                        _running = false;
                        return;
                }
            }
        }
        catch (Exception)
        {
            // 连接断开
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>
    ///     启动一条数据连接的处理任务，并登记到任务表（完成后自动移除，防泄漏）。
    /// </summary>
    private void StartDataConnection(uint connId, CancellationToken ct)
    {
        // 先清理已完成的任务，防止字典无限增长
        ReapCompleted();

        if (!_dataGate.Wait(0))
        {
            // 并发数据连接已达上限，丢弃本次请求（服务端会超时收口）
            return;
        }

        var task = Task.Run(async () =>
        {
            try
            {
                await HandleDataConnection(connId, ct).ConfigureAwait(false);
            }
            finally
            {
                _dataGate.Release();
            }
        }, ct);

        _dataTasks.TryAdd(task, 0);
        _ = task.ContinueWith(t =>
        {
            _dataTasks.TryRemove(t, out _);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void ReapCompleted()
    {
        foreach (var kv in _dataTasks)
        {
            if (kv.Key.IsCompleted)
                _dataTasks.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>
    ///     处理一条数据连接：BIND → 桥接游戏服 ↔ 隧道。
    /// </summary>
    private async Task HandleDataConnection(uint connId, CancellationToken ct)
    {
        // 每条数据连接独立取消域：任一方向结束，整条链路拆除
        using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linkCts.Token;

        TcpClient? dataConn = null;
        TcpClient? gameConn = null;
        try
        {
            // 连接服务端（新 TCP 连接）
            dataConn = new TcpClient { NoDelay = true };
            await dataConn.ConnectAsync(_tunnel.ServerAddr, _tunnel.ServerPort, token).ConfigureAwait(false);
            var dataStream = dataConn.GetStream();

            // BIND
            var idBuf = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(idBuf, connId);
            await WriteFrame(dataStream, FrameBind, idBuf, token).ConfigureAwait(false);

            var (typ, _) = await ReadFrame(dataStream, token).ConfigureAwait(false);
            if (typ != FrameOk) return;

            // 连接本地游戏服
            gameConn = new TcpClient { NoDelay = true };
            await gameConn.ConnectAsync("127.0.0.1", _localPort, token).ConfigureAwait(false);
            var gameStream = gameConn.GetStream();

            // 双向桥接：任一方向结束即取消另一个
            var t1 = Pump(dataStream, gameStream, token);
            var t2 = Pump(gameStream, dataStream, token);
            await Task.WhenAny(t1, t2).ConfigureAwait(false);
            linkCts.Cancel();
            await Task.WhenAll(t1, t2).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 游戏服没开或网络断了
        }
        finally
        {
            dataConn?.Dispose();
            gameConn?.Dispose();
        }
    }

    /// <summary>单向字节搬运。</summary>
    private static async Task Pump(Stream src, Stream dst, CancellationToken ct)
    {
        // 从共享池租缓冲，连接关闭后归还，避免每次转发分配 32KB
        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await src.ReadAsync(buf, ct).ConfigureAwait(false);
                if (n == 0) break;
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 连接关闭
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buf);
        }
    }

    // ---------------------------------------------------------------- 帧编解码

    private static async Task WriteFrame(Stream s, byte typ, byte[] payload, CancellationToken ct)
    {
        var buf = new byte[5 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), (uint)(1 + payload.Length));
        buf[4] = typ;
        payload.CopyTo(buf, 5);
        await s.WriteAsync(buf, ct).ConfigureAwait(false);
    }

    private static async Task<(byte typ, byte[] payload)> ReadFrame(Stream s, CancellationToken ct)
    {
        var hdr = new byte[5];
        await ReadFull(s, hdr, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0, 4));
        if (length == 0 || length > MaxFrameSize)
            throw new InvalidDataException($"非法帧长度: {length}");
        var typ = hdr[4];
        var payloadLen = (int)length - 1;
        var payload = payloadLen > 0 ? new byte[payloadLen] : [];
        if (payloadLen > 0)
            await ReadFull(s, payload, ct).ConfigureAwait(false);
        return (typ, payload);
    }

    private static async Task ReadFull(Stream s, byte[] buf, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(offset), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            offset += n;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _running = false;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { /* ignore */ }
        _control?.Dispose();
        _control = null;
        _controlStream = null;
        _dataGate.Dispose();
        _dataTasks.Clear();
    }
}
