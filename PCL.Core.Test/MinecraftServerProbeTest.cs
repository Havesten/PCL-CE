using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Link.Relay;

namespace PCL.Core.Test;

[TestClass]
public class MinecraftServerProbeTest
{
    [TestMethod]
    public async Task ModernStatusRequiresStructureAndMatchingRandomPong()
    {
        await using var game = new MinecraftFixture();
        var proof = await MinecraftServerProbe.ProbeAsync(game.Endpoint, TimeSpan.FromSeconds(2));
        Assert.IsNotNull(proof);
        Assert.AreEqual(game.Endpoint, proof.Endpoint);
        Assert.AreEqual("Verified World", proof.Name);
        Assert.AreEqual("1.21.1", proof.Version);

        await using var wrongPong = new MinecraftFixture(MinecraftFixtureMode.WrongPong);
        Assert.IsNull(await MinecraftServerProbe.ProbeAsync(wrongPong.Endpoint, TimeSpan.FromSeconds(1)));
        await using var missingPlayers = new MinecraftFixture(status: "{\"version\":{\"name\":\"1.21.1\",\"protocol\":767},\"description\":\"fake\"}");
        Assert.IsNull(await MinecraftServerProbe.ProbeAsync(missingPlayers.Endpoint, TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    public async Task HttpListenerAndOversizedPacketsAreRejected()
    {
        await using var http = new MinecraftFixture(MinecraftFixtureMode.Http);
        Assert.IsNull(await MinecraftServerProbe.ProbeAsync(http.Endpoint, TimeSpan.FromSeconds(1)));
        await using var oversized = new MinecraftFixture(MinecraftFixtureMode.Oversized);
        var elapsed = Stopwatch.StartNew();
        Assert.IsNull(await MinecraftServerProbe.ProbeAsync(oversized.Endpoint, TimeSpan.FromSeconds(1)));
        Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task TimeoutCoversAllRequestsAndCancellationPropagates()
    {
        await using var stalled = new MinecraftFixture(MinecraftFixtureMode.Stall);
        var elapsed = Stopwatch.StartNew();
        Assert.IsNull(await MinecraftServerProbe.ProbeAsync(stalled.Endpoint, TimeSpan.FromMilliseconds(200)));
        Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(1), "Probe exceeded its complete time budget.");
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => MinecraftServerProbe.ProbeAsync(stalled.Endpoint, TimeSpan.FromSeconds(10), budget.Token));
    }

    [TestMethod]
    public async Task LegacyStatusIsValidatedWithUtf16BigEndian()
    {
        await using var game = new MinecraftFixture(MinecraftFixtureMode.Legacy);
        var proof = await MinecraftServerProbe.ProbeAsync(game.Endpoint, TimeSpan.FromSeconds(2));
        Assert.IsNotNull(proof);
        Assert.AreEqual("1.6.4", proof.Version);
        Assert.AreEqual("Legacy World", proof.Name);
    }

    [TestMethod]
    public async Task DiscoveryAcceptsOnlyVerifiedLocalCandidatesAndPreservesLanCapacity()
    {
        await using var game = new MinecraftFixture();
        await using var http = new MinecraftFixture(MinecraftFixtureMode.Http);
        var direct = await RelayGameDiscovery.ScanAsync(
            new[] { game.Endpoint, http.Endpoint, new IPEndPoint(IPAddress.Parse("192.0.2.1"), game.Endpoint.Port) },
            TimeSpan.FromMilliseconds(100), FreeUdpPort(), CancellationToken.None);
        Assert.HasCount(1, direct);
        Assert.AreEqual(game.Endpoint.Port, direct[0].Endpoint.Port);

        var lanPort = FreeUdpPort();
        var candidates = Enumerable.Range(10000, 250).Select(port => new IPEndPoint(IPAddress.Loopback, port));
        var scanning = RelayGameDiscovery.ScanAsync(candidates, TimeSpan.FromMilliseconds(700), lanPort, CancellationToken.None);
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var packet = Encoding.UTF8.GetBytes($"[MOTD]Verified LAN World[/MOTD][AD]{game.Endpoint.Port}[/AD]");
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(100);
            await sender.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, lanPort));
        }
        var viaLan = await scanning;
        Assert.IsTrue(viaLan.Any(item => item.Endpoint.Port == game.Endpoint.Port && item.Name == "Verified LAN World"),
            "TCP candidates exhausted all verification slots before the LAN announcement.");
        Assert.IsFalse(RelayGameDiscovery.IsLocalAddress(IPAddress.Parse("192.0.2.1"), RelayGameDiscovery.GetLocalAddresses()));
    }

    [TestMethod]
    public async Task LanAnnouncementsReachTwoMinecraftStyleMulticastListeners()
    {
        using var first = LanReceiver(0);
        var port = ((IPEndPoint)first.Client.LocalEndPoint!).Port;
        using var second = LanReceiver(port);
        using var announcer = new RelayLanAnnouncer("Room [AD] name", 12345, port) { Enabled = false };
        announcer.Start();
        using (var disabledBudget = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await first.ReceiveAsync(disabledBudget.Token));
        announcer.Enabled = true;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var packets = await Task.WhenAll(first.ReceiveAsync(budget.Token).AsTask(), second.ReceiveAsync(budget.Token).AsTask());
        foreach (var packet in packets)
        {
            Assert.IsTrue(RelayGameDiscovery.IsLocalAddress(packet.RemoteEndPoint.Address, RelayGameDiscovery.GetLocalAddresses()));
            Assert.IsTrue(RelayGameDiscovery.TryParseAnnouncement(Encoding.UTF8.GetString(packet.Buffer), out var name, out var gamePort));
            Assert.AreEqual(12345, gamePort);
            Assert.AreEqual("Room AD name", name);
        }
    }

    internal static int FreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    internal static UdpClient LanReceiver(int port)
    {
        var receiver = new UdpClient(AddressFamily.InterNetwork);
        receiver.ExclusiveAddressUse = false;
        receiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        receiver.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        receiver.JoinMulticastGroup(IPAddress.Parse("224.0.2.60"));
        return receiver;
    }
}

internal enum MinecraftFixtureMode { Normal, WrongPong, Oversized, Http, Stall, Legacy }

/// <summary>A framed status fixture; it does not simulate a complete playable Minecraft server.</summary>
internal sealed class MinecraftFixture : IAsyncDisposable
{
    internal const string GuestToken = "0123456789abcdef0123456789abcdef";
    private const string DefaultStatus = "{\"version\":{\"name\":\"1.21.1\",\"protocol\":767},\"players\":{\"online\":1,\"max\":8},\"description\":{\"text\":\"§aVerified [World]\\n\"}}";
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ConcurrentDictionary<Task, byte> _tasks = new();
    private readonly MinecraftFixtureMode _mode;
    private readonly string _status;
    private readonly bool _protected;
    private readonly Task _accept;
    private int _disposed;
    internal IPEndPoint Endpoint { get; }

    internal MinecraftFixture(MinecraftFixtureMode mode = MinecraftFixtureMode.Normal, string? status = null, bool protectedRoom = false)
    {
        _mode = mode;
        _status = status ?? DefaultStatus;
        _protected = protectedRoom;
        _listener.Start();
        Endpoint = (IPEndPoint)_listener.LocalEndpoint;
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                _clients.TryAdd(client, 0);
                var task = ServeAsync(client);
                _tasks.TryAdd(task, 0);
                _ = task.ContinueWith(done => { _tasks.TryRemove(done, out _); _ = done.Exception; },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var ct = _lifetime.Token;
            if (_protected)
            {
                var header = new byte[37];
                await stream.ReadExactlyAsync(header, ct);
                if (Encoding.ASCII.GetString(header) != "PCLR\x01" + GuestToken) return;
            }
            if (_mode == MinecraftFixtureMode.Http)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"), ct);
                return;
            }
            if (_mode == MinecraftFixtureMode.Legacy)
            {
                var request = new byte[2];
                await stream.ReadExactlyAsync(request, ct);
                if (!request.AsSpan().SequenceEqual(new byte[] { 0xfe, 1 })) return;
                var body = Encoding.BigEndianUnicode.GetBytes("§1\0" + "78\0" + "1.6.4\0" + "Legacy World\0" + "1\0" + "8");
                var response = new byte[body.Length + 3];
                response[0] = 255;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(1), (ushort)(body.Length / 2));
                body.CopyTo(response, 3);
                await stream.WriteAsync(response, ct);
                return;
            }
            var handshake = await MinecraftServerProbe.ReadPacketAsync(stream, ct);
            if (_mode == MinecraftFixtureMode.Stall) { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return; }
            if (handshake[0] == 0x7f)
            {
                await MinecraftServerProbe.WritePacketAsync(stream, handshake, ct);
                while (await stream.ReadAsync(new byte[16], ct) != 0) { }
                await MinecraftServerProbe.WritePacketAsync(stream, Encoding.ASCII.GetBytes("after-eof"), ct);
                return;
            }
            if (handshake[0] != 0) return;
            var requestPacket = await MinecraftServerProbe.ReadPacketAsync(stream, ct);
            if (!requestPacket.AsSpan().SequenceEqual(new byte[] { 0 })) return;
            if (_mode == MinecraftFixtureMode.Oversized)
            {
                using var invalid = new MemoryStream();
                MinecraftServerProbe.WriteVarInt(invalid, 65537);
                await stream.WriteAsync(invalid.ToArray(), ct);
                return;
            }
            var json = Encoding.UTF8.GetBytes(_status);
            using var status = new MemoryStream();
            status.WriteByte(0);
            MinecraftServerProbe.WriteVarInt(status, json.Length);
            status.Write(json);
            await MinecraftServerProbe.WritePacketAsync(stream, status.ToArray(), ct);
            var ping = await MinecraftServerProbe.ReadPacketAsync(stream, ct);
            if (_mode == MinecraftFixtureMode.WrongPong) ping[^1] ^= 1;
            await MinecraftServerProbe.WritePacketAsync(stream, ping, ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or InvalidDataException or ObjectDisposedException) { }
        finally { _clients.TryRemove(client, out _); client.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _listener.Stop();
        foreach (var client in _clients.Keys) client.Dispose();
        await _accept;
        await Task.WhenAll(_tasks.Keys);
        _lifetime.Dispose();
    }
}
