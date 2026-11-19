using System;
using System.Buffers.Binary;
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
public class RelayTunnelClientTest
{
    private const int RemotePort = 40001;

    private static TcpListener Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static TunnelClient Client(TcpListener listener, int localPort = 25565, TimeSpan? heartbeat = null)
    {
        var tunnel = new RelayTunnel("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "same-allocation", RemotePort);
        return new TunnelClient(tunnel, localPort, "test", heartbeat ?? TimeSpan.FromSeconds(25));
    }

    [TestMethod]
    public async Task MissingNodeDnsFailsPromptlyWithSafeActionableMessage()
    {
        const string hostname = "pclce-relay-qa-no-record.invalid";
        const string secret = "private-qa-tunnel-token";
        using var client = new TunnelClient(new RelayTunnel(hostname, 40001, secret, RemotePort), 25565, "dns-test", TimeSpan.FromSeconds(25),
            (_, _, _, _) => ValueTask.FromException(new SocketException((int)SocketError.HostNotFound)));
        var elapsed = Stopwatch.StartNew();
        var error = await client.StartAsync(TimeSpan.FromSeconds(20)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsNotNull(error);
        Assert.AreEqual(TunnelFailureCode.Dns, client.LastFailureCode);
        Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(5), "A missing DNS record used the whole startup retry budget.");
        Assert.IsTrue(error.Contains("解析", StringComparison.Ordinal));
        Assert.IsFalse(error.Contains(hostname, StringComparison.Ordinal));
        Assert.IsFalse(error.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(client.IsRunning);
    }

    [TestMethod]
    public async Task RefusedControlPortReportsConnectionFailureWithoutEndpoint()
    {
        using var client = new TunnelClient(new RelayTunnel("127.0.0.1", 40001, "private-token", RemotePort), 25565, "refused-test", TimeSpan.FromSeconds(25),
            (_, _, _, _) => ValueTask.FromException(new SocketException((int)SocketError.ConnectionRefused)));
        var error = await client.StartAsync(TimeSpan.FromMilliseconds(250));
        Assert.IsNotNull(error);
        Assert.AreEqual(TunnelFailureCode.Connection, client.LastFailureCode);
        Assert.IsFalse(error.Contains("127.0.0.1", StringComparison.Ordinal));
        Assert.IsFalse(error.Contains("private-token", StringComparison.Ordinal));
        Assert.IsFalse(client.IsRunning);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemoteHandshakeRejectionUsesSafeStageAndHidesPayload(bool authSucceeds)
    {
        var listener = Listen();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var client = Client(listener);
        const string remoteText = "do-not-display same-allocation 127.0.0.1 private@example.test";
        var server = Task.Run(async () =>
        {
            try
            {
                while (!budget.IsCancellationRequested)
                {
                    using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                    var stream = peer.GetStream();
                    Assert.AreEqual(0x01, (int)(await ReadFrame(stream, budget.Token)).Type);
                    if (authSucceeds)
                    {
                        await WriteFrame(stream, 0x02, [], budget.Token);
                        Assert.AreEqual(0x04, (int)(await ReadFrame(stream, budget.Token)).Type);
                    }
                    await WriteFrame(stream, 0x03, Encoding.UTF8.GetBytes(remoteText), budget.Token);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or IOException) { }
        });
        try
        {
            var error = await client.StartAsync(TimeSpan.FromMilliseconds(450), budget.Token);
            Assert.IsNotNull(error);
            Assert.AreEqual(authSucceeds ? TunnelFailureCode.Registration : TunnelFailureCode.Authentication, client.LastFailureCode);
            Assert.IsFalse(error.Contains(remoteText, StringComparison.Ordinal));
            Assert.IsFalse(error.Contains("same-allocation", StringComparison.Ordinal));
            Assert.IsFalse(error.Contains("127.0.0.1", StringComparison.Ordinal));
            Assert.IsFalse(error.Contains("private@example.test", StringComparison.Ordinal));
            Assert.AreEqual(client.FailureMessage, error);
            Assert.IsFalse(client.IsRunning);
        }
        finally
        {
            budget.Cancel();
            listener.Stop();
            await server;
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PrematureHandshakeCloseReportsSafeConnectionClosed(bool authSucceeds)
    {
        var listener = Listen();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        const string secret = "handshake-close-private-token";
        const string remoteText = "do-not-display private@example.test control-secret";
        var tunnel = new RelayTunnel("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, secret, RemotePort);
        using var client = new TunnelClient(tunnel, 25565, "close-test", TimeSpan.FromSeconds(25));
        var handshakes = 0;
        var server = Task.Run(async () =>
        {
            try
            {
                while (!budget.IsCancellationRequested)
                {
                    using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                    var stream = peer.GetStream();
                    var auth = await ReadFrame(stream, budget.Token);
                    Assert.AreEqual(0x01, (int)auth.Type);
                    Assert.AreEqual(secret, Encoding.UTF8.GetString(auth.Payload));
                    if (authSucceeds)
                    {
                        await WriteFrame(stream, 0x02, Encoding.UTF8.GetBytes(remoteText), budget.Token);
                        Assert.AreEqual(0x04, (int)(await ReadFrame(stream, budget.Token)).Type);
                    }
                    Interlocked.Increment(ref handshakes);
                    // Close without an ERR frame, as a filtering/forwarding peer can do.
                }
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or IOException) { }
        });
        try
        {
            var error = await client.StartAsync(TimeSpan.FromMilliseconds(450), budget.Token);
            Assert.IsNotNull(error);
            Assert.IsTrue(Volatile.Read(ref handshakes) > 0, "The loopback peer did not reach the requested handshake stage.");
            Assert.AreEqual(TunnelFailureCode.ConnectionClosed, client.LastFailureCode);
            Assert.AreEqual(client.FailureMessage, error);
            Assert.IsFalse(error.Contains(secret, StringComparison.Ordinal));
            Assert.IsFalse(error.Contains(remoteText, StringComparison.Ordinal));
            Assert.IsFalse(error.Contains("private@example.test", StringComparison.Ordinal));
            Assert.IsFalse(error.Contains("127.0.0.1", StringComparison.Ordinal));
            Assert.IsFalse(client.IsRunning);
            Assert.IsFalse(client.IsConnected);
        }
        finally
        {
            budget.Cancel();
            listener.Stop();
            await server;
        }
    }

    [TestMethod]
    public async Task UnexpectedHandshakeFrameIsProtocolFailure()
    {
        var listener = Listen();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var client = Client(listener);
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(budget.Token);
            await ReadFrame(peer.GetStream(), budget.Token);
            await WriteFrame(peer.GetStream(), 0x08, [], budget.Token);
        });
        try
        {
            Assert.IsNotNull(await client.StartAsync(TimeSpan.FromMilliseconds(300), budget.Token));
            Assert.AreEqual(TunnelFailureCode.Protocol, client.LastFailureCode);
            await server;
        }
        finally { budget.Cancel(); listener.Stop(); await server; }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HandshakeTimeoutCoversAuthAndRegister(bool authSucceeds)
    {
        var listener = Listen();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = Client(listener);
        var server = Task.Run(async () =>
        {
            try
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var stream = peer.GetStream();
                Assert.AreEqual(0x01, (int)(await ReadFrame(stream, budget.Token)).Type);
                if (authSucceeds)
                {
                    await WriteFrame(stream, 0x02, [], budget.Token);
                    Assert.AreEqual(0x04, (int)(await ReadFrame(stream, budget.Token)).Type);
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, budget.Token);
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            var elapsed = Stopwatch.StartNew();
            var error = await client.StartAsync(TimeSpan.FromMilliseconds(200), budget.Token);
            Assert.IsNotNull(error);
            Assert.AreEqual(TunnelFailureCode.Timeout, client.LastFailureCode);
            Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(2), "Handshake exceeded its complete timeout budget.");
            Assert.IsFalse(client.IsRunning);
        }
        finally
        {
            budget.Cancel();
            listener.Stop();
            await server;
        }
    }

    [TestMethod]
    public async Task ReconnectKeepsAllocationAndRestoresGameTraffic()
    {
        var listener = Listen();
        var gameListener = Listen();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = Client(listener, ((IPEndPoint)gameListener.LocalEndpoint).Port);
        try
        {
            var firstAccept = AcceptControl(listener, budget.Token);
            var start = client.StartAsync(TimeSpan.FromSeconds(3), budget.Token);
            using var first = await firstAccept;
            Assert.IsNull(await start);
            var secondAccept = AcceptControl(listener, budget.Token);
            first.Dispose();
            using var second = await secondAccept;
            Assert.IsTrue(client.IsRunning, "A transient disconnect terminated the allocation.");

            var request = new byte[6];
            BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(0, 4), 7);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4, 2), RemotePort);
            var dataAccept = listener.AcceptTcpClientAsync(budget.Token);
            await WriteFrame(second.GetStream(), 0x05, request, budget.Token);
            using var data = await dataAccept;
            var bind = await ReadFrame(data.GetStream(), budget.Token);
            Assert.AreEqual(0x06, (int)bind.Type);
            Assert.AreEqual(7u, BinaryPrimitives.ReadUInt32BigEndian(bind.Payload));
            var gameAccept = gameListener.AcceptTcpClientAsync(budget.Token);
            await WriteFrame(data.GetStream(), 0x02, [], budget.Token);
            using var game = await gameAccept;

            var fromPlayer = new byte[] { 1, 3, 5, 7, 9 };
            await data.GetStream().WriteAsync(fromPlayer, budget.Token);
            var actual = new byte[fromPlayer.Length];
            await game.GetStream().ReadExactlyAsync(actual, budget.Token);
            CollectionAssert.AreEqual(fromPlayer, actual);
            var fromGame = new byte[] { 2, 4, 6, 8, 10 };
            await game.GetStream().WriteAsync(fromGame, budget.Token);
            await data.GetStream().ReadExactlyAsync(actual, budget.Token);
            CollectionAssert.AreEqual(fromGame, actual);
            Assert.IsTrue(client.IsConnected);

            client.Dispose();
            client.Dispose();
            Assert.AreEqual(0, await data.GetStream().ReadAsync(new byte[1], budget.Token));
            Assert.IsFalse(client.IsRunning);
        }
        finally
        {
            budget.Cancel();
            listener.Stop();
            gameListener.Stop();
        }
    }

    [TestMethod]
    public async Task ConcurrentPingAndPongPreserveFrameBoundaries()
    {
        var listener = Listen();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var client = Client(listener, heartbeat: TimeSpan.FromMilliseconds(10));
        using var serverWriter = new SemaphoreSlim(1, 1);
        try
        {
            var accept = AcceptControl(listener, budget.Token);
            var start = client.StartAsync(TimeSpan.FromSeconds(3), budget.Token);
            using var peer = await accept;
            Assert.IsNull(await start);
            var stream = peer.GetStream();
            var payload = Enumerable.Range(0, 512).Select(i => (byte)i).ToArray();
            var send = Task.Run(async () =>
            {
                for (var i = 0; i < 128; i++)
                {
                    await WriteFrame(stream, 0x07, payload, budget.Token, serverWriter);
                    await Task.Delay(2, budget.Token);
                }
            });
            var pongCount = 0;
            var pingCount = 0;
            while (pongCount < 128 || pingCount == 0)
            {
                var frame = await ReadFrame(stream, budget.Token);
                if (frame.Type == 0x08)
                {
                    CollectionAssert.AreEqual(payload, frame.Payload, "A concurrent heartbeat corrupted a frame.");
                    pongCount++;
                }
                else if (frame.Type == 0x07)
                {
                    Assert.AreEqual(8, frame.Payload.Length);
                    pingCount++;
                    await WriteFrame(stream, 0x08, frame.Payload, budget.Token, serverWriter);
                }
                else Assert.Fail($"Unexpected control frame {frame.Type}.");
            }
            await send;
            Assert.IsTrue(client.IsRunning);
        }
        finally
        {
            budget.Cancel();
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task DisposeCancelsHandshakePromptly()
    {
        var listener = Listen();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = Client(listener);
        try
        {
            var accept = listener.AcceptTcpClientAsync(budget.Token);
            var start = client.StartAsync(TimeSpan.FromSeconds(30), budget.Token);
            using var peer = await accept;
            await ReadFrame(peer.GetStream(), budget.Token);
            client.Dispose();
            Assert.IsNotNull(await start.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.AreEqual(TunnelFailureCode.Cancelled, client.LastFailureCode);
            Assert.IsFalse(client.IsRunning);
            Assert.IsFalse(client.IsConnected);
        }
        finally
        {
            budget.Cancel();
            listener.Stop();
        }
    }

    private static async Task<TcpClient> AcceptControl(TcpListener listener, CancellationToken ct)
    {
        var peer = await listener.AcceptTcpClientAsync(ct);
        try
        {
            var stream = peer.GetStream();
            var auth = await ReadFrame(stream, ct);
            Assert.AreEqual(0x01, (int)auth.Type);
            Assert.AreEqual("same-allocation", System.Text.Encoding.UTF8.GetString(auth.Payload));
            await WriteFrame(stream, 0x02, [], ct);
            var register = await ReadFrame(stream, ct);
            Assert.AreEqual(0x04, (int)register.Type);
            Assert.AreEqual(RemotePort, (int)BinaryPrimitives.ReadUInt16BigEndian(register.Payload));
            await WriteFrame(stream, 0x02, [], ct);
            return peer;
        }
        catch { peer.Dispose(); throw; }
    }

    private static async Task WriteFrame(Stream stream, byte type, byte[] payload, CancellationToken ct, SemaphoreSlim? writer = null)
    {
        if (writer != null) await writer.WaitAsync(ct);
        try
        {
            var frame = new byte[5 + payload.Length];
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), (uint)(payload.Length + 1));
            frame[4] = type;
            payload.CopyTo(frame, 5);
            await stream.WriteAsync(frame, ct);
        }
        finally { writer?.Release(); }
    }

    private static async Task<(byte Type, byte[] Payload)> ReadFrame(Stream stream, CancellationToken ct)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > 1 << 20) throw new InvalidDataException("Invalid control frame size.");
        var payload = new byte[length - 1];
        await stream.ReadExactlyAsync(payload, ct);
        return (header[4], payload);
    }
}
