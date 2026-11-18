using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Link.Relay;

/// <summary>只检查本机已有监听端口，并验证本机 Minecraft 局域网广播。</summary>
public static class RelayGameDiscovery
{
    private static readonly ConcurrentDictionary<int, byte> InternalPorts = new();
    public static void RegisterInternalPort(int port) => InternalPorts.TryAdd(port, 0);
    public static void UnregisterInternalPort(int port) => InternalPorts.TryRemove(port, out _);
    private static bool IsInternalPort(int port) => InternalPorts.ContainsKey(port) || RelayRoomClient.IsLocalRoomPort(port);
    public static async Task<IReadOnlyList<RelayGameServer>> ScanAsync(CancellationToken ct = default)
    {
        IEnumerable<IPEndPoint> candidates;
        try
        {
            candidates = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Where(endpoint => endpoint.Port > 0 && !IsInternalPort(endpoint.Port))
                .Select(endpoint => new IPEndPoint(
                    endpoint.Address.Equals(IPAddress.Any) ? IPAddress.Loopback :
                    endpoint.Address.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback : endpoint.Address,
                    endpoint.Port))
                .Distinct().OrderBy(endpoint => endpoint.Port == 25565 ? 0 : 1).ToArray();
        }
        catch (NetworkInformationException) { candidates = []; }
        return await ScanAsync(candidates, TimeSpan.FromSeconds(4), 4445, ct).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<RelayGameServer>> ScanAsync(IEnumerable<IPEndPoint> candidates,
        TimeSpan listenDuration, int lanPort, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(8));
        using var gate = new SemaphoreSlim(20);
        using var lanGate = new SemaphoreSlim(4);
        var localAddresses = GetLocalAddresses();
        var found = new ConcurrentDictionary<IPEndPoint, RelayGameServer>();
        var names = new ConcurrentDictionary<IPEndPoint, string>();
        var checks = new ConcurrentDictionary<IPEndPoint, Lazy<Task>>();
        var lanChecks = new ConcurrentDictionary<IPEndPoint, Lazy<Task>>();

        async Task VerifyAsync(IPEndPoint endpoint, SemaphoreSlim lane)
        {
            try
            {
                await lane.WaitAsync(budget.Token).ConfigureAwait(false);
                try
                {
                    var server = await MinecraftServerProbe.ProbeAsync(endpoint, TimeSpan.FromMilliseconds(1500), budget.Token)
                        .ConfigureAwait(false);
                    if (server is not null) found[endpoint] = server;
                }
                finally { lane.Release(); }
            }
            catch (OperationCanceledException) { }
        }

        Task Queue(IPEndPoint endpoint, bool viaLan = false)
        {
            var pending = viaLan ? lanChecks : checks;
            if (IsInternalPort(endpoint.Port) || pending.Count >= (viaLan ? 64 : 192))
                return Task.CompletedTask;
            return pending.GetOrAdd(endpoint, key => new Lazy<Task>(() => VerifyAsync(key, viaLan ? lanGate : gate))).Value;
        }

        async Task ListenAsync(AddressFamily family, IPAddress group)
        {
            using var listening = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            listening.CancelAfter(listenDuration);
            UdpClient? receiver = null;
            try
            {
                receiver = new UdpClient(family);
                receiver.ExclusiveAddressUse = false;
                receiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                receiver.Client.Bind(new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, lanPort));
                receiver.JoinMulticastGroup(group);
                while (!listening.IsCancellationRequested)
                {
                    var packet = await receiver.ReceiveAsync(listening.Token).ConfigureAwait(false);
                    if (packet.Buffer.Length > 4096 || !IsLocalAddress(packet.RemoteEndPoint.Address, localAddresses)) continue;
                    var text = Encoding.UTF8.GetString(packet.Buffer);
                    if (!TryParseAnnouncement(text, out var name, out var port) || IsInternalPort(port)) continue;
                    var endpoint = new IPEndPoint(packet.RemoteEndPoint.Address, port);
                    if (lanChecks.Count >= 64 && !lanChecks.ContainsKey(endpoint)) continue;
                    if (name.Length > 0) names[endpoint] = name;
                    _ = Queue(endpoint, true);
                }
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // 某种地址族或多播不可用时，仍可通过本机 TCP 监听表识别游戏。
            }
            finally { receiver?.Dispose(); }
        }

        // LAN 广播有独立通道，避免本机大量慢响应监听占满全部验证任务。
        var portChecks = Task.WhenAll(candidates.Where(endpoint => IsLocalAddress(endpoint.Address, localAddresses))
            .Take(192).Select(endpoint => Queue(endpoint)));
        await Task.WhenAll(portChecks,
            ListenAsync(AddressFamily.InterNetwork, IPAddress.Parse("224.0.2.60")),
            ListenAsync(AddressFamily.InterNetworkV6, IPAddress.Parse("ff75:230::60"))).ConfigureAwait(false);
        await Task.WhenAll(checks.Values.Concat(lanChecks.Values).Select(check => check.Value)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return found.Select(pair => names.TryGetValue(pair.Key, out var name) ? pair.Value with { Name = name } : pair.Value)
            .GroupBy(server => server.Endpoint.Port)
            .Select(group => group.OrderBy(server => server.Endpoint.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).First())
            .OrderBy(server => server.Name, StringComparer.Ordinal).ToArray();
    }

    internal static bool TryParseAnnouncement(string text, out string name, out int port)
    {
        name = string.Empty;
        port = 0;
        if (text.Length > 4096) return false;
        var start = text.LastIndexOf("[AD]", StringComparison.Ordinal);
        if (start < 0) return false;
        var end = text.IndexOf("[/AD]", start + 4, StringComparison.Ordinal);
        if (end < 0 || !int.TryParse(text.AsSpan(start + 4, end - start - 4), out port) || port is < 1 or > 65535) return false;
        var motdStart = text.IndexOf("[MOTD]", StringComparison.Ordinal);
        var motdEnd = text.IndexOf("[/MOTD]", StringComparison.Ordinal);
        if (motdStart >= 0 && motdEnd >= motdStart + 6 && motdEnd < start)
            name = MinecraftServerProbe.CleanText(text.Substring(motdStart + 6, motdEnd - motdStart - 6), 80);
        return true;
    }

    internal static HashSet<IPAddress> GetLocalAddresses()
    {
        var result = new HashSet<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                    result.Add(address.Address.IsIPv4MappedToIPv6 ? address.Address.MapToIPv4() : address.Address);
        }
        catch (NetworkInformationException) { }
        return result;
    }

    internal static bool IsLocalAddress(IPAddress address, HashSet<IPAddress> localAddresses) =>
        IPAddress.IsLoopback(address) || localAddresses.Contains(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}
