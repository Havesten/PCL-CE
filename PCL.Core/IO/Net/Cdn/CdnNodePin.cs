using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading;
using PCL.Core.App;
using PCL.Core.Logging;

namespace PCL.Core.IO.Net.Cdn;

/// <summary>
///     官方 CDN 节点锁定表。
///     <p>
///         记录每个下载域名优选出的节点 IP，供 <see cref="Http.HostConnectionHandler" /> 在建立 TCP 连接时直接命中，
///         从而跳过系统 DNS 解析。TLS 握手仍使用请求 URI 中的主机名作为 SNI，因此证书校验与 Host 头不受影响。
///     </p>
///     <p>
///         IPv4 与 IPv6 是两条独立的路径，因此每个域名最多可同时锁定一个 v4 与一个 v6 节点。
///         建连侧会并发尝试这两条路径并取先成功者，单栈不可用时不会拖累整体下载。
///     </p>
/// </summary>
public static class CdnNodePin
{
    private const string ModuleName = "CdnNodePin";

    /// <summary>每个域名最多锁定的地址数：v4 与 v6 各一个。</summary>
    private const int MaxAddressesPerHost = 2;

    private static readonly Lock Sync = new();
    private static readonly Dictionary<string, IPAddress[]> Pins = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>最近一次从配置读到的原始 JSON，用于避免重复解析。</summary>
    private static string? _cachedRaw;

    /// <summary>开关的内存缓存：配置服务尚未就绪时以它为准。</summary>
    private static bool _enabledCache;

    /// <summary>
    ///     设置锁定开关，并同步到配置。
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        lock (Sync)
        {
            _enabledCache = enabled;
        }

        try
        {
            Config.Download.CdnPinEnabled = enabled;
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, ModuleName, "写入 CDN 节点锁定开关失败");
        }
    }

    /// <summary>
    ///     锁定是否生效：开关已打开且至少存在一个有效节点。
    /// </summary>
    public static bool IsActive
    {
        get
        {
            if (!_ReadEnabled()) return false;
            lock (Sync)
            {
                _RefreshLocked();
                return Pins.Count > 0;
            }
        }
    }

    /// <summary>
    ///     尝试取出某个域名锁定的全部节点地址（可能包含 IPv4 与 IPv6 各一个）。
    ///     <p>返回的数组由内部持有，调用方不得修改其内容。</p>
    /// </summary>
    public static bool TryGetPins(string host, out IReadOnlyList<IPAddress> addresses)
    {
        lock (Sync)
        {
            _RefreshLocked();
            if (Pins.TryGetValue(host, out var stored))
            {
                addresses = stored;
                return true;
            }

            addresses = [];
            return false;
        }
    }

    /// <summary>
    ///     尝试取出某个域名锁定的首选节点地址（IPv4 优先）。
    /// </summary>
    public static bool TryGetPin(string host, out IPAddress address)
    {
        if (TryGetPins(host, out var addresses) && addresses.Count > 0)
        {
            address = addresses[0];
            return true;
        }

        address = IPAddress.None;
        return false;
    }

    /// <summary>
    ///     获取当前锁定表的快照。
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<IPAddress>> Snapshot()
    {
        lock (Sync)
        {
            _RefreshLocked();
            return Pins.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<IPAddress>)pair.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    ///     锁定单个域名到单个节点，并立即持久化（会覆盖原有的 IPv4 与 IPv6 锁定）。
    /// </summary>
    public static void SetPin(string host, IPAddress address)
    {
        SetAddresses(host, [address]);
    }

    /// <summary>
    ///     批量锁定单节点映射并只持久化一次。
    /// </summary>
    public static void SetPins(IReadOnlyDictionary<string, IPAddress> pins)
    {
        SetAddresses(pins.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<IPAddress>)[pair.Value],
            StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     锁定单个域名，可同时包含 IPv4 与 IPv6 节点；传入空集合等同于解除锁定。
    /// </summary>
    public static void SetAddresses(string host, IEnumerable<IPAddress> addresses)
    {
        var normalized = _Normalize(addresses);
        lock (Sync)
        {
            _RefreshLocked();
            if (normalized.Length == 0)
                Pins.Remove(host);
            else
                Pins[host] = normalized;
            _SaveLocked();
        }
    }

    /// <summary>
    ///     批量锁定多地址映射并只持久化一次。
    /// </summary>
    public static void SetAddresses(IReadOnlyDictionary<string, IReadOnlyList<IPAddress>> pins)
    {
        lock (Sync)
        {
            _RefreshLocked();
            foreach (var (host, addresses) in pins)
            {
                var normalized = _Normalize(addresses);
                if (normalized.Length == 0) continue;
                Pins[host] = normalized;
            }

            _SaveLocked();
        }
    }

    /// <summary>
    ///     解除单个域名的锁定。
    /// </summary>
    public static void RemovePin(string host)
    {
        lock (Sync)
        {
            _RefreshLocked();
            if (!Pins.Remove(host)) return;
            _SaveLocked();
        }
    }

    /// <summary>
    ///     清空全部锁定并关闭锁定开关。
    /// </summary>
    public static void Clear()
    {
        lock (Sync)
        {
            Pins.Clear();
            _SaveLocked();
        }

        SetEnabled(false);
    }

    /// <summary>
    ///     把 v4 / v6 两个最优节点组合成锁定用的地址列表，顺序固定为 IPv4 在前。
    /// </summary>
    internal static IReadOnlyList<IPAddress> Combine(IPAddress? ipv4, IPAddress? ipv6)
    {
        var list = new List<IPAddress>(MaxAddressesPerHost);
        if (ipv4 is not null) list.Add(ipv4);
        if (ipv6 is not null) list.Add(ipv6);
        return list;
    }

    /// <summary>
    ///     去重并限制为每个地址族最多一个，避免脏数据把简单规则搞复杂。
    /// </summary>
    private static IPAddress[] _Normalize(IEnumerable<IPAddress> addresses)
    {
        var seenV4 = false;
        var seenV6 = false;
        var list = new List<IPAddress>(MaxAddressesPerHost);

        foreach (var address in addresses)
        {
            switch (address.AddressFamily)
            {
                case AddressFamily.InterNetwork when !seenV4:
                    seenV4 = true;
                    break;
                case AddressFamily.InterNetworkV6 when !seenV6:
                    seenV6 = true;
                    break;
                default:
                    continue;
            }

            if (!list.Contains(address)) list.Add(address);
        }

        return list.ToArray();
    }

    private static void _RefreshLocked()
    {
        var raw = _ReadRaw();
        // 配置服务不可用时不能把「空字符串」当成权威值，否则会把内存中的锁定表清空
        if (raw is null) return;
        if (string.Equals(raw, _cachedRaw, StringComparison.Ordinal)) return;

        _cachedRaw = raw;
        Pins.Clear();
        if (string.IsNullOrWhiteSpace(raw)) return;

        try
        {
            if (JsonNode.Parse(raw) is not JsonObject json) return;
            foreach (var (host, value) in json)
            {
                if (string.IsNullOrWhiteSpace(host)) continue;

                // 兼容两种格式：单值字符串（旧版）与地址数组（支持双栈）
                var list = new List<IPAddress>(MaxAddressesPerHost);
                if (value is JsonArray array)
                    list.AddRange(array.Select(item => item?.ToString())
                        .Where(text => !string.IsNullOrWhiteSpace(text))
                        .Select(text => IPAddress.TryParse(text, out var parsed) ? parsed : null)
                        .OfType<IPAddress>());
                else if (IPAddress.TryParse(value?.ToString(), out var single))
                    list.Add(single);

                var normalized = _Normalize(list);
                if (normalized.Length > 0) Pins[host] = normalized;
            }
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, ModuleName, "解析 CDN 节点锁定表失败，已忽略其中的内容");
        }
    }

    private static void _SaveLocked()
    {
        var json = new JsonObject();
        foreach (var (host, addresses) in Pins.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            // 单节点仍写字符串，保证旧版本读到的配置依旧可用
            if (addresses.Length == 1)
                json[host] = addresses[0].ToString();
            else
                json[host] = new JsonArray(addresses
                    .Select(address => (JsonNode?)JsonValue.Create(address.ToString()))
                    .ToArray());
        }

        _cachedRaw = json.ToJsonString();
        try
        {
            Config.Download.CdnPinMap = _cachedRaw;
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, ModuleName, "写入 CDN 节点锁定表失败");
        }
    }

    /// <summary>
    ///     建连回调可能早于配置服务就绪，此处的读取必须容错。
    /// </summary>
    private static bool _ReadEnabled()
    {
        try
        {
            return Config.Download.CdnPinEnabled;
        }
        catch
        {
            return _enabledCache;
        }
    }

    /// <summary>
    ///     读取锁定的原始 JSON；若配置服务尚不可用则返回 <c>null</c>（表示「未知」而非「空」）。
    /// </summary>
    private static string? _ReadRaw()
    {
        try
        {
            return Config.Download.CdnPinMap ?? string.Empty;
        }
        catch
        {
            return null;
        }
    }
}
