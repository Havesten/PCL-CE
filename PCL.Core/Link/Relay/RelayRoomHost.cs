using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Link.Relay;

/// <summary>房主隧道与独立于页面可见性的房间保活。</summary>
public sealed class RelayRoomHost : IDisposable, IAsyncDisposable
{
    private readonly object _state = new();
    private readonly RelayApi _api;
    private readonly RelayGameServer _game;
    private readonly TunnelClient _tunnel;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeSpan _heartbeatInterval;
    private Task? _startTask;
    private Task? _heartbeat;
    private Task? _cleanupTask;
    private int _disposed;
    private string? _lastStatus;

    public RelayAllocation Allocation { get; }
    public bool IsRunning => _disposed == 0 && _tunnel.IsRunning;
    public event Action<string>? StatusChanged;
    public event Action<string>? Closed;

    public RelayRoomHost(RelayApi api, RelayAllocationResult result, RelayGameServer game)
        : this(api, result, game, TimeSpan.FromSeconds(20)) { }

    internal RelayRoomHost(RelayApi api, RelayAllocationResult result, RelayGameServer game, TimeSpan heartbeatInterval)
    {
        if (!RelayRoomCode.TryNormalize(result.Allocation.RoomCode, out _))
            throw new InvalidOperationException("云端尚未支持房间码，请先升级云端至 3.2.1");
        _api = api;
        _game = game;
        Allocation = result.Allocation;
        _heartbeatInterval = heartbeatInterval;
        _tunnel = new TunnelClient(result.Tunnel, game.Endpoint, "room-" + Allocation.Id);
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_startTask is not null) throw new InvalidOperationException("房间已经创建");
            _startTask = StartCoreAsync(ct);
            return _startTask;
        }
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        using var cancellation = ct.Register(() => _lifetime.Cancel());
        var error = await _tunnel.StartAsync(TimeSpan.FromSeconds(20), _lifetime.Token).ConfigureAwait(false);
        if (error is not null) throw new IOException("房间连接失败，请检查网络或选择其他节点后重试");
        var proof = await MinecraftServerProbe.ProbeAsync(_game.Endpoint, TimeSpan.FromSeconds(3), _lifetime.Token).ConfigureAwait(false);
        if (proof is null) throw new IOException("游戏房间已关闭，请先在游戏中打开局域网后重试");
        await _api.SetRoomReadyAsync(Allocation.Id, true, _lifetime.Token).ConfigureAwait(false);
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _lifetime.Token.ThrowIfCancellationRequested();
            _heartbeat = Task.Run(HeartbeatAsync);
        }
    }

    private void SetStatus(string text)
    {
        if (_lastStatus == text) return;
        _lastStatus = text;
        StatusChanged?.Invoke(text);
    }

    private async Task HeartbeatAsync()
    {
        string? closingReason = null;
        var missingGame = 0;
        try
        {
            using var timer = new PeriodicTimer(_heartbeatInterval);
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (!_tunnel.IsRunning) { closingReason = "房间连接已停止，请重新创建房间"; break; }
                try
                {
                    var ready = _tunnel.IsConnected;
                    if (ready)
                    {
                        ready = await MinecraftServerProbe.ProbeAsync(_game.Endpoint, TimeSpan.FromSeconds(3), _lifetime.Token)
                            .ConfigureAwait(false) is not null;
                        missingGame = ready ? 0 : missingGame + 1;
                    }
                    await _api.SetRoomReadyAsync(Allocation.Id, ready, _lifetime.Token).ConfigureAwait(false);
                    SetStatus(ready ? "房间运行中，可将房间码分享给朋友" : "房间正在等待游戏或网络连接恢复…");
                    if (missingGame >= 3) { closingReason = "游戏房间已关闭，房间码已停止使用"; break; }
                }
                catch (RelayApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    closingReason = ex.Message;
                    break;
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                catch (Exception) { SetStatus("云端连接暂时中断，正在重试…"); }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (closingReason is not null && _disposed == 0)
            {
                _tunnel.Dispose();
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await _api.ReleaseAsync(Allocation.Id, budget.Token).ConfigureAwait(false); }
                catch (Exception) { }
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
            _tunnel.Dispose();
            _cleanupTask = DisposeAfterTaskAsync();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        lock (_state) return new ValueTask(_cleanupTask!);
    }

    private async Task DisposeAfterTaskAsync()
    {
        try { if (_startTask is not null) await _startTask.ConfigureAwait(false); }
        catch (Exception) { }
        try { if (_heartbeat is not null) await _heartbeat.ConfigureAwait(false); }
        catch (Exception) { }
        finally { _lifetime.Dispose(); }
    }
}
