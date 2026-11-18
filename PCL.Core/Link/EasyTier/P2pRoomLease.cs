using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using PCL.Core.Link.Relay;

namespace PCL.Core.Link.EasyTier;

/// <summary>云端房间生命周期独立于页面导航。</summary>
public sealed class P2pRoomLease : IAsyncDisposable
{
    private readonly RelayApi _api;
    private readonly bool _isHost;
    private readonly Func<CancellationToken, Task<bool>> _checkReady;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeSpan _interval;
    private Task? _monitor;
    private readonly object _state = new();
    private Task? _start;
    private Task? _cleanup;
    private int _disposed;
    public P2pCloudRoom Room { get; }
    public event Action<string>? Closed;

    public P2pRoomLease(RelayApi api, P2pCloudRoom room, bool isHost,
        Func<CancellationToken, Task<bool>> checkReady) : this(api, room, isHost, checkReady, TimeSpan.FromSeconds(20)) { }

    internal P2pRoomLease(RelayApi api, P2pCloudRoom room, bool isHost,
        Func<CancellationToken, Task<bool>> checkReady, TimeSpan interval)
    {
        _api = api; Room = room; _isHost = isHost; _checkReady = checkReady; _interval = interval;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_start is not null) throw new InvalidOperationException("房间已启动");
            _start = StartCoreAsync(ct);
            return _start;
        }
    }
    private async Task StartCoreAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        if (_isHost) await _api.SetP2pRoomReadyAsync(Room.Id, true, linked.Token).ConfigureAwait(false);
        lock (_state)
        {
            linked.Token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _monitor = Task.Run(MonitorAsync);
        }
    }

    private async Task MonitorAsync()
    {
        var missing = 0;
        try
        {
            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    if (_isHost)
                    {
                        var ready = await _checkReady(_lifetime.Token).ConfigureAwait(false);
                        missing = ready ? 0 : missing + 1;
                        if (missing >= 3) { Closed?.Invoke("游戏房间已关闭"); break; }
                        await _api.SetP2pRoomReadyAsync(Room.Id, ready, _lifetime.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        var latest = await _api.JoinP2pRoomAsync(Room.Code, _lifetime.Token).ConfigureAwait(false);
                        if (latest.Id != Room.Id || latest.NetworkName != Room.NetworkName ||
                            latest.NetworkSecret != Room.NetworkSecret || latest.GamePort != Room.GamePort)
                        { Closed?.Invoke("房间已更新，请重新加入"); break; }
                    }
                }
                catch (RelayApiException ex) when (!_isHost && ExistingConnectionMayContinue(ex)) { }
                catch (RelayApiException ex) when (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                { Closed?.Invoke(ex.Message); break; }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                catch (Exception) { }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static bool ExistingConnectionMayContinue(RelayApiException ex)
    {
        if (ex.Status != HttpStatusCode.Forbidden || ex.Body is null) return false;
        try
        {
            using var body = JsonDocument.Parse(ex.Body);
            return body.RootElement.ValueKind == JsonValueKind.Object &&
                body.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String &&
                error.GetString() is "p2p_disabled" or "version_too_low";
        }
        catch (JsonException) { return false; }
    }

    public ValueTask DisposeAsync()
    {
        lock (_state)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _lifetime.Cancel();
                _cleanup = CleanupAsync();
            }
            return new ValueTask(_cleanup!);
        }
    }
    private async Task CleanupAsync()
    {
        if (_start is not null) try { await _start.ConfigureAwait(false); } catch (Exception) { }
        if (_monitor is not null) try { await _monitor.ConfigureAwait(false); } catch (Exception) { }
        if (_isHost)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _api.ReleaseP2pRoomAsync(Room.Id, budget.Token).ConfigureAwait(false); } catch (Exception) { }
        }
        _lifetime.Dispose();
    }
}
