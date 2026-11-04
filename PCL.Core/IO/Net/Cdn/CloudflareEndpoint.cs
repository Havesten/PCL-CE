using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Logging;

namespace PCL.Core.IO.Net.Cdn;

/// <summary>
///     Cloudflare 优选节点。
///     <para>
///         Cloudflare 是 Anycast：任意一个边缘 IP 都能服务所有接入 CF 的域名，只要 TLS 的 SNI 仍然是原域名。
///         因此对 CF 站点来说「优选」的是一组 IP，而不是某个域名自己的 DNS 解析结果 —— 国内常用的优选做法正是如此。
///     </para>
///     <para>
///         优选节点由 <see cref="PreferredDomain"/> 的 A / AAAA 记录直接给出（该域名只用来发布优选 IP，
///         并不承载网页），取到之后仍然会逐个探测延迟，最终锁定最快的一个。
///         拿不到记录时调用方应回退到常规 DNS 节点；真正建连时 URI 仍是原地址，只是 TCP 目标换成优选 IP，
///         证书校验与 Host 头不受影响。
///     </para>
/// </summary>
public static class CloudflareEndpoint
{
    private const string ModuleName = "CloudflareEndpoint";

    /// <summary>
    ///     优选 IP 的发布域名：它的 A / AAAA 记录就是优选出来的 Cloudflare 边缘地址。
    /// </summary>
    public const string PreferredDomain = "cloudflare.182682.xyz";

    /// <summary>每个地址族最多取多少个候选节点参与测速。</summary>
    public const int MaxPerFamily = 4;

    /// <summary>列表缓存时长，过期后重新解析。</summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    /// <summary>解析失败后的重试间隔，避免优选域名不可用时每个域名都去撞一次。</summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(10);

    /// <summary>Cloudflare 公布的 IPv4 网段。</summary>
    private static readonly string[] IPv4Ranges =
    [
        "173.245.48.0/20",
        "103.21.244.0/22",
        "103.22.200.0/22",
        "103.31.4.0/22",
        "141.101.64.0/18",
        "108.162.192.0/18",
        "190.93.240.0/20",
        "188.114.96.0/20",
        "197.234.240.0/22",
        "198.41.128.0/17",
        "162.158.0.0/15",
        "104.16.0.0/13",
        "104.24.0.0/14",
        "172.64.0.0/13",
        "131.0.72.0/22"
    ];

    /// <summary>Cloudflare 公布的 IPv6 网段。</summary>
    private static readonly string[] IPv6Ranges =
    [
        "2400:cb00::/32",
        "2606:4700::/32",
        "2803:f800::/32",
        "2405:b500::/32",
        "2405:8100::/32",
        "2a06:98c0::/29",
        "2c0f:f248::/32"
    ];

    private static readonly IPNetwork[] Networks = IPv4Ranges
        .Concat(IPv6Ranges)
        .Select(IPNetwork.Parse)
        .ToArray();

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IReadOnlyList<IPAddress> _cached = [];
    private static DateTime _cachedAt = DateTime.MinValue;
    private static bool _cacheValid;

    /// <summary>
    ///     地址是否属于 Cloudflare 的边缘网段。
    /// </summary>
    public static bool IsCloudflare(IPAddress address)
    {
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        foreach (var network in Networks)
        {
            if (network.BaseAddress.AddressFamily != normalized.AddressFamily) continue;
            if (network.Contains(normalized)) return true;
        }

        return false;
    }

    /// <summary>
    ///     取 Cloudflare 优选节点：解析 <see cref="PreferredDomain"/> 的 A / AAAA 记录。
    ///     结果按 IPv4 在前、IPv6 在后排列，各自最多 <see cref="MaxPerFamily" /> 个。
    ///     返回空集合表示当前拿不到列表，调用方应回退到常规 DNS 节点。
    /// </summary>
    public static async Task<IReadOnlyList<IPAddress>> GetPreferredAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        if (_cacheValid && now - _cachedAt < (_cached.Count > 0 ? CacheDuration : FailureBackoff)) return _cached;

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTime.UtcNow;
            if (_cacheValid && now - _cachedAt < (_cached.Count > 0 ? CacheDuration : FailureBackoff)) return _cached;

            _cached = await _ResolveAsync(cancellationToken).ConfigureAwait(false);
            _cachedAt = DateTime.UtcNow;
            _cacheValid = true;
            return _cached;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<IReadOnlyList<IPAddress>> _ResolveAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 与请求链路共用同一套解析逻辑（启用了 DoH 就走 DoH）
            var addresses = await OfficialCdnOptimizer
                .ResolveAllAsync(PreferredDomain, cancellationToken)
                .ConfigureAwait(false);

            // 解析结果可能被劫持或混入非 CF 地址，只接受落在 Cloudflare 网段内的
            var usable = addresses.Where(IsCloudflare).ToArray();
            if (usable.Length == 0)
            {
                LogWrapper.Warn(ModuleName,
                    addresses.Length == 0
                        ? $"优选域名 {PreferredDomain} 未解析到任何地址"
                        : $"优选域名 {PreferredDomain} 解析结果中没有 Cloudflare 地址，已忽略");
                return [];
            }

            var result = usable.Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Take(MaxPerFamily)
                .Concat(usable.Where(address => address.AddressFamily == AddressFamily.InterNetworkV6)
                    .Take(MaxPerFamily))
                .ToArray();

            LogWrapper.Info(ModuleName,
                $"已从 {PreferredDomain} 获取 {result.Length} 个优选节点（解析到 {addresses.Length} 个地址）");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, ModuleName, $"获取 Cloudflare 优选节点失败，将回退到 DNS 节点");
            return [];
        }
    }
}
