using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using PCL.Core.Link.Relay;

namespace PCL.Core.Link.EasyTier;

/// <summary>云端房间生命周期独立于页面导航。</summary>
public sealed class P2pRoomLease : IAsyncDisposable
{
    private readonly RelayApi _api;
    private readonly bool _isHost;
    private readonly Func<CancellationToken, Task<bool>> _checkReady;
    private readonly Func<bool> _transportReady;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeSpan _interval;
    private Task? _monitor;
    private readonly object _state = new();
    private Task? _start;
    private Task? _cleanup;
    private int _disposed;
    private string? _phase;
    private string? _failureCode;
    private int _retryCount;
    private string? _notice;
    public P2pCloudRoom Room { get; }
    public P2pRoomStatus? LastStatus { get; private set; }
    public event Action<string>? Closed;
    public event Action<string?>? Changed;

    public P2pRoomLease(RelayApi api, P2pCloudRoom room, bool isHost,
        Func<CancellationToken, Task<bool>> checkReady, Func<bool>? transportReady = null)
        : this(api, room, isHost, checkReady, TimeSpan.FromSeconds(20), transportReady) { }

    internal P2pRoomLease(RelayApi api, P2pCloudRoom room, bool isHost,
        Func<CancellationToken, Task<bool>> checkReady, TimeSpan interval, Func<bool>? transportReady = null)
    {
        _api = api; Room = room; _isHost = isHost; _checkReady = checkReady; _interval = interval;
        _transportReady = transportReady ?? (() => true);
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
        if (_isHost) await _api.SetP2pRoomReadyAsync(Room.Id, _transportReady(), linked.Token).ConfigureAwait(false);
        var status = await _api.GetP2pRoomStatusAsync(Room.Id, ct: linked.Token).ConfigureAwait(false);
        if (!Matches(status)) throw new InvalidOperationException("房间已更新，请重新连接");
        LastStatus = status;
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
        var failures = 0;
        var elapsed = Stopwatch.StartNew();
        var nextCloud = TimeSpan.Zero;
        try
        {
            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    var ready = true;
                    if (_isHost)
                    {
                        try { ready = await _checkReady(_lifetime.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
                        catch (Exception) { ready = false; }
                        missing = ready ? 0 : missing + 1;
                        if (missing >= 3) { Closed?.Invoke("游戏房间已关闭"); break; }
                    }
                    if (elapsed.Elapsed < nextCloud) continue;
                    if (_isHost) await _api.SetP2pRoomReadyAsync(Room.Id, ready && _transportReady(), _lifetime.Token).ConfigureAwait(false);
                    string? phase; string? failureCode; int retryCount;
                    lock (_state) { phase = _phase; failureCode = _failureCode; retryCount = _retryCount; }
                    var status = await _api.GetP2pRoomStatusAsync(Room.Id, phase, failureCode, retryCount, _lifetime.Token).ConfigureAwait(false);
                    if (!Matches(status)) { Closed?.Invoke("房间已更新，请重新加入"); break; }
                    LastStatus = status;
                    failures = 0; nextCloud = TimeSpan.Zero;
                    SetNotice(!status.Ready ? "房间码暂不可用，正在等待房主恢复；已有连接可继续。" :
                        !status.NodeAvailable ? "会合节点暂不可用；已有直连可继续。" : null);
                }
                catch (RelayApiException ex) when (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                { Closed?.Invoke(ex.Message); break; }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    failures = Math.Min(failures + 1, 4);
                    nextCloud = elapsed.Elapsed + TimeSpan.FromTicks(Math.Min(_interval.Ticks * (1L << failures), TimeSpan.FromMinutes(2).Ticks));
                    SetNotice("云端状态暂时无法同步，正在重试；已有连接可继续。");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Report(string phase, string? failureCode = null, int retryCount = 0)
    {
        lock (_state)
        {
            _phase = phase; _failureCode = failureCode; _retryCount = Math.Clamp(retryCount, 0, 99);
        }
    }
    private bool Matches(P2pRoomStatus? status) => status is not null && status.Id == Room.Id && status.Generation == Room.Id;
    private void SetNotice(string? notice)
    {
        if (_notice != notice)
        {
            _notice = notice;
            Changed?.Invoke(notice);
        }
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
