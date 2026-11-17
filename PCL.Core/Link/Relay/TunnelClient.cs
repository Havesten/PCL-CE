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
    private readonly TimeSpan _pingInterval;

    private readonly object _stateLock = new();
    private ControlConnection? _connection;
    private Task? _controlTask;
    private volatile bool _connected;
    private int _cleanupStarted;
    private CancellationTokenSource? _cts;

    // 数据连接任务表：只增不删会无限涨内存——完成后必须移除。
    private readonly ConcurrentDictionary<Task, byte> _dataTasks = new();
    private readonly SemaphoreSlim _dataGate = new(MaxDataConnections, MaxDataConnections);

    private volatile bool _running;
    private int _disposed;

    public TunnelClient(RelayTunnel tunnel, int localPort, string label)
        : this(tunnel, localPort, label, PingInterval)
    {
    }

    internal TunnelClient(RelayTunnel tunnel, int localPort, string label, TimeSpan pingInterval)
    {
        _tunnel = tunnel;
        _localPort = localPort;
        _label = string.IsNullOrWhiteSpace(label) ? "pcl-relay" : label;
        _pingInterval = pingInterval;
    }

    private sealed class ControlConnection(TcpClient client, CancellationToken ct) : IDisposable
    {
        private int _disposed;
        public TcpClient Client { get; } = client;
        public NetworkStream Stream { get; } = client.GetStream();
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(ct);
        public SemaphoreSlim Writer { get; } = new(1, 1);
        public void Stop()
        {
            try { Cancellation.Cancel(); } catch (ObjectDisposedException) { }
            Client.Dispose();
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            Cancellation.Dispose();
            Writer.Dispose();
        }
    }

    public bool IsRunning => _running;
    public bool IsConnected => _connected;

    public async Task<string?> StartAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            if (_disposed != 0) return "隧道已关闭";
            if (_cts != null) return "隧道已经启动";
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }
        try
        {
            var connection = await ConnectWithRetryAsync(timeout, _cts.Token).ConfigureAwait(false);
            lock (_stateLock)
            {
                if (_disposed != 0 || _cts.IsCancellationRequested)
                {
                    connection.Dispose();
                    throw new OperationCanceledException(_cts.Token);
                }
                _running = true;
                _connected = true;
                _connection = connection;
                _controlTask = Task.Run(() => RunSessionAsync(connection, _cts.Token));
            }
            return null;
        }
        catch (Exception ex)
        {
            _running = false;
            await CleanupAsync().ConfigureAwait(false);
            return ex is OperationCanceledException ? "隧道连接已取消或超时" : ex.Message;
        }
    }

    private async Task<ControlConnection> ConnectWithRetryAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;
        while (!ct.IsCancellationRequested)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            try { return await ConnectControlAsync(remaining, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { lastError = ex; }
            remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < AuthRetryDelay ? remaining : AuthRetryDelay, ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        throw new IOException(lastError?.Message ?? "隧道连接超时", lastError);
    }

    private async Task<ControlConnection> ConnectControlAsync(TimeSpan timeout, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(timeout); // 连接、AUTH、REGISTER 共用完整超时预算
            var token = budget.Token;
            await client.ConnectAsync(_tunnel.ServerAddr, _tunnel.ServerPort, token).ConfigureAwait(false);
            var stream = client.GetStream();
            await WriteFrame(stream, FrameAuth, System.Text.Encoding.UTF8.GetBytes(_tunnel.Token), token).ConfigureAwait(false);
            var (typ, payload) = await ReadFrame(stream, token).ConfigureAwait(false);
            if (typ == FrameErr) throw new IOException("鉴权失败：" + System.Text.Encoding.UTF8.GetString(payload));
            if (typ != FrameOk) throw new InvalidDataException($"意外的响应帧 0x{typ:X2}");
            var port = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(port, (ushort)_tunnel.RemotePort);
            await WriteFrame(stream, FrameRegister, port, token).ConfigureAwait(false);
            (typ, payload) = await ReadFrame(stream, token).ConfigureAwait(false);
            if (typ == FrameErr) throw new IOException("注册失败：" + System.Text.Encoding.UTF8.GetString(payload));
            if (typ != FrameOk) throw new InvalidDataException($"意外的响应帧 0x{typ:X2}");
            return new ControlConnection(client, ct);
        }
        catch { client.Dispose(); throw; }
    }

    private async Task RunSessionAsync(ControlConnection connection, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _connected = true;
                lock (_stateLock) _connection = connection;
                var ping = PingLoop(connection);
                try { await ControlLoop(connection).ConfigureAwait(false); }
                catch (Exception) { /* 瞬时网络错误由下面的重连处理 */ }
                finally
                {
                    _connected = false;
                    connection.Stop();
                    try { await ping.ConfigureAwait(false); } catch (Exception) { }
                    lock (_stateLock) { if (_connection == connection) _connection = null; }
                    connection.Dispose();
                }
                if (ct.IsCancellationRequested) break;
                // 更新重启、NAT 断开等情况下自动重新注册同一分配，保持原公网地址。
                connection = await ConnectWithRetryAsync(TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
            }
        }
        catch (Exception) { /* 重连预算耗尽或用户停止 */ }
        finally
        {
            _running = false;
            _connected = false;
            connection.Dispose();
            await CleanupAsync().ConfigureAwait(false);
        }
    }

    private static async Task WriteControl(ControlConnection connection, byte typ, byte[] payload)
    {
        var ct = connection.Cancellation.Token;
        await connection.Writer.WaitAsync(ct).ConfigureAwait(false);
        try { await WriteFrame(connection.Stream, typ, payload, ct).ConfigureAwait(false); }
        finally { connection.Writer.Release(); }
    }

    private async Task PingLoop(ControlConnection connection)
    {
        var ct = connection.Cancellation.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_pingInterval, ct).ConfigureAwait(false);
                await WriteControl(connection, FramePing, BitConverter.GetBytes(Environment.TickCount64)).ConfigureAwait(false);
            }
        }
        catch (Exception) { connection.Stop(); }
    }

    private async Task ControlLoop(ControlConnection connection)
    {
        var ct = connection.Cancellation.Token;
        while (!ct.IsCancellationRequested)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(90));
            var (typ, payload) = await ReadFrame(connection.Stream, readTimeout.Token).ConfigureAwait(false);
            switch (typ)
            {
                case FrameNewConn:
                    if (payload.Length != 6 || BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(4, 2)) != _tunnel.RemotePort) break;
                    StartDataConnection(BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4)), ct);
                    break;
                case FramePing:
                    await WriteControl(connection, FramePong, payload).ConfigureAwait(false);
                    break;
                case FrameErr:
                    return;
            }
        }
    }

    private async Task CleanupAsync()
    {
        if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0) return;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        lock (_stateLock) { _connection?.Stop(); _connection = null; }
        try { await Task.WhenAll(_dataTasks.Keys).ConfigureAwait(false); } catch (Exception) { }
        _dataTasks.Clear();
        _dataGate.Dispose();
        _cts?.Dispose();
    }

    /// <summary>
    ///     启动一条数据连接的处理任务，并登记到任务表（完成后自动移除，防泄漏）。
    /// </summary>
    private void StartDataConnection(uint connId, CancellationToken ct)
    {
        // 先清理已完成的任务，防止字典无限增长
        if (ct.IsCancellationRequested || _disposed != 0) return;
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
        });

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
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        connectCts.CancelAfter(TimeSpan.FromSeconds(10));
        var connectToken = connectCts.Token;

        TcpClient? dataConn = null;
        TcpClient? gameConn = null;
        try
        {
            // 连接服务端（新 TCP 连接）
            dataConn = new TcpClient { NoDelay = true };
            await dataConn.ConnectAsync(_tunnel.ServerAddr, _tunnel.ServerPort, connectToken).ConfigureAwait(false);
            var dataStream = dataConn.GetStream();

            // BIND
            var idBuf = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(idBuf, connId);
            await WriteFrame(dataStream, FrameBind, idBuf, connectToken).ConfigureAwait(false);

            var (typ, _) = await ReadFrame(dataStream, connectToken).ConfigureAwait(false);
            if (typ != FrameOk) return;

            // 连接本地游戏服
            gameConn = new TcpClient { NoDelay = true };
            await gameConn.ConnectAsync("127.0.0.1", _localPort, connectToken).ConfigureAwait(false);
            var gameStream = gameConn.GetStream();
            connectCts.CancelAfter(Timeout.InfiniteTimeSpan);

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
        lock (_stateLock) _connection?.Stop();
        // 后台循环在数据任务退出后统一释放信号量，避免 Release 与 Dispose 竞态。
    }
}
