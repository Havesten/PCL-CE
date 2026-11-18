using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Link.Relay;

/// <summary>已加入房间的本机 Minecraft 入口。地址与访问凭据仅在内部使用。</summary>
public sealed class RelayRoomClient : IDisposable, IAsyncDisposable
{
    private static readonly ConcurrentDictionary<int, byte> LocalRoomPorts = new();
    private readonly object _state = new();
    private readonly RelayApi _api;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _connections = new(32, 32);
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ConcurrentDictionary<Task, byte> _tasks = new();
    private readonly TimeSpan _heartbeatInterval;
    private readonly int _discoveryPort;
    private TcpListener? _listener;
    private RelayLanAnnouncer? _announcer;
    private Task? _startTask;
    private Task? _acceptTask;
    private Task? _monitorTask;
    private Task? _cleanupTask;
    private int _disposed;
    private string? _lastStatus;

    public RelayRoom Room { get; }
    public IPEndPoint LocalEndpoint { get; private set; } = new(IPAddress.Loopback, 0);
    public event Action<string>? StatusChanged;
    public event Action<string>? Closed;

    public RelayRoomClient(RelayApi api, RelayRoom room)
        : this(api, room, TimeSpan.FromSeconds(20), 4445) { }

    internal RelayRoomClient(RelayApi api, RelayRoom room, TimeSpan heartbeatInterval, int discoveryPort)
    {
        if (!RelayRoomCode.TryNormalize(room.Code, out _) || string.IsNullOrWhiteSpace(room.ServerHost) ||
            room.ServerHost.Length > 255 || room.ServerPort is < 1 or > 65535 ||
            string.IsNullOrEmpty(room.JoinToken) || room.JoinToken.Length != 32 || !room.JoinToken.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("云端返回的房间信息无效，请稍后重试");
        _api = api;
        Room = room;
        _heartbeatInterval = heartbeatInterval;
        _discoveryPort = discoveryPort;
    }

    internal static bool IsLocalRoomPort(int port) => LocalRoomPorts.ContainsKey(port);

    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_startTask is not null) throw new InvalidOperationException("房间已经加入");
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start(32);
            LocalEndpoint = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
            LocalRoomPorts.TryAdd(LocalEndpoint.Port, 0);
            _acceptTask = Task.Run(AcceptAsync);
            _startTask = StartCoreAsync(ct);
            return _startTask;
        }
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        using var cancellation = ct.Register(() => _lifetime.Cancel());
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        budget.CancelAfter(TimeSpan.FromSeconds(18));
        var verified = false;
        try
        {
            while (!budget.IsCancellationRequested)
            {
                if (await MinecraftServerProbe.ProbeAsync(LocalEndpoint, TimeSpan.FromSeconds(3), budget.Token)
                    .ConfigureAwait(false) is not null) { verified = true; break; }
                await Task.Delay(500, budget.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested) { }
        _lifetime.Token.ThrowIfCancellationRequested();
        if (!verified) throw new IOException("暂时无法连接游戏房间，请确认房主仍在游戏中并已打开局域网");
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _announcer = new RelayLanAnnouncer("中继房间 · " + Room.Name, LocalEndpoint.Port, _discoveryPort);
            _announcer.Start();
            _monitorTask = Task.Run(MonitorAsync);
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var local = await _listener!.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
                if (local.Client.RemoteEndPoint is not IPEndPoint source ||
                    !RelayGameDiscovery.IsLocalAddress(source.Address, RelayGameDiscovery.GetLocalAddresses()) ||
                    !_connections.Wait(0))
                {
                    local.Dispose();
                    continue;
                }
                _clients.TryAdd(local, 0);
                var task = HandleAsync(local, _lifetime.Token);
                _tasks.TryAdd(task, 0);
                _ = task.ContinueWith(done =>
                {
                    _tasks.TryRemove(done, out _);
                    _ = done.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task HandleAsync(TcpClient local, CancellationToken ct)
    {
        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(link.Token);
        connecting.CancelAfter(TimeSpan.FromSeconds(10));
        using var remote = new TcpClient { NoDelay = true };
        _clients.TryAdd(remote, 0);
        try
        {
            local.NoDelay = true;
            await remote.ConnectAsync(Room.ServerHost, Room.ServerPort, connecting.Token).ConfigureAwait(false);
            await remote.GetStream().WriteAsync(Encoding.ASCII.GetBytes("PCLR\x01" + Room.JoinToken), connecting.Token)
                .ConfigureAwait(false);
            connecting.CancelAfter(Timeout.InfiniteTimeSpan);

            async Task PumpAsync(TcpClient source, TcpClient destination)
            {
                try
                {
                    await source.GetStream().CopyToAsync(destination.GetStream(), 32 * 1024, link.Token).ConfigureAwait(false);
                    try { destination.Client.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
                }
                catch (Exception) { link.Cancel(); throw; }
            }

            await Task.WhenAll(PumpAsync(local, remote), PumpAsync(remote, local)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            _clients.TryRemove(local, out _);
            _clients.TryRemove(remote, out _);
            local.Dispose();
            _connections.Release();
        }
    }

    private void SetStatus(string text)
    {
        if (_lastStatus == text) return;
        _lastStatus = text;
        StatusChanged?.Invoke(text);
    }

    private async Task MonitorAsync()
    {
        string? closingReason = null;
        try
        {
            using var timer = new PeriodicTimer(_heartbeatInterval);
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    var latest = await _api.JoinRoomAsync(Room.Code, _lifetime.Token).ConfigureAwait(false);
                    if (latest.Id != Room.Id || latest.ServerHost != Room.ServerHost || latest.ServerPort != Room.ServerPort ||
                        latest.JoinToken != Room.JoinToken) { closingReason = "房间连接已更新，请重新加入"; break; }
                    var ready = await MinecraftServerProbe.ProbeAsync(LocalEndpoint, TimeSpan.FromSeconds(3), _lifetime.Token)
                        .ConfigureAwait(false) is not null;
                    if (_announcer is not null) _announcer.Enabled = ready;
                    SetStatus(ready ? "已加入房间，在游戏的多人游戏列表中可直接进入" : "游戏房间正在恢复连接…");
                }
                catch (RelayApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    closingReason = ex.Message;
                    break;
                }
                catch (RelayApiException ex) when (ex.Status == HttpStatusCode.Conflict)
                {
                    if (_announcer is not null) _announcer.Enabled = false;
                    SetStatus("房主正在恢复房间连接…");
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    // 云端短暂不可用不打断正在进行的游戏。
                    SetStatus("云端连接暂时中断，正在重试…");
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (closingReason is not null && _disposed == 0)
            {
                Closed?.Invoke(closingReason);
                Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_state)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _lifetime.Cancel();
            _announcer?.Dispose();
            _listener?.Stop();
            LocalRoomPorts.TryRemove(LocalEndpoint.Port, out _);
            foreach (var client in _clients.Keys) client.Dispose();
            _cleanupTask = CleanupAsync();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        lock (_state) return new ValueTask(_cleanupTask!);
    }

    private async Task CleanupAsync()
    {
        try { if (_startTask is not null) await _startTask.ConfigureAwait(false); } catch (Exception) { }
        try { if (_acceptTask is not null) await _acceptTask.ConfigureAwait(false); } catch (Exception) { }
        try { if (_monitorTask is not null) await _monitorTask.ConfigureAwait(false); } catch (Exception) { }
        try { await Task.WhenAll(_tasks.Keys).ConfigureAwait(false); } catch (Exception) { }
        _connections.Dispose();
        _lifetime.Dispose();
    }
}
