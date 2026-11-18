using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Link.Relay;

/// <summary>使用 Minecraft LAN 协议持续发布本机入口，多播 TTL 为零。</summary>
internal sealed class RelayLanAnnouncer : IDisposable
{
    private readonly object _state = new();
    private readonly byte[] _packet;
    private readonly int _discoveryPort;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _task;
    private int _disposed;
    private volatile bool _enabled = true;
    public bool Enabled { get => _enabled; set => _enabled = value; }

    public RelayLanAnnouncer(string name, int gamePort, int discoveryPort = 4445)
    {
        _discoveryPort = discoveryPort;
        var description = MinecraftServerProbe.CleanText(name, 80, "中继房间");
        _packet = Encoding.UTF8.GetBytes($"[MOTD]{description}[/MOTD][AD]{gamePort}[/AD]");
    }

    public void Start()
    {
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _task ??= Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            using var multicast = new UdpClient(AddressFamily.InterNetwork);
            multicast.MulticastLoopback = true;
            multicast.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 0);
            using var loopback = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var group = new IPEndPoint(IPAddress.Parse("224.0.2.60"), _discoveryPort);
            var local = new IPEndPoint(IPAddress.Loopback, _discoveryPort);
            while (!_lifetime.IsCancellationRequested)
            {
                if (Enabled)
                {
                    // 多播可同时通知多个游戏实例；发送失败时才回退单播，避免列表出现重复房间。
                    try { await multicast.SendAsync(_packet, group, _lifetime.Token).ConfigureAwait(false); }
                    catch (SocketException)
                    {
                        try { await loopback.SendAsync(_packet, local, _lifetime.Token).ConfigureAwait(false); }
                        catch (SocketException) { }
                    }
                }
                await Task.Delay(1500, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (_state)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _lifetime.Cancel();
            _ = DisposeAfterTaskAsync();
        }
    }

    private async Task DisposeAfterTaskAsync()
    {
        try { if (_task is not null) await _task.ConfigureAwait(false); }
        catch (Exception) { }
        finally { _lifetime.Dispose(); }
    }
}
