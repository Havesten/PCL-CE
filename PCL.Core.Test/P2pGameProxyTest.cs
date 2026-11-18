using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Link.EasyTier;
using PCL.Core.Link.Relay;

namespace PCL.Core.Test;

[TestClass]
public class P2pGameProxyTest
{
    [TestMethod]
    public async Task RandomEntrancesAreExcludedFromGameDiscoveryAndReleasedPortsCanBeRediscovered()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var game = new MinecraftFixture();
        await using var first = new P2pGameProxy(game.Endpoint);
        await using var second = new P2pGameProxy(game.Endpoint);
        first.Start();
        second.Start();
        Assert.IsTrue(first.LocalEndpoint.Port > 0 && second.LocalEndpoint.Port > 0);
        Assert.AreNotEqual(game.Endpoint.Port, first.LocalEndpoint.Port);
        Assert.AreNotEqual(first.LocalEndpoint.Port, second.LocalEndpoint.Port);
        Assert.IsNotNull(await MinecraftServerProbe.ProbeAsync(first.LocalEndpoint, TimeSpan.FromSeconds(2), budget.Token));
        Assert.IsNotNull(await MinecraftServerProbe.ProbeAsync(second.LocalEndpoint, TimeSpan.FromSeconds(2), budget.Token));

        var discovered = await RelayGameDiscovery.ScanAsync(
            new[] { game.Endpoint, first.LocalEndpoint, second.LocalEndpoint },
            TimeSpan.FromMilliseconds(50), MinecraftServerProbeTest.FreeUdpPort(), budget.Token);
        Assert.HasCount(1, discovered);
        Assert.AreEqual(game.Endpoint, discovered[0].Endpoint);

        var releasedEndpoint = first.LocalEndpoint;
        await first.DisposeAsync();
        var reusedListener = new TcpListener(releasedEndpoint);
        reusedListener.Start();
        try
        {
            var serving = ServeMinecraftStatusAsync(reusedListener, budget.Token);
            var reusedGame = await RelayGameDiscovery.ScanAsync(new[] { releasedEndpoint },
                TimeSpan.FromMilliseconds(50), MinecraftServerProbeTest.FreeUdpPort(), budget.Token);
            await serving;
            Assert.HasCount(1, reusedGame, "A disposed proxy left its port excluded from later game discovery.");
            Assert.AreEqual(releasedEndpoint, reusedGame[0].Endpoint);
        }
        finally { reusedListener.Stop(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LoopbackAndLanInterfaceEntrancesPreserveConcurrentBidirectionalBytes(bool useLanInterface)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var listener = Listen();
        try
        {
            await using var proxy = new P2pGameProxy((IPEndPoint)listener.LocalEndpoint, acceptLocalInterfaces: true);
            proxy.Start();
            using var local = new TcpClient { NoDelay = true };
            var accepting = listener.AcceptTcpClientAsync(budget.Token).AsTask();
            await local.ConnectAsync(new IPEndPoint(useLanInterface ? LocalIPv4() : IPAddress.Loopback,
                proxy.LocalEndpoint.Port), budget.Token);
            using var target = await accepting;
            target.NoDelay = true;
            var sentToGame = Payload(262144, 17);
            var sentToPlayer = Payload(262144, 43);
            var receivedByGame = new byte[sentToGame.Length];
            var receivedByPlayer = new byte[sentToPlayer.Length];
            await Task.WhenAll(
                local.GetStream().WriteAsync(sentToGame, budget.Token).AsTask(),
                target.GetStream().WriteAsync(sentToPlayer, budget.Token).AsTask(),
                target.GetStream().ReadExactlyAsync(receivedByGame, budget.Token).AsTask(),
                local.GetStream().ReadExactlyAsync(receivedByPlayer, budget.Token).AsTask());
            CollectionAssert.AreEqual(sentToGame, receivedByGame);
            CollectionAssert.AreEqual(sentToPlayer, receivedByPlayer);
        }
        finally { listener.Stop(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HalfClosedSenderStillReceivesTheOtherDirectionsCompleteResponse(bool gameClosesFirst)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var listener = Listen();
        try
        {
            await using var proxy = new P2pGameProxy((IPEndPoint)listener.LocalEndpoint);
            proxy.Start();
            using var local = new TcpClient { NoDelay = true };
            var accepting = listener.AcceptTcpClientAsync(budget.Token).AsTask();
            await local.ConnectAsync(proxy.LocalEndpoint, budget.Token);
            using var target = await accepting;
            target.NoDelay = true;
            var firstSender = gameClosesFirst ? target : local;
            var secondSender = gameClosesFirst ? local : target;
            var firstStream = firstSender.GetStream();
            var secondStream = secondSender.GetStream();
            var request = Payload(65536, 19);
            var response = Payload(131072, 31);

            var responding = Task.Run(async () =>
            {
                CollectionAssert.AreEqual(request, await ReadUntilEofAsync(secondStream, budget.Token));
                await secondStream.WriteAsync(response, budget.Token);
                secondSender.Client.Shutdown(SocketShutdown.Send);
            }, budget.Token);
            await firstStream.WriteAsync(request, budget.Token);
            firstSender.Client.Shutdown(SocketShutdown.Send);
            CollectionAssert.AreEqual(response, await ReadUntilEofAsync(firstStream, budget.Token));
            await responding;
        }
        finally { listener.Stop(); }
    }

    [TestMethod]
    public async Task DisposalClosesActivePlayersTargetsAndListenerAndIsIdempotent()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var listener = Listen();
        var local = new TcpClient[4];
        var targets = new TcpClient[4];
        await using var proxy = new P2pGameProxy((IPEndPoint)listener.LocalEndpoint, acceptLocalInterfaces: true);
        proxy.Start();
        try
        {
            for (var index = 0; index < local.Length; index++)
            {
                local[index] = new TcpClient();
                var accepting = listener.AcceptTcpClientAsync(budget.Token).AsTask();
                await local[index].ConnectAsync(proxy.LocalEndpoint, budget.Token);
                targets[index] = await accepting;
                await local[index].GetStream().WriteAsync(new byte[] { (byte)index }, budget.Token);
                var received = new byte[1];
                await targets[index].GetStream().ReadExactlyAsync(received, budget.Token);
                Assert.AreEqual((byte)index, received[0]);
            }
            await Task.WhenAll(proxy.DisposeAsync().AsTask(), proxy.DisposeAsync().AsTask())
                .WaitAsync(TimeSpan.FromSeconds(3), budget.Token);
            await Task.WhenAll(local.Concat(targets).Select(client => AssertClosedAsync(client, budget.Token)));
            await proxy.DisposeAsync();
            Assert.ThrowsExactly<ObjectDisposedException>(() => proxy.Start());
            using var reconnecting = new TcpClient();
            await Assert.ThrowsExactlyAsync<SocketException>(
                async () => await reconnecting.ConnectAsync(proxy.LocalEndpoint, budget.Token));
        }
        finally
        {
            foreach (var client in local.Concat(targets)) client?.Dispose();
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task DisposingBeforeStartIsIdempotentAndPreventsOpeningAListener()
    {
        var proxy = new P2pGameProxy(new IPEndPoint(IPAddress.Loopback, 25565));
        await Task.WhenAll(proxy.DisposeAsync().AsTask(), proxy.DisposeAsync().AsTask());
        Assert.ThrowsExactly<ObjectDisposedException>(() => proxy.Start());
    }

    private static TcpListener Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static IPAddress LocalIPv4()
    {
        var address = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(item => item.Address)
            .Where(item => item.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(item))
            .OrderBy(item => item.GetAddressBytes()[0] == 169 && item.GetAddressBytes()[1] == 254 ? 1 : 0)
            .FirstOrDefault();
        if (address is null) Assert.Inconclusive("This host has no active IPv4 LAN interface to exercise a Minecraft LAN address.");
        return address!;
    }

    private static byte[] Payload(int length, int multiplier) =>
        Enumerable.Range(0, length).Select(index => (byte)((index * multiplier + 7) & 255)).ToArray();

    private static async Task<byte[]> ReadUntilEofAsync(NetworkStream stream, CancellationToken ct)
    {
        using var result = new MemoryStream();
        await stream.CopyToAsync(result, ct);
        return result.ToArray();
    }

    private static async Task AssertClosedAsync(TcpClient client, CancellationToken ct)
    {
        try { Assert.AreEqual(0, await client.GetStream().ReadAsync(new byte[1], ct), "Disposal left an active TCP connection."); }
        catch (Exception ex) when (ex is IOException or SocketException) { }
    }

    private static async Task ServeMinecraftStatusAsync(TcpListener listener, CancellationToken ct)
    {
        using var player = await listener.AcceptTcpClientAsync(ct);
        var stream = player.GetStream();
        await MinecraftServerProbe.ReadPacketAsync(stream, ct);
        await MinecraftServerProbe.ReadPacketAsync(stream, ct);
        var json = Encoding.UTF8.GetBytes("{\"version\":{\"name\":\"1.21.1\",\"protocol\":767},\"players\":{\"online\":1,\"max\":8},\"description\":\"Reused game port\"}");
        using var response = new MemoryStream();
        response.WriteByte(0);
        MinecraftServerProbe.WriteVarInt(response, json.Length);
        response.Write(json);
        await MinecraftServerProbe.WritePacketAsync(stream, response.ToArray(), ct);
        await MinecraftServerProbe.WritePacketAsync(stream, await MinecraftServerProbe.ReadPacketAsync(stream, ct), ct);
    }
}
