using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Link.Relay;

namespace PCL.Core.Test;

[TestClass]
public class RelayRoomTest
{
    private const string Code = "PCLCE-ABCDE-FGHJK-LMNPQ-RSTUV";

    [TestMethod]
    public void TunnelSyncIntervalSupportsOldCloudAndExplicitPolling()
    {
        var legacy = JsonSerializer.Deserialize<RelayTunnel>("{\"server_addr\":\"127.0.0.1\",\"server_port\":40001,\"token\":\"qa\",\"remote_port\":40007}");
        Assert.IsNotNull(legacy);
        Assert.AreEqual(5, legacy.SyncIntervalSeconds);
        var slow = JsonSerializer.Deserialize<RelayTunnel>("{\"server_addr\":\"127.0.0.1\",\"server_port\":40001,\"token\":\"qa\",\"remote_port\":40007,\"sync_interval_seconds\":30}");
        Assert.IsNotNull(slow);
        Assert.AreEqual(30, slow.SyncIntervalSeconds);
    }

    [TestMethod]
    public async Task HostDnsFailureDoesNotPublishReadyAndHidesCredentials()
    {
        await using var game = new MinecraftFixture();
        await using var control = new ControlFixture(stall: true);
        var requests = 0;
        using var http = FakeHttp(_ => { Interlocked.Increment(ref requests); return Json(new { ok = true }); });
        var allocation = Allocation(control);
        allocation = allocation with { Tunnel = allocation.Tunnel with { ServerAddr = "pclce-relay-host-no-record.invalid" } };
        using var tunnel = new TunnelClient(allocation.Tunnel, game.Endpoint.Port, "host-dns-test", TimeSpan.FromSeconds(25),
            (_, _, _, _) => ValueTask.FromException(new SocketException((int)SocketError.HostNotFound)));
        await using var host = new RelayRoomHost(new RelayApi(http, "3.2.1"), allocation, new RelayGameServer(game.Endpoint, "World", "1.21.1"),
            TimeSpan.FromSeconds(20), tunnel);
        var error = await Assert.ThrowsAsync<IOException>(() => host.StartAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(error.Message.Contains("解析", StringComparison.Ordinal));
        Assert.IsFalse(error.Message.Contains(allocation.Tunnel.Token, StringComparison.Ordinal));
        Assert.IsFalse(error.Message.Contains(allocation.Tunnel.ServerAddr, StringComparison.Ordinal));
        Assert.AreEqual(0, requests, "Failed tunnel published a ready room.");
        Assert.IsFalse(host.IsRunning);
    }

    [TestMethod]
    public async Task HostDisposeDuringHandshakeWaitsForStartupAndDoesNotMarkReady()
    {
        await using var game = new MinecraftFixture();
        await using var control = new ControlFixture(stall: true);
        var requests = 0;
        using var http = FakeHttp(_ => { Interlocked.Increment(ref requests); return Json(new { ok = true }); });
        var api = new RelayApi(http, "3.2.1");
        await using var host = new RelayRoomHost(api, Allocation(control), new RelayGameServer(game.Endpoint, "World", "1.21.1"));
        var starting = host.StartAsync();
        await control.AuthReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.ThrowsExactly<InvalidOperationException>(() => host.StartAsync());
        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<IOException>(() => starting);
        Assert.IsFalse(host.IsRunning);
        Assert.AreEqual(0, requests, "Disposed startup published a ready room.");
    }

    [TestMethod]
    public async Task HostClosesAndReleasesAfterGameDisappears()
    {
        await using var game = new MinecraftFixture();
        await using var control = new ControlFixture();
        var readyStates = new ConcurrentQueue<bool>();
        var releases = 0;
        using var http = new HttpClient(new FixtureHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/rooms/ready", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                readyStates.Enqueue(document.RootElement.GetProperty("ready").GetBoolean());
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("/allocations/release", StringComparison.Ordinal))
                Interlocked.Increment(ref releases);
            return Json(new { ok = true });
        }));
        await using var host = new RelayRoomHost(new RelayApi(http, "3.2.1"), Allocation(control),
            new RelayGameServer(game.Endpoint, "World", "1.21.1"), TimeSpan.FromMilliseconds(40));
        var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Closed += reason => closed.TrySetResult(reason);
        await host.StartAsync();
        await game.DisposeAsync();
        var reason = await closed.Task.WaitAsync(TimeSpan.FromSeconds(12));
        Assert.IsTrue(reason.Contains("游戏房间已关闭", StringComparison.Ordinal));
        Assert.AreEqual(1, releases);
        Assert.IsTrue(readyStates.TryDequeue(out var first) && first);
        Assert.IsTrue(readyStates.Count >= 3);
        Assert.IsFalse(host.IsRunning);
    }

    [TestMethod]
    public async Task ReadyEndpointMustAcknowledgeSuccess()
    {
        using var http = FakeHttp(_ => Json(new { ok = false }));
        var api = new RelayApi(http, "3.2.1");
        await Assert.ThrowsExactlyAsync<RelayApiException>(() => api.SetRoomReadyAsync("allocation", true));
    }

    [TestMethod]
    public async Task GuestProxyAuthenticatesAndSupportsHalfCloseWithoutLosingResponse()
    {
        await using var game = new MinecraftFixture(protectedRoom: true);
        var room = Room(game);
        using var http = FakeHttp(_ => Json(new { room }));
        await using var guest = new RelayRoomClient(new RelayApi(http, "3.2.1"), room, TimeSpan.FromHours(1), MinecraftServerProbeTest.FreeUdpPort());
        await guest.StartAsync();
        var endpoint = guest.LocalEndpoint;
        Assert.IsTrue(RelayRoomClient.IsLocalRoomPort(endpoint.Port));
        using var player = new TcpClient();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await player.ConnectAsync(endpoint, budget.Token);
        var packet = new byte[] { 0x7f, 0, 9, 255, 17 };
        var stream = player.GetStream();
        await MinecraftServerProbe.WritePacketAsync(stream, packet, budget.Token);
        CollectionAssert.AreEqual(packet, await MinecraftServerProbe.ReadPacketAsync(stream, budget.Token));
        player.Client.Shutdown(SocketShutdown.Send);
        var response = await MinecraftServerProbe.ReadPacketAsync(stream, budget.Token);
        Assert.AreEqual("after-eof", Encoding.ASCII.GetString(response));
        Assert.AreEqual(0, await stream.ReadAsync(new byte[1], budget.Token));
        await guest.DisposeAsync();
        Assert.IsFalse(RelayRoomClient.IsLocalRoomPort(endpoint.Port));
        using var afterClose = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(async () => await afterClose.ConnectAsync(endpoint, budget.Token));
    }

    [TestMethod]
    public async Task GuestDisposeDuringVerificationCancelsPipesAndRemovesLocalListener()
    {
        await using var game = new MinecraftFixture(MinecraftFixtureMode.Stall, protectedRoom: true);
        var room = Room(game);
        using var http = FakeHttp(_ => Json(new { room }));
        await using var guest = new RelayRoomClient(new RelayApi(http, "3.2.1"), room);
        var starting = guest.StartAsync();
        var endpoint = guest.LocalEndpoint;
        await Task.Delay(80);
        await guest.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<OperationCanceledException>(() => starting);
        Assert.IsFalse(RelayRoomClient.IsLocalRoomPort(endpoint.Port));
    }

    [TestMethod]
    public async Task GuestKeepsExistingGameConnectionDuringCloudOutage()
    {
        await using var game = new MinecraftFixture(protectedRoom: true);
        var room = Room(game);
        using var http = FakeHttp(_ => Json(new { error = "unavailable" }, HttpStatusCode.ServiceUnavailable));
        await using var guest = new RelayRoomClient(new RelayApi(http, "3.2.1"), room, TimeSpan.FromMilliseconds(40), MinecraftServerProbeTest.FreeUdpPort());
        var outage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        guest.StatusChanged += text => { if (text.Contains("云端连接暂时中断", StringComparison.Ordinal)) outage.TrySetResult(); };
        await guest.StartAsync();
        using var player = new TcpClient();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await player.ConnectAsync(guest.LocalEndpoint, budget.Token);
        var stream = player.GetStream();
        await MinecraftServerProbe.WritePacketAsync(stream, new byte[] { 0x7f, 9 }, budget.Token);
        await MinecraftServerProbe.ReadPacketAsync(stream, budget.Token);
        await outage.Task.WaitAsync(budget.Token);
        player.Client.Shutdown(SocketShutdown.Send);
        Assert.AreEqual("after-eof", Encoding.ASCII.GetString(await MinecraftServerProbe.ReadPacketAsync(stream, budget.Token)));
    }

    [TestMethod]
    public async Task DeletedRoomClosesGuestConnectionAndListener()
    {
        await using var game = new MinecraftFixture(protectedRoom: true);
        var room = Room(game);
        using var http = FakeHttp(_ => Json(new { error = "room_not_found" }, HttpStatusCode.NotFound));
        await using var guest = new RelayRoomClient(new RelayApi(http, "3.2.1"), room, TimeSpan.FromMilliseconds(200), MinecraftServerProbeTest.FreeUdpPort());
        var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        guest.Closed += reason => closed.TrySetResult(reason);
        await guest.StartAsync();
        using var player = new TcpClient();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await player.ConnectAsync(guest.LocalEndpoint, budget.Token);
        var stream = player.GetStream();
        await MinecraftServerProbe.WritePacketAsync(stream, new byte[] { 0x7f, 7 }, budget.Token);
        await MinecraftServerProbe.ReadPacketAsync(stream, budget.Token);
        Assert.IsTrue((await closed.Task.WaitAsync(budget.Token)).Contains("房间已关闭", StringComparison.Ordinal));
        Assert.AreEqual(0, await stream.ReadAsync(new byte[1], budget.Token));
        await guest.DisposeAsync();
        Assert.IsFalse(RelayRoomClient.IsLocalRoomPort(guest.LocalEndpoint.Port));
    }

    [TestMethod]
    public void MissingGuestCredentialsAndMalformedCodesAreRejected()
    {
        using var http = FakeHttp(_ => Json(new { ok = true }));
        var api = new RelayApi(http, "3.2.1");
        var room = new RelayRoom("id", Code, "name", "1.21.1", "node", "127.0.0.1", 12345, "", DateTimeOffset.UtcNow.AddHours(1));
        Assert.ThrowsExactly<InvalidDataException>(() => new RelayRoomClient(api, room));
        Assert.ThrowsExactly<InvalidDataException>(() => new RelayRoomClient(api, room with { JoinToken = MinecraftFixture.GuestToken, Code = "bad" }));
        Assert.IsTrue(RelayRoomCode.TryNormalize(" pclce abcde fghjk lmnpq rstuv ", out var normalized));
        Assert.AreEqual(Code, normalized);
        Assert.IsFalse(RelayRoomCode.TryNormalize("PCLCE-ABCDE-FGHIJ-LMNPQ-RSTUV", out _));
    }

    private static RelayRoom Room(MinecraftFixture game) => new("allocation", Code, "World", "1.21.1", "node",
        "127.0.0.1", game.Endpoint.Port, MinecraftFixture.GuestToken, DateTimeOffset.UtcNow.AddHours(1));

    private static RelayAllocationResult Allocation(ControlFixture control) => new(
        new RelayAllocation("allocation", "World", "node", "node", "127.0.0.1", 30000, 40007,
            DateTimeOffset.UtcNow.AddHours(1), 0, 0, Code, "1.21.1"),
        new RelayTunnel("127.0.0.1", control.Endpoint.Port, "host-token", 40007),
        new RelayQuota(1 << 30, 0, 1 << 30, 0, 2, 1, "2026-11"));

    private static HttpClient FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new FixtureHandler((request, _) => Task.FromResult(respond(request))));

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}

internal sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
}

internal sealed class ControlFixture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _task;
    private readonly bool _stall;
    private TcpClient? _peer;
    internal IPEndPoint Endpoint { get; }
    internal TaskCompletionSource AuthReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ControlFixture(bool stall = false)
    {
        _stall = stall;
        _listener.Start();
        Endpoint = (IPEndPoint)_listener.LocalEndpoint;
        _task = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            _peer = await _listener.AcceptTcpClientAsync(_lifetime.Token);
            var stream = _peer.GetStream();
            var auth = await ReadFrame(stream);
            Assert.AreEqual(1, (int)auth.Type);
            Assert.AreEqual("host-token", Encoding.UTF8.GetString(auth.Payload));
            AuthReceived.TrySetResult();
            if (_stall) { await Task.Delay(Timeout.InfiniteTimeSpan, _lifetime.Token); return; }
            await WriteFrame(stream, 2, []);
            var register = await ReadFrame(stream);
            Assert.AreEqual(4, (int)register.Type);
            await WriteFrame(stream, 2, []);
            while (!_lifetime.IsCancellationRequested)
            {
                var frame = await ReadFrame(stream);
                if (frame.Type == 7) await WriteFrame(stream, 8, frame.Payload);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException) { }
        finally { _peer?.Dispose(); }
    }

    private async Task<(byte Type, byte[] Payload)> ReadFrame(NetworkStream stream)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, _lifetime.Token);
        var size = BinaryPrimitives.ReadUInt32BigEndian(header);
        Assert.IsTrue(size is > 0 and <= 1048576);
        var payload = new byte[size - 1];
        await stream.ReadExactlyAsync(payload, _lifetime.Token);
        return (header[4], payload);
    }

    private Task WriteFrame(NetworkStream stream, byte type, byte[] payload)
    {
        var frame = new byte[payload.Length + 5];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)(payload.Length + 1));
        frame[4] = type;
        payload.CopyTo(frame, 5);
        return stream.WriteAsync(frame, _lifetime.Token).AsTask();
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Stop();
        _peer?.Dispose();
        await _task;
        _lifetime.Dispose();
    }
}
