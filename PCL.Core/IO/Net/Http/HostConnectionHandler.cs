using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.IO.Net.Cdn;
using PCL.Core.IO.Net.Dns;
using PCL.Core.Logging;
using PCL.Core.Utils.Exts;

namespace PCL.Core.IO.Net.Http;

public class HostConnectionHandler
{
    public static HostConnectionHandler Instance { get; } = new();
    private const string ModuleName = "HostConnectionHandler";

    private readonly DnsQuery _dnsQuery = DnsQuery.Instance;
    private static readonly TimeSpan _CacheDuration = TimeSpan.FromMinutes(10); // 10 分钟缓存(RFC 建议值)
    private const int WaitTasks = 2;

    /// <summary>尝试连接锁定节点的限时，超时后回退到常规解析。</summary>
    private const int PinnedConnectTimeoutMs = 6000;

    /// <summary>锁定节点连接失败后的冷却时长，避免每次新建连接都重复等待。</summary>
    private static readonly TimeSpan PinCooldownDuration = TimeSpan.FromMinutes(5);

    private static readonly ConcurrentDictionary<(string host, string ip), DateTime> PinCooldowns = new();

    // 缓存结构: (host, port) -> (IPAddress -> LastSuccessUtc)
    private readonly ConcurrentDictionary<(string host, int port), ConcurrentDictionary<IPAddress, DateTime>>
        _connectionCache = new();

    public async ValueTask<Stream> GetConnectionAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var now = DateTime.UtcNow;

        // 代理地址或者直连 IP 直接返回结果
        if (IPAddress.TryParse(host, out var directIp))
        {
            return await _ConnectToAddressAsync(directIp, port, cancellationToken).ConfigureAwait(false);
        }

        // 已锁定的 CDN 节点优先直连，跳过 DNS 解析。
        // SNI 与 Host 头仍由 HttpClient 依据请求 URI 填写，证书校验不受影响。
        if (CdnNodePin.IsActive && CdnNodePin.TryGetPins(host, out var pinnedAddresses))
        {
            var candidates = pinnedAddresses.Where(address => !_IsPinCoolingDown(host, address)).ToArray();
            if (candidates.Length > 0)
            {
                var pinnedStream = await _ConnectPinnedAsync(host, port, candidates, cancellationToken)
                    .ConfigureAwait(false);
                if (pinnedStream is not null) return pinnedStream;
            }
        }

        var addresses = await _ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        if (addresses is null || addresses.Length == 0)
        {
            throw new HttpRequestException($"DNS resolution failed for {host}");
        }

        // 对待连接地址进行排序 (缓存成功+IPv6优先)
        var sortedAddresses = _SortAddresses(host, port, addresses, now);

        // Happy Eyeballs 连接逻辑，优先 IPv6 稍后
        var connectionTasks = new List<Task<NetworkStream>>(WaitTasks);
        var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connectCancellationToken = cancellationSource.Token;

        try
        {
            for (int i = 0; i < sortedAddresses.Length; i++)
            {
                var address = sortedAddresses[i];
                var delayMs = i * (address.AddressFamily.Equals(AddressFamily.InterNetwork) ? 150 : 80); // 偏向使用 v6

                connectionTasks.Add(_DelayedConnectAsync(address, port, delayMs, connectCancellationToken));
            }

            // 等待首个成功连接
            var winner = await connectionTasks
                .ToArray()
                .WhenAnySuccessAsync()
                .ConfigureAwait(false);
            var stream = await winner.ConfigureAwait(false);

            // 更新缓存 + 记录成功
            var remoteIp = ((IPEndPoint)stream.Socket.RemoteEndPoint!).Address;
            _UpdateConnectionCache(host, port, remoteIp, now);
            LogWrapper.Debug(ModuleName, $"Connected to {host} via {remoteIp}");

            // 取消其他连接
            // ReSharper disable once MethodHasAsyncOverload
            cancellationSource.Cancel();
            _ = _CleanupUnusedConnectionsAsync(connectionTasks, winner).ConfigureAwait(false);

            return stream;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 清理所有连接
            // ReSharper disable once MethodHasAsyncOverload
            cancellationSource.Cancel();
            _ = _CleanupUnusedConnectionsAsync(connectionTasks, null).ConfigureAwait(false);

            LogWrapper.Error(ex, ModuleName, $"All connection attempts failed for {host}");
            throw new HttpRequestException($"Connection failed for {host}", ex);
        }
        finally
        {
            cancellationSource.Dispose();
        }
    }

    private async Task<IPAddress[]?> _ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (_IsDoHEnabled())
            return await _dnsQuery.QueryForIpAsync(host, cancellationToken).ConfigureAwait(false);

        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken)
                .ConfigureAwait(false);
            return addresses.Distinct().ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogWrapper.Warn(ex, ModuleName, $"系统 DNS 解析 {host} 失败");
            return null;
        }
    }

    /// <summary>
    ///     尝试连接锁定节点。同时锁定 IPv4 与 IPv6 时并发建连（v6 先发、v4 稍后），取先成功者。
    /// </summary>
    /// <returns>连接成功返回流；全部失败返回 <c>null</c>，调用方应回退到常规解析。</returns>
    private async Task<Stream?> _ConnectPinnedAsync(
        string host,
        int port,
        IPAddress[] candidates,
        CancellationToken cancellationToken)
    {
        // 锁定节点可能已经失效或不可达，必须限时，否则会一直挂到 HttpClient 整体超时
        using var pinTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pinTimeout.CancelAfter(PinnedConnectTimeoutMs);

        try
        {
            if (candidates.Length == 1)
            {
                var stream = await _ConnectToAddressAsync(candidates[0], port, pinTimeout.Token)
                    .ConfigureAwait(false);
                PinCooldowns.TryRemove((host, candidates[0].ToString()), out _);
                LogWrapper.Debug(ModuleName, $"已锁定 {host} 到节点 {candidates[0]}");
                return stream;
            }

            var tasks = new List<Task<NetworkStream>>(candidates.Length);
            foreach (var address in candidates)
            {
                // 与常规建连保持一致：IPv6 立即发起，IPv4 稍后补上
                var delayMs = address.AddressFamily == AddressFamily.InterNetwork ? 80 : 0;
                tasks.Add(_DelayedConnectAsync(address, port, delayMs, pinTimeout.Token));
            }

            var winnerTask = await tasks.WhenAnySuccessAsync().ConfigureAwait(false);
            var winner = await winnerTask.ConfigureAwait(false);
            var remoteIp = ((IPEndPoint)winner.Socket.RemoteEndPoint!).Address;
            PinCooldowns.TryRemove((host, remoteIp.ToString()), out _);
            LogWrapper.Debug(ModuleName,
                $"已锁定 {host} 到节点 {remoteIp}（双栈并发，共 {tasks.Count} 条路径）");

            // 取消其余路径并释放已经建立的连接
            // ReSharper disable once MethodHasAsyncOverload
            pinTimeout.Cancel();
            _ = _CleanupUnusedConnectionsAsync(tasks, winnerTask).ConfigureAwait(false);
            return winner;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 两条路径都失败（或整体超时）时一起冷却，避免每次请求重复等待
            foreach (var address in candidates)
                PinCooldowns[(host, address.ToString())] = DateTime.UtcNow + PinCooldownDuration;
            LogWrapper.Warn(ex, ModuleName,
                $"锁定节点 {string.Join(", ", candidates.Select(address => address.ToString()))} 连接 {host} 失败，回退到常规解析");
            return null;
        }
    }

    private static bool _IsPinCoolingDown(string host, IPAddress address)    {
        var key = (host, address.ToString());
        if (!PinCooldowns.TryGetValue(key, out var until))
            return false;
        if (until > DateTime.UtcNow)
            return true;
        PinCooldowns.TryRemove(key, out _);
        return false;
    }

    private static bool _IsDoHEnabled()
    {
        try
        {
            return Config.Network.EnableDoH;
        }
        catch
        {
            // 配置服务尚未就绪时按默认值处理
            return true;
        }
    }

    private IPAddress[] _SortAddresses(string host, int port, IPAddress[] addresses, DateTime now)
    {
        var cacheKey = (host, port);
        var addressCache = _connectionCache.GetValueOrDefault(cacheKey);

        var cachedAddresses = new List<IPAddress>();
        var uncachedAddresses = new List<IPAddress>();

        foreach (var ip in addresses)
        {
            if (addressCache is not null &&
                addressCache.TryGetValue(ip, out var successTime) &&
                now - successTime <= _CacheDuration)
            {
                cachedAddresses.Add(ip);
            }
            else
            {
                uncachedAddresses.Add(ip);
            }
        }

        // 缓存地址: 按成功时间倒序 (最近成功的优先)
        cachedAddresses.Sort((a, b) =>
            addressCache![b].CompareTo(addressCache[a]));

        // 非缓存地址: IPv6 优先 + 保持原始顺序
        var ipv6List = uncachedAddresses
            .Where(static ip => ip.AddressFamily == AddressFamily.InterNetworkV6)
            .OrderBy(ip => Array.IndexOf(addresses, ip))
            .ToList();

        var ipv4List = uncachedAddresses
            .Where(static ip => ip.AddressFamily == AddressFamily.InterNetwork)
            .OrderBy(ip => Array.IndexOf(addresses, ip))
            .ToList();

        // 交错合并，确保有一个 v6 和一个 v4
        var sortedUncached = new List<IPAddress>();
        int i = 0, j = 0;
        while (i < ipv6List.Count || j < ipv4List.Count)
        {
            if (i < ipv6List.Count)
                sortedUncached.Add(ipv6List[i++]);
            if (j < ipv4List.Count)
                sortedUncached.Add(ipv4List[j++]);
        }

        return cachedAddresses
            .Concat(sortedUncached)
            .Take(WaitTasks)
            .ToArray();
    }

    private void _UpdateConnectionCache(string host, int port, IPAddress ip, DateTime now)
    {
        var cacheKey = (host, port);
        var addressCache = _connectionCache.GetOrAdd(cacheKey, static _ =>
            new ConcurrentDictionary<IPAddress, DateTime>());

        // 移除过期条目 (懒清理)
        foreach (var entry in addressCache)
        {
            if (now - entry.Value > _CacheDuration)
            {
                addressCache.TryRemove(entry.Key, out _);
            }
        }

        // 更新当前IP
        addressCache[ip] = now;
    }

    private static async Task<NetworkStream> _DelayedConnectAsync(IPAddress ip, int port, int delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        return await _ConnectToAddressAsync(ip, port, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<NetworkStream> _ConnectToAddressAsync(IPAddress ip, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        try
        {
            await socket.ConnectAsync(ip, port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            socket.Dispose();
            throw new HttpRequestException($"Connection to {ip}:{port} failed", ex);
        }
    }

    private static async Task _CleanupUnusedConnectionsAsync(
        List<Task<NetworkStream>> allTasks,
        Task<NetworkStream>? winnerTask)
    {
        foreach (var task in allTasks.Where(t => t != winnerTask))
        {
            try
            {
                if (task.IsCompletedSuccessfully)
                {
                    var stream = await task.ConfigureAwait(false);
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                else if (task.IsCompleted)
                {
                    _ = task.Exception;
                }
                else
                {
                    _ = task.ContinueWith(static t =>
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            t.Result.Dispose();
                        }
                        else
                        {
                            _ = t.Exception;
                        }
                    }, TaskScheduler.Default);
                }
            }
            catch (Exception ex)
            {
                LogWrapper.Warn(ex, ModuleName, "Dispose connection with an error");
            }
        }
    }
}