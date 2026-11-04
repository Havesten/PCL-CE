using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Logging;

namespace PCL.Core.IO.Net.Cdn;

/// <summary>
///     单个节点的带宽测速结果。
/// </summary>
/// <param name="Address">节点地址</param>
/// <param name="BytesPerSecond">平均速率（字节/秒）</param>
/// <param name="TotalBytes">成功样本累计字节数</param>
/// <param name="TotalSeconds">成功样本累计耗时</param>
/// <param name="SuccessCount">成功样本数</param>
/// <param name="TotalCount">样本总数</param>
public sealed record CdnNodeSpeed(
    IPAddress Address,
    double BytesPerSecond,
    long TotalBytes,
    double TotalSeconds,
    int SuccessCount,
    int TotalCount)
{
    /// <summary>是否至少成功完成一次采样。</summary>
    public bool IsReachable => SuccessCount > 0;

    /// <summary>平均速率，单位为 MiB/s。</summary>
    public double MebibytesPerSecond => BytesPerSecond / 1024d / 1024d;

    /// <summary>是否为 IPv6 节点。</summary>
    public bool IsIPv6 => Address.AddressFamily == AddressFamily.InterNetworkV6;

    /// <summary>地址族标签，用于界面展示。</summary>
    public string FamilyLabel => IsIPv6 ? "IPv6" : "IPv4";
}

/// <summary>
///     单个域名的带宽测速结果。
///     <p>IPv4 与 IPv6 各自独立给出速率最高的节点，互不挤占名额。</p>
/// </summary>
public sealed record CdnHostSpeed(string Host, IReadOnlyList<CdnNodeSpeed> Nodes)
{
    /// <summary>仅 IPv4 节点，按速率从高到低排列。</summary>
    public IReadOnlyList<CdnNodeSpeed> IPv4Nodes { get; } = _Split(Nodes, ipv6: false);

    /// <summary>仅 IPv6 节点，按速率从高到低排列。</summary>
    public IReadOnlyList<CdnNodeSpeed> IPv6Nodes { get; } = _Split(Nodes, ipv6: true);

    /// <summary>平均速率最高的节点，全部失败时为 <c>null</c>。</summary>
    public CdnNodeSpeed? Best => _Best(Nodes);

    /// <summary>平均速率最高的 IPv4 节点。</summary>
    public CdnNodeSpeed? BestIPv4 => _Best(IPv4Nodes);

    /// <summary>平均速率最高的 IPv6 节点。</summary>
    public CdnNodeSpeed? BestIPv6 => _Best(IPv6Nodes);

    private static IReadOnlyList<CdnNodeSpeed> _Split(IReadOnlyList<CdnNodeSpeed> nodes, bool ipv6) => nodes
        .Where(node => node.IsIPv6 == ipv6)
        .OrderByDescending(node => node.BytesPerSecond)
        .ToArray();

    private static CdnNodeSpeed? _Best(IReadOnlyList<CdnNodeSpeed> nodes) => nodes
        .Where(node => node.IsReachable)
        .OrderByDescending(node => node.BytesPerSecond)
        .FirstOrDefault();
}

/// <summary>
///     带宽测速进度。
/// </summary>
/// <param name="Completed">已完成节点数</param>
/// <param name="Total">参与测速的节点总数</param>
/// <param name="Host">当前域名</param>
/// <param name="Node">当前节点</param>
/// <param name="MebibytesPerSecond">当前节点的实时平均速率</param>
/// <param name="Sample">当前采样序号，从 1 开始</param>
/// <param name="Samples">单个节点的采样总数</param>
public readonly record struct CdnSpeedProgress(
    int Completed,
    int Total,
    string Host,
    string Node,
    double MebibytesPerSecond,
    int Sample,
    int Samples)
{
    /// <summary>整体进度，取值 0~1。</summary>
    public double Ratio => Total <= 0 ? 0d : Math.Clamp((double)Completed / Total, 0d, 1d);
}

/// <summary>
///     官方 CDN 节点带宽测速器。
///     <p>
///         对每个域名按延迟从低到高取前若干个节点，逐个（串行，避免相互抢占带宽）下载同一份测速载荷，
///         每个节点重复采样若干次，以「累计字节数 ÷ 累计耗时」作为平均速率，最终选出速率最高的节点。
///     </p>
/// </summary>
public static class OfficialCdnSpeedTester
{
    private const string ModuleName = "OfficialCdnSpeedTester";

    /// <summary>每个域名参与带宽测速的节点数（按延迟取前 N）。</summary>
    public const int TopNodes = 5;

    /// <summary>单个节点的采样次数。</summary>
    public const int Samples = 3;

    /// <summary>单次采样的字节上限，达到即停止，避免无谓流量。</summary>
    public const int MaxSampleBytes = 1024 * 1024;

    /// <summary>单次采样的时间上限。</summary>
    public const int SampleTimeoutMs = 2500;

    private const int SampleGapMs = 120;
    private const int ReadBufferSize = 64 * 1024;

    /// <summary>
    ///     对延迟测速结果中每个域名的前 <see cref="TopNodes" /> 个节点执行带宽测速。
    ///     <para>
    ///         Cloudflare 站点共用同一组优选 IP，因此只对第一个 CF 域名实测，其余直接复用结果，
    ///         否则同一批 IP 会被每个 CF 域名各测一遍，耗时成倍增加。
    ///     </para>
    /// </summary>
    /// <param name="latencyResults">延迟测速结果，用于确定候选节点及其顺序</param>
    /// <param name="progress">进度回调</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async Task<IReadOnlyList<CdnHostSpeed>> ProbeAsync(
        IReadOnlyList<CdnHostLatency> latencyResults,
        IProgress<CdnSpeedProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var targets = await _ResolveTargetsAsync(cancellationToken).ConfigureAwait(false);
        var plans = new List<(CdnHostLatency Latency, string Url, IPAddress[] Addresses)>();
        var sharedPoolHosts = new List<string>();
        var poolPlanned = false;

        foreach (var host in latencyResults)
        {
            if (!targets.TryGetValue(host.Host, out var url))
            {
                LogWrapper.Debug(ModuleName, $"域名 {host.Host} 没有可用的测速载荷，跳过带宽测速");
                continue;
            }

            // 优选池是共享的，第一个 CF 域名测完就够了
            if (host.FromPreferredPool && poolPlanned)
            {
                sharedPoolHosts.Add(host.Host);
                continue;
            }

            // IPv4 与 IPv6 各自取延迟最低的前 N 个，互不挤占名额：
            // 混在一起排序时，延迟明显偏高的一族会被另一族整体顶掉，永远得不到测速机会。
            var addresses = new List<IPAddress>(TopNodes * 2);
            addresses.AddRange(host.IPv4Nodes.Where(node => node.IsReachable)
                .Take(TopNodes)
                .Select(node => node.Address));
            addresses.AddRange(host.IPv6Nodes.Where(node => node.IsReachable)
                .Take(TopNodes)
                .Select(node => node.Address));
            if (addresses.Count == 0) continue;

            if (host.FromPreferredPool) poolPlanned = true;
            plans.Add((host, url, addresses.ToArray()));
        }

        var totalNodes = plans.Sum(plan => plan.Addresses.Length);
        var results = new List<CdnHostSpeed>();
        var completed = 0;
        CdnNodeSpeed[]? poolSpeeds = null;

        foreach (var (latency, url, addresses) in plans)
        {
            var nodeResults = new List<CdnNodeSpeed>(addresses.Length);
            foreach (var address in addresses)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 串行测速：同一时刻只压一个节点，否则各节点会互相抢占本机带宽
                var result = await _MeasureNodeAsync(latency.Host, url, address, completed, totalNodes,
                    progress, cancellationToken).ConfigureAwait(false);
                nodeResults.Add(result);
                completed++;

                progress?.Report(new CdnSpeedProgress(completed, totalNodes, latency.Host, address.ToString(),
                    result.MebibytesPerSecond, Samples, Samples));
            }

            var ordered = nodeResults.OrderByDescending(node => node.BytesPerSecond).ToArray();
            results.Add(new CdnHostSpeed(latency.Host, ordered));
            if (latency.FromPreferredPool) poolSpeeds = ordered;
        }

        if (poolSpeeds is not null)
            foreach (var host in sharedPoolHosts)
                results.Add(new CdnHostSpeed(host, poolSpeeds));

        return results;
    }

    /// <summary>
    ///     把每个域名平均速率最高的 IPv4 与 IPv6 节点写入锁定表，并打开锁定开关。没有测出结果的域名保持原有锁定。
    /// </summary>
    /// <returns>写入的域名数量。</returns>
    public static int ApplyBestBySpeed(IEnumerable<CdnHostSpeed> results)
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

    private static async Task<CdnNodeSpeed> _MeasureNodeAsync(
        string host,
        string url,
        IPAddress address,
        int completedNodes,
        int totalNodes,
        IProgress<CdnSpeedProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            // 锁定与测速都以「直连该 IP」为前提，走代理时目标会变成代理地址，量到的不是节点速度
            UseProxy = false,
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token)
                        .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        long totalBytes = 0;
        var totalSeconds = 0d;
        var success = 0;

        for (var sample = 0; sample < Samples; sample++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var sampleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sampleCts.CancelAfter(SampleTimeoutMs);

            var (bytes, seconds) = (0L, 0d);
            var watch = new Stopwatch();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
                using var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, sampleCts.Token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(sampleCts.Token)
                    .ConfigureAwait(false);

                var buffer = new byte[ReadBufferSize];
                while (bytes < MaxSampleBytes)
                {
                    var read = await stream.ReadAsync(buffer, sampleCts.Token).ConfigureAwait(false);
                    if (read <= 0) break;
                    if (!watch.IsRunning) watch.Start();
                    bytes += read;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogWrapper.Debug(ex, ModuleName, $"节点 {address} 测速采样失败（{host}）");
            }
            finally
            {
                // 触达时间上限时已经被取消，但此前读到的字节仍然有效，不能整段丢弃
                if (watch.IsRunning) watch.Stop();
                seconds = watch.Elapsed.TotalSeconds;
                if (bytes > 0 && seconds > 0) success++;
            }

            if (bytes > 0 && seconds > 0)
            {
                totalBytes += bytes;
                totalSeconds += seconds;
            }

            progress?.Report(new CdnSpeedProgress(completedNodes, totalNodes, host, address.ToString(),
                totalSeconds > 0 ? totalBytes / totalSeconds / 1024d / 1024d : 0d, sample + 1, Samples));

            await Task.Delay(SampleGapMs, cancellationToken).ConfigureAwait(false);
        }

        var rate = totalSeconds > 0 ? totalBytes / totalSeconds : 0d;
        return new CdnNodeSpeed(address, rate, totalBytes, totalSeconds, success, Samples);
    }

    /// <summary>
    ///     解析各域名可用的测速载荷。
    ///     <p>
    ///         固定地址只适用于少数域名；其余域名从官方版本清单或公开 API 里取真实文件地址，
    ///         这样才能保证测的是该域名自己的分发路径。
    ///     </p>
    ///     <p>
    ///         没有载荷的域名（例如只提供版本清单的 files.minecraftforge.net、需要 API Key 的
    ///         api.curseforge.com）会跳过带宽测速，只保留延迟优选的结果。
    ///     </p>
    /// </summary>
    private static async Task<Dictionary<string, string>> _ResolveTargetsAsync(CancellationToken cancellationToken)
    {
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["launchermeta.mojang.com"] = "https://launchermeta.mojang.com/mc/game/version_manifest.json",
            ["piston-meta.mojang.com"] = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json",
            ["libraries.minecraft.net"] =
                "https://libraries.minecraft.net/com/google/guava/guava/32.1.2-jre/guava-32.1.2-jre.jar",
            // Maven 仓库的构件永久保留，写死具体版本是安全的
            ["maven.fabricmc.net"] =
                "https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.15.0/fabric-loader-0.15.0.jar",
            ["maven.minecraftforge.net"] =
                "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.2.0/forge-1.20.1-47.2.0-installer.jar",
            ["maven.neoforged.net"] =
                "https://maven.neoforged.net/releases/net/neoforged/neoforge/20.4.167/neoforge-20.4.167-installer.jar"
        };

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        try
        {
            var manifest = JsonNode.Parse(await client
                .GetStringAsync("https://launchermeta.mojang.com/mc/game/version_manifest_v2.json", cancellationToken)
                .ConfigureAwait(false));

            var releaseId = manifest?["latest"]?["release"]?.ToString();
            var versionUrl = (manifest?["versions"] as JsonArray)?
                .OfType<JsonObject>()
                .FirstOrDefault(version => version["id"]?.ToString() == releaseId)?["url"]?.ToString();
            if (!string.IsNullOrEmpty(versionUrl))
            {
                var versionJson = JsonNode.Parse(await client.GetStringAsync(versionUrl, cancellationToken)
                    .ConfigureAwait(false));

                var clientUrl = versionJson?["downloads"]?["client"]?["url"]?.ToString();
                if (!string.IsNullOrEmpty(clientUrl))
                    targets["piston-data.mojang.com"] = clientUrl;

                // assetIndex 位于版本 JSON 的根节点，不在 downloads 下
                var assetIndexUrl = versionJson?["assetIndex"]?["url"]?.ToString()
                                    ?? versionJson?["downloads"]?["assetIndex"]?["url"]?.ToString();
                if (!string.IsNullOrEmpty(assetIndexUrl))
                {
                    var assetIndex = JsonNode.Parse(await client.GetStringAsync(assetIndexUrl, cancellationToken)
                        .ConfigureAwait(false));
                    var hash = (assetIndex?["objects"] as JsonObject)?
                        .Select(pair => pair.Value as JsonObject)
                        .Where(obj => obj is not null)
                        .Select(obj => (hash: obj!["hash"]?.ToString(), size: _ReadSize(obj["size"])))
                        .Where(item => !string.IsNullOrEmpty(item.hash) && item.size >= 256 * 1024)
                        .OrderByDescending(item => item.size)
                        .Select(item => item.hash)
                        .FirstOrDefault();

                    if (!string.IsNullOrEmpty(hash))
                        targets["resources.download.minecraft.net"] =
                            $"https://resources.download.minecraft.net/{hash![..2]}/{hash}";
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogWrapper.Warn(ex, ModuleName, "解析 Mojang 测速载荷失败，将只测速固定地址的域名");
        }

        try
        {
            // Modrinth 的 CDN 文件地址带项目与版本 ID，现取一个足够大的文件，避免把 ID 写死
            var payload = await _ResolveModrinthPayloadAsync(client, cancellationToken).ConfigureAwait(false);
            if (payload is not null) targets["cdn.modrinth.com"] = payload;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogWrapper.Warn(ex, ModuleName, "解析 Modrinth 测速载荷失败，跳过 cdn.modrinth.com 的带宽测速");
        }

        return targets;
    }

    /// <summary>
    ///     从 Modrinth 公开 API 取一个体积足够大的模组文件地址，用作 CDN 带宽测速载荷。
    /// </summary>
    private static async Task<string?> _ResolveModrinthPayloadAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var search = JsonNode.Parse(await client.GetStringAsync(
            "https://api.modrinth.com/v2/search?limit=3&facets=%5B%5B%22project_type%3Amod%22%5D%5D",
            cancellationToken).ConfigureAwait(false));

        var projectId = (search?["hits"] as JsonArray)?
            .OfType<JsonObject>()
            .Select(hit => hit["project_id"]?.ToString())
            .FirstOrDefault(id => !string.IsNullOrEmpty(id));
        if (string.IsNullOrEmpty(projectId)) return null;

        var versions = JsonNode.Parse(await client.GetStringAsync(
            $"https://api.modrinth.com/v2/project/{projectId}/version",
            cancellationToken).ConfigureAwait(false)) as JsonArray;
        if (versions is null) return null;

        return versions
            .OfType<JsonObject>()
            .SelectMany(version => (version["files"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(file => (url: file["url"]?.ToString(), size: _ReadSize(file["size"])))
            .Where(item => !string.IsNullOrEmpty(item.url) && item.size >= 512 * 1024)
            .OrderByDescending(item => item.size)
            .Select(item => item.url)
            .FirstOrDefault();
    }

    /// <summary>
    ///     JSON 数字的底层表示可能是 JsonElement，直接 GetValue&lt;long&gt; 会因类型不匹配抛异常。
    /// </summary>
    private static long _ReadSize(JsonNode? node)
    {
        if (node is not JsonValue value) return 0L;
        if (value.TryGetValue<long>(out var longValue)) return longValue;
        if (value.TryGetValue<int>(out var intValue)) return intValue;
        if (value.TryGetValue<double>(out var doubleValue)) return (long)doubleValue;
        return 0L;
    }
}
