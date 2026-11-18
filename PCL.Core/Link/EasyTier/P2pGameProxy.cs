using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Link.Relay;

namespace PCL.Core.Link.EasyTier;

/// <summary>本机游戏入口，可接受 Minecraft 依据 LAN 通告地址发起的本机连接。</summary>
public sealed class P2pGameProxy : IAsyncDisposable
{
    private readonly IPEndPoint _target;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _slots = new(32, 32);
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ConcurrentDictionary<Task, byte> _tasks = new();
    private readonly object _state = new();
    private Task? _accept;
    private Task? _cleanup;
    private bool _disposed;
    public IPEndPoint LocalEndpoint { get; private set; } = new(IPAddress.Loopback, 0);

    public P2pGameProxy(IPEndPoint target, bool acceptLocalInterfaces = false)
    {
        _target = target;
        _listener = new TcpListener(acceptLocalInterfaces ? IPAddress.Any : IPAddress.Loopback, 0);
    }
    public void Start()
    {
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_accept is not null) throw new InvalidOperationException("游戏入口已启动");
            _listener.Start(32);
            LocalEndpoint = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
            RelayGameDiscovery.RegisterInternalPort(LocalEndpoint.Port);
            _accept = Task.Run(AcceptAsync);
        }
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
                if (client.Client.RemoteEndPoint is not IPEndPoint remote ||
                    !RelayGameDiscovery.IsLocalAddress(remote.Address, RelayGameDiscovery.GetLocalAddresses()) || !_slots.Wait(0))
                { client.Dispose(); continue; }
                _clients.TryAdd(client, 0);
                var task = ForwardAsync(client);
                _tasks.TryAdd(task, 0);
                _ = task.ContinueWith(done => { _tasks.TryRemove(done, out _); _ = done.Exception; },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
    }
    private async Task ForwardAsync(TcpClient local)
    {
        using var target = new TcpClient { NoDelay = true };
        _clients.TryAdd(target, 0);
        using var link = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(link.Token);
        connecting.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            local.NoDelay = true;
            await target.ConnectAsync(_target, connecting.Token).ConfigureAwait(false);
            async Task Pump(TcpClient source, TcpClient destination)
            {
                try
                {
                    await source.GetStream().CopyToAsync(destination.GetStream(), 32768, link.Token).ConfigureAwait(false);
                    try { destination.Client.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
                }
                catch (Exception) { link.Cancel(); throw; }
            }
            await Task.WhenAll(Pump(local, target), Pump(target, local)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { _clients.TryRemove(local, out _); _clients.TryRemove(target, out _); local.Dispose(); _slots.Release(); }
    }
    public ValueTask DisposeAsync()
    {
        lock (_state)
        {
            if (!_disposed)
            {
                _disposed = true;
                _lifetime.Cancel(); _listener.Stop();
                foreach (var client in _clients.Keys) client.Dispose();
                RelayGameDiscovery.UnregisterInternalPort(LocalEndpoint.Port);
                _cleanup = CleanupAsync();
            }
            return new ValueTask(_cleanup!);
        }
    }
    private async Task CleanupAsync()
    {
        if (_accept is not null) try { await _accept.ConfigureAwait(false); } catch (Exception) { }
        try { await Task.WhenAll(_tasks.Keys).ConfigureAwait(false); } catch (Exception) { }
        _slots.Dispose(); _lifetime.Dispose();
    }
}
