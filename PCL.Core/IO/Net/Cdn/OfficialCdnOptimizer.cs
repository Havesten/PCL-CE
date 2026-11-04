using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.IO.Net.Dns;
using PCL.Core.Logging;

namespace PCL.Core.IO.Net.Cdn;

/// <summary>
///     单个 CDN 节点的探测结果。
/// </summary>
/// <param name="Address">节点地址</param>
/// <param name="MedianMs">成功样本的中位数延迟，无成功样本时为 <see cref="double.PositiveInfinity" /></param>
/// <param name="AverageMs">成功样本的平均延迟，无成功样本时为 <see cref="double.PositiveInfinity" /></param>
/// <param name="SuccessCount">成功次数</param>
/// <param name="TotalCount">总探测次数</param>
public sealed record CdnNodeLatency(
    IPAddress Address,
    double MedianMs,
    double AverageMs,
    int SuccessCount,
    int TotalCount)
{
    /// <summary>该节点是否至少成功连通一次。</summary>
    public bool IsReachable => SuccessCount > 0;

    /// <summary>成功率，取值 0~1。</summary>
    public double SuccessRate => TotalCount <= 0 ? 0d : (double)SuccessCount / TotalCount;

    /// <summary>是否为 IPv6 节点。</summary>
    public bool IsIPv6 => Address.AddressFamily == AddressFamily.InterNetworkV6;

    /// <summary>地址族标签，用于界面展示。</summary>
    public string FamilyLabel => IsIPv6 ? "IPv6" : "IPv4";
}

/// <summary>
///     单个域名的探测结果，节点按延迟从低到高排列。
///     <p>
///         IPv4 与 IPv6 是两条独立的链路，混在一起排序时慢的一方会被整体挤出去（通常就是 IPv6），
///         因此这里额外按地址族拆分，各自给出延迟最低的可用节点。
///     </p>
/// </summary>
public sealed record CdnHostLatency(string Host, IReadOnlyList<CdnNodeLatency> Nodes)
{
    /// <summary>
    ///     节点是否来自 Cloudflare 优选列表，而不是该域名自己的 DNS 解析结果。
    ///     <para>CF 是 Anycast，同一组优选 IP 对所有 CF 域名通用，所以这些域名的节点列表会完全一致。</para>
    /// </summary>
    public bool FromPreferredPool { get; init; }

    /// <summary>仅 IPv4 节点，按延迟从低到高排列。</summary>
    public IReadOnlyList<CdnNodeLatency> IPv4Nodes { get; } = _Split(Nodes, ipv6: false);

    /// <summary>仅 IPv6 节点，按延迟从低到高排列。</summary>
    public IReadOnlyList<CdnNodeLatency> IPv6Nodes { get; } = _Split(Nodes, ipv6: true);

    /// <summary>延迟最低的可用节点，全部失败时为 <c>null</c>。</summary>
    public CdnNodeLatency? Best => _Best(Nodes);

    /// <summary>延迟最低的可用 IPv4 节点。</summary>
    public CdnNodeLatency? BestIPv4 => _Best(IPv4Nodes);

    /// <summary>延迟最低的可用 IPv6 节点。</summary>
    public CdnNodeLatency? BestIPv6 => _Best(IPv6Nodes);

    private static IReadOnlyList<CdnNodeLatency> _Split(IReadOnlyList<CdnNodeLatency> nodes, bool ipv6) => nodes
        .Where(node => node.IsIPv6 == ipv6)
        .OrderBy(node => node.MedianMs)
        .ThenBy(node => node.AverageMs)
        .ToArray();

    private static CdnNodeLatency? _Best(IReadOnlyList<CdnNodeLatency> nodes) => nodes
        .Where(node => node.IsReachable)
        .OrderBy(node => node.MedianMs)
        .ThenBy(node => node.AverageMs)
        .FirstOrDefault();
}

/// <summary>
///     探测进度。
/// </summary>
/// <param name="Completed">已完成的探测次数</param>
/// <param name="Total">总探测次数</param>
/// <param name="Host">当前批次涉及的域名</param>
/// <param name="Node">当前批次涉及的节点</param>
/// <param name="Round">当前轮次，从 1 开始</param>
/// <param name="Rounds">总轮次</param>
public readonly record struct CdnProbeProgress(
    int Completed,
    int Total,
    string Host,
    string Node,
    int Round,
    int Rounds)
{
    /// <summary>整体进度，取值 0~1。</summary>
    public double Ratio => Total <= 0 ? 0d : Math.Clamp((double)Completed / Total, 0d, 1d);
}

/// <summary>
///     一组同类下载域名，用于在界面上分组展示。
/// </summary>
/// <param name="TitleKey">分组标题的本地化键</param>
/// <param name="Hosts">该组包含的域名</param>
public sealed record CdnHostGroup(string TitleKey, IReadOnlyList<string> Hosts);

/// <summary>
///     官方 CDN 节点优选器。
///     <p>
///         对下载相关域名解析出的全部节点做 TCP 443 探测：每个节点连续探测 15 轮，
///         以中位数作为排序依据（抗抖动），并分批并发以降低瞬时连接速率，避免被 CDN 误判为异常流量。
///     </p>
/// </summary>
public static class OfficialCdnOptimizer
{
    private const string ModuleName = "OfficialCdnOptimizer";

    /// <summary>每个节点的连续探测次数。</summary>
    public const int Rounds = 15;

    /// <summary>单批并发探测的节点数。</summary>
    public const int BatchSize = 8;

    /// <summary>单次探测的超时（含 TCP 连接与 TLS 握手）。</summary>
    public const int ConnectTimeoutMs = 3000;

    /// <summary>
    ///     连续失败到该轮次仍无任何成功样本的节点，放弃其剩余探测。
    ///     <p>
    ///         模组加载器与 CurseForge 的部分域名在部分网络下完全不可达，
    ///         不提前放弃的话每个节点都要耗满 15 × 3s，整轮优选会被拖到几分钟。
    ///     </p>
    /// </summary>
    public const int AbandonAfterRounds = 4;

    private const int BatchDelayMs = 120;
    private const int RoundDelayMs = 150;
    private const int ProbePort = 443;

    /// <summary>
    ///     参与优选的下载域名，按用途分组，顺序与 UI 展示顺序一致。
    /// </summary>
    public static readonly IReadOnlyList<CdnHostGroup> Groups =
    [
        new("Setup.DownloadSource.Group.Mojang",
        [
            "launchermeta.mojang.com",
            "piston-meta.mojang.com",
            "piston-data.mojang.com",
            "launcher.mojang.com",
            "libraries.minecraft.net",
            "resources.download.minecraft.net"
        ]),
        new("Setup.DownloadSource.Group.Loader",
        [
            "files.minecraftforge.net",
            "maven.minecraftforge.net",
            "maven.neoforged.net",
            "maven.fabricmc.net",
            "meta.fabricmc.net"
        ]),
        new("Setup.DownloadSource.Group.CurseForge",
        [
            "api.curseforge.com",
            "edge.forgecdn.net",
            "mediafilez.forgecdn.net"
        ]),
        new("Setup.DownloadSource.Group.Modrinth",
        [
            "api.modrinth.com",
            "cdn.modrinth.com"
        ])
    ];

    /// <summary>
    ///     全部参与优选的域名，扁平化后的结果。
    /// </summary>
    public static readonly string[] Hosts = Groups.SelectMany(group => group.Hosts).ToArray();

    /// <summary>
    ///     对列表中全部下载域名执行节点优选。
    /// </summary>
    /// <param name="progress">进度回调，会在工作线程上触发，UI 侧请使用 <see cref="Progress{T}" /></param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async Task<IReadOnlyList<CdnHostLatency>> ProbeAsync(
        IProgress<CdnProbeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 先完成全部解析，才能判断哪些域名挂在 Cloudflare 后面
        var resolved = new Dictionary<string, IPAddress[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in Hosts)
        {
            var addresses = await ResolveAllAsync(host, cancellationToken).ConfigureAwait(false);
            if (addresses.Length == 0)
                LogWrapper.Warn(ModuleName, $"域名 {host} 未解析到任何节点，已跳过");
            resolved[host] = addresses;
        }

        // 解析结果全部落在 Cloudflare 网段内，说明该站点接在 CF 后面，改用优选 IP 列表
        var cloudflareHosts = Hosts
            .Where(host => resolved[host].Length > 0 && resolved[host].All(CloudflareEndpoint.IsCloudflare))
            .ToArray();

        var preferredPool = Array.Empty<IPAddress>();
        if (cloudflareHosts.Length > 0)
        {
            preferredPool = (await CloudflareEndpoint.GetPreferredAsync(cancellationToken)
                .ConfigureAwait(false)).ToArray();
            if (preferredPool.Length > 0)
                LogWrapper.Info(ModuleName,
                    $"{cloudflareHosts.Length} 个 Cloudflare 域名改用优选节点（{preferredPool.Length} 个候选）");
            else
                LogWrapper.Warn(ModuleName, "优选节点不可用，Cloudflare 域名回退到 DNS 节点");
        }

        // CF 是 Anycast，一组优选 IP 对所有 CF 域名通用，所以整池只探测一次，各域名共用测量结果
        var poolHost = preferredPool.Length > 0 ? cloudflareHosts[0] : null;

        var nodes = new List<ProbeNode>();
        foreach (var host in Hosts)
        {
            if (poolHost is not null && cloudflareHosts.Contains(host))
            {
                if (host != poolHost) continue;
                nodes.AddRange(preferredPool.Select(address => new ProbeNode(host, address)));
                continue;
            }

            nodes.AddRange(resolved[host].Select(address => new ProbeNode(host, address)));
        }

        if (nodes.Count == 0) return [];

        var total = nodes.Count * Rounds;
        var completed = 0;

        // 已提前放弃的节点会被移出 active，但它们剩余的轮次仍计入 completed，保证进度条能走到 100%
        var active = new List<ProbeNode>(nodes);

        for (var round = 1; round <= Rounds && active.Count > 0; round++)
        {
            for (var offset = 0; offset < active.Count; offset += BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = active.Skip(offset).Take(BatchSize).ToArray();
                await Task.WhenAll(batch.Select(node => _ProbeOnceAsync(node, cancellationToken)))
                    .ConfigureAwait(false);

                completed += batch.Length;
                progress?.Report(new CdnProbeProgress(
                    completed,
                    total,
                    batch[0].Host,
                    string.Join(", ", batch.Select(node => node.Address)),
                    round,
                    Rounds));

                await Task.Delay(BatchDelayMs, cancellationToken).ConfigureAwait(false);
            }

            if (round >= AbandonAfterRounds) _AbandonUnreachable(active, round, ref completed);

            await Task.Delay(RoundDelayMs, cancellationToken).ConfigureAwait(false);
        }

        return Hosts
            .Select(host =>
            {
                // CF 域名共用优选池的探测结果，节点按延迟重排后各自持有一份
                var fromPool = poolHost is not null && cloudflareHosts.Contains(host);
                var source = fromPool ? poolHost! : host;
                return new CdnHostLatency(
                    host,
                    nodes.Where(node => node.Host == source)
                        .Select(node => node.ToResult())
                        .OrderBy(result => result.MedianMs)
                        .ThenBy(result => result.AverageMs)
                        .ToArray())
                {
                    FromPreferredPool = fromPool
                };
            })
            .ToArray();
    }

    /// <summary>
    ///     把连续多轮都没有成功样本的节点移出探测队列，并把它们剩余的轮次记为已完成。
    /// </summary>
    private static void _AbandonUnreachable(List<ProbeNode> active, int round, ref int completed)
    {
        for (var index = active.Count - 1; index >= 0; index--)
        {
            var node = active[index];
            if (node.SuccessCount > 0) continue;

            completed += Rounds - round;
            active.RemoveAt(index);
            LogWrapper.Debug(ModuleName,
                $"节点 {node.Address}（{node.Host}）连续 {round} 轮无成功样本，放弃剩余探测");
        }
    }

    /// <summary>
    ///     把每个域名延迟最低的 IPv4 与 IPv6 节点写入锁定表，并打开锁定开关。仅写入成功连通的节点。
    /// </summary>
    /// <returns>写入的域名数量。</returns>
    public static int ApplyBestNodes(IEnumerable<CdnHostLatency> results)
    {
        var pins = new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in results)
        {
            var addresses = CdnNodePin.Combine(host.BestIPv4?.Address, host.BestIPv6?.Address);
            if (addresses.Count > 0) pins[host.Host] = addresses;
        }

        if (pins.Count == 0) return 0;

        CdnNodePin.SetAddresses(pins);
        CdnNodePin.SetEnabled(true);
        return pins.Count;
    }

    private static bool _IsDoHEnabled()
    {
        try
        {
            return Config.Network.EnableDoH;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    ///     解析域名到全部地址。与请求链路保持一致：启用 DoH 时优先使用 DoH 的结果，否则使用系统 DNS。
    ///     <see cref="CloudflareEndpoint" /> 也用它解析优选域名，保证两处行为一致。
    /// </summary>
    internal static async Task<IPAddress[]> ResolveAllAsync(string host, CancellationToken cancellationToken)    {
        // 与请求链路保持一致：启用 DoH 时优先使用 DoH 的结果，否则使用系统 DNS
        if (_IsDoHEnabled())
        {
            try
            {
                var dohResult = await DnsQuery.Instance.QueryForIpAsync(host, cancellationToken)
                    .ConfigureAwait(false);
                if (dohResult is { Length: > 0 })
                    return dohResult.Distinct().ToArray();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogWrapper.Warn(ex, ModuleName, $"DoH 解析 {host} 失败，回退到系统 DNS");
            }
        }

        try
        {
            var systemResult = await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken)
                .ConfigureAwait(false);
            return systemResult.Distinct().ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogWrapper.Warn(ex, ModuleName, $"系统 DNS 解析 {host} 失败");
            return [];
        }
    }

    private static async Task _ProbeOnceAsync(ProbeNode node, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeoutMs);

        var watch = Stopwatch.StartNew();
        var succeeded = false;
        try
        {
            using var socket = new Socket(node.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            await socket.ConnectAsync(new IPEndPoint(node.Address, ProbePort), timeout.Token)
                .ConfigureAwait(false);

            // 只做 TCP 连接会被本机代理的 fake-ip 或 TUN 模式瞬间「成功」，量出来的不是真实往返耗时。
            // 因此额外完成一次 TLS 握手：既包含真实网络往返，也顺带验证该节点确实为该域名提供服务。
            var stream = new NetworkStream(socket, ownsSocket: false);
            await using var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = node.Host,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, timeout.Token).ConfigureAwait(false);

            succeeded = true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // 超时、握手失败或证书异常，均计为失败样本
        }

        watch.Stop();
        if (succeeded)
            node.Samples.Add(watch.Elapsed.TotalMilliseconds);
        else
            node.Failures++;
    }

    private sealed class ProbeNode(string host, IPAddress address)
    {
        public string Host { get; } = host;

        public IPAddress Address { get; } = address;

        public List<double> Samples { get; } = [];

        public int Failures;

        /// <summary>成功探测到的样本数，用于判断该节点是否值得继续探测。</summary>
        public int SuccessCount
        {
            get
            {
                lock (Samples)
                {
                    return Samples.Count;
                }
            }
        }

        public CdnNodeLatency ToResult()
        {
            double[] samples;
            int failures;
            lock (Samples)
            {
                samples = [.. Samples];
                failures = Failures;
            }

            if (samples.Length == 0)
                return new CdnNodeLatency(Address, double.PositiveInfinity, double.PositiveInfinity, 0,
                    samples.Length + failures);

            Array.Sort(samples);
            var median = samples.Length % 2 == 1
                ? samples[samples.Length / 2]
                : (samples[samples.Length / 2 - 1] + samples[samples.Length / 2]) / 2d;

            return new CdnNodeLatency(Address, median, samples.Average(), samples.Length, samples.Length + failures);
        }
    }
}
